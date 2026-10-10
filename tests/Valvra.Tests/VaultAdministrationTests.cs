using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Xunit;

namespace Valvra.Tests;

public sealed class VaultAdministrationTests
{
    private static Actor Other => new("ad", "other", "Other", new HashSet<string>(), true, false, false);

    [Fact]
    public async Task DelegatedOwnerMustGrantReadAndLosesManagementWhenOwnershipIsRemoved()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var secret = await f.Vault.CreateSecretAsync(f.User, f.ResourceId, "Account", new("u", "p", "n"), null, "test", default);
        var owner = await f.Vault.AddOwnerAsync(f.User, new ResourceOwner { TargetKind = TargetKind.Resource, TargetId = f.ResourceId, Provider = "ad", SubjectKind = SubjectKind.User, SubjectId = "other" }, "test", default);
        Assert.Single((await f.Vault.AccessAsync(Other, TargetKind.Resource, f.ResourceId, default)).Owners);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.RevealSecretAsync(Other, secret, null, "Secret.Reveal", "test", default));
        Assert.Equal(0, f.Cipher.Decryptions);
        var grant = await f.Vault.GrantAsync(Other, new AccessGrant { TargetKind = TargetKind.Resource, TargetId = f.ResourceId, Provider = "ad", SubjectKind = SubjectKind.User, SubjectId = "other", Permissions = VaultPermission.Metadata | VaultPermission.ReadSecret }, "test", default);
        Assert.Equal("p", (await f.Vault.RevealSecretAsync(Other, secret, null, "Secret.Reveal", "test", default)).Password);
        await f.Vault.RemoveOwnerAsync(Other, owner, "test", default);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.RevokeAsync(Other, grant, "test", default));
        await f.Vault.RevokeAsync(f.User, grant, "test", default);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.RevealSecretAsync(Other, secret, null, "Secret.Reveal", "test", default));
        Assert.Contains(f.Transport.Events, x => x.Action == "Access.Revoke" && x.Phase == AuditPhase.Committed);
    }

    [Fact]
    public async Task MovingAResourceChangesInheritedRightsAndPreservesEncryptedSecretContext()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var secret = await f.Vault.CreateSecretAsync(f.User, f.ResourceId, "Account", new("u", "p", "n"), null, "test", default);
        var destination = await f.Vault.CreateGroupAsync(f.User, "Destination", null, "test", default);
        await f.Vault.GrantAsync(f.User, new AccessGrant { TargetKind = TargetKind.Group, TargetId = destination, Provider = "ad", SubjectKind = SubjectKind.User, SubjectId = "other", Permissions = VaultPermission.Metadata | VaultPermission.ReadSecret }, "test", default);
        var preview = await f.Vault.PreviewMoveAsync(f.User, TargetKind.Resource, f.ResourceId, destination, default);
        Assert.Contains(preview.Grants, x => x.SubjectId == "other"); Assert.DoesNotContain(preview.Grants, x => x.SubjectId == "user");
        await f.Vault.MoveAsync(f.User, TargetKind.Resource, f.ResourceId, destination, 1, "test", default);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.RevealSecretAsync(f.User, secret, null, "Secret.Reveal", "test", default));
        Assert.Equal("p", (await f.Vault.RevealSecretAsync(Other, secret, null, "Secret.Reveal", "test", default)).Password);
    }

    [Fact]
    public async Task EmptyTargetDeletionChecksRevisionAndRemovesItsAccessRows()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var child = await f.Vault.CreateGroupAsync(f.User, "Child", f.GroupId, "test", default);
        await f.Vault.RenameGroupAsync(f.User, child, "Renamed", 1, "test", default);
        await Assert.ThrowsAsync<VaultConflictException>(() => f.Vault.DeleteEmptyTargetAsync(f.User, TargetKind.Group, child, 1, "test", default));
        await f.Vault.DeleteEmptyTargetAsync(f.User, TargetKind.Group, child, 2, "test", default);
        Assert.False(await f.Db.Groups.AnyAsync(x => x.Id == child));
        var resource = await f.Vault.CreateResourceAsync(f.User, f.GroupId, "Temporary", "test", default);
        await f.Vault.AddOwnerAsync(f.User, new ResourceOwner { TargetKind = TargetKind.Resource, TargetId = resource, Provider = "ad", SubjectKind = SubjectKind.User, SubjectId = "other" }, "test", default);
        await f.Vault.GrantAsync(f.User, new AccessGrant { TargetKind = TargetKind.Resource, TargetId = resource, Provider = "ad", SubjectKind = SubjectKind.User, SubjectId = "other", Permissions = VaultPermission.Metadata }, "test", default);
        await f.Vault.DeleteEmptyTargetAsync(f.User, TargetKind.Resource, resource, 1, "test", default);
        Assert.False(await f.Db.Resources.AnyAsync(x => x.Id == resource));
        Assert.False(await f.Db.Grants.AnyAsync(x => x.TargetId == resource)); Assert.False(await f.Db.Owners.AnyAsync(x => x.TargetId == resource));
        await Assert.ThrowsAsync<VaultValidationException>(() => f.Vault.DeleteEmptyTargetAsync(f.User, TargetKind.Group, f.GroupId, 1, "test", default));
        var secret = await f.Vault.CreateSecretAsync(f.User, f.ResourceId, "Account", new("u", "p", "n"), null, "test", default);
        await f.Vault.SetDeletedAsync(f.User, secret, true, 1, "test", default);
        Assert.Single(await f.Vault.DeletedSecretsAsync(f.User, f.ResourceId, default));
        await Assert.ThrowsAsync<VaultValidationException>(() => f.Vault.DeleteEmptyTargetAsync(f.User, TargetKind.Resource, f.ResourceId, 1, "test", default));
    }

    [Fact]
    public async Task LicenseAssignmentsAndRecycleBinRequireRightsWithoutDecryption()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var license = await f.Vault.SaveLicenseAsync(f.User, null, f.ResourceId, "P", "V", "R", 2, null, new("key", "note"), 0, "test", default);
        var assignment = await f.Vault.AssignLicenseAsync(f.User, new LicenseAssignment { LicenseId = license, ResourceId = f.ResourceId, Seats = 1 }, "test", default);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.AssignmentsAsync(Other, license, default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.UnassignLicenseAsync(Other, assignment, "test", default));
        Assert.Single(await f.Vault.AssignmentsAsync(f.User, license, default));
        Assert.Equal(1, (await f.Vault.LicensesAsync(f.User, f.ResourceId, default)).Single().AssignedSeats);
        await f.Vault.UnassignLicenseAsync(f.User, assignment, "test", default);
        Assert.Empty(await f.Vault.AssignmentsAsync(f.User, license, default));
        await f.Vault.SetLicenseDeletedAsync(f.User, license, true, 2, "test", default);
        Assert.Single(await f.Vault.DeletedLicensesAsync(f.User, f.ResourceId, default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Vault.DeletedLicensesAsync(Other, f.ResourceId, default));
        await f.Vault.SetLicenseDeletedAsync(f.User, license, false, 3, "test", default);
        Assert.Single(await f.Vault.LicensesAsync(f.User, f.ResourceId, default)); Assert.Equal(0, f.Cipher.Decryptions);
    }
}
