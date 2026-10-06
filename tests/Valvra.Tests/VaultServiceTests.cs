using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Authorization;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Services;
using Valvra.Infrastructure.Security;
using Xunit;

namespace Valvra.Tests;

public sealed class VaultServiceTests
{
    [Fact]
    public async Task LostCommitAcknowledgementIsNotReportedAsRollback()
    {
        var interceptor = new LostCommitAcknowledgement();
        await using var fixture = await Fixture.CreateAsync(interceptor);
        interceptor.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Vault.CreateSecretAsync(fixture.User, fixture.ResourceId,
            "Account", new("u", "p", "n"), null, "request", default));
        Assert.Single(await fixture.Db.Secrets.ToListAsync());
        Assert.DoesNotContain(fixture.Transport.Events, x => x.Phase == AuditPhase.Failed);
        Assert.Contains(fixture.Transport.Events, x => x.Outcome == "CommitUncertain");
        await fixture.Audit.FlushAsync(default);
        Assert.Contains(fixture.Transport.Events, x => x.Phase == AuditPhase.Committed);
    }

    private sealed class LostCommitAcknowledgement : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        private int commits;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            // Intent and its receipt commit first. Lose the acknowledgement of the domain mutation itself.
            if (Enabled && ++commits == 3) { Enabled = false; throw new IOException("Simulated lost commit acknowledgement."); }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AuditFailurePreventsSecretRelease()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Vault.CreateSecretAsync(fixture.User, fixture.ResourceId, "Account", new("u", "p", "n"), null, "request", default);
        fixture.Transport.Fail = true;
        await Assert.ThrowsAsync<VaultUnavailableException>(() => fixture.Vault.RevealSecretAsync(fixture.User, id, null, "Secret.Reveal", "request", default));
        Assert.Equal(0, fixture.Cipher.Decryptions);
    }

    [Fact]
    public async Task AuditFailureBeforeMutationLeavesVaultUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync(); fixture.Transport.Fail = true;
        await Assert.ThrowsAsync<VaultUnavailableException>(() => fixture.Vault.CreateSecretAsync(fixture.User, fixture.ResourceId, "Account", new("u", "p", "n"), null, "request", default));
        Assert.Empty(await fixture.Db.Secrets.ToListAsync());
    }

    [Fact]
    public async Task FailureAfterCommitLeavesDurableResultForRedelivery()
    {
        await using var fixture = await Fixture.CreateAsync(); fixture.Transport.FailPhase = AuditPhase.Committed;
        await Assert.ThrowsAsync<VaultUnavailableException>(() => fixture.Vault.CreateSecretAsync(fixture.User, fixture.ResourceId, "Account", new("u", "p", "n"), null, "request", default));
        Assert.Single(await fixture.Db.Secrets.ToListAsync());
        Assert.Single(await fixture.Db.Audit.Where(x => !x.Delivered && x.Phase == AuditPhase.Committed).ToListAsync());
        fixture.Transport.FailPhase = null; await fixture.Audit.FlushAsync(default);
        Assert.False(await fixture.Db.Audit.AnyAsync(x => !x.Delivered));
    }

    [Fact]
    public async Task ReadIsAuditedAndVersionsAreIndependent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Vault.CreateSecretAsync(fixture.User, fixture.ResourceId, "Account", new("u", "first", "n"), null, "request", default);
        await fixture.Vault.UpdateSecretAsync(fixture.User, id, "Account", new("u", "second", "n"), null, 1, "request", default);
        Assert.Equal("first", (await fixture.Vault.RevealSecretAsync(fixture.User, id, 1, "Secret.Reveal", "request", default)).Password);
        Assert.Equal("second", (await fixture.Vault.RevealSecretAsync(fixture.User, id, null, "Secret.Copy", "request", default)).Password);
        Assert.Contains(fixture.Transport.Events, x => x.Action == "Secret.Copy" && x.Outcome == "ReleaseAuthorized");
    }

    [Fact]
    public async Task StaleVersionCannotOverwriteNewerSecret()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Vault.CreateSecretAsync(fixture.User, fixture.ResourceId, "Account", new("u", "first", "n"), null, "request", default);
        await fixture.Vault.UpdateSecretAsync(fixture.User, id, "Account", new("u", "second", "n"), null, 1, "request", default);
        await Assert.ThrowsAsync<VaultConflictException>(() => fixture.Vault.UpdateSecretAsync(fixture.User, id, "Account", new("u", "third", "n"), null, 1, "request", default));
        Assert.Equal("second", (await fixture.Vault.RevealSecretAsync(fixture.User, id, null, "Secret.Reveal", "request", default)).Password);
    }

    [Fact]
    public async Task DeletedEntryCannotBeRevealed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Vault.CreateSecretAsync(fixture.User, fixture.ResourceId, "Account", new("u", "p", "n"), null, "request", default);
        await fixture.Vault.SetDeletedAsync(fixture.User, id, true, 1, "request", default);
        await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Vault.RevealSecretAsync(fixture.User, id, null, "Secret.Reveal", "request", default));
        Assert.Single(await fixture.Db.SecretVersions.ToListAsync());
    }

    [Fact]
    public async Task LicenseAllocationCannotExceedSeatsAndDoesNotDecryptKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        var licenseId = await fixture.Vault.SaveLicenseAsync(fixture.User, null, fixture.ResourceId, "Product", "Vendor", "Reference", 1, null, new("license-key", "n"), 0, "request", default);
        await fixture.Vault.AssignLicenseAsync(fixture.User, new LicenseAssignment { LicenseId = licenseId, UserProvider = "ad", UserId = "user", Seats = 1 }, "request", default);
        Assert.Equal(0, fixture.Cipher.Decryptions);
        await Assert.ThrowsAsync<VaultValidationException>(() => fixture.Vault.AssignLicenseAsync(fixture.User, new LicenseAssignment { LicenseId = licenseId, UserProvider = "ad", UserId = "other", Seats = 1 }, "request", default));
        Assert.Single(await fixture.Db.Assignments.ToListAsync());
        Assert.Equal("license-key", (await fixture.Vault.RevealLicenseAsync(fixture.User, licenseId, "request", default)).LicenseKey);
    }

    [Fact]
    public async Task GroupCannotMoveIntoDescendant()
    {
        await using var fixture = await Fixture.CreateAsync();
        var child = await fixture.Vault.CreateGroupAsync(fixture.User, "Child", fixture.GroupId, "request", default);
        await Assert.ThrowsAsync<VaultValidationException>(() => fixture.Vault.MoveAsync(fixture.User, TargetKind.Group, fixture.GroupId, child, 1, "request", default));
    }

    [Fact]
    public async Task TemporaryAccessIsRecheckedAfterSlowAuditWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Vault.CreateSecretAsync(fixture.User, fixture.ResourceId, "Account", new("u", "p", "n"), null, "request", default);
        await fixture.Integrity.RunAsync(async () => {
            var grant = await fixture.Db.Grants.SingleAsync();
            grant.ExpiresAt = fixture.Clock.GetUtcNow().AddMinutes(1); await fixture.Integrity.SaveAsync(default);
        }, default);
        fixture.Transport.AfterSend = () => fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Vault.RevealSecretAsync(fixture.User, id, null, "Secret.Reveal", "request", default));
        Assert.Equal(0, fixture.Cipher.Decryptions);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public VaultDbContext Db { get; private set; } = null!;
        public FakeClock Clock { get; } = new();
        public RecordingTransport Transport { get; } = new();
        public SpyCipher Cipher { get; } = new();
        public MemoryCheckpointStore Checkpoints { get; } = new();
        public TestIntegritySigner IntegritySigner { get; } = new();
        public TestAuditSigner AuditSigner { get; } = new();
        public IntegrityOptions IntegrityOptions { get; } = new() { InstallationId = Guid.NewGuid() };
        public VaultIntegrity Integrity { get; private set; } = null!;
        public AuditService Audit { get; private set; } = null!;
        public VaultService Vault { get; private set; } = null!;
        public Actor User { get; } = new("ad", "user", "User", new HashSet<string>(), true, true, true);
        public Guid GroupId { get; } = Guid.NewGuid();
        public Guid ResourceId { get; } = Guid.NewGuid();
        public static async Task<Fixture> CreateAsync(IInterceptor? interceptor = null)
        {
            var f = new Fixture(); await f.Connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VaultDbContext>().UseSqlite(f.Connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            f.Db = new VaultDbContext(options.Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Integrity = new(f.Db, f.Checkpoints, f.IntegritySigner, f.IntegrityOptions);
            await f.Integrity.InitializeEmptyAsync(default);
            await f.Integrity.RunAsync(async () => {
            f.Db.Groups.Add(new ResourceGroup { Id = f.GroupId, Name = "Root", Revision = 1 });
            f.Db.Resources.Add(new VaultResource { Id = f.ResourceId, GroupId = f.GroupId, Name = "Resource", Revision = 1 });
            f.Db.Grants.Add(new AccessGrant { TargetKind = TargetKind.Group, TargetId = f.GroupId, Provider = "ad", SubjectKind = SubjectKind.User,
                SubjectId = "user", Permissions = VaultPermission.Metadata | VaultPermission.ReadSecret | VaultPermission.Modify, CreatedAt = f.Clock.GetUtcNow(), Revision = 1 });
            await f.Integrity.SaveAsync(default);
            }, default);
            f.Audit = new(f.Db, f.Transport, f.Clock, f.AuditSigner, f.Integrity);
            f.Vault = new(new VaultOperations(f.Db, new AccessService(f.Db, f.Clock), f.Audit, f.Cipher, f.Clock, new FakeDirectory(), f.Integrity), f.Integrity);
            return f;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); Cipher.Dispose(); IntegritySigner.Dispose(); AuditSigner.Dispose(); }
    }
}

internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan amount) => now += amount;
}
internal sealed class RecordingTransport : IAuditTransport
{
    public bool Fail { get; set; }
    public AuditPhase? FailPhase { get; set; }
    public Action? AfterSend { get; set; }
    public List<AuditEvent> Events { get; } = [];
    public Task<AuditReceipt> SendAsync(SignedAuditEvent signed, CancellationToken ct)
    {
        var auditEvent = signed.Event;
        if (Fail || auditEvent.Phase == FailPhase) throw new IOException("Test audit outage.");
        if (!Events.Any(x => x.Id == auditEvent.Id)) Events.Add(auditEvent);
        AfterSend?.Invoke();
        return Task.FromResult(new AuditReceipt(auditEvent.Id, AuditSignature.Hash(auditEvent)));
    }
}
internal sealed class SpyCipher : ISecretCipher, IDisposable
{
    private readonly TestKeyProtector keys = new();
    private readonly Valvra.Infrastructure.Security.EnvelopeCipher inner;
    public int Decryptions { get; private set; }
    public SpyCipher() => inner = new(keys);
    public string Encrypt<T>(T payload, Guid id, long version, string purpose, Guid resourceId, Guid installationId) => inner.Encrypt(payload, id, version, purpose, resourceId, installationId);
    public T Decrypt<T>(string envelope, Guid id, long version, string purpose, Guid resourceId, Guid installationId) { Decryptions++; return inner.Decrypt<T>(envelope, id, version, purpose, resourceId, installationId); }
    public string Rewrap(string envelope) => inner.Rewrap(envelope);
    public void Dispose() => keys.Dispose();
}
internal sealed class FakeDirectory : IDirectoryProvider
{
    public bool ReportGlobalRoles { get; set; } = true;
    public string ProviderId => "ad";
    public Task<Actor> ResolveAsync(string id, CancellationToken ct) => Task.FromResult(new Actor("ad", id, id, new HashSet<string>(), true, ReportGlobalRoles && id == "user", ReportGlobalRoles && id == "user"));
    public Task<IReadOnlyList<DirectorySubject>> SearchAsync(string query, SubjectKind kind, CancellationToken ct) => Task.FromResult<IReadOnlyList<DirectorySubject>>([new("ad", query, query, kind)]);
    public Task<DirectorySubject?> FindAsync(string id, SubjectKind kind, CancellationToken ct) => Task.FromResult<DirectorySubject?>(new("ad", id, id, kind));
}
