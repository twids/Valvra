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
using Valvra.Infrastructure.Security;
using Valvra.Web.Setup;
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
        using var integritySigner = new TestIntegritySigner();
        var integrity = new VaultIntegrity(db, new MemoryCheckpointStore(), integritySigner, new IntegrityOptions { InstallationId = f.AuditOptions.InstallationId });
        await integrity.InitializeEmptyAsync(default);
        var audit = new AuditService(db, transport, TimeProvider.System, signer, integrity);
        using var cipher = new SpyCipher();
        var vault = new VaultService(new VaultOperations(db, new AccessService(db, TimeProvider.System), audit, cipher, TimeProvider.System, new FakeDirectory(), integrity), integrity);
        var actor = new Actor("ad", "user", "User", new HashSet<string>(), true, true, true);
        var group = await vault.CreateGroupAsync(actor, "Root", null, "database-test", default);
        var resource = await vault.CreateResourceAsync(actor, group, "Server", "database-test", default);
        await vault.GrantAsync(actor, new AccessGrant { TargetKind = TargetKind.Resource, TargetId = resource, Provider = "ad", SubjectKind = SubjectKind.User,
            SubjectId = "user", Permissions = VaultPermission.Metadata | VaultPermission.ReadSecret | VaultPermission.Modify }, "database-test", default);
        var secret = await vault.CreateSecretAsync(actor, resource, "Account", new("u", "secret", "note"), null, "database-test", default);
        Assert.Equal("secret", (await vault.RevealSecretAsync(actor, secret, null, "Secret.Reveal", "database-test", default)).Password);
        var events = await new AuditReader(f.AuditOptions, signer).ReadAsync(actor, new(null, null, null, null, null), default);
        Assert.NotEmpty(events); Assert.All(events, x => Assert.True(x.SignatureValid));
        var value = new AuditEvent(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "ad", "user", "Duplicate.Test", resource, AuditPhase.Event, "Success", "database-test", InstallationId: f.AuditOptions.InstallationId);
        var signed = signer.Sign(value);
        Assert.Equal(await transport.SendAsync(signed, default), await transport.SendAsync(signed, default));
        events = await new AuditReader(f.AuditOptions, signer).ReadAsync(actor, new(null, null, null, "Duplicate.Test", null), default);
        Assert.Single(events);
        await f.MustDenyAsync(f.AuditOptions.WriterConnectionString, provider == "SqlServer" ? "SELECT * FROM dbo.AuditEvents" : "SELECT * FROM audit_events");
        await f.MustDenyAsync(f.AuditOptions.WriterConnectionString, provider == "SqlServer" ? "UPDATE dbo.AuditEvents SET Action='x' WHERE 1=0" : "UPDATE audit_events SET action='x' WHERE false");
        await f.MustDenyAsync(f.AuditOptions.WriterConnectionString, provider == "SqlServer" ? "DELETE FROM dbo.AuditEvents WHERE 1=0" : "DELETE FROM audit_events WHERE false");
        await f.MustDenyAsync(f.AuditOptions.ReaderConnectionString, provider == "SqlServer" ? "DELETE FROM dbo.AuditEvents WHERE 1=0" : "DELETE FROM audit_events WHERE false");
        var version = await db.SecretVersions.SingleAsync(); Assert.DoesNotContain("secret", version.EnvelopeJson);
        var license = await vault.SaveLicenseAsync(actor, null, resource, "Product", "Vendor", "Reference", 2, null, new("license-secret", "note"), 0, "database-test", default);
        await vault.SaveLicenseAsync(actor, license, resource, "Renamed", "Vendor", "Reference", 2, null, null, 1, "database-test", default, SecretChange.Preserve);
        await vault.SaveLicenseAsync(actor, license, resource, "Renamed", "Vendor", "Reference", 2, null, new("second-key", "note"), 2, "database-test", default, SecretChange.Replace);
        Assert.Equal("license-secret", (await vault.RevealLicenseAsync(actor, license, "database-test", default, 1)).LicenseKey);
        await vault.RestoreLicenseVersionAsync(actor, license, 1, 3, "database-test", default);
        Assert.Equal("license-secret", (await vault.RevealLicenseAsync(actor, license, "database-test", default)).LicenseKey);
        Assert.Equal(3, (await vault.LicenseVersionsAsync(actor, license, default)).Count);
        var other = await vault.CreateResourceAsync(actor, group, "Other resource", "database-test", default);
        var auditReader = new AuditReader(f.AuditOptions, signer);
        var scoped = await auditReader.ReadAsync(actor, new(null, null, null, "Secret.Create", null, ResourceId: resource, GroupId: group), default);
        Assert.Equal(2, scoped.Count); Assert.All(scoped, x => Assert.True(x.SignatureValid));
        Assert.Empty(await auditReader.ReadAsync(actor, new(null, null, null, "Secret.Create", null, ResourceId: other), default));
        var targets = await auditReader.ReadTargetsAsync(actor, default);
        Assert.Contains(targets.Resources, x => x.Id == resource); Assert.Contains(targets.Groups, x => x.Id == group);
        var subgroup = await vault.CreateGroupAsync(actor, "Subgroup", group, "database-test", default);
        var nested = await vault.CreateResourceAsync(actor, subgroup, "Nested server", "database-test", default);
        Assert.Equal(2, (await auditReader.ReadAsync(actor, new(null, null, null, "Resource.Create", nested, GroupId: group), default)).Count);
        Assert.Empty(await auditReader.ReadAsync(actor, new(null, null, null, "Resource.Create", nested, GroupId: group, IncludeSubgroups: false), default));
        await vault.MoveAsync(actor, TargetKind.Resource, nested, group, 1, "database-test", default);
        var oldLocation = await auditReader.ReadAsync(actor, new(null, null, null, "Resource.Create", nested, GroupId: subgroup, IncludeSubgroups: false), default);
        Assert.Equal(2, oldLocation.Count);
        var move = await auditReader.ReadAsync(actor, new(null, null, null, "Resource.Move", nested, GroupId: group, IncludeSubgroups: false), default);
        Assert.Single(move); Assert.Equal(AuditPhase.Committed, move[0].Event.Phase);
        await Assert.ThrowsAsync<AccessDeniedException>(() => auditReader.ReadTargetsAsync(actor with { IsAuditor = false }, default));
        // Corrupt database data must not hide valid targets or crash either provider.
        var historicalResource = Guid.NewGuid();
        var historic = value with { Id = Guid.NewGuid(), TargetId = historicalResource, Format = 3,
            Timestamp = DateTimeOffset.UtcNow, Scope = new(historicalResource, "Old signed name", group, [new(group, "Root")]) };
        var renamed = historic with { Id = Guid.NewGuid(), Timestamp = historic.Timestamp.AddMinutes(1),
            Scope = historic.Scope! with { ResourceName = "Latest signed name" } };
        await f.InsertAuditRowAsync(signer.Sign(historic), projectedTime: historic.Timestamp.AddYears(10));
        var latestSigned = signer.Sign(renamed);
        await f.InsertAuditRowAsync(latestSigned, projectedTime: historic.Timestamp.AddYears(-10));
        await f.InsertAuditRowAsync(latestSigned with { Event = renamed with { Id = Guid.NewGuid() } },
            json: System.Text.Encoding.UTF8.GetString(latestSigned.Payload).Replace("Duplicate.Test", "Forged.Test", StringComparison.Ordinal),
            projectedTime: historic.Timestamp.AddYears(20));
        await f.InsertAuditRowAsync(signer.Sign(historic with { Id = Guid.NewGuid() }), json: "{");
        await f.InsertAuditRowAsync(signer.Sign(historic with { Id = Guid.NewGuid() }), json: "{\"Format\":3,\"Scope\":{\"GroupPath\":null}}");
        targets = await auditReader.ReadTargetsAsync(actor, default);
        Assert.Equal("Latest signed name", Assert.Single(targets.Resources, x => x.Id == historicalResource).Name);
        Assert.Equal(3, targets.InvalidEventCount);
        var corruptPage = await auditReader.ReadAsync(actor, new(null, null, null, null, null, GroupId: group), default);
        Assert.Equal(3, corruptPage.Count(x => !x.SignatureValid));
        Assert.All(corruptPage.Where(x => !x.SignatureValid), x => Assert.Null(x.Event.Scope));
        // Switch to the shared SELECT+INSERT runtime role and verify both adapters
        // plus real permission enforcement, including administrative deletion paths.
        await f.UseSharedAuditIdentityAsync();
        await SetupValidator.CheckPermissionsAsync(f.AuditOptions, true, default);
        await SetupValidator.CheckPermissionsAsync(f.AuditOptions, false, default);
        await transport.SendAsync(signer.Sign(value with { Id = Guid.NewGuid() }), default);
        Assert.NotEmpty(await new AuditReader(f.AuditOptions, signer).ReadAsync(actor, new(null, null, null, null, null), default));
        var table = provider == "SqlServer" ? "dbo.AuditEvents" : "public.audit_events";
        var actionColumn = provider == "SqlServer" ? "Action" : "action";
        foreach (var sql in new[] { $"UPDATE {table} SET {actionColumn}='Forged'", $"DELETE FROM {table}", $"TRUNCATE TABLE {table}", $"DROP TABLE {table}" })
            await f.MustDenyAsync(f.AuditOptions.WriterConnectionString, sql);
        // Execute real SQL updates outside the trusted application boundary on both providers.
        var secretSql = provider == "SqlServer" ? "UPDATE dbo.Secrets SET ResourceId={0} WHERE Id={1}" : "UPDATE \"Secrets\" SET \"ResourceId\"={0} WHERE \"Id\"={1}";
        var licenseSql = provider == "SqlServer" ? "UPDATE dbo.Licenses SET ResourceId={0} WHERE Id={1}" : "UPDATE \"Licenses\" SET \"ResourceId\"={0} WHERE \"Id\"={1}";
        foreach (var (sql, id) in new[] { (secretSql, secret), (licenseSql, license) })
        {
            await db.Database.ExecuteSqlRawAsync(sql, other, id);
            var before = cipher.Decryptions;
            await Assert.ThrowsAsync<VaultUnavailableException>(() => vault.RevealSecretAsync(actor, secret, null, "Secret.Reveal", "database-test", default));
            await Assert.ThrowsAsync<VaultUnavailableException>(() => vault.RevealLicenseAsync(actor, license, "database-test", default));
            Assert.Equal(before, cipher.Decryptions);
            await db.Database.ExecuteSqlRawAsync(sql, resource, id);
        }
        await audit.FlushAsync(default);
        await db.Database.ExecuteSqlRawAsync(provider == "SqlServer" ? "UPDATE dbo.Audit SET Action='Forged'" : "UPDATE \"Audit\" SET \"Action\"='Forged'");
        var signatureCount = signer.Signatures;
        await Assert.ThrowsAsync<VaultUnavailableException>(() => audit.FlushAsync(default));
        Assert.Equal(signatureCount, signer.Signatures);
    }
}

