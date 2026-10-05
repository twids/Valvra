using Valvra.Core;
using Valvra.Infrastructure.Auditing;

namespace Valvra.Web;

public sealed class CurrentActor(IIdentityProvider identity, IDirectoryProvider directory, AuditService audit)
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
        return actor;
    }

    public async Task<T> RunAsync<T>(HttpContext context, Func<Actor, Task<T>> operation)
    {
        var user = await GetAsync(context);
        try { return await operation(user); }
        catch (AccessDeniedException)
        {
            await audit.RecordAsync(user, "Access.Denied", null, "Denied", context.TraceIdentifier, context.RequestAborted);
            throw;
        }
    }
}
