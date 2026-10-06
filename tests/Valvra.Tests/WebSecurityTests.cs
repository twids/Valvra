using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Valvra.Core;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Security;
using Valvra.Web.Setup;
using Xunit;

namespace Valvra.Tests;

public sealed class WebSecurityTests
{
    [Fact]
    public async Task DeepLinksRequireAuthenticationAndDoNotRewriteApiOrStaticRoutes()
    {
        await using var host = new TestWebHost(); using var anonymous = host.Client(null); using var authenticated = host.Client("user");
        foreach (var path in new[] { "/resources", "/resources/99999999-9999-9999-9999-999999999999", "/groups", "/groups/99999999-9999-9999-9999-999999999999",
            "/groups/99999999-9999-9999-9999-999999999999/resources/88888888-8888-8888-8888-888888888888", "/licenses", "/audit", "/settings" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            using var page = await authenticated.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
            Assert.Contains("/js/navigation.js", await page.Content.ReadAsStringAsync());
            Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await authenticated.GetAsync("/api/not-a-real-endpoint")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await authenticated.GetAsync("/js/not-a-real-file.js")).StatusCode);
    }

    [Fact]
    public async Task SetupRequiresPrivateBootstrapCodeEvenWithAuthenticatedCsrf()
    {
        await using var host = new TestWebHost(); using var client = host.Client("user");
        await host.AddCsrfAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/setup/validate", new SetupRequest())).StatusCode);
        client.DefaultRequestHeaders.Add("X-SETUP-TOKEN", new string('0', 64));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/setup/finish", new SetupRequest())).StatusCode);
    }

