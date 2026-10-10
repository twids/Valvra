using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Valvra.Demo;
using Xunit;

namespace Valvra.Tests;

public sealed class DemoSecurityTests
{
    private const string Resource = "20000000-0000-0000-0000-000000000001";
    private const string License = "40000000-0000-0000-0000-000000000001";
    [Fact]
    public async Task DemoIdentifiesSyntheticDataAndProvidesNoAdministrativeRights()
    {
        await using var host = new WebApplicationFactory<DemoProgram>();
        using var client = host.CreateClient();
        using var page = await client.GetAsync("/");
        Assert.Contains("DEMO", await page.Content.ReadAsStringAsync());
        Assert.Equal("synthetic-read-only", page.Headers.GetValues("X-Valvra-Demo").Single());
        Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.False(session.GetProperty("isAccessAdministrator").GetBoolean());
        Assert.False(session.GetProperty("isAuditor").GetBoolean());
        var resources = await client.GetFromJsonAsync<JsonElement>("/api/resources");
        Assert.All(resources.EnumerateArray(), x => { Assert.Equal(3, x.GetProperty("permissions").GetInt32()); Assert.False(x.GetProperty("canManage").GetBoolean()); });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/js/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/css/site.css")).StatusCode);
    }

    [Theory]
    [InlineData("/api/groups")]
    [InlineData("/api/grants")]
    [InlineData("/api/licenses")]
    [InlineData("/api/setup/finish")]
    [InlineData("/api/licenses/40000000-0000-0000-0000-000000000001/update")]
    public async Task DemoCannotPersistChangesOrRunSetup(string path)
    {
        await using var host = new WebApplicationFactory<DemoProgram>();
        using var client = host.CreateClient();
        var before = await client.GetStringAsync($"/api/resources/{Resource}/licenses");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsJsonAsync(path, new { payload = "untrusted-input" })).StatusCode);
        Assert.Equal(before, await client.GetStringAsync($"/api/resources/{Resource}/licenses"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task DemoRevealsOnlyFixedExamplesAndRejectsCredentialInput()
    {
        await using var host = new WebApplicationFactory<DemoProgram>();
        using var client = host.CreateClient();
        var path = $"/api/licenses/{License}/reveal";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { password = "untrusted-input" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { version = 2 })).StatusCode);
        var result = await client.PostAsJsonAsync(path, new { version = 1 });
        Assert.Contains("DEMO-0000-EXAMPLE-0000", await result.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsJsonAsync(path, new { payload = new string('x', 300) })).StatusCode);
    }

    [Fact]
    public void DemoAssemblyDoesNotReferenceVaultOrDatabaseImplementations()
    {
        Assert.DoesNotContain(typeof(DemoProgram).Assembly.GetReferencedAssemblies(), x =>
            x.Name!.StartsWith("Valvra.", StringComparison.Ordinal) || x.Name.Contains("EntityFramework", StringComparison.Ordinal)
            || x.Name.Contains("SqlClient", StringComparison.Ordinal) || x.Name.Contains("Npgsql", StringComparison.Ordinal));
    }
}
