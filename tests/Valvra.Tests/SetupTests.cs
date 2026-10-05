using System.Runtime.Versioning;
using System.Security.Cryptography;
using Valvra.Web.Setup;
using Xunit;

namespace Valvra.Tests;

public sealed class SetupTests
{
    [Fact]
    public void BootstrapCodeIsHighEntropyAndOnlyItsHashIsStored()
    {
        var directory = Path.Combine(Path.GetTempPath(), "valvra-bootstrap-test-" + Guid.NewGuid());
        try
        {
            var store = new SetupConfigurationStore(directory); var code = store.Initialize();
            Assert.Equal(64, code.Length); Assert.True(store.ValidateToken(code));
            Assert.False(store.ValidateToken(new string('0', 64))); Assert.False(store.ValidateToken(null));
            Assert.DoesNotContain(code, File.ReadAllText(Path.Combine(directory, "setup-token.sha256")));
            var replacement = store.Initialize(); Assert.False(store.ValidateToken(code)); Assert.True(store.ValidateToken(replacement));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void WindowsSqlConnectionDoesNotStoreDatabasePassword()
    {
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(SetupValidator.Connection(new DatabaseSetupInput
        { Server = "database.example.test", Database = "Valvra", Authentication = "Windows", Username = "unused", Password = "must-not-be-stored" }));
        Assert.True(connection.IntegratedSecurity); Assert.False(connection.TrustServerCertificate);
        Assert.Equal("", connection.Password); Assert.Equal("", connection.UserID);
    }

    [Fact]
    public void DatabaseFieldsCannotInjectConnectionStringOptions()
    {
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(SetupValidator.Connection(new DatabaseSetupInput
        { Server = "server;TrustServerCertificate=true", Database = "Valvra", Authentication = "Windows" }));
        Assert.False(connection.TrustServerCertificate); Assert.Equal("server;TrustServerCertificate=true", connection.DataSource);
    }

    [Fact]
    public async Task ProtectedSettingsDoNotExposePlaintextAndConsumeBootstrap()
    {
        if (!OperatingSystem.IsWindows()) return;
        await ProtectedSettingsWindowsAsync();
    }

    [SupportedOSPlatform("windows")]
    private static Task ProtectedSettingsWindowsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "valvra-settings-test-" + Guid.NewGuid());
        try
        {
            var store = new SetupConfigurationStore(directory); var code = store.Initialize();
            var settings = new InstallationSettings(); settings.AuditDatabase.ReaderConnectionString = "synthetic-configuration-secret";
            store.Save(settings); Assert.False(store.ValidateToken(code)); Assert.True(store.HasSettings);
            var bytes = File.ReadAllBytes(Path.Combine(directory, "installation.bin"));
            Assert.DoesNotContain("synthetic-configuration-secret", System.Text.Encoding.UTF8.GetString(bytes));
            using var stream = store.Load(); using var reader = new StreamReader(stream!);
            Assert.Contains("synthetic-configuration-secret", reader.ReadToEnd());
            bytes[bytes.Length / 2] ^= 1; File.WriteAllBytes(Path.Combine(directory, "installation.bin"), bytes);
            Assert.Throws<CryptographicException>(() => store.Load());
            return Task.CompletedTask;
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
