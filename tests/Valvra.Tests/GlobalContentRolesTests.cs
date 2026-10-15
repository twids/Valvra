using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Authorization;
using Valvra.Infrastructure.Services;
using Valvra.Infrastructure.Security;
using Valvra.Infrastructure.Auditing;
using Xunit;

namespace Valvra.Tests;

public sealed class GlobalContentRolesTests
{
    [Theory]
    [InlineData(TargetKind.Group)]
    [InlineData(TargetKind.Resource)]
    public async Task DeletionRequiresExplicitRestorationBeforeHistoricalAuditAccessCanReopen(TargetKind kind)
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        var globals=new GlobalRoleService(f.Db,f.Integrity,f.Audit,new FakeDirectory(),f.Clock);
        await globals.BootstrapAsync(f.User,"setup",default);
        var target=kind==TargetKind.Group?await f.Vault.CreateGroupAsync(f.User,"Empty",null,"create",default):f.ResourceId;
        var action=kind==TargetKind.Group?"Group.Delete":"Resource.Delete";
        var exclusion=await globals.ExcludeAsync(f.User,kind,target,"ad","reader","exclude",default);
        var reader=f.User with{SubjectId="reader"};
        var rows=f.Transport.Events.Where(x=>x.TargetId==target&&x.Scope is not null)
            .Select(x=>new AuditEventView(x,"hash","key",true)).ToArray();
        Assert.NotEmpty(rows);
        Assert.Empty(await globals.VisibleAuditAsync(reader,rows,default));
        await Assert.ThrowsAsync<VaultValidationException>(()=>f.Vault.DeleteEmptyTargetAsync(f.User,kind,target,1,"denied",default));
        Assert.True(kind==TargetKind.Group?await f.Db.Groups.AsNoTracking().AnyAsync(x=>x.Id==target):await f.Db.Resources.AsNoTracking().AnyAsync(x=>x.Id==target));
        Assert.Equal(exclusion,(await f.Db.Exclusions.AsNoTracking().SingleAsync()).Id);
        Assert.Empty(await globals.VisibleAuditAsync(reader,rows,default));
        Assert.DoesNotContain(f.Transport.Events,x=>(x.Action==action||x.Action=="Access.Restore")&&x.Phase==AuditPhase.Committed);
        await globals.RestoreAccessAsync(f.User,exclusion,1,"restore",default);
        Assert.Equal(rows.Length,(await globals.VisibleAuditAsync(reader,rows,default)).Count);
        await f.Vault.DeleteEmptyTargetAsync(f.User,kind,target,1,"delete",default);
        Assert.False(kind==TargetKind.Group?await f.Db.Groups.AsNoTracking().AnyAsync(x=>x.Id==target):await f.Db.Resources.AsNoTracking().AnyAsync(x=>x.Id==target));
        Assert.Single(f.Transport.Events,x=>x.Action=="Access.Restore"&&x.Phase==AuditPhase.Committed);
        Assert.Single(f.Transport.Events,x=>x.Action==action&&x.Phase==AuditPhase.Committed);
    }

    [Fact]
    public async Task ExcludedGlobalAdministratorCanRestoreOnlyOwnExclusionUntilTargetAccessIsRestored()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        var globals=new GlobalRoleService(f.Db,f.Integrity,f.Audit,new FakeDirectory(),f.Clock);
        await globals.BootstrapAsync(f.User,"setup",default);
        await globals.SetAsync(f.User,"ad","admin",SubjectKind.User,GlobalRole.GlobalAdministrator,0,"assign",default);
        var admin=await globals.ApplyAsync(f.User with{SubjectId="admin"},default);
        var own=await globals.ExcludeAsync(f.User,TargetKind.Group,f.GroupId,"ad","admin","exclude-own",default);
        var other=await globals.ExcludeAsync(f.User,TargetKind.Group,f.GroupId,"ad","reader","exclude-other",default);
        await Assert.ThrowsAsync<AccessDeniedException>(()=>globals.RestoreAccessAsync(admin,other,1,"denied",default));
        Assert.Equal(2,await f.Db.Exclusions.AsNoTracking().CountAsync());
        Assert.DoesNotContain(f.Transport.Events,x=>x.Action=="Access.Restore"&&x.Phase==AuditPhase.Committed);
        await globals.RestoreAccessAsync(admin,own,1,"restore-own",default);
        Assert.Equal(other,(await f.Db.Exclusions.AsNoTracking().SingleAsync()).Id);
        await globals.RestoreAccessAsync(admin,other,1,"restore-other",default);
        Assert.Empty(await f.Db.Exclusions.AsNoTracking().ToListAsync());
        Assert.Equal(2,f.Transport.Events.Count(x=>x.Action=="Access.Restore"&&x.Phase==AuditPhase.Committed));
    }

    [Fact]
    public async Task LicenseVersionRestorationCombinesLicenseAdministrationWithSeparatelyGrantedRead()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        var actor=f.User with{SubjectId="licensing",IsAccessAdministrator=false,IsAuditor=false,IsLicenseAdministrator=true};
        var id=await f.Vault.SaveLicenseAsync(actor,null,f.ResourceId,"Product","","",1,null,new("first-key",""),0,"create",default);
        await f.Vault.SaveLicenseAsync(actor,id,f.ResourceId,"Product","","",1,null,new("second-key",""),1,"replace",default);
        await Assert.ThrowsAsync<AccessDeniedException>(()=>f.Vault.RestoreLicenseVersionAsync(actor,id,1,2,"denied",default));
        Assert.Equal(0,f.Cipher.Decryptions);
        await f.Vault.GrantAsync(f.User,new AccessGrant{TargetKind=TargetKind.Resource,TargetId=f.ResourceId,Provider="ad",SubjectId="licensing",Permissions=VaultPermission.ReadSecret},"grant-read",default);
        await f.Vault.RestoreLicenseVersionAsync(actor,id,1,2,"restore",default);
        Assert.Equal("first-key",(await f.Vault.RevealLicenseAsync(actor,id,"read",default)).LicenseKey);
    }
    [Fact]
    public async Task EmptyExclusionTablePreservesLegacyIntegrityHashAndUnauthorizedRowDeletionFailsClosed()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        var legacy=new SortedDictionary<string,byte[]>(StringComparer.Ordinal);
        foreach(var x in await f.Db.Groups.AsNoTracking().ToListAsync())legacy.Add($"group/{x.Id:D}",VaultIntegrity.Serialize(x));
        foreach(var x in await f.Db.Resources.AsNoTracking().ToListAsync())legacy.Add($"resource/{x.Id:D}",VaultIntegrity.Serialize(x));
        foreach(var x in await f.Db.Grants.AsNoTracking().ToListAsync())legacy.Add($"grant/{x.Id:D}",VaultIntegrity.Serialize(x));
        Assert.Equal(VaultIntegrity.Hash(legacy,f.Integrity.InstallationId),f.Checkpoints.Value!.Current.Hash);
        var globals=new GlobalRoleService(f.Db,f.Integrity,f.Audit,new FakeDirectory(),f.Clock);
        await globals.BootstrapAsync(f.User,"setup",default);
        await globals.ExcludeAsync(f.User,TargetKind.Group,f.GroupId,"ad","reader","exclude",default);
        await f.Db.Database.ExecuteSqlRawAsync("DELETE FROM Exclusions");
        await Assert.ThrowsAsync<VaultUnavailableException>(()=>f.Vault.ResourcesAsync(f.User,default));
        Assert.Equal(0,f.Cipher.Decryptions);
    }

    [Fact]
    public async Task AuditVisibilityCannotExposeExcludedScopesFromPreviouslyCapturedSnapshots()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        var globals=new GlobalRoleService(f.Db,f.Integrity,f.Audit,new FakeDirectory(),f.Clock);
        await globals.BootstrapAsync(f.User,"setup",default);
        var visibleGroup=await f.Vault.CreateGroupAsync(f.User,"Visible",null,"create",default);
        var visibleResource=await f.Vault.CreateResourceAsync(f.User,visibleGroup,"Visible","create",default);
        AuditEventView Row(Guid resource,Guid group)=>new(new AuditEvent(Guid.NewGuid(),Guid.NewGuid(),f.Clock.GetUtcNow(),"ad","user","Secret.Update",Guid.NewGuid(),AuditPhase.Committed,"Success","test",Scope:new(resource,"Context",group,[new(group,"Group")])),"hash","key",true);
        var rows=new[]{Row(f.ResourceId,f.GroupId),Row(visibleResource,visibleGroup)};
        await globals.ExcludeAsync(f.User,TargetKind.Group,f.GroupId,"ad","reader","exclude",default);
        var reader=f.User with{SubjectId="reader"};
        Assert.Equal(visibleResource,(await globals.VisibleAuditAsync(reader,rows,default)).Single().Event.Scope!.ResourceId);
    }
    [Fact]
    public async Task GlobalAdminHasFullAccessButExclusionsOverrideGrantsOwnershipAndRoleUntilExplicitRestore()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        var globals=new GlobalRoleService(f.Db,f.Integrity,f.Audit,new FakeDirectory(),f.Clock);
        await globals.BootstrapAsync(f.User,"setup",default);
        await globals.SetAsync(f.User,"ad","admin",SubjectKind.User,GlobalRole.GlobalAdministrator,0,"assign",default);
        var admin=await globals.ApplyAsync(f.User with{SubjectId="admin"},default);
        Assert.True(admin.IsAccessAdministrator&&admin.IsSystemAdministrator&&admin.IsAuditor&&admin.IsGlobalAdministrator);
        var access=new AccessService(f.Db,f.Clock);
        Assert.Equal((VaultPermission)31,await access.EffectiveAsync(admin,TargetKind.Resource,f.ResourceId,default));
        var secret=await f.Vault.CreateSecretAsync(admin,f.ResourceId,"Synthetic",new("u","test-secret",""),null,"create",default);
        var exclusion=await globals.ExcludeAsync(admin,TargetKind.Group,f.GroupId,"ad","admin","exclude",default);
        Assert.Empty(await f.Vault.ResourcesAsync(admin,default));
        Assert.False(await access.CanManageAsync(admin,TargetKind.Resource,f.ResourceId,default));
        await Assert.ThrowsAsync<AccessDeniedException>(()=>f.Vault.RevealSecretAsync(admin,secret,null,"Secret.Reveal","denied",default));
        await Assert.ThrowsAsync<AccessDeniedException>(()=>f.Vault.GrantAsync(admin,new AccessGrant{TargetKind=TargetKind.Resource,TargetId=f.ResourceId,Provider="ad",SubjectId="admin",Permissions=(VaultPermission)31},"denied",default));
        Assert.Equal(0,f.Cipher.Decryptions);
        Assert.Single(await globals.ExclusionsAsync(admin,default));
        await Assert.ThrowsAsync<VaultConflictException>(()=>globals.RestoreAccessAsync(admin,exclusion,9,"stale",default));
        f.Transport.Fail=true;await Assert.ThrowsAsync<VaultUnavailableException>(()=>globals.RestoreAccessAsync(admin,exclusion,1,"outage",default));f.Transport.Fail=false;
        Assert.Single(await f.Db.Exclusions.AsNoTracking().ToListAsync());
        await globals.RestoreAccessAsync(admin,exclusion,1,"restore",default);
        Assert.Equal("test-secret",(await f.Vault.RevealSecretAsync(admin,secret,null,"Secret.Reveal","read",default)).Password);
        Assert.Contains(f.Transport.Events,x=>x.Action=="Access.Restore"&&x.Phase==AuditPhase.Committed);
    }

    [Fact]
    public async Task LicenseAdministratorCanManageGlobalLicensesAndAssignmentsWithoutPasswordMetadataOrKeyRead()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        var globals=new GlobalRoleService(f.Db,f.Integrity,f.Audit,new FakeDirectory(),f.Clock);
        await globals.BootstrapAsync(f.User,"setup",default);
        await globals.SetAsync(f.User,"ad","licensing",SubjectKind.User,GlobalRole.LicenseAdministrator,0,"assign",default);
        var actor=await globals.ApplyAsync(f.User with{SubjectId="licensing"},default);
        Assert.Empty(await f.Vault.ResourcesAsync(actor,default));Assert.Empty(await f.Vault.GroupsAsync(actor,default));
        await Assert.ThrowsAsync<AccessDeniedException>(()=>f.Vault.SecretsAsync(actor,f.ResourceId,default));
        await Assert.ThrowsAsync<AccessDeniedException>(()=>f.Vault.CreateSecretAsync(actor,f.ResourceId,"Denied",new("","secret",""),null,"denied",default));
        var license=await f.Vault.SaveLicenseAsync(actor,null,f.ResourceId,"Synthetic","Vendor","",3,null,new("synthetic-key",""),0,"create",default);
        Assert.Single((await f.Vault.LicenseOverviewAsync(actor,0,default)).Items);
        Assert.False((await f.Vault.LicenseDestinationsAsync(actor,default)).Single().CanOpenResource);
        await f.Vault.SaveLicenseAsync(actor,license,f.ResourceId,"Updated","Vendor","",3,null,null,1,"metadata",default,SecretChange.Preserve);
        var assignment=await f.Vault.AssignLicenseAsync(actor,new LicenseAssignment{LicenseId=license,UserProvider="ad",UserId="reader",Seats=1},"assign",default);
        Assert.Single(await f.Vault.AssignmentsAsync(actor,license,default));
        await f.Vault.UnassignLicenseAsync(actor,assignment,"remove",default);
        await Assert.ThrowsAsync<AccessDeniedException>(()=>f.Vault.RevealLicenseAsync(actor,license,"denied",default));
        await Assert.ThrowsAsync<AccessDeniedException>(()=>f.Vault.RevealLicenseAsync(actor,license,"denied",default,1));
        Assert.Equal(0,f.Cipher.Decryptions);
        await globals.ExcludeAsync(f.User,TargetKind.Group,f.GroupId,"ad","licensing","exclude",default);
        Assert.Empty((await f.Vault.LicenseOverviewAsync(actor,0,default)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(()=>f.Vault.LicensesAsync(actor,f.ResourceId,default));
    }

    [Fact]
    public async Task RevokedGlobalRoleCannotRestoreSelfAndMovingCannotSilentlyRemoveInheritedExclusions()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        var globals=new GlobalRoleService(f.Db,f.Integrity,f.Audit,new FakeDirectory(),f.Clock);
        await globals.BootstrapAsync(f.User,"setup",default);
        await globals.SetAsync(f.User,"ad","admin",SubjectKind.User,GlobalRole.GlobalAdministrator,0,"assign",default);
        var admin=await globals.ApplyAsync(f.User with{SubjectId="admin"},default);
        var destination=await f.Vault.CreateGroupAsync(f.User,"Other root",null,"create",default);
        var exclusion=await globals.ExcludeAsync(f.User,TargetKind.Group,f.GroupId,"ad","admin","exclude",default);
        await Assert.ThrowsAsync<VaultValidationException>(()=>f.Vault.MoveAsync(f.User,TargetKind.Resource,f.ResourceId,destination,1,"move",default));
        await globals.SetAsync(f.User,"ad","admin",SubjectKind.User,GlobalRole.None,1,"revoke",default);
        await Assert.ThrowsAsync<AccessDeniedException>(()=>globals.RestoreAccessAsync(admin,exclusion,1,"stale-role",default));
        Assert.Single(await f.Db.Exclusions.ToListAsync());
        await globals.RestoreAccessAsync(f.User,exclusion,1,"restore",default);
        await f.Vault.MoveAsync(f.User,TargetKind.Resource,f.ResourceId,destination,1,"move",default);
    }
}
