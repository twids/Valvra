using System.DirectoryServices.Protocols;
using Valvra.Core;
using Valvra.Web.Setup;
using Xunit;

namespace Valvra.Tests;

public sealed class SetupErrorDetailsTests
{
    [Fact]
    public void ShowsOriginalErrorChainAndLdapServerDiagnostic()
    {
        var error = new VaultUnavailableException("Directory check failed",
            new LdapException(49, "Invalid credentials", "80090308: AcceptSecurityContext error, data 52e"));
        var details = SetupErrorDetails.Format(error, []);
        Assert.Contains("VaultUnavailableException: Directory check failed", details);
        Assert.Contains("LdapException: Invalid credentials", details);
        Assert.Contains("LDAP error code: 49", details);
        Assert.Contains("80090308: AcceptSecurityContext error, data 52e", details);
    }

    [Fact]
    public void RedactsSubmittedSecretsInBothExceptionsAndServerMessages()
    {
        var error = new Exception("password=long-secret; token=setup-code",
            new LdapException(49, "short-secret", "long-secret and short-secret"));
        var details = SetupErrorDetails.Format(error, [null, "", "secret", "long-secret", "short-secret", "setup-code"]);
        Assert.DoesNotContain("secret", details);
        Assert.DoesNotContain("setup-code", details);
        Assert.Contains("[redacted]", details);
        Assert.Contains("LDAP error code: 49", details);
    }

    [Fact]
    public void IncludesAllAggregateFailures()
    {
        var details = SetupErrorDetails.Format(new AggregateException("Failures",
            new InvalidOperationException("First"), new TimeoutException("Second")), []);
        Assert.Contains("InvalidOperationException: First", details);
        Assert.Contains("TimeoutException: Second", details);
    }
}
