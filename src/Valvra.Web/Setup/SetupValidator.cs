using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Configuration;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Identity;
using Valvra.Infrastructure.Security;

namespace Valvra.Web.Setup;

public sealed class DatabaseSetupInput
{
    public string Provider { get; set; } = "SqlServer";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "";
    public string Authentication { get; set; } = "Windows";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
public sealed class SetupRequest
{
    public string AllowedHosts { get; set; } = "";
    public DatabaseSetupInput Vault { get; set; } = new();
    public DatabaseSetupInput AuditWriter { get; set; } = new();
    public DatabaseSetupInput AuditReader { get; set; } = new();
    public WindowsConnectionCredentials? AuditReaderWindowsCredentials { get; set; }
    public KeyProtectionOptions KeyProtection { get; set; } = new();
    public ActiveDirectoryOptions ActiveDirectory { get; set; } = new();
    public LdapTestOptions LdapTests { get; set; } = new();
    public string AuditSigningThumbprint { get; set; } = "";
    public string[] AuditVerificationThumbprints { get; set; } = [];
    public bool ProvisionSchemas { get; set; }
    public DatabaseSetupInput? VaultProvisioning { get; set; }
    public DatabaseSetupInput? AuditProvisioning { get; set; }
}
public sealed record SetupCheck(string Name, bool Passed, string Message);
public sealed record SetupValidation(InstallationSettings Settings, IReadOnlyList<SetupCheck> Checks)
{
    public bool Passed => Checks.All(x => x.Passed);
}

public sealed class SetupValidator(IWebHostEnvironment environment)
{
    public async Task<SetupValidation> ValidateAsync(SetupRequest request, string currentSid, CancellationToken ct)
    {
        var settings = Build(request);
        var checks = new List<SetupCheck>();
        async Task Check(string name, Func<Task> check, string success, string failure)
        {
            try { await check(); checks.Add(new(name, true, success)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { checks.Add(new(name, false, failure)); }
        }
        await Check("Konfiguration", () =>
        {
            using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(settings));
            ValvraRegistration.ValidateProduction(new ConfigurationBuilder().AddJsonStream(json).Build());
            return Task.CompletedTask;
        }, "Databaser och nycklar är åtskilda; TLS krävs.", "Kontrollera att valv och audit är separata databaser, att auditläsaren har separat identitet och att certifikaten är olika.");
        if (!checks.All(x => x.Passed)) return new(settings, checks);
        await Check("Active Directory", async () =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var actor = await new ActiveDirectoryProvider(settings.ActiveDirectory).ResolveAsync(currentSid, ct);
            if (!actor.IsAccessAdministrator) throw new AccessDeniedException();
        }, "Ditt konto är aktivt och ingår i behörighetsadministratörsgruppen.", "Kontrollera LDAPS-certifikat, katalogläsrätt, SID och medlemskap i administratörsgruppen.");
        await Check("Krypteringscertifikat", () =>
        {
            var protector = new CertificateKeyProtector(settings.KeyProtection);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var unwrapped = protector.Unwrap(protector.ActiveKeyId, protector.Wrap(key));
                try { if (!CryptographicOperations.FixedTimeEquals(key, unwrapped)) throw new CryptographicException(); }
                finally { CryptographicOperations.ZeroMemory(unwrapped); }
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            return Task.CompletedTask;
        }, "Tjänsten kan kryptera och dekryptera datanycklar.", "Certifikatet måste finnas i LocalMachine/My, ha RSA-3072 eller större och rätt privatnyckelbehörighet.");
        await Check("Auditcertifikat", () =>
        {
            var signer = new CertificateAuditSigner(settings.AuditDatabase);
            var value = new AuditEvent(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "ad", currentSid,
                "Setup.CertificateCheck", null, AuditPhase.Event, "Success", "setup");
            if (!signer.Verify(signer.Sign(value))) throw new CryptographicException();
            return Task.CompletedTask;
        }, "Tjänsten kan signera och verifiera audithändelser.", "Kontrollera auditcertifikat och tjänstens privatnyckelåtkomst.");
        if (!checks.All(x => x.Passed)) return new(settings, checks);
        if (request.ProvisionSchemas)
        {
            await Check("Databasscheman", async () =>
            {
                if (request.VaultProvisioning is null || request.AuditProvisioning is null)
                    throw new VaultValidationException("Installationsanslutningar krävs.");
                var vaultProvision = Connection(request.VaultProvisioning);
                var auditProvision = Connection(request.AuditProvisioning);
                if (request.VaultProvisioning.Provider != request.Vault.Provider || request.VaultProvisioning.Server != request.Vault.Server
                    || request.VaultProvisioning.Database != request.Vault.Database
                    || request.AuditProvisioning.Provider != request.AuditWriter.Provider || request.AuditProvisioning.Server != request.AuditWriter.Server
                    || request.AuditProvisioning.Database != request.AuditWriter.Database)
                    throw new VaultValidationException("Installationsanslutningarna måste peka på samma databaser som runtime-anslutningarna.");
                await using var db = Db(request.Vault.Provider, vaultProvision);
                await db.Database.MigrateAsync(ct);
                await using var connection = Open(request.AuditWriter.Provider, auditProvision);
                await connection.OpenAsync(ct);
                await using var exists = connection.CreateCommand();
                exists.CommandText = request.AuditWriter.Provider == "SqlServer"
                    ? "SELECT CASE WHEN OBJECT_ID('dbo.AuditEvents', 'U') IS NULL THEN 0 ELSE 1 END"
                    : "SELECT CASE WHEN to_regclass('public.audit_events') IS NULL THEN 0 ELSE 1 END";
                if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) == 0)
                {
                    var path = Path.Combine(environment.ContentRootPath, "deploy", request.AuditWriter.Provider == "SqlServer" ? "audit-sqlserver.sql" : "audit-postgresql.sql");
                    var script = await File.ReadAllTextAsync(path, ct);
                    foreach (var batch in Regex.Split(script, @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(batch)) continue;
                        await using var command = connection.CreateCommand(); command.CommandTimeout = 60; command.CommandText = batch;
                        await command.ExecuteNonQueryAsync(ct);
                    }
                }
            }, "Valvschema migrerat och audittabell förberedd.", "Kontrollera installationskontonas rättigheter. Befintliga databaser kan ha uppdaterats; kontrollera dem innan nytt försök.");
            if (!checks.All(x => x.Passed)) return new(settings, checks);
        }
        await Check("Valvdatabas", async () =>
        {
            await using var db = Db(settings.VaultDatabase.Provider, settings.VaultDatabase.ConnectionString);
            if (!await db.Database.CanConnectAsync(ct) || (await db.Database.GetPendingMigrationsAsync(ct)).Any()) throw new InvalidOperationException();
            await db.Groups.Take(1).ToListAsync(ct);
        }, "Runtime-identiteten kan ansluta och valvschemat är aktuellt.", "Kontrollera anslutning, Windows/SQL-rättigheter och att schemat har installerats.");
        await Check("Audit writer: INSERT-only", async () =>
        {
            await CheckPermissionsAsync(settings.AuditDatabase, writer: true, ct);
            var signer = new CertificateAuditSigner(settings.AuditDatabase);
            await new DatabaseAuditTransport(settings.AuditDatabase, signer).SendAsync(new AuditEvent(Guid.NewGuid(), Guid.NewGuid(),
                DateTimeOffset.UtcNow, "ad", currentSid, "Setup.DatabaseCheck", null, AuditPhase.Event, "Success", "setup"), ct);
        }, "Auditskrivaren kan lägga till en signerad rad och saknar läs-, ändrings- och raderingsrätt.", "Kontrollera audittabell, INSERT-rätt, separata identiteter och att skrivaren inte är DBA/tabellägare.");
        await Check("Audit reader: SELECT-only", async () =>
        {
            await WindowsCredentialRunner.RunAsync(settings.AuditDatabase.ReaderWindowsCredentials, async () =>
            { await CheckPermissionsAsync(settings.AuditDatabase, writer: false, ct); return true; });
            await new AuditReader(settings.AuditDatabase, new CertificateAuditSigner(settings.AuditDatabase))
                .ReadAsync(new Actor("ad", currentSid, currentSid, new HashSet<string>(), true, true, true), new(null, null, null, null, null), ct);
        }, "Auditläsaren kan läsa och verifiera poster men saknar skriv- och raderingsrätt.", "Kontrollera läskontot, eventuell Windows-identitet och SELECT-only-rättigheter.");
        return new(settings, checks);
    }

    public static InstallationSettings Build(SetupRequest request)
    {
        if (request.AuditWriter.Provider != request.AuditReader.Provider) throw new VaultValidationException("Auditanslutningarna måste använda samma databasprovider.");
        return new InstallationSettings
        {
            AllowedHosts = request.AllowedHosts,
            VaultDatabase = new() { Provider = request.Vault.Provider, ConnectionString = Connection(request.Vault) },
            AuditDatabase = new() { Provider = request.AuditWriter.Provider, WriterConnectionString = Connection(request.AuditWriter),
                ReaderConnectionString = Connection(request.AuditReader), ReaderWindowsCredentials = request.AuditReaderWindowsCredentials,
                SigningCertificateThumbprint = request.AuditSigningThumbprint, VerificationCertificateThumbprints = request.AuditVerificationThumbprints },
            KeyProtection = request.KeyProtection, ActiveDirectory = request.ActiveDirectory, LdapTests = request.LdapTests
        };
    }

    public static string Connection(DatabaseSetupInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Server) || string.IsNullOrWhiteSpace(input.Database)
            || input.Server.Length > 256 || input.Database.Length > 128 || input.Username.Length > 256 || input.Password.Length > 1024
            || input.Authentication is not ("Windows" or "Password")) throw new VaultValidationException("Ogiltiga databasinställningar.");
        if (input.Provider == "SqlServer")
        {
            var sql = new SqlConnectionStringBuilder { DataSource = input.Server, InitialCatalog = input.Database,
                IntegratedSecurity = input.Authentication == "Windows", Encrypt = SqlConnectionEncryptOption.Mandatory, TrustServerCertificate = false, ConnectTimeout = 10 };
            if (!sql.IntegratedSecurity) { sql.UserID = input.Username; sql.Password = input.Password; }
            return sql.ConnectionString;
        }
        if (input.Provider == "PostgreSql")
        {
            if (input.Port is < 1 or > 65535) throw new VaultValidationException("Ogiltig databasport.");
            var pg = new NpgsqlConnectionStringBuilder { Host = input.Server, Port = input.Port, Database = input.Database,
                SslMode = SslMode.VerifyFull, Timeout = 10 };
            if (!string.IsNullOrWhiteSpace(input.Username)) pg.Username = input.Username;
            if (input.Authentication == "Password") pg.Password = input.Password;
            return pg.ConnectionString;
        }
        throw new VaultValidationException("Databasprovider saknar stöd.");
    }

    private static VaultDbContext Db(string provider, string connection)
    {
        var options = new DbContextOptionsBuilder<VaultDbContext>();
        if (provider == "SqlServer") options.UseSqlServer(connection, x => x.MigrationsAssembly("Valvra.Migrations.SqlServer"));
        else options.UseNpgsql(connection, x => x.MigrationsAssembly("Valvra.Migrations.PostgreSql"));
        return new(options.Options);
    }
    private static DbConnection Open(string provider, string connection) => provider == "SqlServer" ? new SqlConnection(connection) : new NpgsqlConnection(connection);

    private static async Task CheckPermissionsAsync(AuditDatabaseOptions options, bool writer, CancellationToken ct)
    {
        var connectionString = writer ? options.WriterConnectionString : options.ReaderConnectionString;
        if (!writer && !string.IsNullOrWhiteSpace(options.ReaderWindowsCredentials?.Username))
        {
            if (options.Provider == "SqlServer") connectionString = new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
            else connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        }
        await using var connection = Open(options.Provider, connectionString);
        await connection.OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = options.Provider == "SqlServer"
            ? "SELECT HAS_PERMS_BY_NAME('dbo.AuditEvents','OBJECT','INSERT'), HAS_PERMS_BY_NAME('dbo.AuditEvents','OBJECT','SELECT'), HAS_PERMS_BY_NAME('dbo.AuditEvents','OBJECT','UPDATE'), HAS_PERMS_BY_NAME('dbo.AuditEvents','OBJECT','DELETE'), HAS_PERMS_BY_NAME('dbo.AuditEvents','OBJECT','ALTER')"
            : "SELECT has_table_privilege(current_user,'public.audit_events','INSERT'), has_table_privilege(current_user,'public.audit_events','SELECT'), has_table_privilege(current_user,'public.audit_events','UPDATE'), has_table_privilege(current_user,'public.audit_events','DELETE'), has_table_privilege(current_user,'public.audit_events','TRUNCATE')";
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw new InvalidOperationException();
        var permissions = Enumerable.Range(0, 5).Select(i => !reader.IsDBNull(i) && Convert.ToBoolean(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (permissions[0] != writer || permissions[1] == writer || permissions.Skip(2).Any(x => x)) throw new InvalidOperationException("Audit identity has invalid privileges.");
    }
}
