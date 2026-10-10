using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Valvra.Infrastructure.Data;
using Valvra.Core;
using Valvra.Tests;

// Isolated browser-test host. Synthetic data only; never published with Valvra.Web.
await using var host = new TestWebHost(preview: true);
var port = int.TryParse(Environment.GetEnvironmentVariable("VALVRA_BROWSER_TEST_PORT"), out var requestedPort) ? requestedPort : 0;
host.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
host.StartServer();
var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VaultDbContext>(); await db.Database.EnsureCreatedAsync();
    var integrity = scope.ServiceProvider.GetRequiredService<Valvra.Infrastructure.Security.VaultIntegrity>();
    await integrity.InitializeEmptyAsync(default);
    // Synthetic loopback identity only. Real installations bootstrap through protected setup.
    await integrity.RunAsync(async () =>
    {
        db.Grants.Add(new AccessGrant { TargetKind = TargetKind.System, TargetId = integrity.InstallationId, SubjectKind = SubjectKind.User,
            Provider = "ad", SubjectId = "user", Permissions = (VaultPermission)(int)(GlobalRole.AccessAdministrator | GlobalRole.SystemAdministrator | GlobalRole.Auditor), Revision = 1 });
        await integrity.SaveAsync(default);
    }, default);
}
using var handler = new HttpClientHandler();
using var client = new HttpClient(handler) { BaseAddress = new Uri(address) };
var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
async Task<Guid> Post(string path, object body)
{
    using var response = await client.PostAsJsonAsync(path, body); response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
}
var group = await Post("/api/groups", new { name = "IT och infrastruktur", parentId = (Guid?)null });
foreach (var name in new[] { "Databasplattform", "Windows-servrar", "Nätverk och åtkomst" })
{
    var resource = await Post("/api/resources", new { name, groupId = group });
    await Post("/api/grants", new { targetKind = 1, targetId = resource, subjectKind = 0, provider = "ad", subjectId = "user", permissions = 31 });
    await Post($"/api/resources/{resource}/secrets", new { title = "Administratörskonto", payload = new { username = "synthetic-account", password = "synthetic-browser-test-password", notes = "Endast testdata" } });
    await Post("/api/licenses", new { resourceId = resource, product = "Exempellicens", vendor = "Exempelleverantör", purchaseReference = "TEST-2026", seats = 20,
        expiresAt = DateTimeOffset.UtcNow.AddDays(20), secretChange = 1, payload = new { licenseKey = "SYNTHETIC-LICENSE", notes = "Endast testdata" } });
}
Console.WriteLine("BROWSER_TEST_URL=" + address);
await Task.Delay(Timeout.InfiniteTimeSpan);
