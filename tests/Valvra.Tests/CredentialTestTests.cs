using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Authorization;
using Valvra.Infrastructure.Services;
using Xunit;

namespace Valvra.Tests;

public sealed class CredentialTestTests
{
    [Fact]
    public async Task ReadPermissionAloneCannotTriggerLdapBind()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var id = await f.Vault.CreateSecretAsync(f.User, f.ResourceId, "LDAP account", new("u", "p", ""), "approved", "test", default);
        var tester = new CountingTester(); var service = Service(f, tester);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.TestAsync(f.User, id, "test", default));
        Assert.Equal(0, tester.Calls); Assert.Equal(0, f.Cipher.Decryptions);
    }

    [Fact]
    public async Task OneBindIsAuditedAndRepeatedAttemptIsThrottled()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        await f.Integrity.RunAsync(async () => { var grant = await f.Db.Grants.SingleAsync(); grant.Permissions |= VaultPermission.TestCredential; await f.Integrity.SaveAsync(default); }, default);
        var id = await f.Vault.CreateSecretAsync(f.User, f.ResourceId, "LDAP account", new("u", "p", ""), "approved", "test", default);
        var tester = new CountingTester(); var service = Service(f, tester);
        Assert.Equal(CredentialTestResult.Rejected, await service.TestAsync(f.User, id, "test", default));
        await Assert.ThrowsAsync<VaultValidationException>(() => service.TestAsync(f.User, id, "test", default));
        Assert.Equal(1, tester.Calls);
        Assert.Contains(f.Transport.Events, x => x.Action == "Ldap.TestRead");
        Assert.Contains(f.Transport.Events, x => x.Action == "Ldap.TestResult" && x.Outcome == "Rejected");
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await service.TestAsync(f.User, id, "test", default); Assert.Equal(2, tester.Calls);
    }

    [Fact]
    public async Task ExpirationDuringAuditPreventsBind()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        await f.Integrity.RunAsync(async () => { var grant = await f.Db.Grants.SingleAsync(); grant.Permissions |= VaultPermission.TestCredential;
        grant.ExpiresAt = f.Clock.GetUtcNow().AddMinutes(1); await f.Integrity.SaveAsync(default); }, default);
        var id = await f.Vault.CreateSecretAsync(f.User, f.ResourceId, "LDAP account", new("u", "p", ""), "approved", "test", default);
        var tester = new CountingTester(); var service = Service(f, tester);
        f.Transport.AfterSend = () => { if (f.Transport.Events.Last().Action == "Ldap.Test") f.Clock.Advance(TimeSpan.FromMinutes(2)); };
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.TestAsync(f.User, id, "test", default));
        Assert.Equal(0, tester.Calls);
    }

    [Fact]
    public async Task AuditOutagePreventsBind()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        await f.Integrity.RunAsync(async () => { var grant = await f.Db.Grants.SingleAsync(); grant.Permissions |= VaultPermission.TestCredential; await f.Integrity.SaveAsync(default); }, default);
        var id = await f.Vault.CreateSecretAsync(f.User, f.ResourceId, "LDAP account", new("u", "p", ""), "approved", "test", default);
        f.Transport.Fail = true;
        var tester = new CountingTester(); var service = Service(f, tester);
        await Assert.ThrowsAsync<VaultUnavailableException>(() => service.TestAsync(f.User, id, "test", default));
        Assert.Equal(0, tester.Calls);
    }

    private static CredentialTestService Service(VaultServiceTests.Fixture f, CountingTester tester) =>
        new(f.Db, new AccessService(f.Db, f.Clock), f.Vault, f.Audit, tester, new CredentialTestThrottle(f.Clock), new FakeDirectory(), f.Integrity);

    private sealed class CountingTester : ICredentialTester
    {
        public int Calls { get; private set; }
        public Task<CredentialTestResult> TestAsync(string profileId, SecretPayload payload, CancellationToken ct)
        { Calls++; Assert.Equal("approved", profileId); return Task.FromResult(CredentialTestResult.Rejected); }
    }
}
