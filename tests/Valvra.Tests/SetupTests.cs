using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Valvra.Infrastructure.Configuration;
using Valvra.Core;
using Valvra.Web.Setup;
using Xunit;

namespace Valvra.Tests;

public sealed class SetupTests
{
    private static SetupRequest SharedRequest() => new()
    {
        AllowedHosts = "valvra.example.test",
        Vault = new() { Server = "sql.example.test", Database = "Valvra" },
        AuditWriter = new() { Server = "sql.example.test", Database = "ValvraAudit" },
        KeyProtection = new() { ActiveThumbprint = "encryption" }, AuditSigningThumbprint = "audit",
        IntegritySigningThumbprint = "integrity",
        ActiveDirectory = new() { Server = "ad.example.test", BaseDn = "DC=example,DC=test" }
    };

    [Fact]
    public void SharedServiceIdentityIsTheDefaultAndDoesNotStoreReaderCredentials()
    {
        var request = SharedRequest();
        request.AuditReaderWindowsCredentials = new() { Username = "unused", Password = "must-not-be-stored" };
        var settings = SetupValidator.Build(request);
        Assert.Equal(settings.AuditDatabase.WriterConnectionString, settings.AuditDatabase.ReaderConnectionString);
        Assert.Null(settings.AuditDatabase.ReaderWindowsCredentials);
        Assert.DoesNotContain("must-not-be-stored", JsonSerializer.Serialize(settings));
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(settings.AuditDatabase.ReaderConnectionString);
        Assert.True(connection.IntegratedSecurity); Assert.Equal("", connection.Password);
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(settings));
        ValvraRegistration.ValidateProduction(new ConfigurationBuilder().AddJsonStream(stream).Build());
    }

    [Theory]
    [InlineData("Valvra", "Valvra")]
    [InlineData("Valvra", "OtherAudit")]
    public void SharedIdentityDoesNotRemoveDatabaseSeparation(string writerDatabase, string readerDatabase)
    {
        var request = SharedRequest(); request.UseSeparateAuditReader = true;
        request.AuditWriter.Database = writerDatabase;
        request.AuditReader = new() { Server = "sql.example.test", Database = readerDatabase };
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(SetupValidator.Build(request)));
        Assert.Throws<InvalidOperationException>(() => ValvraRegistration.ValidateProduction(new ConfigurationBuilder().AddJsonStream(stream).Build()));
    }

    [Fact]
    public void SeparateReaderRemainsAnExplicitOption()
    {
        var request = SharedRequest(); request.UseSeparateAuditReader = true;
        request.AuditReader = new() { Server = "sql.example.test", Database = "ValvraAudit", Authentication = "Password", Username = "reader", Password = "synthetic" };
        var settings = SetupValidator.Build(request);
        Assert.NotEqual(settings.AuditDatabase.WriterConnectionString, settings.AuditDatabase.ReaderConnectionString);
        Assert.Equal("reader", new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(settings.AuditDatabase.ReaderConnectionString).UserID);
        request.AuditReader.Provider = "PostgreSql";
        Assert.Throws<VaultValidationException>(() => SetupValidator.Build(request));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void SharedAndSeparateMinimalAuditPermissionsAreAccepted(bool insert, bool select, bool writer) =>
        SetupValidator.ValidateAuditPermissions([insert, select, false, false, false, false], writer);

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public void MissingRequiredOrDestructiveAuditPermissionsAreRejected(int index, bool writer)
    {
        bool?[] permissions = [true, true, false, false, false, false];
        permissions[index] = index >= 2;
        Assert.Throws<InvalidOperationException>(() => SetupValidator.ValidateAuditPermissions(permissions, writer));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void UnknownAuditPermissionsFailClosed(int index)
    {
        bool?[] permissions = [true, true, false, false, false, false]; permissions[index] = null;
        Assert.Throws<InvalidOperationException>(() => SetupValidator.ValidateAuditPermissions(permissions, true));
        Assert.Throws<InvalidOperationException>(() => SetupValidator.ValidateAuditPermissions(permissions, false));
    }

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
            var original = store.ReadSettings()!;
            var candidate = Valvra.Web.Administration.DirectorySettings.From(original) with {Server="new.example.test", UserSearchBaseDn="OU=Users,DC=example"};
            var updated = store.UpdateDirectory(candidate); Assert.Equal(candidate.Revision+1,updated.Revision);
            var reloaded = store.ReadSettings()!;
            Assert.Equal(original.AuditDatabase.ReaderConnectionString,reloaded.AuditDatabase.ReaderConnectionString);
            Assert.Equal(original.Integrity.InstallationId,reloaded.Integrity.InstallationId);
            Assert.Equal(JsonSerializer.Serialize(original.KeyProtection),JsonSerializer.Serialize(reloaded.KeyProtection));
            Assert.Equal(JsonSerializer.Serialize(original.LdapTests),JsonSerializer.Serialize(reloaded.LdapTests));
            Assert.Equal(JsonSerializer.Serialize(original.Identity),JsonSerializer.Serialize(reloaded.Identity));
            Assert.Throws<VaultConflictException>(()=>store.UpdateDirectory(candidate));
            Assert.DoesNotContain("new.example.test",System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory,"installation.bin"))));
            bytes = File.ReadAllBytes(Path.Combine(directory,"installation.bin"));
            bytes[bytes.Length / 2] ^= 1; File.WriteAllBytes(Path.Combine(directory, "installation.bin"), bytes);
            Assert.Throws<CryptographicException>(() => store.Load());
            return Task.CompletedTask;
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