    [Fact]
    public async Task UnauthenticatedApiIsChallenged()
    {
        await using var host = new TestWebHost(); using var client = host.Client(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/resources")).StatusCode);
    }

    [Fact]
    public async Task HttpIsRejectedAndSecurityHeadersArePresent()
    {
        await using var host = new TestWebHost(); using var client = host.Client("user");
        using var html = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        Assert.Contains("no-store", html.Headers.CacheControl!.ToString());
        Assert.Equal("DENY", html.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", html.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("http://localhost/api/resources")).StatusCode);
    }

    [Fact]
    public async Task MutationWithoutCsrfCannotCreateGroup()
    {
        await using var host = new TestWebHost(); using var client = host.Client("user");
        var response = await client.PostAsJsonAsync("/api/groups", new { name = "Unauthorized mutation", parentId = (Guid?)null });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<VaultDbContext>().Groups.ToListAsync());
    }

    [Fact]
    public async Task AuthorizedFlowStoresCiphertextAndAuditsRead()
    {
        await using var host = new TestWebHost(); using var client = host.Client("user");
        await host.AddCsrfAsync(client);
        var group = await PostId(client, "/api/groups", new { name = "Infrastructure", parentId = (Guid?)null });
        var resource = await PostId(client, "/api/resources", new { name = "Domain controller", groupId = group });
        await PostId(client, "/api/grants", new { targetKind = 1, targetId = resource, subjectKind = 0, provider = "ad", subjectId = "user", permissions = 7 });
        var secret = await PostId(client, $"/api/resources/{resource}/secrets", new { title = "Service account", payload = new { username = "service", password = "secret-for-web-test", notes = "private-note" }, ldapProfileId = (string?)null });
        var metadata = await client.GetStringAsync($"/api/resources/{resource}/secrets");
        Assert.DoesNotContain("secret-for-web-test", metadata); Assert.DoesNotContain("envelope", metadata, StringComparison.OrdinalIgnoreCase);
        var revealed = await client.PostAsJsonAsync($"/api/secrets/{secret}/reveal", new { copy = true });
        Assert.Equal(HttpStatusCode.OK, revealed.StatusCode); Assert.Contains("secret-for-web-test", await revealed.Content.ReadAsStringAsync());
        Assert.Contains(host.Transport.Events, x => x.Action == "Secret.Copy");
        using var scope = host.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<VaultDbContext>().SecretVersions.SingleAsync();
        Assert.DoesNotContain("secret-for-web-test", stored.EnvelopeJson); Assert.DoesNotContain("private-note", stored.EnvelopeJson);
    }

    [Fact]
    public async Task OtherIdentityCannotReadByGuessingSecretId()
    {
        await using var host = new TestWebHost(); using var owner = host.Client("user"); await host.AddCsrfAsync(owner);
        var group = await PostId(owner, "/api/groups", new { name = "Group", parentId = (Guid?)null });
        var resource = await PostId(owner, "/api/resources", new { name = "Resource", groupId = group });
        await PostId(owner, "/api/grants", new { targetKind = 1, targetId = resource, subjectKind = 0, provider = "ad", subjectId = "user", permissions = 7 });
        var secret = await PostId(owner, $"/api/resources/{resource}/secrets", new { title = "Account", payload = new { username = "u", password = "p", notes = "n" } });
        using var other = host.Client("outsider"); await host.AddCsrfAsync(other);
        Assert.Equal("[]", await other.GetStringAsync("/api/resources"));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"/api/secrets/{secret}/reveal", new { })).StatusCode);
        Assert.Contains(host.Transport.Events, x => x.Action == "Access.Denied" && x.ActorId == "outsider");
    }

    [Fact]
    public async Task LicenseApiPreservesMetadataAndEnforcesHistoryAndExplicitSecretChanges()
    {
        await using var host = new TestWebHost(); using var owner = host.Client("user"); await host.AddCsrfAsync(owner);
        var group = await PostId(owner, "/api/groups", new { name = "Group", parentId = (Guid?)null });
        var resource = await PostId(owner, "/api/resources", new { name = "Resource", groupId = group });
        await PostId(owner, "/api/grants", new { targetKind = 1, targetId = resource, subjectKind = 0, provider = "ad", subjectId = "user", permissions = 7 });
        var license = await PostId(owner, "/api/licenses", new { resourceId = resource, product = "Product", vendor = "Vendor", purchaseReference = "Reference", seats = 2, secretChange = 1, payload = new { licenseKey = "api-secret-key", notes = "api-private-notes" } });
        var update = new { resourceId = resource, product = "Renamed", vendor = "Vendor", purchaseReference = "Reference", seats = 3, revision = 1 };
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/licenses/{license}/update", update)).StatusCode);
        var metadata = await owner.GetStringAsync($"/api/resources/{resource}/licenses");
        Assert.DoesNotContain("api-secret", metadata); Assert.DoesNotContain("envelope", metadata, StringComparison.OrdinalIgnoreCase);
        var versions = await owner.GetFromJsonAsync<JsonElement>($"/api/licenses/{license}/versions"); Assert.Equal(1, versions.GetArrayLength());
        using var outsider = host.Client("outsider"); await host.AddCsrfAsync(outsider);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/licenses/{license}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync($"/api/licenses/{license}/reveal", new { version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync($"/api/licenses/{license}/restore", new { version = 1, revision = 2 })).StatusCode);
        var invalid = new { resourceId = resource, product = "Product", vendor = "Vendor", purchaseReference = "Reference", seats = 3, revision = 2, secretChange = 1, payload = new { licenseKey = "", notes = "" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/licenses/{license}/update", invalid)).StatusCode);
        Assert.Contains("api-secret-key", await (await owner.PostAsJsonAsync($"/api/licenses/{license}/reveal", new { version = 1 })).Content.ReadAsStringAsync());
    }

    private static async Task<Guid> PostId(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}

internal sealed class TestWebHost(bool preview = false) : WebApplicationFactory<Program>
{
    private readonly string setupDirectory = Path.Combine(Path.GetTempPath(), "valvra-setup-tests-" + Guid.NewGuid());
    // Keep one anchor open, but give every request its own connection. Sharing a
    // SqliteConnection instance is unsafe when the UI loads metadata concurrently.
    private readonly SqliteConnection connection = new($"Data Source=valvra_web_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
    private readonly SpyCipher cipher = new();
    private readonly MemoryCheckpointStore checkpoints = new();
    private readonly TestIntegritySigner integritySigner = new();
    private readonly TestAuditSigner auditSigner = new();
    private readonly IntegrityOptions integrityOptions = new() { InstallationId = Guid.NewGuid() };
    public RecordingTransport Transport { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Valvra.slnx"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Valvra source root missing.");
        builder.UseContentRoot(Path.Combine(root.FullName, "src", "Valvra.Web"));
        connection.Open(); builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<VaultDbContext>(); services.RemoveAll<DbContextOptions<VaultDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<VaultDbContext>>();
            services.AddDbContext<VaultDbContext>(options => options.UseSqlite(connection.ConnectionString));
            services.RemoveAll<IDirectoryProvider>(); services.AddSingleton<IDirectoryProvider>(new FakeDirectory {ReportGlobalRoles=false});
            services.RemoveAll<Valvra.Web.Administration.IApplicationSettingsStore>();
            services.AddSingleton<Valvra.Web.Administration.IApplicationSettingsStore, MemoryApplicationSettings>();
            services.RemoveAll<Valvra.Web.Administration.IDirectoryConfigurationProbe>();
            services.AddSingleton<Valvra.Web.Administration.IDirectoryConfigurationProbe, FakeDirectoryProbe>();
            services.RemoveAll<ISecretCipher>(); services.AddSingleton<ISecretCipher>(cipher);
            services.RemoveAll<IntegrityOptions>(); services.AddSingleton(integrityOptions);
            services.RemoveAll<IIntegrityCheckpointStore>(); services.AddSingleton<IIntegrityCheckpointStore>(checkpoints);
            services.RemoveAll<IIntegritySigner>(); services.AddSingleton<IIntegritySigner>(integritySigner);
            services.RemoveAll<IAuditSigner>(); services.AddSingleton<IAuditSigner>(auditSigner);
            services.RemoveAll<IAuditTransport>(); services.AddSingleton<IAuditTransport>(Transport);
            services.RemoveAll<IAuditReader>(); services.AddSingleton<IAuditReader>(new TestAuditReader(Transport));
            services.AddSingleton(new TestIdentityOptions(preview ? "user" : null));
            if (preview)
            {
                services.AddSingleton<IStartupFilter, LoopbackPreviewScheme>();
                // Browser suites share one synthetic identity and exceed a normal
                // user's request budget. Only this loopback test adapter disables
                // throttling; ordinary security tests retain production limits.
                services.PostConfigure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(options => options.GlobalLimiter = null);
                services.PostConfigure<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>(options =>
                {
                    options.Cookie.Name = "Valvra-Browser-Test-CSRF";
                    options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None;
                });
            }
            var store = new SetupConfigurationStore(setupDirectory); store.Initialize();
            services.RemoveAll<SetupConfigurationStore>(); services.AddSingleton(store);
            services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
            services.PostConfigure<AuthenticationOptions>(options => { options.DefaultAuthenticateScheme = "Test"; options.DefaultChallengeScheme = "Test"; });
            // TestServer has no Negotiate connection state; use a test-only scheme provider.
            services.RemoveAll<IAuthenticationSchemeProvider>();
            services.AddSingleton<IAuthenticationSchemeProvider>(_ =>
            {
                var authentication = new AuthenticationOptions { DefaultAuthenticateScheme = "Test", DefaultChallengeScheme = "Test" };
                authentication.AddScheme("Test", scheme => scheme.HandlerType = typeof(TestAuthentication));
                return new AuthenticationSchemeProvider(Options.Create(authentication));
            });
        });
    }
    public HttpClient Client(string? id)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var scope = Services.CreateScope(); scope.ServiceProvider.GetRequiredService<VaultDbContext>().Database.EnsureCreated();
        if (checkpoints.Value is null)
        {
            var integrity = scope.ServiceProvider.GetRequiredService<VaultIntegrity>(); integrity.InitializeEmptyAsync(default).GetAwaiter().GetResult();
            integrity.RunAsync(async () => {
                var db = scope.ServiceProvider.GetRequiredService<VaultDbContext>();
                db.Grants.Add(new AccessGrant { TargetKind = TargetKind.System, TargetId = integrity.InstallationId, SubjectKind = SubjectKind.User,
                    Provider = "ad", SubjectId = "user", Permissions = (VaultPermission)(int)(GlobalRole.AccessAdministrator | GlobalRole.SystemAdministrator | GlobalRole.Auditor), Revision = 1 });
                await integrity.SaveAsync(default);
            }, default).GetAwaiter().GetResult();
        }
        if (id is not null) client.DefaultRequestHeaders.Add("X-Test-Identity", id);
        return client;
    }
    public async Task AddCsrfAsync(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
    }
    public override async ValueTask DisposeAsync() { await base.DisposeAsync(); await connection.DisposeAsync(); cipher.Dispose(); integritySigner.Dispose(); auditSigner.Dispose(); if (Directory.Exists(setupDirectory)) Directory.Delete(setupDirectory, true); }
}

