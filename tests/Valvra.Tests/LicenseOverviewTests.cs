using Valvra.Core;using Xunit;
namespace Valvra.Tests;
public sealed class LicenseOverviewTests{
 [Fact]public async Task AggregateIsBoundedAndNeverIncludesHiddenMetadataOrDecryptsSecrets(){
  await using var f=await VaultServiceTests.Fixture.CreateAsync();
  await f.Integrity.RunAsync(async()=>{
   for(var i=0;i<205;i++)f.Db.Licenses.Add(new(){ResourceId=f.ResourceId,Product="Product "+i,Seats=1,Revision=1,EnvelopeJson="never-read"});
   var hiddenGroup=new ResourceGroup{Name="Hidden",Revision=1};f.Db.Groups.Add(hiddenGroup);var hidden=new VaultResource{GroupId=hiddenGroup.Id,Name="Hidden",Revision=1};f.Db.Resources.Add(hidden);
   f.Db.Licenses.Add(new(){ResourceId=hidden.Id,Product="Hidden",Seats=1,Revision=1,EnvelopeJson="never-read"});await f.Integrity.SaveAsync(default);
  },default);
  var first=await f.Vault.LicenseOverviewAsync(f.User,0,default);Assert.Equal(200,first.Items.Count);Assert.True(first.HasMore);
  var last=await f.Vault.LicenseOverviewAsync(f.User,200,default);Assert.Equal(5,last.Items.Count);Assert.False(last.HasMore);Assert.DoesNotContain(first.Items.Concat(last.Items),x=>x.Product=="Hidden");Assert.Equal(0,f.Cipher.Decryptions);
  var outsider=await f.Vault.LicenseOverviewAsync(f.User with{SubjectId="outsider",IsAccessAdministrator=false},0,default);Assert.Empty(outsider.Items);
  await Assert.ThrowsAsync<VaultValidationException>(()=>f.Vault.LicenseOverviewAsync(f.User,-1,default));
 }
}
