using System.Data.Common;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;
using Valvra.Core;
using Valvra.Infrastructure.Security;

namespace Valvra.Infrastructure.Auditing;

public sealed class AuditDatabaseOptions
{
    public Guid InstallationId { get; set; }
    public string Provider { get; set; } = "SqlServer";
    public string WriterConnectionString { get; set; } = "";
    public string ReaderConnectionString { get; set; } = "";
    public string SigningCertificateThumbprint { get; set; } = "";
    public string[] VerificationCertificateThumbprints { get; set; } = [];
    public int CommandTimeoutSeconds { get; set; } = 10;
    public int MaximumReadEvents { get; set; } = 100000;
    public long MaximumReadBytes { get; set; } = 64 * 1024 * 1024;
    public WindowsConnectionCredentials? ReaderWindowsCredentials { get; set; }
}

public sealed class CertificateAuditSigner(AuditDatabaseOptions options) : IAuditSigner
{
    public SignedAuditEvent Sign(AuditEvent value)
    {
        using var certificate = Load(options.SigningCertificateThumbprint);
        if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
            throw new CryptographicException("Active audit signing certificate is not valid at the current time.");
        using var rsa = certificate.GetRSAPrivateKey() ?? throw new CryptographicException("Audit signing private key unavailable.");
        if (rsa.KeySize < 3072) throw new CryptographicException("Audit RSA key must be at least 3072 bits.");
        return AuditSignature.Sign(value, Normalize(certificate.Thumbprint), rsa);
    }

    public bool Verify(SignedAuditEvent value)
    {
        if (!options.VerificationCertificateThumbprints.Append(options.SigningCertificateThumbprint)
            .Any(x => Normalize(x) == Normalize(value.SigningKeyId))) return false;
        using var certificate = Load(value.SigningKeyId);
        using var rsa = certificate.GetRSAPublicKey() ?? throw new CryptographicException("Audit verification key unavailable.");
        return AuditSignature.Verify(value, rsa);
    }

    private static X509Certificate2 Load(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, Normalize(thumbprint), false);
        try
        {
            if (matches.Count != 1) throw new CryptographicException("Audit signing/verification certificate missing or ambiguous.");
            return new X509Certificate2(matches[0]);
        }
        finally { foreach (var certificate in matches) certificate.Dispose(); }
    }
    private static string Normalize(string value) => value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
}

