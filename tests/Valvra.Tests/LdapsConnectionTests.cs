using System.DirectoryServices.Protocols;
using System.Net;
using Valvra.Infrastructure.Identity;
using Xunit;

namespace Valvra.Tests;

public sealed class LdapsConnectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsLdapsSessionCanBeConfiguredBeforeBind(bool explicitCredential)
    {
        if (!OperatingSystem.IsWindows()) return;
        var credential = explicitCredential
            ? new NetworkCredential("synthetic-user", "synthetic-password", "example.test")
            : CredentialCache.DefaultNetworkCredentials;

        // No server or domain membership is needed: the reported failure happens before Bind.
        using var connection = LdapsConnection.Create("dc.example.test", 636, credential, 5);
        // The native SSL getter does not report an established TLS session before Bind.
        // Certificate trust and the TLS handshake require a real domain-controller check.
        Assert.Equal(3, connection.SessionOptions.ProtocolVersion);
        Assert.Equal(ReferralChasingOptions.None, connection.SessionOptions.ReferralChasing);
        Assert.Equal(AuthType.Negotiate, connection.AuthType);
        Assert.Equal(TimeSpan.FromSeconds(5), connection.Timeout);
    }
}
