using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Valvra.Web;
using Valvra.Web.Administration;
using Xunit;

namespace Valvra.Tests;

public sealed class AdministrationPresentationTests
{
    [Fact]
    public async Task SessionVersionComesFromTheRunningAssembly()
    {
        await using var host = new TestWebHost(); using var client = host.Client("user");
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal(BuildInformation.Version,session.GetProperty("applicationVersion").GetString());
        Assert.Equal(BuildInformation.Build,session.GetProperty("build").GetString());
    }
    [Fact]
    public async Task CertificateMetadataRequiresFreshAdministratorAuthorization()
    {
        await using var host = new TestWebHost(); using var admin = host.Client("user"); await host.AddCsrfAsync(admin);
        using var other = host.Client("outsider");
        Assert.Equal(HttpStatusCode.Forbidden,(await other.GetAsync("/api/settings/certificates")).StatusCode);
        (await admin.PostAsJsonAsync("/api/settings/roles",new {provider="ad",subjectId="outsider",kind=0,roles=64,revision=0})).EnsureSuccessStatusCode();
        var rows = await other.GetFromJsonAsync<CertificateStatusView[]>("/api/settings/certificates");
        Assert.Equal(3,rows!.Length); Assert.All(rows,x=>Assert.Null(x.HasPrivateKey));
        (await admin.PostAsJsonAsync("/api/settings/roles",new {provider="ad",subjectId="outsider",kind=0,roles=0,revision=1})).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,(await other.GetAsync("/api/settings/certificates")).StatusCode);
    }
}
