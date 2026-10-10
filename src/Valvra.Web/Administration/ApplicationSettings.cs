using System.DirectoryServices.Protocols;
using System.Text.RegularExpressions;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Identity;
using Valvra.Infrastructure.Services;
using Valvra.Web.Setup;

namespace Valvra.Web.Administration;

public sealed record DirectorySettings(long Revision, string LoginProviderId, string DirectoryProviderId,
    string Server, int Port, string LookupBaseDn, string UserSearchBaseDn, string GroupSearchBaseDn, int TimeoutSeconds)
{
    public ActiveDirectoryOptions Options() => new() { Server = Server, Port = Port, BaseDn = LookupBaseDn,
        UserSearchBaseDn = UserSearchBaseDn, GroupSearchBaseDn = GroupSearchBaseDn, TimeoutSeconds = TimeoutSeconds };
    public IdentitySelection Identity() => new() { LoginProviderId = LoginProviderId, DirectoryProviderId = DirectoryProviderId };
    public static DirectorySettings From(InstallationSettings settings) => new(settings.SettingsRevision,
        settings.Identity.LoginProviderId, settings.Identity.DirectoryProviderId, settings.ActiveDirectory.Server,
        settings.ActiveDirectory.Port, settings.ActiveDirectory.BaseDn, settings.ActiveDirectory.UserSearchBaseDn,
        settings.ActiveDirectory.GroupSearchBaseDn, settings.ActiveDirectory.TimeoutSeconds);
    public void Validate(ProviderRegistry modules)
    {
        modules.Validate(Identity());
        if (Revision < 0 || Server is null || Server.Length is < 1 or > 253
            || !Regex.IsMatch(Server, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?\z", RegexOptions.CultureInvariant)
            || Port is < 1 or > 65535 || TimeoutSeconds is < 1 or > 30)
            throw new VaultValidationException("Ange ett giltigt servernamn, port och en timeout på 1–30 sekunder.");
        if (!ValidDn(LookupBaseDn) || LookupBaseDn.Length == 0 || !ValidDn(UserSearchBaseDn) || !ValidDn(GroupSearchBaseDn))
            throw new VaultValidationException("Ange giltiga sökbaser utan kontrolltecken.");
        foreach (var value in new[] { UserSearchBaseDn, GroupSearchBaseDn })
            if (value.Length > 0 && !value.Equals(LookupBaseDn, StringComparison.OrdinalIgnoreCase)
                && !value.EndsWith("," + LookupBaseDn, StringComparison.OrdinalIgnoreCase))
                throw new VaultValidationException("Användar- och gruppbasen måste ligga inom katalogens uppslagsbas.");
    }
    private static bool ValidDn(string value) => value is not null && value.Length <= 2048 && !value.Any(char.IsControl)
        && (value.Length == 0 || value.Contains('='));
}
public interface IApplicationSettingsStore
{
    DirectorySettings Read();
    DirectorySettings Update(DirectorySettings candidate);
}
public sealed class ProtectedApplicationSettingsStore(SetupConfigurationStore setup) : IApplicationSettingsStore
{
    public DirectorySettings Read() => DirectorySettings.From(setup.ReadSettings() ?? throw new VaultUnavailableException("Installationen är inte slutförd."));
    public DirectorySettings Update(DirectorySettings candidate) => setup.UpdateDirectory(candidate);
}
public sealed record DirectoryProbeResult(string Provider, string SubjectId, IReadOnlyList<DirectorySubject> Users, IReadOnlyList<DirectorySubject> Groups);
public interface IDirectoryConfigurationProbe
{
    Task<DirectoryProbeResult> TestAsync(DirectorySettings candidate, string subjectId, string query, CancellationToken ct);
}
public sealed class DirectoryConfigurationProbe(ProviderRegistry modules) : IDirectoryConfigurationProbe
{
    public async Task<DirectoryProbeResult> TestAsync(DirectorySettings candidate, string subjectId, string query, CancellationToken ct)
    {
        candidate.Validate(modules);
        if (query is null || query.Length is < 2 or > 128) throw new VaultValidationException("Ange 2–128 tecken för katalogsökning.");
        try
        {
            var provider = modules.Directory(candidate.DirectoryProviderId).Create(candidate.Options());
            var account = await provider.ResolveAsync(subjectId, ct);
            if (!account.IsEnabled || account.SubjectId != subjectId || account.Provider != provider.ProviderId) throw new AccessDeniedException();
            var users = await provider.SearchAsync(query, SubjectKind.User, ct);
            var groups = await provider.SearchAsync(query, SubjectKind.Group, ct);
            return new(account.Provider, account.SubjectId, users.Take(5).ToArray(), groups.Take(5).ToArray());
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException or PlatformNotSupportedException)
        { throw new VaultValidationException("Kataloganslutningen kunde inte verifieras. Kontrollera server, certifikat och sökbaser."); }
    }
}
public sealed record SettingsUpdateRequest(DirectorySettings Settings, string Query);
public sealed record GlobalRoleRequest(string Provider, string SubjectId, SubjectKind Kind, GlobalRole Roles, long Revision);
public sealed class ApplicationSettingsService(IApplicationSettingsStore store, ProviderRegistry modules, DirectoryConfiguration runtime,
    IDirectoryConfigurationProbe probe, GlobalRoleService globals, AuditService audit, Valvra.Infrastructure.Security.VaultIntegrity integrity)
{
    public async Task<DirectorySettings> ReadAsync(Actor actor, CancellationToken ct)
    { await globals.RequireAsync(actor, GlobalRole.SystemAdministrator, ct); return store.Read(); }
    private DirectorySettings Validate(DirectorySettings candidate)
    {
        candidate.Validate(modules); var current = store.Read();
        if (candidate.LoginProviderId != current.LoginProviderId || candidate.DirectoryProviderId != current.DirectoryProviderId)
            throw new VaultValidationException("Providerbyte kräver en operatörskontrollerad koppling av befintliga identiteter.");
        if (candidate.Revision != current.Revision) throw new VaultConflictException("Posten har ändrats. Ladda om före nytt försök.");
        return current;
    }
    private static void ValidateRequest(SettingsUpdateRequest request)
    {
        if (request?.Settings is null || request.Query is null || request.Query.Trim().Length is < 2 or > 128)
            throw new VaultValidationException("Ange 2–128 tecken för katalogsökning.");
    }
    public async Task<DirectoryProbeResult> TestAsync(Actor actor, SettingsUpdateRequest request, string correlation, CancellationToken ct)
    {
        await globals.RequireAsync(actor, GlobalRole.SystemAdministrator, ct); ValidateRequest(request); Validate(request.Settings);
        try
        {
            var result = await probe.TestAsync(request.Settings, actor.SubjectId, request.Query, ct);
            if (result.SubjectId != actor.SubjectId || result.Provider != actor.Provider) throw new AccessDeniedException();
            await audit.RecordAsync(actor, "Settings.DirectoryTest", null, "Verified", correlation, ct); return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { await audit.RecordAsync(actor, "Settings.DirectoryTest", null, "Failed", correlation, ct); throw; }
    }
    public Task<DirectorySettings> UpdateAsync(Actor actor, SettingsUpdateRequest request, string correlation, CancellationToken ct) => integrity.RunAsync(async () =>
    {
        await globals.RequireAsync(actor, GlobalRole.SystemAdministrator, ct); ValidateRequest(request); Validate(request.Settings);
        return await audit.MutationAsync(actor, "Settings.DirectoryUpdate", null, correlation, async token =>
        {
            await globals.RequireAsync(actor, GlobalRole.SystemAdministrator, token); Validate(request.Settings);
            var result = await probe.TestAsync(request.Settings, actor.SubjectId, request.Query, token);
            if (result.SubjectId != actor.SubjectId || result.Provider != actor.Provider) throw new AccessDeniedException();
            var saved = store.Update(request.Settings); runtime.Apply(saved.Options()); return saved;
        }, ct, System.Text.Json.JsonSerializer.Serialize(request.Settings));
    }, ct);
}
public static class SettingsApi
{
    public static void MapSettingsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/settings").RequireAuthorization();
        api.MapGet("/directory", (HttpContext ctx, CurrentActor current, ApplicationSettingsService settings) =>
            current.RunAsync(ctx, async actor => Results.Ok(await settings.ReadAsync(actor, ctx.RequestAborted))));
        api.MapGet("/providers", (HttpContext ctx, CurrentActor current, GlobalRoleService globals, ProviderRegistry modules) =>
            current.RunAsync(ctx, async actor => { await globals.RequireAsync(actor, GlobalRole.SystemAdministrator, ctx.RequestAborted);
                return Results.Ok(new { logins = modules.Logins, directories = modules.Directories }); }));
        api.MapPost("/directory/test", (SettingsUpdateRequest body, HttpContext ctx, CurrentActor current, ApplicationSettingsService settings) =>
            current.RunAsync(ctx, async actor => Results.Ok(await settings.TestAsync(actor, body, ctx.TraceIdentifier, ctx.RequestAborted))));
        api.MapPost("/directory", (SettingsUpdateRequest body, HttpContext ctx, CurrentActor current, ApplicationSettingsService settings) =>
            current.RunAsync(ctx, async actor => Results.Ok(await settings.UpdateAsync(actor, body, ctx.TraceIdentifier, ctx.RequestAborted))));
        api.MapGet("/roles", (HttpContext ctx, CurrentActor current, GlobalRoleService globals) =>
            current.RunAsync(ctx, async actor => Results.Ok(await globals.ReadAsync(actor, ctx.RequestAborted))));
        api.MapPost("/roles", (GlobalRoleRequest body, HttpContext ctx, CurrentActor current, GlobalRoleService globals) =>
            current.RunAsync(ctx, async actor => Results.Ok(new { id = await globals.SetAsync(actor, body.Provider, body.SubjectId, body.Kind,
                body.Roles, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted) })));
        api.MapGet("/people", (string query, HttpContext ctx, CurrentActor current, GlobalRoleService globals, IDirectoryProvider directory) =>
            current.RunAsync(ctx, async actor => { await globals.RequireAsync(actor, GlobalRole.AccessAdministrator, ctx.RequestAborted);
                return Results.Ok(await directory.SearchAsync(query, SubjectKind.User, ctx.RequestAborted)); }));
    }
}
