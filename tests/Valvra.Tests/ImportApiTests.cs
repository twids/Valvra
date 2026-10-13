using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Valvra.Core;
using Valvra.Web;
using Microsoft.Extensions.DependencyInjection;
using Valvra.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Valvra.Tests;

public sealed class ImportApiTests
{
    private static async Task<Guid> Create(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task<(Guid Group, Guid Resource)> Destination(HttpClient client, bool contentRights)
    {
        var group = await Create(client,"/api/groups",new {name="Import test",parentId=(Guid?)null});
        if (contentRights) await Create(client,"/api/grants",new {targetKind=0,targetId=group,subjectKind=0,provider="ad",subjectId="user",permissions=5});
        return (group, await Create(client,"/api/resources",new {name="Destination",groupId=group}));
    }
    private static ImportOperation Entry(Guid resource, int row = 2) => new("row:"+row,"secret",resource.ToString(),"Imported",new("synthetic","NEVER-LOG-THIS",""),row);

    [Fact]
    public async Task OrdinaryEditorCannotImportButManualCreationStillWorks()
    {
        await using var host = new TestWebHost(); using var admin = host.Client("user"); await host.AddCsrfAsync(admin);
        var (_, resource) = await Destination(admin,true);
        await Create(admin,"/api/grants",new {targetKind=1,targetId=resource,subjectKind=0,provider="ad",subjectId="outsider",permissions=5});
        using var editor = host.Client("outsider"); await host.AddCsrfAsync(editor);
        using var denied = await editor.PostAsJsonAsync("/api/imports/batch",new ImportBatchRequest([Entry(resource)]));
        Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        await Create(editor,$"/api/resources/{resource}/secrets",new {title="Manual",payload=new SecretPayload("u","p","")});
    }
    [Fact]
    public async Task AdministratorStillRequiresDestinationContentRights()
    {
        await using var host = new TestWebHost(); using var client = host.Client("user"); await host.AddCsrfAsync(client);
        var (_, resource) = await Destination(client,false);
        var result = await (await client.PostAsJsonAsync("/api/imports/batch",new ImportBatchRequest([Entry(resource)]))).Content.ReadFromJsonAsync<ImportBatchResult>();
        Assert.Equal("Denied",result!.ErrorCode); Assert.Empty(result.Results);
        using var scope = host.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<VaultDbContext>().Audit.ToListAsync();
        Assert.Contains(audit,x=>x.Action=="Access.Denied");
    }
    [Fact]
    public async Task RetrySkipsDuplicatesAndBatchReferencesCreateInheritedHierarchyWithoutGrants()
    {
        await using var host = new TestWebHost(); using var client = host.Client("user"); await host.AddCsrfAsync(client);
        var (group, _) = await Destination(client,true);
        var request = new ImportBatchRequest([
            new("group:0","group",group.ToString(),"Subgroup"),
            new("resource:0","resource","group:0","Imported resource"),
            new("row:2","secret","resource:0","Imported",new("u","NEVER-LOG-THIS",""),2)]);
        var first = await (await client.PostAsJsonAsync("/api/imports/batch",request)).Content.ReadFromJsonAsync<ImportBatchResult>();
        Assert.Null(first!.ErrorCode); Assert.Equal(3,first.Results.Count); Assert.All(first.Results,x=>Assert.True(x.Created));
        var second = await (await client.PostAsJsonAsync("/api/imports/batch",request)).Content.ReadFromJsonAsync<ImportBatchResult>();
        Assert.Null(second!.ErrorCode); Assert.All(second.Results,x=>Assert.False(x.Created));
        var access = await client.GetFromJsonAsync<JsonElement>($"/api/access/1/{first.Results[1].Id}");
        Assert.All(access.GetProperty("grants").EnumerateArray(),x=>Assert.Equal(group,x.GetProperty("targetId").GetGuid()));
    }
    [Fact]
    public async Task BatchIsBoundedCsrfProtectedAndReportsSafePartialFailure()
    {
        await using var host = new TestWebHost(); using var client = host.Client("user"); await host.AddCsrfAsync(client);
        var (_, resource) = await Destination(client,true);
        using var tooMany = await client.PostAsJsonAsync("/api/imports/batch",new ImportBatchRequest(Enumerable.Range(2,13).Select(x=>Entry(resource,x)).ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest,tooMany.StatusCode);
        using var noCsrf = host.Client("user");
        Assert.Equal(HttpStatusCode.BadRequest,(await noCsrf.PostAsJsonAsync("/api/imports/batch",new ImportBatchRequest([Entry(resource)]))).StatusCode);
        var invalid = Entry(Guid.NewGuid(),3);
        using var response = await client.PostAsJsonAsync("/api/imports/batch",new ImportBatchRequest([Entry(resource),invalid]));
        var result = await response.Content.ReadFromJsonAsync<ImportBatchResult>();
        Assert.Single(result!.Results); Assert.Equal("Denied",result.ErrorCode); Assert.Equal("row:3",result.FailedKey);
        Assert.DoesNotContain("NEVER-LOG-THIS",await response.Content.ReadAsStringAsync());
    }
}
