using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Valvra.Infrastructure.Data;

public sealed class VaultDesignFactory : IDesignTimeDbContextFactory<VaultDbContext>
{
    public VaultDbContext CreateDbContext(string[] args)
    {
        var provider = Environment.GetEnvironmentVariable("VALVRA_DB_PROVIDER") ?? "SqlServer";
        var connection = Environment.GetEnvironmentVariable("VALVRA_MIGRATION_CONNECTION");
        var builder = new DbContextOptionsBuilder<VaultDbContext>();
        switch (provider)
        {
            case "SqlServer": builder.UseSqlServer(connection ?? "Server=localhost;Database=Valvra;Integrated Security=true;Encrypt=true;TrustServerCertificate=false",
                sql => sql.MigrationsAssembly("Valvra.Migrations.SqlServer")); break;
            case "PostgreSql": builder.UseNpgsql(connection ?? "Host=localhost;Database=valvra;Username=valvra;SSL Mode=VerifyFull",
                sql => sql.MigrationsAssembly("Valvra.Migrations.PostgreSql")); break;
            default: throw new InvalidOperationException("Set VALVRA_DB_PROVIDER to SqlServer or PostgreSql.");
        }
        return new VaultDbContext(builder.Options);
    }
}
