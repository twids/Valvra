using Valvra.Core;
using Xunit;

namespace Valvra.Tests;

public sealed class ContentSearchTests
{
    [Fact]
    public async Task SearchSeparatesAccountAndLicenseAccessAndSkipsDeletedRecords()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var account = await f.Vault.CreateSecretAsync(f.User, f.ResourceId, "Office admin",
            new("private username", "private password", "private notes"), null, "request", default);
        await f.Vault.SaveLicenseAsync(f.User, null, f.ResourceId, "Office suite", "Vendor", "Reference",
            1, null, new("private key", "private notes"), 0, "request", default);
        var stranger = f.User with { SubjectId = "stranger", IsAccessAdministrator = false };
        var licenseAdmin = stranger with { IsLicenseAdministrator = true };

        Assert.Equal(2, (await f.Vault.SearchContentAsync(f.User, "  oFfiCe ", default)).Items.Count);
        Assert.Empty((await f.Vault.SearchContentAsync(stranger, "Office", default)).Items);
        Assert.Equal("license", Assert.Single((await f.Vault.SearchContentAsync(licenseAdmin, "Office", default)).Items).Kind);
        Assert.Empty((await f.Vault.SearchContentAsync(licenseAdmin with { IsEnabled = false }, "Office", default)).Items);
        Assert.Empty((await f.Vault.SearchContentAsync(f.User, "private", default)).Items);
        var myAccounts = await f.Vault.SearchContentAsync(f.User, "", default, accountsOnly: true);
        Assert.Equal(account, Assert.Single(myAccounts.Items).Id);
        Assert.Empty((await f.Vault.SearchContentAsync(f.User, "", default, accountsOnly: true, offset: 1)).Items);
        Assert.Empty((await f.Vault.SearchContentAsync(licenseAdmin, "", default, accountsOnly: true)).Items);
        Assert.Equal(0, f.Cipher.Decryptions);

        await f.Vault.SetDeletedAsync(f.User, account, true, 1, "request", default);
        Assert.Equal("license", Assert.Single((await f.Vault.SearchContentAsync(f.User, "Office", default)).Items).Kind);

        await f.Integrity.RunAsync(async () =>
        {
            f.Db.Exclusions.Add(new AccessExclusion { TargetKind = TargetKind.Group, TargetId = f.GroupId,
                Provider = licenseAdmin.Provider, SubjectId = licenseAdmin.SubjectId, Revision = 1 });
            await f.Integrity.SaveAsync(default);
        }, default);
        Assert.Empty((await f.Vault.SearchContentAsync(licenseAdmin, "Office", default)).Items);
    }
}
