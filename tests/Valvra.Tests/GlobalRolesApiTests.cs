using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Valvra.Tests;

public sealed class GlobalRolesApiTests
{
    [Fact]
    public async Task RoleAssignmentExclusionsAndSelfRestorationUseFreshRolesAndRequireCsrf()
    {
        await using var host=new TestWebHost();using var owner=host.Client("user");await host.AddCsrfAsync(owner);
        async Task<Guid> Id(HttpResponseMessage response){response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();}
        var group=await Id(await owner.PostAsJsonAsync("/api/groups",new{name="API root",parentId=(Guid?)null}));
        var resource=await Id(await owner.PostAsJsonAsync("/api/resources",new{name="API resource",groupId=group}));
        (await owner.PostAsJsonAsync("/api/settings/roles",new{provider="ad",subjectId="admin",kind=0,roles=256,revision=0})).EnsureSuccessStatusCode();
        using var admin=host.Client("admin");
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.PostAsJsonAsync("/api/settings/exclusions",new{targetKind=0,targetId=group,provider="ad",subjectId="admin"})).StatusCode);
        await host.AddCsrfAsync(admin);
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("isGlobalAdministrator").GetBoolean());
        var batch=new{operations=new[]{new{key="row-2",kind="secret",target=resource.ToString(),name="Imported by global admin",row=2,payload=new{username="synthetic",password="synthetic-value",notes=""}}}};
        using var imported=await admin.PostAsJsonAsync("/api/imports/batch",batch);imported.EnsureSuccessStatusCode();
        Assert.Single((await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray());
        var exclusion=await Id(await admin.PostAsJsonAsync("/api/settings/exclusions",new{targetKind=0,targetId=group,provider="ad",subjectId="admin"}));
        Assert.Equal(HttpStatusCode.Forbidden,(await admin.GetAsync($"/api/resources/{resource}/secrets")).StatusCode);
        using var deniedImport=await admin.PostAsJsonAsync("/api/imports/batch",batch);
        Assert.Empty((await deniedImport.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray());
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/settings/exclusions")).EnumerateArray());
        (await admin.PostAsJsonAsync($"/api/settings/exclusions/{exclusion}/restore",new{revision=1})).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK,(await admin.GetAsync($"/api/resources/{resource}/secrets")).StatusCode);
        exclusion=await Id(await owner.PostAsJsonAsync("/api/settings/exclusions",new{targetKind=0,targetId=group,provider="ad",subjectId="admin"}));
        (await owner.PostAsJsonAsync("/api/settings/roles",new{provider="ad",subjectId="admin",kind=0,roles=0,revision=1})).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,(await admin.PostAsJsonAsync($"/api/settings/exclusions/{exclusion}/restore",new{revision=1})).StatusCode);
    }
}