public static class AuditSignature
{
    public static byte[] Serialize(AuditEvent value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Format", value.Format); writer.WriteString("InstallationId", value.InstallationId);
            writer.WriteString("Id", value.Id); writer.WriteString("OperationId", value.OperationId);
            writer.WriteString("Timestamp", value.Timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("ActorProvider", value.ActorProvider); writer.WriteString("ActorId", value.ActorId);
            writer.WriteString("Action", value.Action);
            if (value.TargetId is { } id) writer.WriteString("TargetId", id); else writer.WriteNull("TargetId");
            writer.WriteNumber("Phase", (int)value.Phase); writer.WriteString("Outcome", value.Outcome);
            writer.WriteString("CorrelationId", value.CorrelationId); writer.WriteString("DetailsJson", value.DetailsJson);
            if (value.Format == 3)
            {
                var scope = value.Scope ?? throw new CryptographicException("Scoped audit event is missing its context.");
                writer.WritePropertyName("Scope"); writer.WriteStartObject();
                if (scope.ResourceId is { } resourceId) writer.WriteString("ResourceId", resourceId); else writer.WriteNull("ResourceId");
                writer.WriteString("ResourceName", scope.ResourceName); writer.WriteString("GroupId", scope.GroupId);
                writer.WriteStartArray("GroupPath");
                foreach (var group in scope.GroupPath) { writer.WriteStartObject(); writer.WriteString("Id", group.Id); writer.WriteString("Name", group.Name); writer.WriteEndObject(); }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
    public static string Hash(AuditEvent value) => Convert.ToHexStringLower(SHA256.HashData(Serialize(value)));
    public static SignedAuditEvent Sign(AuditEvent value, string keyId, RSA key)
    {
        var hash = Hash(value);
        var signed = Encoding.UTF8.GetBytes($"Valvra.Audit|{value.Format}|{keyId}|{hash}");
        return new(value, hash, keyId, key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), Serialize(value));
    }
    public static bool Verify(SignedAuditEvent value, RSA key)
    {
        if (value.Event.Format is not (2 or 3) || (value.Event.Format == 2 && value.Event.Scope is not null)
            || (value.Event.Format == 3 && (value.Event.Scope is not { GroupPath.Length: > 0 and <= 129 } scope
                || scope.GroupPath.Any(x => x is null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Name))
                || scope.GroupPath[0].Id != scope.GroupId || scope.GroupPath.Select(x => x.Id).Distinct().Count() != scope.GroupPath.Length))
            || !Serialize(value.Event).AsSpan().SequenceEqual(value.Payload)
            || Convert.ToHexStringLower(SHA256.HashData(value.Payload)) != value.PayloadHash) return false;
        return key.VerifyData(Encoding.UTF8.GetBytes($"Valvra.Audit|{value.Event.Format}|{value.SigningKeyId}|{value.PayloadHash}"),
            value.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }
}

/// <summary>Uses INSERT only: no RETURNING, OUTPUT, SELECT, schema creation, UPDATE or DELETE.</summary>
public sealed class DatabaseAuditTransport(AuditDatabaseOptions options, IAuditSigner signer) : IAuditTransport
{
    public async Task<AuditReceipt> SendAsync(SignedAuditEvent signed, CancellationToken cancellationToken)
    {
        if (signed.Event.InstallationId != options.InstallationId || !signer.Verify(signed)) throw new CryptographicException("Invalid signed audit event.");
        var auditEvent = signed.Event;
        await using var connection = AuditConnections.Create(options.Provider, options.WriterConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = options.Provider switch
        {
            "SqlServer" => "INSERT INTO dbo.AuditEvents (EventId, PayloadHash, Timestamp, ActorId, Action, TargetId, EventJson, SigningKeyId, Signature) VALUES (@id, @hash, @time, @actor, @action, @target, @json, @key, @signature)",
            "PostgreSql" => "INSERT INTO public.audit_events (event_id, payload_hash, timestamp, actor_id, action, target_id, event_json, signing_key_id, signature) VALUES (@id, @hash, @time, @actor, @action, @target, @json, @key, @signature) ON CONFLICT DO NOTHING",
            _ => throw new InvalidOperationException("Unsupported audit database provider.")
        };
        AuditConnections.Parameter(command, "id", auditEvent.Id);
        AuditConnections.Parameter(command, "hash", signed.PayloadHash);
        AuditConnections.Parameter(command, "time", auditEvent.Timestamp.UtcDateTime);
        AuditConnections.Parameter(command, "actor", auditEvent.ActorId);
        AuditConnections.Parameter(command, "action", auditEvent.Action);
        AuditConnections.Parameter(command, "target", auditEvent.TargetId);
        AuditConnections.Parameter(command, "json", Encoding.UTF8.GetString(signed.Payload));
        AuditConnections.Parameter(command, "key", signed.SigningKeyId);
        AuditConnections.Parameter(command, "signature", signed.Signature);
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // The only unique constraint is (EventId, PayloadHash), making identical re-delivery safe without SELECT.
        }
        return new(auditEvent.Id, signed.PayloadHash);
    }
}

public sealed record AuditQuery(DateTimeOffset? From, DateTimeOffset? To, string? ActorId, string? Action, Guid? TargetId, int Offset = 0,
    Guid? ResourceId = null, Guid? GroupId = null, bool IncludeSubgroups = true)
{
    public bool MatchesScope(AuditEvent value) =>
        (ResourceId is null || value.Scope?.ResourceId == ResourceId)
        && (GroupId is null || (IncludeSubgroups ? value.Scope?.GroupPath?.Any(x => x is not null && x.Id == GroupId) == true : value.Scope?.GroupId == GroupId));
}
public sealed record AuditTargetOption(Guid Id, string Name);
public sealed record AuditTargetOptions(IReadOnlyList<AuditTargetOption> Resources, IReadOnlyList<AuditTargetOption> Groups, int InvalidEventCount = 0);
public sealed record AuditEventView(AuditEvent Event, string PayloadHash, string SigningKeyId, bool SignatureValid);

public interface IAuditReader
{
    Task<IReadOnlyList<AuditEventView>> ReadAsync(Actor actor, AuditQuery query, CancellationToken ct);
    Task<AuditTargetOptions> ReadTargetsAsync(Actor actor, CancellationToken ct);
}

public sealed class AuditReader(AuditDatabaseOptions options, IAuditSigner signer) : IAuditReader
{
    public Task<IReadOnlyList<AuditEventView>> ReadAsync(Actor actor, AuditQuery query, CancellationToken ct) =>
        WindowsCredentialRunner.RunAsync(options.ReaderWindowsCredentials, () => ReadCoreAsync(actor, query, ct));

    public Task<AuditTargetOptions> ReadTargetsAsync(Actor actor, CancellationToken ct) =>
        WindowsCredentialRunner.RunAsync(options.ReaderWindowsCredentials, () => ReadTargetsCoreAsync(actor, ct));

    private async Task<AuditTargetOptions> ReadTargetsCoreAsync(Actor actor, CancellationToken ct) =>
        BuildTargets(await ReadAllAsync(actor, ct));

    internal static AuditTargetOptions BuildTargets(IEnumerable<AuditEventView> events)
    {
        var resources = new Dictionary<Guid, string>(); var groups = new Dictionary<Guid, string>();
        var invalid = 0;
        // Verify before deduplicating. Only signed timestamps/IDs decide the latest name.
        foreach (var view in events.OrderByDescending(x => x.Event.Timestamp).ThenByDescending(x => x.Event.Id))
        {
            if (!view.SignatureValid) { invalid++; continue; }
            if (view.Event.Scope is not { } scope) continue;
            if (scope.ResourceId is { } id) resources.TryAdd(id, scope.ResourceName ?? id.ToString());
            foreach (var group in scope.GroupPath) groups.TryAdd(group.Id, group.Name);
        }
        return new(resources.Select(x => new AuditTargetOption(x.Key, x.Value)).OrderBy(x => x.Name).ThenBy(x => x.Id).ToArray(),
            groups.Select(x => new AuditTargetOption(x.Key, x.Value)).OrderBy(x => x.Name).ThenBy(x => x.Id).ToArray(), invalid);
    }

    internal AuditEventView ReadVerified(DbDataReader reader)
    {
        var hash = reader.GetString(1); var key = reader.GetString(2);
        try
        {
            // Bound allocation before materializing an untrusted nvarchar(max)/text payload.
            if (reader.GetChars(0, 0, null, 0, 0) > 262144) return Invalid();
            var json = reader.GetString(0);
            var value = JsonSerializer.Deserialize<AuditEvent>(json);
            if (value is null) return Invalid();
            var signed = new SignedAuditEvent(value, hash, key, (byte[])reader[3], Encoding.UTF8.GetBytes(json));
            if (value.InstallationId != options.InstallationId || !signer.Verify(signed)) return Invalid();
            return new(value, hash, key, true);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or ArgumentException)
        { return Invalid(); }

        AuditEventView Invalid() => new(new AuditEvent(reader.GetGuid(5), Guid.Empty,
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
            "", "", "Audit.InvalidPayload", null, AuditPhase.Event, "InvalidSignature", ""), hash, key, false);
    }

    private async Task<IReadOnlyList<AuditEventView>> ReadCoreAsync(Actor actor, AuditQuery query, CancellationToken ct)
    {
        if (!actor.IsEnabled || !actor.IsAuditor) throw new AccessDeniedException();
        if (query.Offset < 0 || query.Offset > 1000000) throw new VaultValidationException("Ogiltig sidposition.");
        return SelectPage(await ReadAllAsync(actor, ct), query);
    }

    internal static IReadOnlyList<AuditEventView> SelectPage(IEnumerable<AuditEventView> events, AuditQuery query) =>
        events.Where(x => !x.SignatureValid || Matches(x.Event, query))
            .OrderByDescending(x => x.Event.Timestamp).ThenByDescending(x => x.Event.Id)
            .Skip(query.Offset).Take(200).ToArray();

    private static bool Matches(AuditEvent value, AuditQuery query) =>
        (query.From is null || value.Timestamp >= query.From) && (query.To is null || value.Timestamp < query.To)
        && (string.IsNullOrWhiteSpace(query.ActorId) || value.ActorId == query.ActorId)
        && (string.IsNullOrWhiteSpace(query.Action) || value.Action == query.Action)
        && (query.TargetId is null || value.TargetId == query.TargetId) && query.MatchesScope(value);

    private async Task<IReadOnlyList<AuditEventView>> ReadAllAsync(Actor actor, CancellationToken ct)
    {
        if (!actor.IsEnabled || !actor.IsAuditor) throw new AccessDeniedException();
        if (options.MaximumReadEvents is < 1 or > 1000000 || options.MaximumReadBytes < 1 || options.CommandTimeoutSeconds < 1)
            throw new VaultUnavailableException("Ogiltiga gränser för auditläsning.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.CommandTimeoutSeconds));
        try
        {
            await using var connection = AuditConnections.Create(options.Provider, options.ReaderConnectionString,
                disablePooling: !string.IsNullOrWhiteSpace(options.ReaderWindowsCredentials?.Username));
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand(); command.CommandTimeout = options.CommandTimeoutSeconds;
            // JSON and search projections are untrusted. Never parse, aggregate or page them in SQL.
            command.CommandText = options.Provider == "SqlServer"
                ? "SELECT TOP (@limit) EventJson, PayloadHash, SigningKeyId, Signature, Timestamp, EventId FROM dbo.AuditEvents"
                : "SELECT event_json, payload_hash, signing_key_id, signature, timestamp, event_id FROM public.audit_events LIMIT @limit";
            AuditConnections.Parameter(command, "limit", options.MaximumReadEvents + 1);
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            return await ReadRowsAsync(reader, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new VaultUnavailableException("Auditläsningens tidsgräns överskreds. Inget ofullständigt resultat visas."); }
    }

    internal async Task<IReadOnlyList<AuditEventView>> ReadRowsAsync(DbDataReader reader, CancellationToken ct)
    {
        var result = new List<AuditEventView>(); long bytes = 0;
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            bytes = checked(bytes + reader.GetChars(0, 0, null, 0, 0) * 2);
            if (result.Count >= options.MaximumReadEvents || bytes > options.MaximumReadBytes)
                throw new VaultUnavailableException("Auditläsningens datagräns överskreds. Inget ofullständigt resultat visas.");
            result.Add(ReadVerified(reader));
        }
        return result;
    }

}

internal static class AuditConnections
{
    public static DbConnection Create(string provider, string connectionString, bool disablePooling = false)
    {
        if (provider == "SqlServer")
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (disablePooling) builder.Pooling = false;
            return new SqlConnection(builder.ConnectionString);
        }
        if (provider == "PostgreSql")
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (disablePooling) builder.Pooling = false;
            return new NpgsqlConnection(builder.ConnectionString);
        }
        throw new InvalidOperationException("Unsupported audit database provider.");
    }
    public static void Parameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value;
        if (value is null) parameter.DbType = System.Data.DbType.Guid;
        command.Parameters.Add(parameter);
    }
}
