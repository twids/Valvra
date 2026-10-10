using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Security;
using Valvra.Infrastructure.Services;

namespace Valvra.Web;

public sealed class CurrentActor(IIdentityProvider identity, IDirectoryProvider directory, AuditService audit, VaultIntegrity integrity, GlobalRoleService globals)
{
    private Actor? actor;
    public async Task<Actor> GetAsync(HttpContext context)
    {
        if (actor is not null) return actor;
        var sid = identity.GetSubjectId(context.User) ?? throw new AccessDeniedException();
        try { actor = await directory.ResolveAsync(sid, context.RequestAborted); }
        catch (Exception ex) when (ex is AccessDeniedException or VaultUnavailableException)
        {
            var rejected = new Actor(identity.ProviderId, sid, sid, new HashSet<string>(), false, false, false);
            await audit.RecordAsync(rejected, "Identity.Rejected", null, ex is AccessDeniedException ? "Denied" : "DirectoryUnavailable",
                context.TraceIdentifier, context.RequestAborted);
            throw;
        }
        if (!actor.IsEnabled) throw new AccessDeniedException();
        if (actor.Provider != identity.ProviderId || actor.Provider != directory.ProviderId || actor.SubjectId != sid) throw new AccessDeniedException();
        actor = await globals.ApplyAsync(actor, context.RequestAborted);
        return actor;
    }

    public async Task<T> RunAsync<T>(HttpContext context, Func<Actor, Task<T>> operation)
    {
        var user = await GetAsync(context);
        try { return await integrity.RunAsync(async () => await operation(await globals.ApplyAsync(user, context.RequestAborted)), context.RequestAborted); }
        catch (AccessDeniedException)
        {
            await audit.RecordAsync(user, "Access.Denied", null, "Denied", context.TraceIdentifier, context.RequestAborted);
            throw;
        }
    }
}
