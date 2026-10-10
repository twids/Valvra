using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Services;
using Xunit;

namespace Valvra.Tests;

public sealed class GlobalRoleRevocationTests
{
    private sealed class Directory : IDirectoryProvider
    {
        public string ProviderId => "ad";
        public HashSet<string> Disabled {get;}=[];
        public Task<Actor> ResolveAsync(string id,CancellationToken ct)=>Disabled.Contains(id)
            ? throw new AccessDeniedException() : Task.FromResult(new Actor("ad",id,id,new HashSet<string>(),true,false,false));
        public Task<IReadOnlyList<DirectorySubject>> SearchAsync(string query,SubjectKind kind,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DirectorySubject>>([]);
        public Task<DirectorySubject?> FindAsync(string id,SubjectKind kind,CancellationToken ct)=>Task.FromResult<DirectorySubject?>(Disabled.Contains(id)?null:new("ad",id,id,kind));
    }
    [Fact]
    public async Task DisabledAccountsCanBeRevokedButCannotReplaceTheLastAdministrator()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync(); var directory=new Directory();
        var globals=new GlobalRoleService(f.Db,f.Integrity,f.Audit,directory,f.Clock);
        var roles=GlobalRole.AccessAdministrator|GlobalRole.SystemAdministrator;
        await globals.BootstrapAsync(f.User,"setup",default);
        await globals.SetAsync(f.User,"ad","disabled",SubjectKind.User,roles,0,"grant",default);
        directory.Disabled.Add("disabled");
        await Assert.ThrowsAsync<VaultValidationException>(()=>globals.SetAsync(f.User,"ad","user",SubjectKind.User,GlobalRole.None,1,"remove",default));
        await globals.SetAsync(f.User,"ad","other",SubjectKind.User,roles,0,"grant",default);
        await globals.SetAsync(f.User,"ad","disabled",SubjectKind.User,GlobalRole.None,1,"revoke",default);
        await globals.SetAsync(f.User,"ad","user",SubjectKind.User,GlobalRole.None,1,"revoke",default);
        Assert.Equal("other",(await globals.ReadAsync(f.User with {SubjectId="other"},default)).Single().SubjectId);
    }
    [Fact]
    public async Task NormalResourceGrantApiCannotWriteGlobalRoleBitsOrSystemTargets()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        await Assert.ThrowsAsync<VaultValidationException>(()=>f.Vault.GrantAsync(f.User,new AccessGrant {TargetKind=TargetKind.System,TargetId=f.Integrity.InstallationId,SubjectKind=SubjectKind.User,
            Provider="ad",SubjectId="outsider",Permissions=(VaultPermission)(int)GlobalRole.AccessAdministrator},"attempt",default));
        Assert.Empty(await f.Db.Grants.Where(x=>x.TargetKind==TargetKind.System).ToListAsync());
    }
}
