using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Data;

namespace Valvra.Infrastructure.Auditing;

// Creation intents need context before the new object exists in the database.
public sealed record AuditScopeHint(Guid? ResourceId = null, Guid? GroupId = null, string? Name = null, Guid? ParentId = null);

internal static class AuditScopeCapture
{
    public static async Task<AuditScope?> CaptureAsync(VaultDbContext db, Guid? target, AuditScopeHint? hint, CancellationToken ct)
    {
        VaultResource? resource = null;
        ResourceGroup? group = null;
        if (hint?.ResourceId is { } resourceId)
            resource = await db.Resources.FindAsync([resourceId], ct)
                ?? (hint.Name is not null && hint.GroupId is { } resourceGroup
                    ? new VaultResource { Id = resourceId, Name = hint.Name, GroupId = resourceGroup }
                    : throw new VaultUnavailableException("Resursens auditkontext saknas."));
        else if (hint?.GroupId is { } groupId)
            group = await db.Groups.FindAsync([groupId], ct)
                ?? (hint.Name is not null ? new ResourceGroup { Id = groupId, Name = hint.Name, ParentId = hint.ParentId }
                    : throw new VaultUnavailableException("Gruppens auditkontext saknas."));
        else if (target is { } id)
        {
            resource = await db.Resources.FindAsync([id], ct);
            group = resource is null ? await db.Groups.FindAsync([id], ct) : null;
            if (resource is null && group is null)
            {
                var secret = await db.Secrets.FindAsync([id], ct);
                var license = secret is null ? await db.Licenses.FindAsync([id], ct) : null;
                var assignment = secret is null && license is null ? await db.Assignments.FindAsync([id], ct) : null;
                if (assignment is not null) license = await db.Licenses.FindAsync([assignment.LicenseId], ct);
                var ownerResource = secret?.ResourceId ?? license?.ResourceId;
                if (ownerResource is { } ownerId) resource = await db.Resources.FindAsync([ownerId], ct)
                    ?? throw new VaultUnavailableException("Resursens auditkontext saknas.");
            }
        }
        if (resource is not null) group = await db.Groups.FindAsync([resource.GroupId], ct)
            ?? throw new VaultUnavailableException("Gruppens auditkontext saknas.");
        if (group is null) return null; // Identity, setup and other service-wide events.
        var immediateGroup = group.Id;
        var path = new List<AuditScopeGroup>(); var visited = new HashSet<Guid>();
        while (true)
        {
            if (!visited.Add(group.Id) || path.Count >= 129) throw new VaultUnavailableException("Ogiltigt gruppträd i auditkontexten.");
            path.Add(new(group.Id, group.Name));
            if (group.ParentId is not { } parentId) break;
            group = await db.Groups.FindAsync([parentId], ct) ?? throw new VaultUnavailableException("Gruppens auditkontext saknas.");
        }
        return new(resource?.Id, resource?.Name, immediateGroup, path.ToArray());
    }
}
