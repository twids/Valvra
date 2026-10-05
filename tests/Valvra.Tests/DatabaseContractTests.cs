using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Authorization;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Services;
using Xunit;

namespace Valvra.Tests;

public sealed class RequiresDatabaseAttribute : FactAttribute
{
    public RequiresDatabaseAttribute(string provider)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(provider == "SqlServer" ? "VALVRA_TEST_SQLSERVER" : "VALVRA_TEST_POSTGRESQL")))
            Skip = $"Set the dedicated {provider} test server connection to run real database/permission tests.";
    }
}

public sealed class DatabaseContractTests
{
    [RequiresDatabase("SqlServer")]
    [Trait("Category", "Database")]
    public Task SqlServerMigrationAndAppendOnlyAuditContract() => RunAsync("SqlServer");

    [RequiresDatabase("PostgreSql")]
    [Trait("Category", "Database")]
    public Task PostgreSqlMigrationAndAppendOnlyAuditContract() => RunAsync("PostgreSql");

    private static async Task RunAsync(string provider)
    {
        await using var f = await DatabaseFixture.CreateAsync(provider);
        await using var db = f.Db(); await db.Database.MigrateAsync();
        Assert.False((await db.Database.GetPendingMigrationsAsync()).Any());
        using var signer = new TestAuditSigner();
        var transport = new DatabaseAuditTransport(f.AuditOptions, signer);
        var audit = new AuditService(db, transport, TimeProvider.System);
        using var cipher = new SpyCipher();
        var vault = new VaultService(db, new AccessService(db, TimeProvider.System), audit, cipher, TimeProvider.System, new FakeDirectory());
        var actor = new Actor("ad", "user", "User", new HashSet<string>(), true, true, true);
        var group = await vault.CreateGroupAsync(actor, "Root", null, "database-test", default);
        var resource = await vault.CreateResourceAsync(actor, group, "Server", "database-test", default);
        await vault.GrantAsync(actor, new AccessGrant { TargetKind = TargetKind.Resource, TargetId = resource, Provider = "ad", SubjectKind = SubjectKind.User,
            SubjectId = "user", Permissions = VaultPermission.Metadata | VaultPermission.ReadSecret | VaultPermission.Modify }, "database-test", default);
        var secret = await vault.CreateSecretAsync(actor, resource, "Account", new("u", "secret", "note"), null, "database-test", default);
        Assert.Equal("secret", (await vault.RevealSecretAsync(actor, secret, null, "Secret.Reveal", "database-test", default)).Password);
        var events = await new AuditReader(f.AuditOptions, signer).ReadAsync(actor, new(null, null, null, null, null), default);
        Assert.NotEmpty(events); Assert.All(events, x => Assert.True(x.SignatureValid));
        var value = new AuditEvent(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "ad", "user", "Duplicate.Test", resource, AuditPhase.Event, "Success", "database-test");
        Assert.Equal(await transport.SendAsync(value, default), await transport.SendAsync(value, default));
        events = await new AuditReader(f.AuditOptions, signer).ReadAsync(actor, new(null, null, null, "Duplicate.Test", null), default);
        Assert.Single(events);
        await f.MustDenyAsync(f.AuditOptions.WriterConnectionString, provider == "SqlServer" ? "SELECT * FROM dbo.AuditEvents" : "SELECT * FROM audit_events");
        await f.MustDenyAsync(f.AuditOptions.WriterConnectionString, provider == "SqlServer" ? "UPDATE dbo.AuditEvents SET Action='x' WHERE 1=0" : "UPDATE audit_events SET action='x' WHERE false");
        await f.MustDenyAsync(f.AuditOptions.WriterConnectionString, provider == "SqlServer" ? "DELETE FROM dbo.AuditEvents WHERE 1=0" : "DELETE FROM audit_events WHERE false");
        await f.MustDenyAsync(f.AuditOptions.ReaderConnectionString, provider == "SqlServer" ? "DELETE FROM dbo.AuditEvents WHERE 1=0" : "DELETE FROM audit_events WHERE false");
        var version = await db.SecretVersions.SingleAsync(); Assert.DoesNotContain("secret", version.EnvelopeJson);
    }
}

internal sealed class TestAuditSigner : IAuditSigner, IDisposable
{
    private readonly RSA rsa = RSA.Create(3072);
    public SignedAuditEvent Sign(AuditEvent value) => AuditSignature.Sign(value, "test-key", rsa);
    public bool Verify(SignedAuditEvent value) => AuditSignature.Verify(value, rsa);
    public void Dispose() => rsa.Dispose();
}

internal sealed class DatabaseFixture : IAsyncDisposable
{
    private string provider = "";
    private string serverConnection = "";
    private string vaultConnection = "";
    private string auditAdminConnection = "";
    private string vaultName = "";
    private string auditName = "";
    private string writerName = "";
    private string readerName = "";
    private string writerRole = "";
    private string readerRole = "";
    public AuditDatabaseOptions AuditOptions { get; private set; } = new();

