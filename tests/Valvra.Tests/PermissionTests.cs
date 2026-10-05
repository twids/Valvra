using Valvra.Core;
using Xunit;

namespace Valvra.Tests;

public sealed class PermissionTests
{
    private static readonly Guid Group = Guid.NewGuid();
    private static readonly Guid Resource = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static Actor User(bool enabled = true, bool admin = false) => new("ad", "user-1", "User", new HashSet<string> { "group-1" }, enabled, admin, false);
    private static AccessGrant Grant() => new() { TargetKind = TargetKind.Group, TargetId = Group,
        Provider = "ad", SubjectKind = SubjectKind.Group, SubjectId = "group-1", Permissions = VaultPermission.ReadSecret };

    [Fact]
    public void NestedGroupGrantIsInherited() => Assert.Equal(VaultPermission.ReadSecret,
        PermissionEvaluator.Effective(User(), [Grant()], new HashSet<Guid> { Group }, Resource, Now));

    [Fact]
    public void UnrelatedResourceCannotBeRead() => Assert.Equal(VaultPermission.None,
        PermissionEvaluator.Effective(User(), [Grant()], new HashSet<Guid>(), Guid.NewGuid(), Now));

    [Fact]
    public void AdministratorDoesNotAutomaticallyReadSecrets() => Assert.Equal(VaultPermission.None,
        PermissionEvaluator.Effective(User(admin: true), [], new HashSet<Guid> { Group }, Resource, Now));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(59, true)]
    [InlineData(60, false)]
    [InlineData(61, false)]
    public void TemporaryAccessHasExactBoundaries(int seconds, bool allowed)
    {
        var grant = Grant();
        grant.StartsAt = Now;
        grant.ExpiresAt = Now.AddSeconds(60);
        Assert.Equal(allowed, PermissionEvaluator.Effective(User(), [grant], new HashSet<Guid> { Group }, Resource,
            Now.AddSeconds(seconds)).HasFlag(VaultPermission.ReadSecret));
    }

    [Fact]
    public void DisabledAccountCannotReadOrManage()
    {
        var actor = User(enabled: false, admin: true);
        Assert.Equal(VaultPermission.None, PermissionEvaluator.Effective(actor, [Grant()], new HashSet<Guid> { Group }, Resource, Now));
        Assert.False(PermissionEvaluator.CanManage(actor, [], new HashSet<Guid> { Group }, Resource, VaultPermission.ManageAccess));
    }

    [Fact]
    public void ModifyDoesNotImplyReadOrManage()
    {
        var grant = Grant(); grant.Permissions = VaultPermission.Modify;
        var effective = PermissionEvaluator.Effective(User(), [grant], new HashSet<Guid> { Group }, Resource, Now);
        Assert.False(effective.HasFlag(VaultPermission.ReadSecret));
        Assert.False(PermissionEvaluator.CanManage(User(), [], new HashSet<Guid> { Group }, Resource, effective));
    }

    [Fact]
    public void ProviderIdsDoNotCrossIdentityBoundaries()
    {
        var grant = Grant(); grant.Provider = "other";
        Assert.Equal(VaultPermission.None, PermissionEvaluator.Effective(User(), [grant], new HashSet<Guid> { Group }, Resource, Now));
    }

    [Fact]
    public void OwnerCanManageOnlyItsSubtree()
    {
        var owner = new ResourceOwner { TargetKind = TargetKind.Group, TargetId = Group, Provider = "ad", SubjectKind = SubjectKind.User, SubjectId = "user-1" };
        Assert.True(PermissionEvaluator.CanManage(User(), [owner], new HashSet<Guid> { Group }, Resource, VaultPermission.None));
        Assert.False(PermissionEvaluator.CanManage(User(), [owner], new HashSet<Guid>(), Resource, VaultPermission.None));
    }

    [Fact]
    public void TemporaryWriteOrGroupGrantsAreRejected()
    {
        var grant = Grant(); grant.StartsAt = Now; grant.ExpiresAt = Now.AddHours(1);
        Assert.Throws<VaultValidationException>(() => PermissionEvaluator.ValidateGrant(grant));
        grant.SubjectKind = SubjectKind.User; grant.Permissions = VaultPermission.Modify;
        Assert.Throws<VaultValidationException>(() => PermissionEvaluator.ValidateGrant(grant));
        grant.Permissions = VaultPermission.ReadSecret | VaultPermission.Metadata;
        PermissionEvaluator.ValidateGrant(grant);
    }
}
