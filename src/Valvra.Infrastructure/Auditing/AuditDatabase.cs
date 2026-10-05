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
    public string Provider { get; set; } = "SqlServer";
    public string WriterConnectionString { get; set; } = "";
    public string ReaderConnectionString { get; set; } = "";
    public string SigningCertificateThumbprint { get; set; } = "";
    public string[] VerificationCertificateThumbprints { get; set; } = [];
    public int CommandTimeoutSeconds { get; set; } = 10;
    public WindowsConnectionCredentials? ReaderWindowsCredentials { get; set; }
}

public sealed record SignedAuditEvent(AuditEvent Event, string PayloadHash, string SigningKeyId, byte[] Signature);

public interface IAuditSigner
{
    SignedAuditEvent Sign(AuditEvent value);
    bool Verify(SignedAuditEvent value);
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
    public static string Hash(AuditEvent value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    public static SignedAuditEvent Sign(AuditEvent value, string keyId, RSA key)
    {
        var hash = Hash(value);
        var signed = Encoding.UTF8.GetBytes($"Valvra.Audit|1|{keyId}|{hash}");
        return new(value, hash, keyId, key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }
    public static bool Verify(SignedAuditEvent value, RSA key)
    {
        if (Hash(value.Event) != value.PayloadHash) return false;
        return key.VerifyData(Encoding.UTF8.GetBytes($"Valvra.Audit|1|{value.SigningKeyId}|{value.PayloadHash}"),
            value.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }
}

/// <summary>Uses INSERT only: no RETURNING, OUTPUT, SELECT, schema creation, UPDATE or DELETE.</summary>
public sealed class DatabaseAuditTransport(AuditDatabaseOptions options, IAuditSigner signer) : IAuditTransport
{
    public async Task<AuditReceipt> SendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        var signed = signer.Sign(auditEvent);
        await using var connection = AuditConnections.Create(options.Provider, options.WriterConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = options.Provider switch
        {
            "SqlServer" => "INSERT INTO dbo.AuditEvents (EventId, PayloadHash, Timestamp, ActorId, Action, TargetId, EventJson, SigningKeyId, Signature) VALUES (@id, @hash, @time, @actor, @action, @target, @json, @key, @signature)",
            "PostgreSql" => "INSERT INTO audit_events (event_id, payload_hash, timestamp, actor_id, action, target_id, event_json, signing_key_id, signature) VALUES (@id, @hash, @time, @actor, @action, @target, @json, @key, @signature) ON CONFLICT DO NOTHING",
            _ => throw new InvalidOperationException("Unsupported audit database provider.")
        };
        AuditConnections.Parameter(command, "id", auditEvent.Id);
        AuditConnections.Parameter(command, "hash", signed.PayloadHash);
        AuditConnections.Parameter(command, "time", auditEvent.Timestamp.UtcDateTime);
        AuditConnections.Parameter(command, "actor", auditEvent.ActorId);
        AuditConnections.Parameter(command, "action", auditEvent.Action);
        AuditConnections.Parameter(command, "target", auditEvent.TargetId);
        AuditConnections.Parameter(command, "json", JsonSerializer.Serialize(auditEvent));
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

public sealed record AuditQuery(DateTimeOffset? From, DateTimeOffset? To, string? ActorId, string? Action, Guid? TargetId, int Offset = 0);
public sealed record AuditEventView(AuditEvent Event, string PayloadHash, string SigningKeyId, bool SignatureValid);

public interface IAuditReader
{
    Task<IReadOnlyList<AuditEventView>> ReadAsync(Actor actor, AuditQuery query, CancellationToken ct);
}

public sealed class AuditReader(AuditDatabaseOptions options, IAuditSigner signer) : IAuditReader
{
    public Task<IReadOnlyList<AuditEventView>> ReadAsync(Actor actor, AuditQuery query, CancellationToken ct) =>
        WindowsCredentialRunner.RunAsync(options.ReaderWindowsCredentials, () => ReadCoreAsync(actor, query, ct));

    private async Task<IReadOnlyList<AuditEventView>> ReadCoreAsync(Actor actor, AuditQuery query, CancellationToken ct)
    {
        if (!actor.IsEnabled || !actor.IsAuditor) throw new AccessDeniedException();
        if (query.Offset < 0 || query.Offset > 1000000) throw new VaultValidationException("Ogiltig sidposition.");
        // NEW_CREDENTIALS keeps the local SID: do not reuse pools across outbound identities.
        await using var connection = AuditConnections.Create(options.Provider, options.ReaderConnectionString,
            disablePooling: !string.IsNullOrWhiteSpace(options.ReaderWindowsCredentials?.Username));
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        var sqlServer = options.Provider == "SqlServer";
        var columns = sqlServer ? "EventJson, PayloadHash, SigningKeyId, Signature" : "event_json, payload_hash, signing_key_id, signature";
        var table = sqlServer ? "dbo.AuditEvents" : "audit_events";
        string Col(string name) => sqlServer ? name : name switch
        { "ActorId" => "actor_id", "TargetId" => "target_id", "Timestamp" => "timestamp", "Action" => "action", _ => throw new InvalidOperationException() };
        var filters = new List<string>();
        if (query.From is { } from) { filters.Add($"{Col("Timestamp")} >= @from"); AuditConnections.Parameter(command, "from", from.UtcDateTime); }
        if (query.To is { } to) { filters.Add($"{Col("Timestamp")} < @to"); AuditConnections.Parameter(command, "to", to.UtcDateTime); }
        if (!string.IsNullOrWhiteSpace(query.ActorId)) { filters.Add($"{Col("ActorId")} = @actor"); AuditConnections.Parameter(command, "actor", query.ActorId); }
        if (!string.IsNullOrWhiteSpace(query.Action)) { filters.Add($"{Col("Action")} = @action"); AuditConnections.Parameter(command, "action", query.Action); }
        if (query.TargetId is { } target) { filters.Add($"{Col("TargetId")} = @target"); AuditConnections.Parameter(command, "target", target); }
        AuditConnections.Parameter(command, "offset", query.Offset);
        command.CommandText = $"SELECT {columns} FROM {table}" + (filters.Count > 0 ? " WHERE " + string.Join(" AND ", filters) : "")
            + $" ORDER BY {Col("Timestamp")} DESC, " + (sqlServer ? "EventId DESC OFFSET @offset ROWS FETCH NEXT 200 ROWS ONLY" : "event_id DESC LIMIT 200 OFFSET @offset");
        var result = new List<AuditEventView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var value = JsonSerializer.Deserialize<AuditEvent>(reader.GetString(0)) ?? throw new InvalidDataException("Invalid audit payload.");
            var signed = new SignedAuditEvent(value, reader.GetString(1), reader.GetString(2), (byte[])reader[3]);
            bool valid;
            try { valid = signer.Verify(signed); }
            catch (CryptographicException) { valid = false; }
            result.Add(new(value, signed.PayloadHash, signed.SigningKeyId, valid));
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
