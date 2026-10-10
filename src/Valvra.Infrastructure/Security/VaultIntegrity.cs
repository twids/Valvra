using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Valvra.Core;
using Valvra.Infrastructure.Data;

namespace Valvra.Infrastructure.Security;

public sealed class IntegrityOptions
{
    public Guid InstallationId { get; set; }
    public string SigningCertificateThumbprint { get; set; } = "";
    public string[] VerificationCertificateThumbprints { get; set; } = [];
}

public sealed record IntegrityHead(Guid InstallationId, long Generation, string Hash, string KeyId, byte[] Signature);
public sealed record IntegrityCheckpoint(IntegrityHead Current, IntegrityHead? Prepared = null);
public interface IIntegrityCheckpointStore
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken ct);
    Task<IntegrityCheckpoint?> ReadAsync(CancellationToken ct);
    Task WriteAsync(IntegrityCheckpoint checkpoint, CancellationToken ct);
}
public interface IIntegritySigner
{
    IntegrityHead Sign(Guid installationId, long generation, string hash);
    bool Verify(IntegrityHead head);
}

// Every decision and write uses a serializable snapshot held under an external
// process-independent lease. The checkpoint is outside the database trust domain.
public sealed class VaultIntegrity(VaultDbContext db, IIntegrityCheckpointStore store, IIntegritySigner signer, IntegrityOptions options)
{
    private IDbContextTransaction? transaction;
    private IntegrityHead? head;
    private SortedDictionary<string, byte[]> snapshot = new(StringComparer.Ordinal);
    private bool active;
    public Guid InstallationId => options.InstallationId;
    public bool IsActive => active;
    public bool CommitUncertain { get; private set; }

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        if (active) return await operation();
        await using var lease = await store.AcquireAsync(ct);
        active = true;
        try
        {
            db.ChangeTracker.Clear();
            await BeginVerifiedAsync(ct);
            return await operation();
        }
        finally
        {
            if (transaction is not null) { await transaction.DisposeAsync(); transaction = null; }
            db.ChangeTracker.Clear(); active = false; head = null;
        }
    }

    public Task RunAsync(Func<Task> operation, CancellationToken ct) => RunAsync(async () => { await operation(); return true; }, ct);

    public async Task InitializeEmptyAsync(CancellationToken ct)
    {
        await using var lease = await store.AcquireAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var rows = await ReadSnapshotAsync(ct);
        if (rows.Count != 0) throw new VaultUnavailableException("Nyinstallation kräver ett tomt valv.");
        var existing = await store.ReadAsync(ct);
        if (existing is not null)
        {
            // Recover only an interrupted empty bootstrap; never re-baseline data.
            if (!signer.Verify(existing.Current) || existing.Prepared is not null || existing.Current.Generation != 0
                || existing.Current.Hash != Hash(rows, existing.Current.InstallationId)) throw Failure();
            options.InstallationId = existing.Current.InstallationId;
            return;
        }
        if (options.InstallationId == Guid.Empty) throw Failure();
        await store.WriteAsync(new(signer.Sign(options.InstallationId, 0, Hash(rows, options.InstallationId))), ct);
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        CommitUncertain = false;
        if (!active || transaction is null || head is null) throw new InvalidOperationException("Protected write requires an integrity scope.");
        db.ChangeTracker.DetectChanges();
        var expected = new SortedDictionary<string, byte[]>(snapshot, StringComparer.Ordinal);
        foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var key = RowKey(entry.Entity);
            if (entry.State == EntityState.Deleted) expected.Remove(key);
            else expected[key] = Serialize(entry.Entity);
        }
        await db.SaveChangesAsync(ct);
        var actual = await ReadSnapshotAsync(ct);
        // Do not bless trigger side effects, phantom rows, or arbitrary SQL changes.
        var expectedHash = Hash(expected, options.InstallationId);
        if (Hash(actual, options.InstallationId) != expectedHash) throw Failure();
        var next = signer.Sign(options.InstallationId, checked(head.Generation + 1), expectedHash);
        ct.ThrowIfCancellationRequested();
        // Once preparation starts, a client disconnect must not leave an old
        // database beside a prepared checkpoint. Complete this exact authorized
        // commit under a bounded internal deadline, independent of RequestAborted.
        // Storage/commit failures still fail closed and retain existing recovery rules.
        using var commitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var commitToken = commitDeadline.Token;
        await store.WriteAsync(new(head, next), commitToken);
        var committing = transaction;
        transaction = null;
        try { await committing.CommitAsync(commitToken); }
        catch { CommitUncertain = true; throw; }
        finally { await committing.DisposeAsync(); }
        // If this fails, the durable prepared head permits only recovery to this
        // exact authorized state. A rollback to the previous state is never guessed.
        try { await store.WriteAsync(new(next), commitToken); }
        catch { CommitUncertain = true; throw; }
        head = next;
        await BeginVerifiedAsync(commitToken);
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        if (!active) throw new InvalidOperationException("No integrity scope.");
        if (transaction is not null) { await transaction.DisposeAsync(); transaction = null; }
        db.ChangeTracker.Clear();
        await BeginVerifiedAsync(ct);
    }

    public async Task RecoverUncommittedAsync(Actor actor, CancellationToken ct)
    {
        if (!actor.IsEnabled) throw new AccessDeniedException();
        await using var lease = await store.AcquireAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var checkpoint = await store.ReadAsync(ct) ?? throw Failure();
        Validate(checkpoint);
        if (checkpoint.Prepared is null) throw new VaultValidationException("Ingen förberedd kontrollpunkt finns.");
        if (Hash(await ReadSnapshotAsync(ct), options.InstallationId) != checkpoint.Current.Hash) throw Failure();
        // A normal integrity scope cannot open this interrupted state. Resolve
        // the operator's role only AFTER verifying the signed previous snapshot,
        // in the same serializable transaction and external lease.
        if (!await db.Grants.AsNoTracking().AnyAsync(x => x.TargetKind == TargetKind.System && x.TargetId == options.InstallationId
            && x.SubjectKind == SubjectKind.User && x.Provider == actor.Provider && x.SubjectId == actor.SubjectId
            && x.StartsAt == null && x.ExpiresAt == null && x.Permissions.HasFlag((VaultPermission)(int)GlobalRole.AccessAdministrator), ct))
            throw new AccessDeniedException();
        // Explicit operator recovery of a failed write, not automatic rollback.
        await store.WriteAsync(new(checkpoint.Current), ct);
    }

    private async Task BeginVerifiedAsync(CancellationToken ct)
    {
        if (transaction is not null) throw new InvalidOperationException("Snapshot already active.");
        transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var checkpoint = await store.ReadAsync(ct) ?? throw Failure();
        Validate(checkpoint);
        snapshot = await ReadSnapshotAsync(ct);
        var hash = Hash(snapshot, options.InstallationId);
        if (checkpoint.Prepared is { } prepared)
        {
            if (hash != prepared.Hash) throw new VaultUnavailableException("Integritetsåterhämtning krävs. Inga hemligheter lämnas ut.");
            await store.WriteAsync(new(prepared), ct);
            head = prepared;
        }
        else
        {
            if (hash != checkpoint.Current.Hash) throw Failure();
            head = checkpoint.Current;
        }
    }

    private void Validate(IntegrityCheckpoint value)
    {
        if (value.Current.InstallationId != options.InstallationId || !signer.Verify(value.Current)
            || value.Prepared is { } next && (next.InstallationId != options.InstallationId
                || next.Generation != value.Current.Generation + 1 || !signer.Verify(next))) throw Failure();
    }

    private async Task<SortedDictionary<string, byte[]>> ReadSnapshotAsync(CancellationToken ct)
    {
        var rows = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        async Task Add<T>() where T : class
        { foreach (var row in await db.Set<T>().AsNoTracking().ToListAsync(ct)) rows.Add(RowKey(row), Serialize(row)); }
        await Add<ResourceGroup>(); await Add<VaultResource>(); await Add<AccessGrant>(); await Add<ResourceOwner>();
        await Add<SecretEntry>(); await Add<SecretVersion>(); await Add<SoftwareLicense>(); await Add<LicenseVersion>();
        await Add<LicenseAssignment>(); await Add<AuditRecord>();
        return rows;
    }

    private static string RowKey(object value) => value switch
    {
        ResourceGroup x => $"group/{x.Id:D}", VaultResource x => $"resource/{x.Id:D}",
        AccessGrant x => $"grant/{x.Id:D}", ResourceOwner x => $"owner/{x.Id:D}",
        SecretEntry x => $"secret/{x.Id:D}", SecretVersion x => $"secret-version/{x.EntryId:D}/{x.Version}",
        SoftwareLicense x => $"license/{x.Id:D}", LicenseVersion x => $"license-version/{x.LicenseId:D}/{x.Version}",
        LicenseAssignment x => $"assignment/{x.Id:D}", AuditRecord x => $"audit/{x.Id:D}",
        _ => throw new InvalidOperationException("Unprotected entity type.")
    };
    private static readonly JsonSerializerOptions Canonical = new() { Converters = { new UtcDateTimeConverter() } };
    internal static byte[] Serialize(object value) => JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Canonical);
    internal static string Hash(SortedDictionary<string, byte[]> rows, Guid installationId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"Valvra.Integrity|1|{installationId:D}|"));
        foreach (var row in rows)
        {
            // Length delimiters prevent ambiguous concatenation; ordinal keys are
            // independent of SQL Server/PostgreSQL's different GUID ordering.
            hash.AppendData(Encoding.UTF8.GetBytes($"{row.Key.Length}:{row.Key}:{row.Value.Length}:"));
            hash.AppendData(row.Value);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static VaultUnavailableException Failure() => new("Valvets integritet kan inte verifieras. Inga hemligheter lämnas ut.");
    private sealed class UtcDateTimeConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToUniversalTime());
    }
}