internal sealed record TestIdentityOptions(string? DefaultSubject);
// Browser test adapter, excluded from production. No real identities or credentials are used.
internal sealed class LoopbackPreviewScheme : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
            { context.Response.StatusCode = 403; return; }
            context.Request.Scheme = "https";
            await continuation(context);
        });
        next(app);
    };
}
internal sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TestIdentityOptions identityOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = Request.Headers["X-Test-Identity"].FirstOrDefault() ?? identityOptions.DefaultSubject;
        if (id is null) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, id), new Claim(ClaimTypes.Name, id)], "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}

internal sealed class TestAuditReader(RecordingTransport transport) : IAuditReader
{
    public Task<AuditTargetOptions> ReadTargetsAsync(Actor actor, CancellationToken ct)
    {
        if (!actor.IsEnabled || !actor.IsAuditor) throw new AccessDeniedException();
        var scopes = transport.Events.Where(x => x.Scope is not null).Select(x => x.Scope!).ToArray();
        return Task.FromResult(new AuditTargetOptions(
            scopes.Where(x => x.ResourceId is not null).GroupBy(x => x.ResourceId!.Value).Select(x => new AuditTargetOption(x.Key, x.Last().ResourceName!)).ToArray(),
            scopes.SelectMany(x => x.GroupPath).GroupBy(x => x.Id).Select(x => new AuditTargetOption(x.Key, x.Last().Name)).ToArray()));
    }
    public Task<IReadOnlyList<AuditEventView>> ReadAsync(Actor actor, AuditQuery query, CancellationToken ct)
    {
        if (!actor.IsEnabled || !actor.IsAuditor) throw new AccessDeniedException();
        if (query.Offset < 0 || query.Offset > 1000000) throw new VaultValidationException("Ogiltig sidposition.");
        return Task.FromResult<IReadOnlyList<AuditEventView>>(transport.Events.OrderByDescending(x => x.Timestamp)
            .Where(x => (query.From is null || x.Timestamp >= query.From) && (query.To is null || x.Timestamp < query.To)
                && (query.Action is null || x.Action == query.Action) && (query.ActorId is null || x.ActorId == query.ActorId)
                && (query.TargetId is null || x.TargetId == query.TargetId) && query.MatchesScope(x))
            .Skip(query.Offset).Take(200).Select(x => new AuditEventView(x, AuditSignature.Hash(x), "test-key", true)).ToArray());
    }
}
