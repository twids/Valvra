using System.DirectoryServices.Protocols;
using System.Net;
using System.Runtime.Versioning;
using Valvra.Core;

namespace Valvra.Infrastructure.Identity;

public sealed class LdapTestProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 636;
    public string Domain { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 5;
}
public sealed class LdapTestOptions { public LdapTestProfile[] Profiles { get; set; } = []; }

[SupportedOSPlatform("windows")]
public sealed class LdapCredentialTester(LdapTestOptions options) : ICredentialTester
{
    public Task<CredentialTestResult> TestAsync(string profileId, SecretPayload credential, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var profile = options.Profiles.SingleOrDefault(x => x.Id == profileId)
            ?? throw new VaultValidationException("LDAP-profilen är inte godkänd.");
        if (string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrEmpty(credential.Password))
            throw new VaultValidationException("Användarnamn och lösenord krävs för test.");
        if (string.IsNullOrWhiteSpace(profile.Server) || profile.Server.Contains('/') || profile.Port is < 1 or > 65535
            || profile.TimeoutSeconds is < 1 or > 30) throw new VaultValidationException("Ogiltig LDAP-profil.");
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new LdapConnection(new LdapDirectoryIdentifier(profile.Server, profile.Port),
            new NetworkCredential(credential.Username, credential.Password, profile.Domain), AuthType.Negotiate)
        { Timeout = TimeSpan.FromSeconds(profile.TimeoutSeconds) };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = true;
        connection.SessionOptions.Signing = true;
        connection.SessionOptions.Sealing = true;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        try { connection.Bind(); return CredentialTestResult.Success; }
        catch (LdapException ex) when (ex.ErrorCode == 49) { return CredentialTestResult.Rejected; }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException) { return CredentialTestResult.ConnectionFailed; }
    }, cancellationToken);
}
