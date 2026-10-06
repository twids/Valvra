using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Valvra.Core;
using Valvra.Infrastructure.Security;
using Xunit;

namespace Valvra.Tests;

public sealed class IntegritySecurityTests
{
    [Theory]
    [InlineData("secret-resource")]
    [InlineData("license-resource")]
    [InlineData("resource-parent")]
    [InlineData("group-parent")]
    [InlineData("grant-edit")]
    [InlineData("grant-insert")]
    [InlineData("grant-delete")]
    [InlineData("owner-insert")]
    [InlineData("version-edit")]
    [InlineData("license-history")]
    [InlineData("assignment")]
    [InlineData("audit-edit")]
    [InlineData("audit-delete")]
    public async Task DatabaseTamperingBlocksBothSecretTypesBeforeDecryption(string attack)
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var secret = await f.Vault.CreateSecretAsync(f.User, f.ResourceId, "Account", new("u", "private-password", "note"), null, "test", default);
        var license = await f.Vault.SaveLicenseAsync(f.User, null, f.ResourceId, "Product", "Vendor", "ref", 2, null, new("private-key", "note"), 0, "test", default);
        var otherGroup = await f.Vault.CreateGroupAsync(f.User, "Other", null, "test", default);
        var otherResource = await f.Vault.CreateResourceAsync(f.User, otherGroup, "Other", "test", default);
        var assignment = await f.Vault.AssignLicenseAsync(f.User, new LicenseAssignment { LicenseId = license, UserProvider = "ad", UserId = "user", Seats = 1 }, "test", default);
        // Model a DBA/SQL-writer compromise, deliberately bypassing the application's integrity boundary.
        switch (attack)
        {
            case "secret-resource": await f.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Secrets SET ResourceId={otherResource} WHERE Id={secret}"); break;
            case "license-resource": await f.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Licenses SET ResourceId={otherResource} WHERE Id={license}"); break;
            case "resource-parent": await f.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Resources SET GroupId={otherGroup} WHERE Id={f.ResourceId}"); break;
            case "group-parent": await f.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Groups SET ParentId={otherGroup} WHERE Id={f.GroupId}"); break;
            case "grant-edit": await f.Db.Database.ExecuteSqlRawAsync("UPDATE Grants SET SubjectId='outsider', Permissions=31"); break;
            case "grant-delete": await f.Db.Database.ExecuteSqlRawAsync("DELETE FROM Grants"); break;
            case "grant-insert": f.Db.Grants.Add(new AccessGrant { TargetKind = TargetKind.Resource, TargetId = f.ResourceId, SubjectId = "outsider", Permissions = (VaultPermission)31 }); await f.Db.SaveChangesAsync(); break;
            case "owner-insert": f.Db.Owners.Add(new ResourceOwner { TargetKind = TargetKind.Resource, TargetId = f.ResourceId, SubjectId = "outsider" }); await f.Db.SaveChangesAsync(); break;
            case "version-edit": await f.Db.Database.ExecuteSqlRawAsync("UPDATE SecretVersions SET CreatedBy='outsider'"); break;
            case "license-history": await f.Db.Database.ExecuteSqlRawAsync("UPDATE LicenseVersions SET CreatedBy='outsider'"); break;
            case "assignment": await f.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Assignments SET Seats=2 WHERE Id={assignment}"); break;
            case "audit-edit": await f.Db.Database.ExecuteSqlRawAsync("UPDATE Audit SET Action='Forged.Action'"); break;
            case "audit-delete": await f.Db.Database.ExecuteSqlRawAsync("DELETE FROM Audit"); break;
        }
        var delivered = f.Transport.Events.Count;
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Vault.RevealSecretAsync(f.User, secret, null, "Secret.Reveal", "test", default));
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Vault.RevealLicenseAsync(f.User, license, "test", default));
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Vault.ResourcesAsync(f.User, default));
        Assert.Equal(0, f.Cipher.Decryptions); Assert.Equal(delivered, f.Transport.Events.Count);
    }

    [Fact]
    public async Task ModifiedPendingOutboxIsNeverSignedOrDelivered()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        f.Transport.Fail = true;
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Audit.RecordAsync(f.User, "Original", null, "Success", "test", default));
        var signs = f.AuditSigner.Signatures;
        await f.Db.Database.ExecuteSqlRawAsync("UPDATE Audit SET ActorId='forged-actor', Action='Forged', Delivered=1");
        f.Transport.Fail = false;
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Audit.FlushAsync(default));
        Assert.Equal(signs, f.AuditSigner.Signatures); Assert.Empty(f.Transport.Events);
    }

    [Fact]
    public async Task TriggerSideEffectsCannotReceiveANewTrustedCheckpoint()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER corrupt_permissions AFTER UPDATE ON Resources BEGIN UPDATE Grants SET SubjectId='outsider'; END;");
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Vault.RenameResourceAsync(f.User, f.ResourceId, "Changed", 1, "test", default));
        Assert.Equal("Resource", (await f.Db.Resources.SingleAsync()).Name);
        Assert.Equal("user", (await f.Db.Grants.SingleAsync()).SubjectId);
        Assert.Contains(f.Transport.Events, x => x.Phase == AuditPhase.Failed);
        Assert.Single(await f.Vault.ResourcesAsync(f.User, default));
    }

    [Fact]
    public async Task DatabaseRollbackIsRejectedAfterServiceRestart()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        await f.Integrity.RunAsync(async () => { (await f.Db.Resources.SingleAsync()).Name = "New state"; await f.Integrity.SaveAsync(default); }, default);
        await f.Db.Database.ExecuteSqlRawAsync("UPDATE Resources SET Name='Resource'");
        var restarted = Restarted(f);
        await Assert.ThrowsAsync<VaultUnavailableException>(() => restarted.RunAsync(() => Task.FromResult(true), default));
    }

    [Fact]
    public async Task MissingOrCorruptCheckpointFailsClosedAndCannotBeInitializedOverData()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var original = f.Checkpoints.Value!;
        f.Checkpoints.Value = null;
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Vault.ResourcesAsync(f.User, default));
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Integrity.InitializeEmptyAsync(default));
        f.Checkpoints.Value = original with { Current = original.Current with { Hash = new string('0', 64) } };
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Vault.ResourcesAsync(f.User, default));
        f.Checkpoints.Value = original with { Current = original.Current with { InstallationId = Guid.NewGuid() } };
        await Assert.ThrowsAsync<VaultUnavailableException>(() => f.Vault.ResourcesAsync(f.User, default));
    }

    [Fact]
    public async Task FailureBeforePreparingCheckpointRollsBackAndCanBeRetried()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        f.Checkpoints.FailPreparation = true;
        await Assert.ThrowsAsync<IOException>(() => ChangeName(f));
        Assert.Null(f.Checkpoints.Value!.Prepared);
        Assert.Equal("Resource", (await f.Vault.ResourcesAsync(f.User, default)).Single().Name);
        await ChangeName(f); Assert.Equal("Changed", (await f.Vault.ResourcesAsync(f.User, default)).Single().Name);
    }

    [Fact]
    public async Task CommittedPreparedCheckpointRecoversOnlyTheExactNewState()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        f.Checkpoints.FailFinalization = true;
        await Assert.ThrowsAsync<IOException>(() => ChangeName(f));
        Assert.NotNull(f.Checkpoints.Value!.Prepared);
        var restarted = Restarted(f);
        await restarted.RunAsync(() => Task.CompletedTask, default);
        Assert.Null(f.Checkpoints.Value!.Prepared);
        Assert.Equal("Changed", (await f.Db.Resources.SingleAsync()).Name);
    }

    [Fact]
    public async Task OldDatabaseWithPreparedCheckpointRequiresExplicitAuthorizedRecovery()
    {
        var interceptor = new AbortCommit();
        await using var f = await VaultServiceTests.Fixture.CreateAsync(interceptor);
        interceptor.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => ChangeName(f));
        Assert.NotNull(f.Checkpoints.Value!.Prepared);
        var restarted = Restarted(f);
        await Assert.ThrowsAsync<VaultUnavailableException>(() => restarted.RunAsync(() => Task.CompletedTask, default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => restarted.RecoverUncommittedAsync(f.User with { IsAccessAdministrator = false }, default));
        await restarted.RecoverUncommittedAsync(f.User, default);
        await restarted.RunAsync(() => Task.CompletedTask, default);
        Assert.Equal("Resource", (await f.Db.Resources.SingleAsync()).Name);
    }

    private static VaultIntegrity Restarted(VaultServiceTests.Fixture f) => new(f.Db, f.Checkpoints, f.IntegritySigner, f.IntegrityOptions);
    private static Task ChangeName(VaultServiceTests.Fixture f) => f.Integrity.RunAsync(async () =>
    { (await f.Db.Resources.SingleAsync()).Name = "Changed"; await f.Integrity.SaveAsync(default); }, default);
    private sealed class AbortCommit : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
        { if (Enabled) { Enabled = false; throw new IOException("Injected commit failure."); } return ValueTask.FromResult(result); }
    }
}
