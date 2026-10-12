using System.DirectoryServices.Protocols;
using System.Net;
using System.Runtime.Versioning;

namespace Valvra.Infrastructure.Identity;

[SupportedOSPlatform("windows")]
internal static class LdapsConnection
{
    // Returns an unbound connection; the caller owns and disposes it.
    internal static LdapConnection Create(string server, int port, NetworkCredential credential, int timeoutSeconds)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(server, port), credential, AuthType.Negotiate);
        try
        {
            connection.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.SecureSocketLayer = true;
            // TLS supplies integrity and confidentiality. Windows rejects SASL
            // signing/sealing options on an SSL session, even before Bind.
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
