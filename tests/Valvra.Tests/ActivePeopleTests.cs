using Valvra.Core;
using Valvra.Infrastructure.Authorization;
using Valvra.Infrastructure.Services;
using Xunit;

namespace Valvra.Tests;

public sealed class ActivePeopleTests
{
    private sealed class Directory : IDirectoryProvider
    {
        public string ProviderId=>"ad";
        public int Lookups{get;private set;}
        public bool Mismatch{get;set;}
        public Task<Actor> ResolveAsync(string id,CancellationToken ct)=>Task.FromResult(new Actor("ad",id,id,new HashSet<string>(),true,false,false));
        public Task<IReadOnlyList<DirectorySubject>> SearchAsync(string query,SubjectKind kind,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DirectorySubject>>([]);
        public Task<DirectorySubject?> FindAsync(string id,SubjectKind kind,CancellationToken ct)
        {Lookups++;return Task.FromResult<DirectorySubject?>(id=="disabled"?null:new("ad",Mismatch?"different":id,"Name "+id,kind,"sam."+id));}
    }
    [Fact]
    public async Task PreviouslySignedInPeopleAreRevalidatedAndNeverGrantPermissions()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();var directory=new Directory();
        var vault=new VaultService(new VaultOperations(f.Db,new AccessService(f.Db,f.Clock),f.Audit,f.Cipher,f.Clock,directory,f.Integrity),f.Integrity);
        foreach(var id in new[]{"reader","disabled","reader"})
            await f.Audit.RecordAsync(f.User with{SubjectId=id},"Session.Identified",null,"Success","test",default);
        var people=await vault.ActivePeopleAsync(f.User,null,default);
        Assert.Equal(new[]{"reader","user"},people.Select(x=>x.Id).Order().ToArray());
        Assert.All(people,x=>Assert.StartsWith("sam.",x.SamAccountName));
        var reader=f.User with{SubjectId="reader",IsAccessAdministrator=false,IsAuditor=false};
        await Assert.ThrowsAsync<AccessDeniedException>(()=>vault.SecretsAsync(reader,f.ResourceId,default));
        var lookups=directory.Lookups;
        await Assert.ThrowsAsync<AccessDeniedException>(()=>vault.ActivePeopleAsync(reader,null,default));Assert.Equal(lookups,directory.Lookups);
        directory.Mismatch=true;await Assert.ThrowsAsync<AccessDeniedException>(()=>vault.ActivePeopleAsync(f.User,null,default));
    }
}
