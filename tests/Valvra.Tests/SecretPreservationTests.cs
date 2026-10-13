using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Xunit;
namespace Valvra.Tests;
public sealed class SecretPreservationTests
{
    [Fact]public async Task MetadataPreservesEncryptedVersionWithoutReadPermissionOrDecryption(){
        await using var f=await VaultServiceTests.Fixture.CreateAsync();var id=await f.Vault.CreateSecretAsync(f.User,f.ResourceId,"Account",new("username","password","notes"),null,"test",default);
        var editor=f.User with{IsAccessAdministrator=false};
        await f.Integrity.RunAsync(async()=>{var grant=await f.Db.Grants.SingleAsync(x=>x.TargetId==f.GroupId);grant.Permissions=VaultPermission.Metadata|VaultPermission.Modify;await f.Integrity.SaveAsync(default);},default);
        await f.Vault.UpdateSecretAsync(editor,id,"Renamed",null,null,1,"test",default,SecretChange.Preserve);
        var entry=await f.Db.Secrets.SingleAsync();Assert.Equal(1,entry.CurrentVersion);Assert.Equal(2,entry.Revision);Assert.Single(await f.Db.SecretVersions.ToListAsync());Assert.Equal(0,f.Cipher.Decryptions);
    }
    [Fact]public async Task PreserveRejectsPayloadAndUnsupportedClearBeforeWriting(){
        await using var f=await VaultServiceTests.Fixture.CreateAsync();var id=await f.Vault.CreateSecretAsync(f.User,f.ResourceId,"Account",new("u","p","n"),null,"test",default);
        await Assert.ThrowsAsync<VaultValidationException>(()=>f.Vault.UpdateSecretAsync(f.User,id,"Changed",new("x","y","z"),null,1,"test",default,SecretChange.Preserve));
        await Assert.ThrowsAsync<VaultValidationException>(()=>f.Vault.UpdateSecretAsync(f.User,id,"Changed",null,null,1,"test",default,SecretChange.Clear));
        await Assert.ThrowsAsync<VaultValidationException>(()=>f.Vault.UpdateSecretAsync(f.User,id,"Changed",null,null,1,"test",default,SecretChange.Replace));
        Assert.Equal(1,(await f.Db.Secrets.SingleAsync()).Revision);
    }
}
