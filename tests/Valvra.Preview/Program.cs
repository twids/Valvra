using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Valvra.Infrastructure.Data;
using Valvra.Tests;

// Isolated browser-test host. Synthetic data only; never published with Valvra.Web.
await using var host = new TestWebHost(preview: true);
host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
host.StartServer();
var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
using (var scope = host.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<VaultDbContext>().Database.EnsureCreatedAsync();
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
        expiresAt = DateTimeOffset.UtcNow.AddDays(20), payload = new { licenseKey = "SYNTHETIC-LICENSE", notes = "Endast testdata" } });
}
Console.WriteLine("BROWSER_TEST_URL=" + address);
await Task.Delay(Timeout.InfiniteTimeSpan);
