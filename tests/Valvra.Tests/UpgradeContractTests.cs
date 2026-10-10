using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Security;
using Valvra.Infrastructure.Services;
using Xunit;

namespace Valvra.Tests;

// Frozen persisted-format vectors: an SDK/EF/JSON upgrade must not silently invalidate existing vaults or audit.
public sealed class UpgradeContractTests
{
    private static readonly Guid Installation = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id = Guid.Parse("22222222-2222-2222-2222-222222222222");
    [Theory]
    [InlineData("sv-SE")]
    [InlineData("ar-SA")]
    public void FrozenVersionTwoEnvelopeRemainsDecryptableAcrossRuntimeUpgrades(string culture)
    {
        // Fixed public test key/nonce and ciphertext, generated with raw AES-GCM
        // outside EnvelopeCipher. Never regenerate this vector during an upgrade.
        const string envelope = """
            {"Format":2,"InstallationId":"11111111-1111-1111-1111-111111111111","ResourceId":"44444444-4444-4444-4444-444444444444","KeyId":"frozen-test-key","WrappedKey":"AQID","Nonce":"AAECAwQFBgcICQoL","Ciphertext":"PCCDaKCXrHrgJLWxk40dAOz78keVCX1QGjeE9m4eb8BlMpTeyahq7AHWGsD45ltLmTYS6Xj6gZRQ409qOtnXiJlEsg+htAsPcyDPTJI=","Tag":"kjpmKXeiXrmNpXuy+uBibg=="}
            """;
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var cipher = new EnvelopeCipher(new FrozenTestProtector());
            Assert.Equal(new SecretPayload("demo-user", "fixture-password", "fixture-note"),
                cipher.Decrypt<SecretPayload>(envelope, Id, 7, "secret", Guid.Parse("44444444-4444-4444-4444-444444444444"), Installation));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private sealed class FrozenTestProtector : IKeyProtector
    {
        public string ActiveKeyId => "frozen-test-key";
        public byte[] Wrap(ReadOnlySpan<byte> key) => throw new InvalidOperationException("The frozen vector only supports reads.");
        public byte[] Unwrap(string keyId, ReadOnlySpan<byte> wrappedKey)
        {
            if (keyId != ActiveKeyId || !wrappedKey.SequenceEqual(new byte[] { 1, 2, 3 })) throw new CryptographicException();
            return Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        }
    }
    [Theory]
    [InlineData("sv-SE")]
    [InlineData("ar-SA")]
    public void PersistedIntegrityJsonAndHashHaveFrozenCanonicalBytes(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var group = new ResourceGroup { Id = Id, Name = "Root", Revision = 1 };
            var bytes = VaultIntegrity.Serialize(group);
            Assert.Equal("{\"Id\":\"22222222-2222-2222-2222-222222222222\",\"ParentId\":null,\"Name\":\"Root\",\"Revision\":1}", Encoding.UTF8.GetString(bytes));
            var rows = new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { ["group/22222222-2222-2222-2222-222222222222"] = bytes };
            Assert.Equal("ed8173be6b64ac6f3b75929507c4cdfc2c42a01517287a7337a687e474ea94a4", VaultIntegrity.Hash(rows, Installation));
            var utc = new SecretVersion { EntryId = Id, Version = 1, CreatedAt = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(2)) };
            Assert.Equal(VaultIntegrity.Serialize(utc), VaultIntegrity.Serialize(new SecretVersion { EntryId = Id, Version = 1, CreatedAt = utc.CreatedAt.ToUniversalTime() }));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void PersistedAuditJsonHasFrozenHashAndUtcRepresentation()
    {
        var value = new AuditEvent(Id, Guid.Parse("33333333-3333-3333-3333-333333333333"), new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(2)),
            "ad", "user", "Secret.Reveal", null, AuditPhase.Event, "ReleaseAuthorized", "request", InstallationId: Installation);
        Assert.Equal("02ee6f75d48589a42e0c8075fc15dc2e751783b1833625b650d63f49117a9818", AuditSignature.Hash(value));
        Assert.Contains("2026-10-05T10:00:00.0000000", Encoding.UTF8.GetString(AuditSignature.Serialize(value)));
        Assert.Equal(AuditSignature.Serialize(value), AuditSignature.Serialize(value with { Timestamp = value.Timestamp.ToUniversalTime() }));
    }

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("PostgreSql")]
    public void ProviderMigrationsMatchCurrentModelAndIncludeProtectedHistory(string provider)
    {
        var options = new DbContextOptionsBuilder<VaultDbContext>();
        if (provider == "SqlServer") options.UseSqlServer("Server=localhost;Database=unused", x => x.MigrationsAssembly("Valvra.Migrations.SqlServer"));
        else options.UseNpgsql("Host=localhost;Database=unused", x => x.MigrationsAssembly("Valvra.Migrations.PostgreSql"));
        using var db = new VaultDbContext(options.Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var expected = new[] { nameof(AccessGrant), nameof(AuditRecord), nameof(LicenseAssignment), nameof(LicenseVersion), nameof(ResourceGroup), nameof(ResourceOwner), nameof(SecretEntry), nameof(SecretVersion), nameof(SoftwareLicense), nameof(VaultResource) };
        Assert.Equal(expected.Order(), db.Model.GetEntityTypes().Select(x => x.ClrType.Name).Order());
    }

    [Fact]
    public void EveryPublicVaultOperationHasAProtectedFacade()
    {
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
        var operations = typeof(VaultOperations).GetMethods(flags);
        var protectedOperations = typeof(VaultService).GetMethods(flags);
        Assert.Equal(operations.Length, protectedOperations.Length);
        foreach (var method in operations)
            Assert.Contains(protectedOperations, x => x.Name == method.Name && x.ReturnType == method.ReturnType
                && x.GetParameters().Select(p => p.ParameterType).SequenceEqual(method.GetParameters().Select(p => p.ParameterType)));
    }
}
