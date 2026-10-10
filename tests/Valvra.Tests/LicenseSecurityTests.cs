using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Xunit;

namespace Valvra.Tests;

public sealed class LicenseSecurityTests
{
    [Fact]
    public async Task MetadataUpdatePreservesCiphertextAndRequiresNoSecretRead()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var id = await Create(f);
        var original = (await f.Db.Licenses.SingleAsync()).EnvelopeJson;
        await SetPermissions(f, VaultPermission.Metadata | VaultPermission.Modify);
        await f.Vault.SaveLicenseAsync(f.User, id, f.ResourceId, "New product", "New vendor", "New ref", 3, null, null, 1, "test", default, SecretChange.Preserve);
        var stored = await f.Db.Licenses.SingleAsync();
        Assert.Equal(original, stored.EnvelopeJson); Assert.Equal(1, stored.SecretVersion); Assert.Equal(2, stored.Revision);
        Assert.Single(await f.Db.LicenseVersions.ToListAsync()); Assert.Equal(0, f.Cipher.Decryptions);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.RevealLicenseAsync(f.User, id, "test", default));
    }

    [Theory]
    [InlineData(SecretChange.Replace, true)]
    [InlineData(SecretChange.Preserve, false)]
    [InlineData(SecretChange.Clear, false)]
    [InlineData((SecretChange)99, false)]
    public async Task AmbiguousOrInvalidSecretUpdatesCannotEraseStoredKey(SecretChange change, bool blankPayload)
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); var id = await Create(f);
        LicensePayload? payload = blankPayload ? new(" ", "") : change == SecretChange.Preserve ? new("unwanted", "") : change == SecretChange.Clear ? new("unwanted", "") : null;
        await Assert.ThrowsAsync<VaultValidationException>(() => f.Vault.SaveLicenseAsync(f.User, id, f.ResourceId, "P", "V", "R", 2, null, payload, 1, "test", default, change));
        Assert.Equal("original-key", (await f.Vault.RevealLicenseAsync(f.User, id, "test", default)).LicenseKey);
        Assert.Single(await f.Db.LicenseVersions.ToListAsync());
    }

    [Fact]
    public async Task ReplaceClearAndRestoreCreateIndependentEncryptedVersions()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); var id = await Create(f);
        await f.Vault.SaveLicenseAsync(f.User, id, f.ResourceId, "Product", "Vendor", "R", 2, null, new("replacement-key", "replacement-note"), 1, "test", default, SecretChange.Replace);
        await f.Vault.SaveLicenseAsync(f.User, id, f.ResourceId, "Product", "Vendor", "R", 2, null, null, 2, "test", default, SecretChange.Clear);
        Assert.Equal("", (await f.Vault.RevealLicenseAsync(f.User, id, "test", default)).LicenseKey);
        Assert.Equal("original-key", (await f.Vault.RevealLicenseAsync(f.User, id, "test", default, 1)).LicenseKey);
        await f.Vault.RestoreLicenseVersionAsync(f.User, id, 2, 3, "test", default);
        Assert.Equal("replacement-key", (await f.Vault.RevealLicenseAsync(f.User, id, "test", default)).LicenseKey);
        Assert.Equal(4, (await f.Vault.LicenseVersionsAsync(f.User, id, default)).Count);
        var versions = await f.Db.LicenseVersions.ToListAsync();
        Assert.Equal(4, versions.Select(x => x.EnvelopeJson).Distinct().Count());
        Assert.All(versions, x => { Assert.DoesNotContain("replacement", x.EnvelopeJson); Assert.DoesNotContain("original", x.EnvelopeJson); });
        Assert.Contains(f.Transport.Events, x => x.Action == "License.Reveal" && x.Outcome == "ReleaseAuthorized");
    }

    [Theory]
    [InlineData(VaultPermission.Metadata)]
    [InlineData(VaultPermission.Metadata | VaultPermission.Modify)]
    [InlineData(VaultPermission.Metadata | VaultPermission.ReadSecret)]
    public async Task RestoreRequiresBothReadAndModify(VaultPermission permission)
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); var id = await Create(f);
        await SetPermissions(f, permission);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.RestoreLicenseVersionAsync(f.User, id, 1, 1, "test", default));
        Assert.Single(await f.Db.LicenseVersions.ToListAsync()); Assert.Equal(0, f.Cipher.Decryptions);
    }

    [Fact]
    public async Task ExpiredReadAccessBlocksHistoricalLicenseReleaseAfterAuditDelay()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); var id = await Create(f);
        await f.Integrity.RunAsync(async () => { (await f.Db.Grants.SingleAsync()).ExpiresAt = f.Clock.GetUtcNow().AddSeconds(30); await f.Integrity.SaveAsync(default); }, default);
        f.Transport.AfterSend = () => f.Clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.RevealLicenseAsync(f.User, id, "test", default, 1));
        Assert.Equal(0, f.Cipher.Decryptions);
    }

    [Fact]
    public async Task AuditFailureBlocksCurrentAndHistoricalLicenseReleaseAndRestore()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); var id = await Create(f);
        f.Transport.Fail = true;
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Vault.RevealLicenseAsync(f.User, id, "test", default));
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Vault.RevealLicenseAsync(f.User, id, "test", default, 1));
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Vault.RestoreLicenseVersionAsync(f.User, id, 1, 1, "test", default));
        Assert.Equal(0, f.Cipher.Decryptions); Assert.Single(await f.Db.LicenseVersions.ToListAsync());
    }

    [Fact]
    public async Task DeletedLicenseAndStaleRevisionCannotOverwriteOrRevealHistory()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); var id = await Create(f);
        await f.Vault.SaveLicenseAsync(f.User, id, f.ResourceId, "Changed", "V", "R", 2, null, null, 1, "test", default, SecretChange.Preserve);
        await Assert.ThrowsAsync<VaultConflictException>(() => f.Vault.RestoreLicenseVersionAsync(f.User, id, 1, 1, "test", default));
        await f.Vault.SetLicenseDeletedAsync(f.User, id, true, 2, "test", default);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.RevealLicenseAsync(f.User, id, "test", default, 1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.LicenseVersionsAsync(f.User, id, default));
    }

    private static Task<Guid> Create(VaultServiceTests.Fixture f) => f.Vault.SaveLicenseAsync(f.User, null, f.ResourceId, "Product", "Vendor", "R", 2, null, new("original-key", "original-note"), 0, "test", default);
    private static Task SetPermissions(VaultServiceTests.Fixture f, VaultPermission permissions) => f.Integrity.RunAsync(async () =>
    { (await f.Db.Grants.SingleAsync()).Permissions = permissions; await f.Integrity.SaveAsync(default); }, default);
}
