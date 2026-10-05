using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Data;

namespace Valvra.Infrastructure.Authorization;

public sealed class AccessService(VaultDbContext db, TimeProvider clock)
{
    public async Task<HashSet<Guid>> GroupPathAsync(Guid groupId, CancellationToken ct)
    {
        var path = new HashSet<Guid>();
        Guid? current = groupId;
        while (current is { } id)
        {
            if (!path.Add(id) || path.Count > 128) throw new VaultUnavailableException("Ogiltig resurshierarki.");
            var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new AccessDeniedException();
            current = group.ParentId;
        }
        return path;
    }

    public async Task<VaultPermission> EffectiveAsync(Actor actor, TargetKind kind, Guid id, CancellationToken ct)
    {
        var (path, resourceId) = await ResolveTargetAsync(kind, id, ct);
        var grants = await db.Grants.AsNoTracking().Where(x => path.Contains(x.TargetId)
            && x.TargetKind == TargetKind.Group || x.TargetKind == TargetKind.Resource && x.TargetId == resourceId).ToListAsync(ct);
        return PermissionEvaluator.Effective(actor, grants, path, resourceId, clock.GetUtcNow());
    }

    public async Task<bool> CanManageAsync(Actor actor, TargetKind kind, Guid id, CancellationToken ct)
    {
        var (path, resourceId) = await ResolveTargetAsync(kind, id, ct);
        var grants = await db.Grants.AsNoTracking().Where(x => path.Contains(x.TargetId)
            && x.TargetKind == TargetKind.Group || x.TargetKind == TargetKind.Resource && x.TargetId == resourceId).ToListAsync(ct);
        var owners = await db.Owners.AsNoTracking().Where(x => path.Contains(x.TargetId)
            && x.TargetKind == TargetKind.Group || x.TargetKind == TargetKind.Resource && x.TargetId == resourceId).ToListAsync(ct);
        return PermissionEvaluator.CanManage(actor, owners, path, resourceId,
            PermissionEvaluator.Effective(actor, grants, path, resourceId, clock.GetUtcNow()));
    }

    public async Task RequireAsync(Actor actor, TargetKind kind, Guid id, VaultPermission required, CancellationToken ct)
    {
        if (required == VaultPermission.ManageAccess)
        {
            if (!await CanManageAsync(actor, kind, id, ct)) throw new AccessDeniedException();
        }
        else if (!(await EffectiveAsync(actor, kind, id, ct)).HasFlag(required)) throw new AccessDeniedException();
    }

    private async Task<(HashSet<Guid> Path, Guid? ResourceId)> ResolveTargetAsync(TargetKind kind, Guid id, CancellationToken ct)
    {
        if (kind == TargetKind.Group) return (await GroupPathAsync(id, ct), null);
        if (kind != TargetKind.Resource) throw new AccessDeniedException();
        var resource = await db.Resources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AccessDeniedException();
        return (await GroupPathAsync(resource.GroupId, ct), resource.Id);
    }
}