    public static async Task<DatabaseFixture> CreateAsync(string provider)
    {
        var f = new DatabaseFixture { provider = provider };
        f.serverConnection = Environment.GetEnvironmentVariable(provider == "SqlServer" ? "VALVRA_TEST_SQLSERVER" : "VALVRA_TEST_POSTGRESQL")!;
        var suffix = Guid.NewGuid().ToString("N");
        f.vaultName = "valvra_test_v_" + suffix; f.auditName = "valvra_test_a_" + suffix;
        f.writerName = "valvra_test_w_" + suffix; f.readerName = "valvra_test_r_" + suffix;
        f.writerRole = "valvra_test_wrole_" + suffix; f.readerRole = "valvra_test_rrole_" + suffix;
        var password = "V!a1" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        try
        {
            if (provider == "SqlServer")
            {
                var root = new SqlConnectionStringBuilder(f.serverConnection) { InitialCatalog = "master" }; f.serverConnection = root.ConnectionString;
                await f.ExecuteAsync(f.serverConnection, $"CREATE DATABASE [{f.vaultName}]");
                await f.ExecuteAsync(f.serverConnection, $"CREATE DATABASE [{f.auditName}]");
                await f.ExecuteAsync(f.serverConnection, $"CREATE LOGIN [{f.writerName}] WITH PASSWORD='{password}', CHECK_POLICY=OFF; CREATE LOGIN [{f.readerName}] WITH PASSWORD='{password}', CHECK_POLICY=OFF;");
                root.InitialCatalog = f.vaultName; f.vaultConnection = root.ConnectionString;
                root.InitialCatalog = f.auditName; f.auditAdminConnection = root.ConnectionString;
                await f.ExecuteAsync(f.auditAdminConnection, $"CREATE USER [{f.writerName}] FOR LOGIN [{f.writerName}]; CREATE USER [{f.readerName}] FOR LOGIN [{f.readerName}];");
                var script = await File.ReadAllTextAsync(ScriptPath("audit-sqlserver.sql"));
                foreach (var batch in System.Text.RegularExpressions.Regex.Split(script, @"^GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline))
                    if (!string.IsNullOrWhiteSpace(batch)) await f.ExecuteAsync(f.auditAdminConnection, batch);
                await f.ExecuteAsync(f.auditAdminConnection, $"ALTER ROLE valvra_audit_writer ADD MEMBER [{f.writerName}]; ALTER ROLE valvra_audit_reader ADD MEMBER [{f.readerName}];");
                root.IntegratedSecurity = false; root.UserID = f.writerName; root.Password = password; var writer = root.ConnectionString;
                root.UserID = f.readerName;
                f.AuditOptions = new() { Provider = provider, WriterConnectionString = writer, ReaderConnectionString = root.ConnectionString };
            }
            else
            {
                var root = new NpgsqlConnectionStringBuilder(f.serverConnection) { Database = "postgres", Pooling = false }; f.serverConnection = root.ConnectionString;
                await f.ExecuteAsync(f.serverConnection, $"CREATE DATABASE {f.vaultName}"); await f.ExecuteAsync(f.serverConnection, $"CREATE DATABASE {f.auditName}");
                await f.ExecuteAsync(f.serverConnection, $"CREATE ROLE {f.writerName} LOGIN PASSWORD '{password}'; CREATE ROLE {f.readerName} LOGIN PASSWORD '{password}';");
                root.Database = f.vaultName; f.vaultConnection = root.ConnectionString; root.Database = f.auditName; f.auditAdminConnection = root.ConnectionString;
                var script = (await File.ReadAllTextAsync(ScriptPath("audit-postgresql.sql"))).Replace("valvra_audit_writer", f.writerRole, StringComparison.Ordinal).Replace("valvra_audit_reader", f.readerRole, StringComparison.Ordinal);
                await f.ExecuteAsync(f.auditAdminConnection, script);
                await f.ExecuteAsync(f.auditAdminConnection, $"GRANT {f.writerRole} TO {f.writerName}; GRANT {f.readerRole} TO {f.readerName};");
                root.Username = f.writerName; root.Password = password; var writer = root.ConnectionString; root.Username = f.readerName;
                f.AuditOptions = new() { Provider = provider, WriterConnectionString = writer, ReaderConnectionString = root.ConnectionString };
            }
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }

    public VaultDbContext Db()
    {
        var options = new DbContextOptionsBuilder<VaultDbContext>();
        if (provider == "SqlServer") options.UseSqlServer(vaultConnection, x => x.MigrationsAssembly("Valvra.Migrations.SqlServer"));
        else options.UseNpgsql(vaultConnection, x => x.MigrationsAssembly("Valvra.Migrations.PostgreSql"));
        return new(options.Options);
    }
    public async Task MustDenyAsync(string connection, string sql) =>
        await Assert.ThrowsAnyAsync<DbException>(() => ExecuteAsync(connection, sql));
    private DbConnection Open(string connection) => provider == "SqlServer" ? new SqlConnection(connection) : new NpgsqlConnection(connection);
    private async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = Open(connectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandTimeout = 60; command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static string ScriptPath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        { var path = Path.Combine(directory.FullName, "deploy", name); if (File.Exists(path)) return path; directory = directory.Parent; }
        throw new FileNotFoundException("Audit schema script missing.");
    }
    public async ValueTask DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(serverConnection)) return;
        // Only this fixture's random, explicitly prefixed databases/logins may be removed.
        if (!new[] { vaultName, auditName, writerName, readerName }.All(x => x.StartsWith("valvra_test_", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(x, "^[a-z0-9_]+$"))) throw new InvalidOperationException("Unsafe test cleanup target.");
        SqlConnection.ClearAllPools(); NpgsqlConnection.ClearAllPools();
        if (provider == "SqlServer")
        {
            foreach (var db in new[] { vaultName, auditName })
                await ExecuteAsync(serverConnection, $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END");
            foreach (var login in new[] { writerName, readerName })
                await ExecuteAsync(serverConnection, $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name='{login}') DROP LOGIN [{login}]");
        }
        else
        {
            foreach (var db in new[] { vaultName, auditName }) await ExecuteAsync(serverConnection, $"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
            foreach (var role in new[] { writerName, readerName, writerRole, readerRole }) await ExecuteAsync(serverConnection, $"DROP ROLE IF EXISTS {role}");
        }
    }
}
