using System.Security.Claims;

namespace Valvra.Core;

public sealed record Actor(string Provider, string SubjectId, string DisplayName,
    IReadOnlySet<string> GroupIds, bool IsEnabled, bool IsAccessAdministrator, bool IsAuditor);
public sealed record DirectorySubject(string Provider, string Id, string Name, SubjectKind Kind);

public interface IIdentityProvider
{
    string ProviderId { get; }
    string? GetSubjectId(ClaimsPrincipal principal);
}

public interface IDirectoryProvider
{
    string ProviderId { get; }
    Task<Actor> ResolveAsync(string subjectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DirectorySubject>> SearchAsync(string query, SubjectKind kind, CancellationToken cancellationToken);
    Task<DirectorySubject?> FindAsync(string subjectId, SubjectKind kind, CancellationToken cancellationToken);
}

public sealed record SecretPayload(string Username, string Password, string Notes);
public sealed record LicensePayload(string LicenseKey, string Notes);

public interface IKeyProtector
{
    string ActiveKeyId { get; }
    byte[] Wrap(ReadOnlySpan<byte> key);
    byte[] Unwrap(string keyId, ReadOnlySpan<byte> wrappedKey);
}

public interface ISecretCipher
{
    string Encrypt<T>(T payload, Guid id, long version, string purpose);
    T Decrypt<T>(string envelope, Guid id, long version, string purpose);
    string Rewrap(string envelope);
}

public sealed record AuditEvent(Guid Id, Guid OperationId, DateTimeOffset Timestamp,
    string ActorProvider, string ActorId, string Action, Guid? TargetId,
    AuditPhase Phase, string Outcome, string CorrelationId, string DetailsJson = "{}");
public sealed record AuditReceipt(Guid EventId, string Hash);

public interface IAuditTransport
{
    Task<AuditReceipt> SendAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}

public enum CredentialTestResult { Success, Rejected, ConnectionFailed }
public interface ICredentialTester
{
    Task<CredentialTestResult> TestAsync(string profileId, SecretPayload credential, CancellationToken cancellationToken);
}

public sealed class AccessDeniedException : Exception
{
    public AccessDeniedException() : base("Åtkomst nekad.") { }
}
public sealed class VaultUnavailableException : Exception
{
    public VaultUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
public sealed class VaultConflictException(string message) : Exception(message);
public sealed class VaultValidationException(string message) : Exception(message);
