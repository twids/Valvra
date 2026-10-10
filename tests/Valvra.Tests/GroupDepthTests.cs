using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Xunit;

namespace Valvra.Tests;

public sealed class GroupDepthTests
{
    [Fact]
    public async Task DelegatedCreationAllows128LevelsAndRejects129WithoutBreakingListings()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var parents = await SeedChain(f, 127);
        var actor = new Actor("ad", "delegate", "Delegate", new HashSet<string>(), true, false, false);
        await f.Vault.GrantAsync(f.User, new AccessGrant { TargetKind = TargetKind.Group, TargetId = f.GroupId, SubjectId = actor.SubjectId, Permissions = VaultPermission.Metadata | VaultPermission.ManageAccess }, "test", default);
        var last = await f.Vault.CreateGroupAsync(actor, "Level 128", parents[^1], "test", default);
        await Assert.ThrowsAsync<VaultValidationException>(() => f.Vault.CreateGroupAsync(actor, "Level 129", last, "test", default));
        Assert.Equal(128, (await f.Vault.GroupsAsync(actor, default)).Count);
        Assert.Single(await f.Vault.ResourcesAsync(f.User, default));
    }

    [Fact]
    public async Task MoveChecksFullSubtreeBeforePreviewAndCommit()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); var parents = await SeedChain(f, 127);
        var moving = await f.Vault.CreateGroupAsync(f.User, "Moving root", null, "test", default);
        await f.Vault.CreateGroupAsync(f.User, "Moving child", moving, "test", default);
        await Assert.ThrowsAsync<VaultValidationException>(() => f.Vault.PreviewMoveAsync(f.User, TargetKind.Group, moving, parents[^1], default));
        await Assert.ThrowsAsync<VaultValidationException>(() => f.Vault.MoveAsync(f.User, TargetKind.Group, moving, parents[^1], 1, "test", default));
        Assert.Null((await f.Db.Groups.SingleAsync(x => x.Id == moving)).ParentId);
        await f.Vault.MoveAsync(f.User, TargetKind.Group, moving, parents[^2], 1, "test", default);
        Assert.Equal(129, (await f.Vault.GroupsAsync(f.User, default)).Count); // 127 original + 2 moved, max path 128.
    }

    private static async Task<List<Guid>> SeedChain(VaultServiceTests.Fixture f, int levels)
    {
        var ids = new List<Guid> { f.GroupId };
        await f.Integrity.RunAsync(async () =>
        {
            for (var i = 1; i < levels; i++) { var id = Guid.NewGuid(); f.Db.Groups.Add(new ResourceGroup { Id = id, ParentId = ids[^1], Name = "Level " + (i + 1), Revision = 1 }); ids.Add(id); }
            await f.Integrity.SaveAsync(default);
        }, default);
        return ids;
    }
}
