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
    [Fact]
    public void DraftsKeepIncompleteSettingsButExcludeAllPasswordsAndCodes()
    {
        var draft = new SetupDraft { Step = 99, Fields = new()
        {
            ["vaultServer"] = "unfinished,", ["hosts"] = "", ["code"] = "private-code",
            ["vaultPassword"] = "vault-secret", ["writerPassword"] = "writer-secret",
            ["readerPassword"] = "reader-secret", ["readerWindowsPassword"] = "windows-secret",
            ["vaultAdminPassword"] = "admin-secret", ["auditAdminPassword"] = "audit-admin-secret",
            ["unknown"] = "ignored", ["vaultAdminUsername"] = "installer"
        }};
        var sanitized = draft.Sanitized();
        Assert.Equal(4, sanitized.Step);
        Assert.Equal(3, sanitized.Fields.Count);
        Assert.Equal("unfinished,", sanitized.Fields["vaultServer"]);
        Assert.Equal("", sanitized.Fields["hosts"]);
        Assert.Equal("installer", sanitized.Fields["vaultAdminUsername"]);
    }

    [Fact]
    public void ProtectedDraftSurvivesReloadWithoutCompletingInstallation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "valvra-draft-test-" + Guid.NewGuid());
        try
        {
            var store = new SetupConfigurationStore(directory); var code = store.Initialize();
            Assert.Null(store.ReadDraft());
            store.SaveDraft(new() { Step = 2, Fields = new() { ["adServer"] = "draft.example.test", ["vaultPassword"] = "excluded-secret" } });
            var reloaded = new SetupConfigurationStore(directory);
            Assert.True(reloaded.ValidateToken(code)); Assert.False(reloaded.HasSettings); Assert.False(reloaded.HasCheckpoint);
            var draft = reloaded.ReadDraft()!;
            Assert.Equal(2, draft.Step); Assert.Equal("draft.example.test", Assert.Single(draft.Fields).Value);
            Assert.DoesNotContain("draft.example.test", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "setup-draft.bin"))));
            store.SaveDraft(new() { Step = 3, Fields = new() { ["hosts"] = "new.example.test" } });
            Assert.Equal(3, reloaded.ReadDraft()!.Step);
            store.Save(new());
            Assert.Null(reloaded.ReadDraft()); Assert.False(File.Exists(Path.Combine(directory, "setup-draft.bin")));
            Assert.Throws<AccessDeniedException>(() => reloaded.SaveDraft(new()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SqlConnectionOptionsSurviveSetupAndProductionValidation(bool separateReader)
    {
        var request = SharedRequest();
        request.Vault.TrustServerCertificate = true;
        request.Vault.MultiSubnetFailover = true;
        request.Vault.ConnectTimeoutSeconds = 45;
        request.AuditWriter.TrustServerCertificate = true;
        request.UseSeparateAuditReader = separateReader;
        request.AuditReader = new() { Server = "sql.example.test", Database = "ValvraAudit" };
        var settings = SetupValidator.Build(request);
        var vault = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(settings.VaultDatabase.ConnectionString);
        Assert.True(vault.TrustServerCertificate); Assert.True(vault.MultiSubnetFailover);
        Assert.Equal(45, vault.ConnectTimeout);
        Assert.Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, vault.Encrypt);
        Assert.True(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(settings.AuditDatabase.WriterConnectionString).TrustServerCertificate);
        Assert.Equal(!separateReader, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(settings.AuditDatabase.ReaderConnectionString).TrustServerCertificate);
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(settings));
        ValvraRegistration.ValidateProduction(new ConfigurationBuilder().AddJsonStream(stream).Build());
        vault.Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional;
        settings.VaultDatabase.ConnectionString = vault.ConnectionString;
        using var invalid = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(settings));
        Assert.Throws<InvalidOperationException>(() => ValvraRegistration.ValidateProduction(new ConfigurationBuilder().AddJsonStream(invalid).Build()));
    }

    [Theory]
    [InlineData("SqlServer", 0)]
    [InlineData("SqlServer", 121)]
    [InlineData("PostgreSql", 0)]
    [InlineData("PostgreSql", 121)]
    public void ConnectionTimeoutMustBeBounded(string provider, int timeout) =>
        Assert.Throws<VaultValidationException>(() => SetupValidator.Connection(new()
        { Provider = provider, Server = "db.example.test", Database = "Valvra", ConnectTimeoutSeconds = timeout }));

    [Fact]
    public void PostgresKeepsCertificateValidationAndRejectsSqlOnlyOptions()
    {
        var input = new DatabaseSetupInput { Provider = "PostgreSql", Server = "db.example.test", Database = "Valvra", ConnectTimeoutSeconds = 30 };
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(SetupValidator.Connection(input));
        Assert.Equal(Npgsql.SslMode.VerifyFull, connection.SslMode); Assert.Equal(30, connection.Timeout);
        input.TrustServerCertificate = true;
        Assert.Throws<VaultValidationException>(() => SetupValidator.Connection(input));
        input.TrustServerCertificate = false; input.MultiSubnetFailover = true;
        Assert.Throws<VaultValidationException>(() => SetupValidator.Connection(input));
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
