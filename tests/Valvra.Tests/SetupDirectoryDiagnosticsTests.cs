using System.DirectoryServices.Protocols;
using Valvra.Core;
using Valvra.Web.Setup;
using Xunit;

namespace Valvra.Tests;

public sealed class SetupDirectoryDiagnosticsTests
{
    [Theory]
    [InlineData(81, "LDAPS-anslutningen")]
    [InlineData(91, "LDAPS-anslutningen")]
    [InlineData(85, "tog för lång tid")]
    [InlineData(49, "tjänstens autentisering")]
    [InlineData(48, "tjänstens autentisering")]
    [InlineData(50, "katalogläsrätt")]
    [InlineData(32, "sökbas")]
    [InlineData(34, "sökbas")]
    [InlineData(8, "starkare anslutningsskydd")]
    [InlineData(13, "starkare anslutningsskydd")]
    [InlineData(999, "användar- och gruppsökning")]
    public void WrappedLdapFailuresGiveSafeActionableGuidance(int code, string expected)
    {
        var exception = new VaultUnavailableException("private wrapper data",
            new LdapException(code, "private exception data", "private server data"));
        var message = SetupDirectoryDiagnostics.Describe(exception);
        Assert.Contains(expected, message);
        Assert.DoesNotContain("private", message);
    }

    [Fact]
    public void InstallerAccountRejectionIsDifferentFromServiceAuthenticationFailure()
    {
        Assert.Contains("personkonto", SetupDirectoryDiagnostics.Describe(new AccessDeniedException()));
        Assert.Contains("tjänstens autentisering", SetupDirectoryDiagnostics.Describe(new LdapException(49)));
    }

    [Fact]
    public void UnknownFailuresDoNotExposeExceptionDetails()
    {
        Assert.DoesNotContain("private", SetupDirectoryDiagnostics.Describe(new InvalidOperationException("private")));
        Assert.Contains("Windows", SetupDirectoryDiagnostics.Describe(new PlatformNotSupportedException("private")));
    }
}
