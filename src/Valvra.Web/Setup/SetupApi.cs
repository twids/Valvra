using Microsoft.AspNetCore.Antiforgery;
using Valvra.Core;

namespace Valvra.Web.Setup;

public static class SetupApi
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    public static void MapSetupApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/setup").RequireAuthorization();
        group.MapGet("/status", (SetupConfigurationStore store, IAntiforgery csrf, HttpContext context) =>
            Results.Ok(new { installed = store.HasSettings, canConfigure = store.CanConfigure, csrfToken = csrf.GetAndStoreTokens(context).RequestToken }));
        group.MapPost("/validate", async (SetupRequest body, SetupConfigurationStore store, SetupValidator validator, IIdentityProvider identity, HttpContext context) =>
        {
            RequireToken(store, context);
            await SetupLock.WaitAsync(context.RequestAborted);
            try
            {
                RequireToken(store, context);
                var sid = identity.GetSubjectId(context.User) ?? throw new AccessDeniedException();
                var result = await validator.ValidateAsync(body, sid, context.RequestAborted);
                return Results.Ok(new { passed = result.Passed, checks = result.Checks });
            }
            finally { SetupLock.Release(); }
        });
        group.MapPost("/finish", async (SetupRequest body, SetupConfigurationStore store, SetupValidator validator, IIdentityProvider identity, HttpContext context) =>
        {
            RequireToken(store, context);
            await SetupLock.WaitAsync(context.RequestAborted);
            try
            {
                RequireToken(store, context);
                var sid = identity.GetSubjectId(context.User) ?? throw new AccessDeniedException();
                var result = await validator.ValidateAsync(body, sid, context.RequestAborted);
                if (!result.Passed) return Results.BadRequest(new { error = "Installationskontrollerna måste passera innan konfigurationen sparas.", checks = result.Checks });
                store.Save(result.Settings);
                return Results.Ok(new { saved = true, restartRequired = true });
            }
            finally { SetupLock.Release(); }
        });
    }
    private static void RequireToken(SetupConfigurationStore store, HttpContext context)
    { if (!store.ValidateToken(context.Request.Headers["X-SETUP-TOKEN"].ToString())) throw new AccessDeniedException(); }
}
