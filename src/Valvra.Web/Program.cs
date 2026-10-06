using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Configuration;
using Valvra.Infrastructure.Security;
using Valvra.Web;
using Valvra.Web.Setup;
using Microsoft.AspNetCore.DataProtection;
using Valvra.Web.Localization;
using Valvra.Web.Administration;

var builder = WebApplication.CreateBuilder(args);
var configurationDirectory = Environment.GetEnvironmentVariable("VALVRA_CONFIG_DIR") ?? Path.Combine(AppContext.BaseDirectory, "App_Data");
var installation = new SetupConfigurationStore(configurationDirectory);
if (args.Contains("--initialize-setup", StringComparer.Ordinal))
{
    Console.WriteLine("Valvra installation code (keep private): " + installation.Initialize());
    return;
}
using (var saved = installation.Load()) { if (saved is not null) builder.Configuration.AddJsonStream(saved); }
builder.Services.AddSingleton(installation);
builder.Services.AddSingleton<IApplicationSettingsStore, ProtectedApplicationSettingsStore>();
builder.Services.AddScoped<IDirectoryConfigurationProbe, DirectoryConfigurationProbe>();
builder.Services.AddScoped<ApplicationSettingsService>();
if (OperatingSystem.IsWindows() && !builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDataProtection().SetApplicationName("Valvra")
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(configurationDirectory, "DataProtection")))
        .ProtectKeysWithDpapi(protectToLocalMachine: true);
}
builder.Services.AddScoped<SetupValidator>();
builder.Services.AddValvra(builder.Configuration);
builder.Services.AddRazorPages(options =>
{
    // Explicit UI routes share the authenticated shell. Never rewrite API/static paths.
    foreach (var route in new[] { "/resources", "/resources/{resourceId}", "/groups", "/groups/{groupId}", "/groups/{groupId}/resources/{resourceId}", "/licenses", "/audit", "/settings" })
        options.Conventions.AddPageRoute("/Index", route);
});
builder.Services.AddSingleton<UiText>();
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-Valvra-CSRF";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddScoped<CurrentActor>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseRequestLocalization(UiText.Options());
// Integration tests replace infrastructure and run in a separate test environment.
// There is no runtime simulated identity or demo authentication switch.
if (!app.Environment.IsEnvironment("Testing"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Valvra's AD/IIS deployment requires Windows.");
    if (installation.HasSettings) ValvraRegistration.ValidateProduction(app.Configuration);
    else if (!installation.CanConfigure) throw new InvalidOperationException("Run Valvra.Web --initialize-setup before first startup. See README.");
}
var installedAtStartup = installation.HasSettings || app.Environment.IsEnvironment("Testing");
if (args.Contains("--recover-integrity", StringComparer.Ordinal))
{
    if (!OperatingSystem.IsWindows() || !installation.HasSettings || !args.Contains("--discard-uncommitted", StringComparer.Ordinal))
        throw new InvalidOperationException("Explicit installed operator recovery requires --recover-integrity --discard-uncommitted. See SECURITY-MODEL.");
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    using var scope = app.Services.CreateScope();
    var actor = await scope.ServiceProvider.GetRequiredService<IDirectoryProvider>().ResolveAsync(identity.User?.Value ?? throw new AccessDeniedException(), CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<VaultIntegrity>().RecoverUncommittedAsync(actor, CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<AuditService>().RecordAsync(actor, "Integrity.RecoverUncommitted", null, "OperatorConfirmed", "operator-recovery", CancellationToken.None);
    Console.WriteLine("Verified previous checkpoint restored. No data was re-baselined. Review the audit and interrupted operation before restarting IIS.");
    return;
}
if (args.Contains("--rewrap-keys", StringComparer.Ordinal))
{
    if (!OperatingSystem.IsWindows() || !installation.HasSettings) throw new InvalidOperationException("Key rotation requires an installed Windows deployment.");
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    var sid = identity.User?.Value ?? throw new AccessDeniedException();
    using var scope = app.Services.CreateScope();
    var actor = await scope.ServiceProvider.GetRequiredService<IDirectoryProvider>().ResolveAsync(sid, CancellationToken.None);
    var count = await scope.ServiceProvider.GetRequiredService<Valvra.Infrastructure.Services.KeyRotationService>().RewrapAsync(actor, CancellationToken.None);
    Console.WriteLine($"Rewrapped {count} encrypted records. Retain old keys for backup recovery.");
    return;
}
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    if (!context.Request.IsHttps) { context.Response.StatusCode = 400; return; }
    context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        if (app.Environment.IsEnvironment("Testing") && ex is not (AccessDeniedException or VaultValidationException or VaultConflictException or VaultUnavailableException or DbUpdateConcurrencyException or CryptographicException)) throw;
        if (context.Response.HasStarted) throw;
        var (status, message) = ex switch
        {
            AccessDeniedException => (403, "Åtkomst nekad."),
            VaultValidationException => (400, app.Services.GetRequiredService<UiText>().Validation(ex.Message)),
            VaultConflictException or DbUpdateConcurrencyException => (409, "Posten har ändrats. Ladda om innan du försöker igen."),
            VaultUnavailableException or CryptographicException => (503, "Säkerhetskontroll eller audit är inte tillgänglig. Kontrollera aktuell post innan du försöker igen."),
            _ => (500, "Operationen kunde inte slutföras. Kontrollera aktuell post innan du försöker igen.")
        };
        // Log only classification/correlation, never exceptions that could contain SQL parameters or credentials.
        app.Logger.LogWarning("Request failed: {Classification}; correlation {Correlation}", ex.GetType().Name, context.TraceIdentifier);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = app.Services.GetRequiredService<UiText>().Get(message), correlationId = context.TraceIdentifier });
    }
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (!installedAtStartup && !context.Request.Path.StartsWithSegments("/setup") && !context.Request.Path.StartsWithSegments("/api/setup"))
    {
        if (context.Request.Path.StartsWithSegments("/api")) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = app.Services.GetRequiredService<UiText>().Get("Installationen måste slutföras och applikationen startas om.") }); return; }
        context.Response.Redirect("/setup"); return;
    }
    await next(context);
});
app.Use(async (context, next) =>
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
        && !HttpMethods.IsOptions(context.Request.Method))
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = app.Services.GetRequiredService<UiText>().Get("Ogiltig säkerhetstoken. Ladda om sidan.") }); return; }
    }
    await next(context);
});
app.MapRazorPages();
app.MapVaultApi();
app.MapSetupApi();
app.MapSettingsApi();
app.Run();

public partial class Program;
