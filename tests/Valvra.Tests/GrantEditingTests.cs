using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Authorization;
using Xunit;

namespace Valvra.Tests;

public sealed class GrantEditingTests
{
    [Fact]
    public async Task UpdatingRightsRetainsRecipientAndChecksRevisionAndAudit()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        var grant=await f.Db.Grants.SingleAsync();var id=grant.Id;var recipient=grant.SubjectId;
        await f.Vault.UpdateGrantAsync(f.User,id,(VaultPermission)31,null,null,grant.Revision,"edit",default);
        grant=await f.Db.Grants.AsNoTracking().SingleAsync();
        Assert.Equal((VaultPermission)31,grant.Permissions);Assert.Equal(2,grant.Revision);Assert.Equal(recipient,grant.SubjectId);
        Assert.Single(await f.Db.Grants.ToListAsync());Assert.Equal(0,f.Cipher.Decryptions);
        Assert.Contains(f.Transport.Events,x=>x.Action=="Access.Update"&&x.Phase==AuditPhase.Committed);
        await Assert.ThrowsAsync<VaultConflictException>(()=>f.Vault.UpdateGrantAsync(f.User,id,VaultPermission.Metadata,null,null,1,"stale",default));
        Assert.Equal((VaultPermission)31,grant.Permissions);
    }

    [Fact]
    public async Task UnauthorizedInvalidAndUnauditedEditsCannotChangeRights()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();var grant=await f.Db.Grants.SingleAsync();
        var outsider=f.User with{SubjectId="outsider",IsAccessAdministrator=false,IsSystemAdministrator=false};
        await Assert.ThrowsAsync<AccessDeniedException>(()=>f.Vault.UpdateGrantAsync(outsider,grant.Id,(VaultPermission)31,null,null,1,"denied",default));
        await Assert.ThrowsAsync<VaultValidationException>(()=>f.Vault.UpdateGrantAsync(f.User,grant.Id,(VaultPermission)31,f.Clock.GetUtcNow(),f.Clock.GetUtcNow().AddHours(1),1,"invalid",default));
        f.Transport.Fail=true;
        await Assert.ThrowsAsync<VaultUnavailableException>(()=>f.Vault.UpdateGrantAsync(f.User,grant.Id,(VaultPermission)31,null,null,1,"audit-failed",default));
        grant=await f.Db.Grants.AsNoTracking().SingleAsync();
        Assert.Equal(1,grant.Revision);Assert.Equal((VaultPermission)7,grant.Permissions);
    }

    [Fact]
    public async Task RootGrantAppliesThroughNestedGroupsAndRevocationRemovesInheritedAccess()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        var child=await f.Vault.CreateGroupAsync(f.User,"Child",f.GroupId,"test",default);
        var grandchild=await f.Vault.CreateGroupAsync(f.User,"Grandchild",child,"test",default);
        var resource=await f.Vault.CreateResourceAsync(f.User,grandchild,"Nested resource","test",default);
        var grant=await f.Vault.GrantAsync(f.User,new AccessGrant{TargetKind=TargetKind.Group,TargetId=f.GroupId,Provider="ad",SubjectId="reader",SubjectKind=SubjectKind.User,Permissions=(VaultPermission)31},"test",default);
        var reader=f.User with{SubjectId="reader",IsAccessAdministrator=false,IsSystemAdministrator=false};var access=new AccessService(f.Db,f.Clock);
        Assert.Equal((VaultPermission)31,await access.EffectiveAsync(reader,TargetKind.Group,grandchild,default));
        Assert.Equal((VaultPermission)31,await access.EffectiveAsync(reader,TargetKind.Resource,resource,default));
        await f.Vault.CreateSecretAsync(reader,resource,"Inherited account",new("example","synthetic",""),null,"test",default);
        await f.Vault.RevokeAsync(f.User,grant,"test",default);
        Assert.Equal(VaultPermission.None,await access.EffectiveAsync(reader,TargetKind.Resource,resource,default));
        await Assert.ThrowsAsync<AccessDeniedException>(()=>f.Vault.CreateSecretAsync(reader,resource,"Denied",new("example","synthetic",""),null,"test",default));
    }
}
