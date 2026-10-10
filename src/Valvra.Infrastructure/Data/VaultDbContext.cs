using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Valvra.Core;

namespace Valvra.Infrastructure.Data;

public sealed class VaultDbContext(DbContextOptions<VaultDbContext> options) : DbContext(options)
{
    public DbSet<ResourceGroup> Groups => Set<ResourceGroup>();
    public DbSet<VaultResource> Resources => Set<VaultResource>();
    public DbSet<AccessGrant> Grants => Set<AccessGrant>();
    public DbSet<ResourceOwner> Owners => Set<ResourceOwner>();
    public DbSet<SecretEntry> Secrets => Set<SecretEntry>();
    public DbSet<SecretVersion> SecretVersions => Set<SecretVersion>();
    public DbSet<SoftwareLicense> Licenses => Set<SoftwareLicense>();
    public DbSet<LicenseVersion> LicenseVersions => Set<LicenseVersion>();
    public DbSet<LicenseAssignment> Assignments => Set<LicenseAssignment>();
    public DbSet<AuditRecord> Audit => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ResourceGroup>().HasOne<ResourceGroup>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<VaultResource>().HasOne<ResourceGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SecretEntry>().HasOne<VaultResource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SecretVersion>().HasKey(x => new { x.EntryId, x.Version });
        model.Entity<SecretVersion>().Property(x => x.EnvelopeJson).IsConcurrencyToken();
        model.Entity<SecretVersion>().HasOne<SecretEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SoftwareLicense>().HasOne<VaultResource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<LicenseVersion>().HasKey(x => new { x.LicenseId, x.Version });
        model.Entity<LicenseVersion>().HasOne<SoftwareLicense>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<LicenseVersion>().Property(x => x.EnvelopeJson).IsConcurrencyToken();
        model.Entity<LicenseAssignment>().HasOne<SoftwareLicense>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<LicenseAssignment>().HasOne<VaultResource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AccessGrant>().HasIndex(x => new { x.TargetKind, x.TargetId });
        model.Entity<ResourceOwner>().HasIndex(x => new { x.TargetKind, x.TargetId });
        model.Entity<AuditRecord>().HasIndex(x => x.Timestamp);
        model.Entity<AuditRecord>().HasIndex(x => new { x.Delivered, x.Timestamp });
        model.Entity<AuditRecord>().HasIndex(x => x.OperationId);
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(string) && property.Name is not ("EnvelopeJson" or "EventJson")) property.SetMaxLength(property.Name == "DetailsJson" ? 4096 : 512);
                if (property.Name == "Revision") property.IsConcurrencyToken = true;
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks,
                        value => new DateTimeOffset(value, TimeSpan.Zero)));
            }
        }
    }
}
