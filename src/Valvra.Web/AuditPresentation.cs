using System.DirectoryServices.Protocols;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;

namespace Valvra.Web;

public static class AuditPresentation
{
    // Only identities from signature-verified history are candidates. Names are
    // current, optional display metadata; they never change the recorded ID.
    public static async Task<AuditTargetOptions> NamesAsync(Actor actor, AuditTargetOptions targets, IDirectoryProvider directory, CancellationToken ct)
    {
        if (!actor.IsEnabled || !actor.IsAuditor) throw new AccessDeniedException();
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(5));
        var actors=new List<AuditActorOption>();
        foreach(var item in (targets.Actors??[]).Take(100))
        {
            var value=item;
            if(item.Provider==directory.ProviderId&&!budget.IsCancellationRequested)
            {
                try
                {
                    var subject=await directory.FindAsync(item.Id,SubjectKind.User,budget.Token);
                    if(subject is not null)
                    {
                        if(subject.Provider!=item.Provider||subject.Id!=item.Id||subject.Kind!=SubjectKind.User)throw new AccessDeniedException();
                        value=item with{Name=subject.Name};
                    }
                }
                catch(Exception error) when(error is VaultUnavailableException or LdapException or DirectoryOperationException) { budget.Cancel(); }
                catch(OperationCanceledException) when(!ct.IsCancellationRequested) { budget.Cancel(); }
            }
            actors.Add(value);
        }
        ct.ThrowIfCancellationRequested();return targets with{Actors=actors};
    }
}
