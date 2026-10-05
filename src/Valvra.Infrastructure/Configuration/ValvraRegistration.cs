using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Authorization;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Identity;
using Valvra.Infrastructure.Security;
using Valvra.Infrastructure.Services;

namespace Valvra.Infrastructure.Configuration;

public sealed class VaultDatabaseOptions
{
    public string Provider { get; set; } = "SqlServer";
    public string ConnectionString { get; set; } = "";
}

public static class ValvraRegistration
{
    public static IServiceCollection AddValvra(this IServiceCollection services, IConfiguration configuration)
    {
        var database = configuration.GetSection("VaultDatabase").Get<VaultDatabaseOptions>() ?? new();
        var audit = configuration.GetSection("AuditDatabase").Get<AuditDatabaseOptions>() ?? new();
        var keys = configuration.GetSection("KeyProtection").Get<KeyProtectionOptions>() ?? new();
        var ad = configuration.GetSection("ActiveDirectory").Get<ActiveDirectoryOptions>() ?? new();
        var ldap = configuration.GetSection("LdapTests").Get<LdapTestOptions>() ?? new();
        services.AddSingleton(database); services.AddSingleton(audit); services.AddSingleton(keys);
        services.AddSingleton(ad); services.AddSingleton(ldap); services.AddSingleton(TimeProvider.System);
        services.AddDbContext<VaultDbContext>(options =>
        {
            switch (database.Provider)
            {
                case "SqlServer": options.UseSqlServer(database.ConnectionString, sql => sql.MigrationsAssembly("Valvra.Migrations.SqlServer")); break;
                case "PostgreSql": options.UseNpgsql(database.ConnectionString, sql => sql.MigrationsAssembly("Valvra.Migrations.PostgreSql")); break;
                default: throw new InvalidOperationException("Unsupported vault database provider.");
            }
        });
        services.AddSingleton<IIdentityProvider, WindowsIdentityProvider>();
        services.AddScoped<IDirectoryProvider>(_ => OperatingSystem.IsWindows()
            ? new ActiveDirectoryProvider(ad) : throw new PlatformNotSupportedException("AD implementation requires Windows."));
        services.AddScoped<ICredentialTester>(_ => OperatingSystem.IsWindows()
            ? new LdapCredentialTester(ldap) : throw new PlatformNotSupportedException("LDAP test implementation requires Windows."));
        services.AddSingleton<IKeyProtector, CertificateKeyProtector>();
        services.AddSingleton<ISecretCipher, EnvelopeCipher>();
        services.AddSingleton<IAuditSigner, CertificateAuditSigner>();
        services.AddScoped<IAuditTransport, DatabaseAuditTransport>();
        services.AddScoped<IAuditReader, AuditReader>(); services.AddScoped<AuditService>();
        services.AddScoped<AccessService>(); services.AddScoped<VaultService>();
        services.AddScoped<KeyRotationService>();
        services.AddSingleton<CredentialTestThrottle>(); services.AddScoped<CredentialTestService>();
        return services;
    }

    public static void ValidateProduction(IConfiguration configuration)
    {
        var vault = configuration.GetSection("VaultDatabase").Get<VaultDatabaseOptions>() ?? new();
        var audit = configuration.GetSection("AuditDatabase").Get<AuditDatabaseOptions>() ?? new();
        var ad = configuration.GetSection("ActiveDirectory").Get<ActiveDirectoryOptions>() ?? new();
        var keys = configuration.GetSection("KeyProtection").Get<KeyProtectionOptions>() ?? new();
        var vaultName = DatabaseName(vault.Provider, vault.ConnectionString);
        var auditName = DatabaseName(audit.Provider, audit.WriterConnectionString);
        var readName = DatabaseName(audit.Provider, audit.ReaderConnectionString);
        if (vaultName == auditName || auditName != readName
            || audit.WriterConnectionString == audit.ReaderConnectionString && string.IsNullOrWhiteSpace(audit.ReaderWindowsCredentials?.Username))
            throw new InvalidOperationException("Vault and audit databases must be separate; audit reader and writer must target the same audit database with separate identities.");
        if (string.IsNullOrWhiteSpace(ad.Server) || string.IsNullOrWhiteSpace(ad.BaseDn)
            || string.IsNullOrWhiteSpace(ad.AccessAdministratorGroupSid) || string.IsNullOrWhiteSpace(ad.AuditorGroupSid)
            || string.IsNullOrWhiteSpace(keys.ActiveThumbprint) || string.IsNullOrWhiteSpace(audit.SigningCertificateThumbprint)
            || keys.ActiveThumbprint.Replace(" ", "", StringComparison.Ordinal).Equals(audit.SigningCertificateThumbprint.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configure AD groups and separate encryption/audit signing certificates.");
        if (configuration["AllowedHosts"] is not { Length: > 0 } hosts || hosts.Contains('*'))
            throw new InvalidOperationException("Configure explicit AllowedHosts.");
    }

    private static string DatabaseName(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("Database connection is not configured.");
        if (provider == "SqlServer")
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.InitialCatalog) || builder.TrustServerCertificate
                || !(builder.Encrypt.Equals(SqlConnectionEncryptOption.Mandatory) || builder.Encrypt.Equals(SqlConnectionEncryptOption.Strict)))
                throw new InvalidOperationException("SQL Server requires an explicit database, encryption and trusted certificate validation.");
            return "sql|" + builder.DataSource.ToUpperInvariant() + "|" + builder.InitialCatalog.ToUpperInvariant();
        }
        if (provider == "PostgreSql")
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Database) || builder.SslMode != SslMode.VerifyFull)
                throw new InvalidOperationException("PostgreSQL requires an explicit database and SSL Mode=VerifyFull.");
            return "pg|" + builder.Host?.ToUpperInvariant() + "|" + builder.Port + "|" + builder.Database;
        }
        throw new InvalidOperationException("Unsupported database provider.");
    }
}
