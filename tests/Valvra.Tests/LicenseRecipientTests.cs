using Valvra.Core;using Valvra.Infrastructure.Authorization;using Valvra.Infrastructure.Services;using Xunit;
namespace Valvra.Tests;
public sealed class LicenseRecipientTests{
 private sealed class Directory:IDirectoryProvider{
  public string ProviderId=>"ad";public int Searches,Finds;public bool Mismatch;
  public Task<Actor> ResolveAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
  public Task<IReadOnlyList<DirectorySubject>> SearchAsync(string query,SubjectKind kind,CancellationToken ct){Searches++;Assert.Equal(SubjectKind.User,kind);return Task.FromResult<IReadOnlyList<DirectorySubject>>([new("ad","recipient","Alex Example",kind,"alex.one")]);}
  public Task<DirectorySubject?> FindAsync(string id,SubjectKind kind,CancellationToken ct){Finds++;return Task.FromResult<DirectorySubject?>(new("ad",Mismatch?"wrong":id,"Alex Example",kind,"alex.one"));}
 }
 private static VaultService Service(VaultServiceTests.Fixture f,Directory directory)=>new(new VaultOperations(f.Db,new AccessService(f.Db,f.Clock),f.Audit,f.Cipher,f.Clock,directory,f.Integrity),f.Integrity);
 private static Task<Guid> License(VaultServiceTests.Fixture f)=>f.Vault.SaveLicenseAsync(f.User,null,f.ResourceId,"Product","Vendor","",4,null,new("key",""),0,"test",default);
 [Fact]public async Task EditorCanSearchLicenseRecipientsButOutsiderCannotTriggerDirectorySearch(){
  await using var f=await VaultServiceTests.Fixture.CreateAsync();var id=await License(f);var directory=new Directory();var service=Service(f,directory);
  var people=await service.LicensePeopleAsync(f.User with{IsAccessAdministrator=false},id,"Alex",default);Assert.Equal("alex.one",Assert.Single(people).SamAccountName);
  await Assert.ThrowsAsync<AccessDeniedException>(()=>service.LicensePeopleAsync(f.User with{IsAccessAdministrator=false,SubjectId="outsider"},id,"Alex",default));Assert.Equal(1,directory.Searches);
 }
 [Fact]public async Task AssignmentLabelsResolveOnceAndPreserveStableIds(){
  await using var f=await VaultServiceTests.Fixture.CreateAsync();var id=await License(f);
  for(var i=0;i<2;i++)await f.Vault.AssignLicenseAsync(f.User,new(){LicenseId=id,UserProvider="ad",UserId="recipient",Seats=1},"test",default);
  var directory=new Directory();var rows=await Service(f,directory).AssignmentsAsync(f.User,id,default);Assert.Equal(1,directory.Finds);Assert.All(rows,x=>{Assert.Equal("recipient",x.UserId);Assert.Equal("Alex Example",x.UserName);Assert.Equal("alex.one",x.SamAccountName);});
  await Assert.ThrowsAsync<AccessDeniedException>(()=>Service(f,new Directory{Mismatch=true}).AssignmentsAsync(f.User,id,default));
 }
}
