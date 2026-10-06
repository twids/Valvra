using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Valvra.Demo;

public partial class DemoProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
        // This executable can never connect to a vault. Reject accidental production configuration.
        foreach (var name in new[] { "ConnectionStrings", "VaultDatabase", "AuditDatabase", "KeyProtection", "Integrity", "ActiveDirectory" })
            if (builder.Configuration.GetSection(name).Exists())
                throw new InvalidOperationException("Production configuration is forbidden in the synthetic demo.");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VALVRA_CONFIG_DIR")))
            throw new InvalidOperationException("Production configuration directory is forbidden in the synthetic demo.");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter("synthetic-demo", _ => new() { PermitLimit = 20, QueueLimit = 0 }));
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            context.Response.Headers["X-Valvra-Demo"] = "synthetic-read-only";
            if (context.Request.ContentLength > 256) { context.Response.StatusCode = 413; return; }
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                && !(HttpMethods.IsPost(context.Request.Method) && DemoData.RevealPaths.Contains(context.Request.Path.Value!)))
            { context.Response.StatusCode = 405; return; }
            await next();
        });
        app.UseRateLimiter();
        app.UseStaticFiles();
        app.MapGet("/", () => Results.File(Path.Combine(app.Environment.WebRootPath, "index.html"), "text/html; charset=utf-8"));
        foreach (var route in new[] { "/resources", "/resources/{resourceId}", "/groups", "/groups/{groupId}", "/groups/{groupId}/resources/{resourceId}", "/licenses", "/audit" })
            app.MapGet(route, () => Results.File(Path.Combine(app.Environment.WebRootPath, "index.html"), "text/html; charset=utf-8"));
        app.MapGet("/api/session", () => new { displayName = "Demobesökare · endast exempeldata", isAccessAdministrator = false, isAuditor = false, csrfToken = "public-synthetic-demo" });
        app.MapGet("/api/groups", () => new[] { new { id = DemoData.Group, parentId = (string?)null, name = "Exempelinfrastruktur", revision = 1, canManage = false } });
        app.MapGet("/api/resources", () => new[] { new { id = DemoData.Resource, groupId = DemoData.Group, name = "Exempeldatabasserver", revision = 1, permissions = 3, canManage = false } });
        app.MapGet("/api/ldap-profiles", () => Array.Empty<object>());
        app.MapGet("/api/resources/{id}/secrets", (string id) => id == DemoData.Resource
            ? Results.Ok(new[] { new { id = DemoData.Secret, title = "Exempelkonto", currentVersion = 1, revision = 1, ldapProfileId = (string?)null } }) : Results.NotFound());
        app.MapGet("/api/resources/{id}/licenses", (string id) => id == DemoData.Resource
            ? Results.Ok(new[] { new { id = DemoData.License, resourceId = DemoData.Resource, product = "Exempelprogram", vendor = "Exempelleverantör", purchaseReference = "DEMO-ORDER-001", seats = 10, assignedSeats = 3, expiresAt = "2027-12-31T00:00:00Z", revision = 1, currentVersion = 1 } }) : Results.NotFound());
        app.MapGet("/api/secrets/{id}/versions", (string id) => id == DemoData.Secret ? Results.Ok(DemoData.Versions) : Results.NotFound());
        app.MapGet("/api/licenses/{id}/versions", (string id) => id == DemoData.License ? Results.Ok(DemoData.Versions) : Results.NotFound());
        app.MapGet("/api/licenses/{id}/assignments", (string id) => id == DemoData.License
            ? Results.Ok(new[] { new { id = "50000000-0000-0000-0000-000000000001", resourceId = DemoData.Resource, userId = (string?)null, seats = 3, createdAt = "2026-10-05T12:00:00Z" } }) : Results.NotFound());
        app.MapPost("/api/secrets/{id}/reveal", (string id, JsonElement body) => id != DemoData.Secret ? Results.NotFound()
            : !DemoData.ValidReveal(body) ? Results.BadRequest() : Results.Ok(new { username = "demo-user", password = "DEMO-EXAMPLE-NOT-A-REAL-PASSWORD", notes = "Fasta syntetiska data. Ingen anslutning till ett valv." }));
        app.MapPost("/api/licenses/{id}/reveal", (string id, JsonElement body) => id != DemoData.License ? Results.NotFound()
            : !DemoData.ValidReveal(body) ? Results.BadRequest() : Results.Ok(new { licenseKey = "DEMO-0000-EXAMPLE-0000", notes = "Exempelnyckel utan funktion eller värde." }));
        app.Run();

    }
}
internal static class DemoData
{
    public const string Group = "10000000-0000-0000-0000-000000000001";
    public const string Resource = "20000000-0000-0000-0000-000000000001";
    public const string Secret = "30000000-0000-0000-0000-000000000001";
    public const string License = "40000000-0000-0000-0000-000000000001";
    public static readonly HashSet<string> RevealPaths = [$"/api/secrets/{Secret}/reveal", $"/api/licenses/{License}/reveal"];
    public static readonly object[] Versions = [new { version = 1, createdAt = "2026-10-05T12:00:00Z", createdBy = "Syntetisk demo" }];
    public static bool ValidReveal(JsonElement body) => body.ValueKind == JsonValueKind.Object
        && body.EnumerateObject().All(x => x.Name switch
        {
            "copy" => x.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "version" => x.Value.ValueKind == JsonValueKind.Null || x.Value.ValueKind == JsonValueKind.Number && x.Value.TryGetInt32(out var value) && value == 1,
            _ => false
        });
}