internal sealed class TestAuditSigner : IAuditSigner, IDisposable
{
    private readonly RSA rsa = RSA.Create(3072);
    public int Signatures { get; private set; }
    public SignedAuditEvent Sign(AuditEvent value) { Signatures++; return AuditSignature.Sign(value, "test-key", rsa); }
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
    private string runtimeRole = "";
    public AuditDatabaseOptions AuditOptions { get; private set; } = new();

    public static async Task<DatabaseFixture> CreateAsync(string provider)
    {
        var f = new DatabaseFixture { provider = provider };
        f.serverConnection = Environment.GetEnvironmentVariable(provider == "SqlServer" ? "VALVRA_TEST_SQLSERVER" : "VALVRA_TEST_POSTGRESQL")!;
        var suffix = Guid.NewGuid().ToString("N");
        f.vaultName = "valvra_test_v_" + suffix; f.auditName = "valvra_test_a_" + suffix;
        f.writerName = "valvra_test_w_" + suffix; f.readerName = "valvra_test_r_" + suffix;
        f.writerRole = "valvra_test_wrole_" + suffix; f.readerRole = "valvra_test_rrole_" + suffix;
        f.runtimeRole = "valvra_test_arole_" + suffix;
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
                f.AuditOptions = new() { InstallationId = Guid.NewGuid(), Provider = provider, WriterConnectionString = writer, ReaderConnectionString = root.ConnectionString };
            }
            else
            {
                var root = new NpgsqlConnectionStringBuilder(f.serverConnection) { Database = "postgres", Pooling = false }; f.serverConnection = root.ConnectionString;
                await f.ExecuteAsync(f.serverConnection, $"CREATE DATABASE {f.vaultName}"); await f.ExecuteAsync(f.serverConnection, $"CREATE DATABASE {f.auditName}");
                await f.ExecuteAsync(f.serverConnection, $"CREATE ROLE {f.writerName} LOGIN PASSWORD '{password}'; CREATE ROLE {f.readerName} LOGIN PASSWORD '{password}';");
                root.Database = f.vaultName; f.vaultConnection = root.ConnectionString; root.Database = f.auditName; f.auditAdminConnection = root.ConnectionString;
                var script = (await File.ReadAllTextAsync(ScriptPath("audit-postgresql.sql"))).Replace("valvra_audit_writer", f.writerRole, StringComparison.Ordinal).Replace("valvra_audit_reader", f.readerRole, StringComparison.Ordinal).Replace("valvra_audit_runtime", f.runtimeRole, StringComparison.Ordinal);
                await f.ExecuteAsync(f.auditAdminConnection, script);
                await f.ExecuteAsync(f.auditAdminConnection, $"GRANT {f.writerRole} TO {f.writerName}; GRANT {f.readerRole} TO {f.readerName};");
                root.Username = f.writerName; root.Password = password; var writer = root.ConnectionString; root.Username = f.readerName;
                f.AuditOptions = new() { InstallationId = Guid.NewGuid(), Provider = provider, WriterConnectionString = writer, ReaderConnectionString = root.ConnectionString };
            }
            return f;
        }
        catch (Exception setupError)
        {
            try { await f.DisposeAsync(); }
            catch (Exception cleanupError) { throw new AggregateException("Disposable database setup and cleanup failed.", setupError, cleanupError); }
            throw;
        }
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
    public async Task InsertAuditRowAsync(SignedAuditEvent signed, string? json = null, DateTimeOffset? projectedTime = null)
    {
        await using var connection = Open(AuditOptions.WriterConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = provider == "SqlServer"
            ? "INSERT INTO dbo.AuditEvents (EventId,PayloadHash,Timestamp,ActorId,Action,TargetId,EventJson,SigningKeyId,Signature) VALUES (@id,@hash,@time,@actor,@action,@target,@json,@key,@signature)"
            : "INSERT INTO public.audit_events (event_id,payload_hash,timestamp,actor_id,action,target_id,event_json,signing_key_id,signature) VALUES (@id,@hash,@time,@actor,@action,@target,@json,@key,@signature)";
        AuditConnections.Parameter(command, "id", signed.Event.Id); AuditConnections.Parameter(command, "hash", signed.PayloadHash);
        AuditConnections.Parameter(command, "time", (projectedTime ?? signed.Event.Timestamp).UtcDateTime);
        AuditConnections.Parameter(command, "actor", signed.Event.ActorId); AuditConnections.Parameter(command, "action", signed.Event.Action);
        AuditConnections.Parameter(command, "target", signed.Event.TargetId);
        AuditConnections.Parameter(command, "json", json ?? System.Text.Encoding.UTF8.GetString(signed.Payload));
        AuditConnections.Parameter(command, "key", signed.SigningKeyId); AuditConnections.Parameter(command, "signature", signed.Signature);
        await command.ExecuteNonQueryAsync();
    }
    public async Task UseSharedAuditIdentityAsync()
    {
        await ExecuteAsync(auditAdminConnection, provider == "SqlServer"
            ? $"ALTER ROLE valvra_audit_writer DROP MEMBER [{writerName}]; ALTER ROLE valvra_audit_runtime ADD MEMBER [{writerName}];"
            : $"REVOKE {writerRole} FROM {writerName}; GRANT {runtimeRole} TO {writerName};");
        AuditOptions.ReaderConnectionString = AuditOptions.WriterConnectionString;
    }
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
            foreach (var role in new[] { writerName, readerName, writerRole, readerRole, runtimeRole }) await ExecuteAsync(serverConnection, $"DROP ROLE IF EXISTS {role}");
        }
    }
}
