using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Authorization;
using Valvra.Infrastructure.Data;

namespace Valvra.Infrastructure.Services;

public sealed record GroupView(Guid Id, Guid? ParentId, string Name, long Revision, bool CanManage);
public sealed record ResourceView(Guid Id, Guid GroupId, string Name, long Revision, VaultPermission Permissions, bool CanManage);
public sealed record SecretView(Guid Id, string Title, int CurrentVersion, long Revision, string? LdapProfileId);
public sealed record SecretVersionView(int Version, DateTimeOffset CreatedAt, string CreatedBy);
public sealed record LicenseView(Guid Id, Guid ResourceId, string Product, string Vendor, string PurchaseReference,
    int Seats, int AssignedSeats, DateTimeOffset? ExpiresAt, long Revision);
public sealed record AccessView(IReadOnlyList<AccessGrant> Grants, IReadOnlyList<ResourceOwner> Owners);

public sealed class VaultService(VaultDbContext db, AccessService access, AuditService audit, ISecretCipher cipher, TimeProvider clock,
    IDirectoryProvider directory)
{
    public async Task<IReadOnlyList<GroupView>> GroupsAsync(Actor actor, CancellationToken ct)
    {
        var result = new List<GroupView>();
        foreach (var group in await db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
        {
            var manage = await access.CanManageAsync(actor, TargetKind.Group, group.Id, ct);
            if (manage || (await access.EffectiveAsync(actor, TargetKind.Group, group.Id, ct)).HasFlag(VaultPermission.Metadata))
                result.Add(new(group.Id, group.ParentId, group.Name, group.Revision, manage));
        }
        return result;
    }

    public async Task<IReadOnlyList<ResourceView>> ResourcesAsync(Actor actor, CancellationToken ct)
    {
        var result = new List<ResourceView>();
        foreach (var resource in await db.Resources.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
        {
            var permissions = await access.EffectiveAsync(actor, TargetKind.Resource, resource.Id, ct);
            var manage = await access.CanManageAsync(actor, TargetKind.Resource, resource.Id, ct);
            if (manage || permissions.HasFlag(VaultPermission.Metadata))
                result.Add(new(resource.Id, resource.GroupId, resource.Name, resource.Revision, permissions, manage));
        }
        return result;
    }

    public async Task<Guid> CreateGroupAsync(Actor actor, string name, Guid? parentId, string correlation, CancellationToken ct)
    {
        Name(name);
        if (parentId is { } parent) await access.RequireAsync(actor, TargetKind.Group, parent, VaultPermission.ManageAccess, ct);
        else if (!actor.IsEnabled || !actor.IsAccessAdministrator) throw new AccessDeniedException();
        var id = Guid.NewGuid();
        return await audit.MutationAsync(actor, "Group.Create", id, correlation, async token =>
        {
            var fresh = await directory.ResolveAsync(actor.SubjectId, token);
            if (parentId is { } parent) await access.RequireAsync(fresh, TargetKind.Group, parent, VaultPermission.ManageAccess, token);
            else if (!fresh.IsEnabled || !fresh.IsAccessAdministrator) throw new AccessDeniedException();
            db.Groups.Add(new ResourceGroup { Id = id, Name = name.Trim(), ParentId = parentId, Revision = 1 });
            return id;
        }, ct);
    }

    public async Task<Guid> CreateResourceAsync(Actor actor, Guid groupId, string name, string correlation, CancellationToken ct)
    {
        Name(name);
        await access.RequireAsync(actor, TargetKind.Group, groupId, VaultPermission.ManageAccess, ct);
        var id = Guid.NewGuid();
        return await audit.MutationAsync(actor, "Resource.Create", id, correlation, async token =>
        {
            await RequireFreshAsync(actor, TargetKind.Group, groupId, VaultPermission.ManageAccess, token);
            db.Resources.Add(new VaultResource { Id = id, GroupId = groupId, Name = name.Trim(), Revision = 1 });
            return id;
        }, ct);
    }

    public async Task RenameGroupAsync(Actor actor, Guid id, string name, long revision, string correlation, CancellationToken ct)
    {
        Name(name);
        await access.RequireAsync(actor, TargetKind.Group, id, VaultPermission.ManageAccess, ct);
        await audit.MutationAsync(actor, "Group.Rename", id, correlation, async token =>
        {
            await RequireFreshAsync(actor, TargetKind.Group, id, VaultPermission.ManageAccess, token);
            var group = await db.Groups.SingleAsync(x => x.Id == id, token);
            Revision(group.Revision, revision);
            group.Name = name.Trim(); group.Revision++;
            return true;
        }, ct);
    }

    public async Task RenameResourceAsync(Actor actor, Guid id, string name, long revision, string correlation, CancellationToken ct)
    {
        Name(name);
        await access.RequireAsync(actor, TargetKind.Resource, id, VaultPermission.ManageAccess, ct);
        await audit.MutationAsync(actor, "Resource.Rename", id, correlation, async token =>
        {
            await RequireFreshAsync(actor, TargetKind.Resource, id, VaultPermission.ManageAccess, token);
            var resource = await db.Resources.SingleAsync(x => x.Id == id, token);
            Revision(resource.Revision, revision);
            resource.Name = name.Trim(); resource.Revision++;
            return true;
        }, ct);
    }

    public async Task<IReadOnlyList<SecretView>> SecretsAsync(Actor actor, Guid resourceId, CancellationToken ct)
    {
        await access.RequireAsync(actor, TargetKind.Resource, resourceId, VaultPermission.Metadata, ct);
        return await db.Secrets.AsNoTracking().Where(x => x.ResourceId == resourceId && !x.Deleted).OrderBy(x => x.Title)
            .Select(x => new SecretView(x.Id, x.Title, x.CurrentVersion, x.Revision, x.LdapProfileId)).ToListAsync(ct);
    }

    public async Task<Guid> CreateSecretAsync(Actor actor, Guid resourceId, string title, SecretPayload payload,
        string? ldapProfileId, string correlation, CancellationToken ct)
    {
        Name(title); ValidatePayload(payload);
        await access.RequireAsync(actor, TargetKind.Resource, resourceId, VaultPermission.Modify, ct);
        var id = Guid.NewGuid();
        var envelope = cipher.Encrypt(payload, id, 1, "secret");
        return await audit.MutationAsync(actor, "Secret.Create", id, correlation, async token =>
        {
            await RequireFreshAsync(actor, TargetKind.Resource, resourceId, VaultPermission.Modify, token);
            db.Secrets.Add(new SecretEntry { Id = id, ResourceId = resourceId, Title = title.Trim(), CurrentVersion = 1,
                Revision = 1, LdapProfileId = ldapProfileId });
            db.SecretVersions.Add(new SecretVersion { EntryId = id, Version = 1, EnvelopeJson = envelope,
                CreatedAt = clock.GetUtcNow(), CreatedBy = actor.SubjectId });
            return id;
        }, ct);
    }

    public async Task UpdateSecretAsync(Actor actor, Guid id, string title, SecretPayload payload,
        string? ldapProfileId, long revision, string correlation, CancellationToken ct)
    {
        Name(title); ValidatePayload(payload);
        var entry = await EntryAsync(id, ct);
        await access.RequireAsync(actor, TargetKind.Resource, entry.ResourceId, VaultPermission.Modify, ct);
        await audit.MutationAsync(actor, "Secret.Update", id, correlation, async token =>
        {
            await RequireFreshAsync(actor, TargetKind.Resource, entry.ResourceId, VaultPermission.Modify, token);
            Revision(entry.Revision, revision);
            entry.CurrentVersion++; entry.Revision++; entry.Title = title.Trim(); entry.LdapProfileId = ldapProfileId;
            db.SecretVersions.Add(new SecretVersion { EntryId = id, Version = entry.CurrentVersion,
                EnvelopeJson = cipher.Encrypt(payload, id, entry.CurrentVersion, "secret"),
                CreatedAt = clock.GetUtcNow(), CreatedBy = actor.SubjectId });
            return true;
        }, ct);
    }

    public async Task<SecretPayload> RevealSecretAsync(Actor actor, Guid id, int? version, string action,
        string correlation, CancellationToken ct)
    {
        var entry = await EntryAsync(id, ct);
        await access.RequireAsync(actor, TargetKind.Resource, entry.ResourceId, VaultPermission.ReadSecret, ct);
        var selectedVersion = version ?? entry.CurrentVersion;
        var secret = await db.SecretVersions.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == id && x.Version == selectedVersion, ct)
            ?? throw new VaultValidationException("Versionen finns inte.");
        await audit.FlushAsync(ct);
        // Authorized release is acknowledged externally before plaintext is produced.
        await audit.RecordAsync(actor, action, id, "ReleaseAuthorized", correlation, ct);
        actor = await directory.ResolveAsync(actor.SubjectId, ct);
        await access.RequireAsync(actor, TargetKind.Resource, entry.ResourceId, VaultPermission.ReadSecret, ct);
        return cipher.Decrypt<SecretPayload>(secret.EnvelopeJson, id, selectedVersion, "secret");
    }

    public async Task<IReadOnlyList<SecretVersionView>> VersionsAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var entry = await EntryAsync(id, ct);
        await access.RequireAsync(actor, TargetKind.Resource, entry.ResourceId, VaultPermission.Metadata, ct);
        return await db.SecretVersions.AsNoTracking().Where(x => x.EntryId == id).OrderByDescending(x => x.Version)
            .Select(x => new SecretVersionView(x.Version, x.CreatedAt, x.CreatedBy)).ToListAsync(ct);
    }

    public async Task RestoreVersionAsync(Actor actor, Guid id, int version, long revision, string correlation, CancellationToken ct)
    {
        var entry = await EntryAsync(id, ct);
        await access.RequireAsync(actor, TargetKind.Resource, entry.ResourceId, VaultPermission.Modify, ct);
        await access.RequireAsync(actor, TargetKind.Resource, entry.ResourceId, VaultPermission.ReadSecret, ct);
        var payload = await RevealSecretAsync(actor, id, version, "Secret.RestoreRead", correlation, ct);
        await UpdateSecretAsync(actor, id, entry.Title, payload, entry.LdapProfileId, revision, correlation, ct);
    }

    public async Task SetDeletedAsync(Actor actor, Guid id, bool deleted, long revision, string correlation, CancellationToken ct)
    {
        var entry = await db.Secrets.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AccessDeniedException();
        await access.RequireAsync(actor, TargetKind.Resource, entry.ResourceId, VaultPermission.Modify, ct);
        await audit.MutationAsync(actor, deleted ? "Secret.Delete" : "Secret.Undelete", id, correlation, async token =>
        {
            await RequireFreshAsync(actor, TargetKind.Resource, entry.ResourceId, VaultPermission.Modify, token);
            Revision(entry.Revision, revision); entry.Deleted = deleted; entry.Revision++;
            return true;
        }, ct);
    }

    public async Task<AccessView> AccessAsync(Actor actor, TargetKind kind, Guid id, CancellationToken ct)
    {
        await access.RequireAsync(actor, kind, id, VaultPermission.ManageAccess, ct);
        var groupId = kind == TargetKind.Group ? id : (await db.Resources.AsNoTracking().SingleAsync(x => x.Id == id, ct)).GroupId;
        var path = await access.GroupPathAsync(groupId, ct);
        var grants = await db.Grants.AsNoTracking().Where(x => x.TargetKind == TargetKind.Group && path.Contains(x.TargetId)
            || kind == TargetKind.Resource && x.TargetKind == TargetKind.Resource && x.TargetId == id).ToListAsync(ct);
        var owners = await db.Owners.AsNoTracking().Where(x => x.TargetKind == TargetKind.Group && path.Contains(x.TargetId)
            || kind == TargetKind.Resource && x.TargetKind == TargetKind.Resource && x.TargetId == id).ToListAsync(ct);
        return new(grants, owners);
    }

    public async Task<Guid> GrantAsync(Actor actor, AccessGrant grant, string correlation, CancellationToken ct)
    {
        PermissionEvaluator.ValidateGrant(grant);
        await access.RequireAsync(actor, grant.TargetKind, grant.TargetId, VaultPermission.ManageAccess, ct);
        await ValidateSubjectAsync(grant.Provider, grant.SubjectId, grant.SubjectKind, ct);
        grant.Id = Guid.NewGuid(); grant.CreatedAt = clock.GetUtcNow(); grant.GrantedBy = actor.SubjectId; grant.Revision = 1;
        return await audit.MutationAsync(actor, "Access.Grant", grant.TargetId, correlation, async token =>
        { await RequireFreshAsync(actor, grant.TargetKind, grant.TargetId, VaultPermission.ManageAccess, token); db.Grants.Add(grant); return grant.Id; }, ct, JsonSerializer.Serialize(new
        { grant.Id, grant.Provider, grant.SubjectId, grant.SubjectKind, grant.Permissions, grant.StartsAt, grant.ExpiresAt, grant.TargetKind }));
    }

    public async Task RevokeAsync(Actor actor, Guid grantId, string correlation, CancellationToken ct)
    {
        var grant = await db.Grants.SingleOrDefaultAsync(x => x.Id == grantId, ct) ?? throw new AccessDeniedException();
        await access.RequireAsync(actor, grant.TargetKind, grant.TargetId, VaultPermission.ManageAccess, ct);
        await audit.MutationAsync(actor, "Access.Revoke", grant.TargetId, correlation, async token =>
        { await RequireFreshAsync(actor, grant.TargetKind, grant.TargetId, VaultPermission.ManageAccess, token); db.Grants.Remove(grant); return true; }, ct, JsonSerializer.Serialize(new
        { grant.Id, grant.Provider, grant.SubjectId, grant.SubjectKind, grant.Permissions, grant.StartsAt, grant.ExpiresAt, grant.TargetKind }));
    }

    public async Task<Guid> AddOwnerAsync(Actor actor, ResourceOwner owner, string correlation, CancellationToken ct)
    {
        if (!Enum.IsDefined(owner.TargetKind) || !Enum.IsDefined(owner.SubjectKind)
            || string.IsNullOrWhiteSpace(owner.Provider) || string.IsNullOrWhiteSpace(owner.SubjectId))
            throw new VaultValidationException("Ogiltig ägare.");
        await access.RequireAsync(actor, owner.TargetKind, owner.TargetId, VaultPermission.ManageAccess, ct);
        await ValidateSubjectAsync(owner.Provider, owner.SubjectId, owner.SubjectKind, ct);
        owner.Id = Guid.NewGuid();
        return await audit.MutationAsync(actor, "Owner.Add", owner.TargetId, correlation, async token =>
        { await RequireFreshAsync(actor, owner.TargetKind, owner.TargetId, VaultPermission.ManageAccess, token); db.Owners.Add(owner); return owner.Id; }, ct, JsonSerializer.Serialize(new
        { owner.Id, owner.Provider, owner.SubjectId, owner.SubjectKind, owner.TargetKind }));
    }

    public async Task RemoveOwnerAsync(Actor actor, Guid ownerId, string correlation, CancellationToken ct)
    {
        var owner = await db.Owners.SingleOrDefaultAsync(x => x.Id == ownerId, ct) ?? throw new AccessDeniedException();
        await access.RequireAsync(actor, owner.TargetKind, owner.TargetId, VaultPermission.ManageAccess, ct);
        await audit.MutationAsync(actor, "Owner.Remove", owner.TargetId, correlation, async token =>
        { await RequireFreshAsync(actor, owner.TargetKind, owner.TargetId, VaultPermission.ManageAccess, token); db.Owners.Remove(owner); return true; }, ct, JsonSerializer.Serialize(new
        { owner.Id, owner.Provider, owner.SubjectId, owner.SubjectKind, owner.TargetKind }));
    }

    public async Task<AccessView> PreviewMoveAsync(Actor actor, TargetKind kind, Guid id, Guid destination, CancellationToken ct)
    {
        await access.RequireAsync(actor, kind, id, VaultPermission.ManageAccess, ct);
        await access.RequireAsync(actor, TargetKind.Group, destination, VaultPermission.ManageAccess, ct);
        var targetAccess = await AccessAsync(actor, TargetKind.Group, destination, ct);
        var currentAccess = await AccessAsync(actor, kind, id, ct);
        var directGrants = currentAccess.Grants.Where(x => x.TargetKind == kind && x.TargetId == id);
        var directOwners = currentAccess.Owners.Where(x => x.TargetKind == kind && x.TargetId == id);
        if (kind == TargetKind.Group && (await access.GroupPathAsync(destination, ct)).Contains(id))
            throw new VaultValidationException("En grupp kan inte flyttas till sitt eget underträd.");
        return new(targetAccess.Grants.Concat(directGrants).ToArray(), targetAccess.Owners.Concat(directOwners).ToArray());
    }

    public async Task MoveAsync(Actor actor, TargetKind kind, Guid id, Guid destination, long revision, string correlation, CancellationToken ct)
    {
        await PreviewMoveAsync(actor, kind, id, destination, ct);
        await audit.MutationAsync(actor, "Resource.Move", id, correlation, async token =>
        {
            var fresh = await directory.ResolveAsync(actor.SubjectId, token);
            await PreviewMoveAsync(fresh, kind, id, destination, token);
            if (kind == TargetKind.Group)
            {
                var group = await db.Groups.SingleAsync(x => x.Id == id, token);
                Revision(group.Revision, revision); group.ParentId = destination; group.Revision++;
            }
            else
            {
                var resource = await db.Resources.SingleAsync(x => x.Id == id, token);
                Revision(resource.Revision, revision); resource.GroupId = destination; resource.Revision++;
            }
            return true;
        }, ct);
    }

    public async Task<IReadOnlyList<LicenseView>> LicensesAsync(Actor actor, Guid resourceId, CancellationToken ct)
    {
        await access.RequireAsync(actor, TargetKind.Resource, resourceId, VaultPermission.Metadata, ct);
        return await db.Licenses.AsNoTracking().Where(x => x.ResourceId == resourceId && !x.Deleted).OrderBy(x => x.Product)
            .Select(x => new LicenseView(x.Id, x.ResourceId, x.Product, x.Vendor, x.PurchaseReference, x.Seats,
                db.Assignments.Where(a => a.LicenseId == x.Id).Sum(a => (int?)a.Seats) ?? 0, x.ExpiresAt, x.Revision)).ToListAsync(ct);
    }

    public async Task<Guid> SaveLicenseAsync(Actor actor, Guid? id, Guid resourceId, string product, string vendor,
        string purchaseReference, int seats, DateTimeOffset? expiresAt, LicensePayload payload, long revision, string correlation, CancellationToken ct)
    {
        Name(product); Length(vendor, 512); Length(purchaseReference, 512); Length(payload.LicenseKey, 65536); Length(payload.Notes, 65536);
        if (seats < 1 || seats > 1000000) throw new VaultValidationException("Antal platser måste vara 1–1 000 000.");
        SoftwareLicense license;
        if (id is { } existing)
        {
            license = await db.Licenses.SingleOrDefaultAsync(x => x.Id == existing && !x.Deleted, ct) ?? throw new AccessDeniedException();
            if (resourceId != license.ResourceId) throw new VaultValidationException("Resurstillhörighet kan inte ändras här.");
            Revision(license.Revision, revision);
        }
        else license = new SoftwareLicense { Id = Guid.NewGuid(), ResourceId = resourceId };
        await access.RequireAsync(actor, TargetKind.Resource, resourceId, VaultPermission.Modify, ct);
        return await audit.MutationAsync(actor, id is null ? "License.Create" : "License.Update", license.Id, correlation, async token =>
        {
            await RequireFreshAsync(actor, TargetKind.Resource, resourceId, VaultPermission.Modify, token);
            var assigned = await db.Assignments.Where(x => x.LicenseId == license.Id).SumAsync(x => (int?)x.Seats, token) ?? 0;
            if (seats < assigned) throw new VaultValidationException("Antal platser understiger befintliga tilldelningar.");
            if (id is null) db.Licenses.Add(license);
            license.Product = product.Trim(); license.Vendor = vendor; license.PurchaseReference = purchaseReference;
            license.Seats = seats; license.ExpiresAt = expiresAt; license.Revision++; license.SecretVersion++;
            license.EnvelopeJson = cipher.Encrypt(payload, license.Id, license.SecretVersion, "license");
            return license.Id;
        }, ct);
    }

    public async Task<LicensePayload> RevealLicenseAsync(Actor actor, Guid id, string correlation, CancellationToken ct)
    {
        var license = await db.Licenses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && !x.Deleted, ct) ?? throw new AccessDeniedException();
        await access.RequireAsync(actor, TargetKind.Resource, license.ResourceId, VaultPermission.ReadSecret, ct);
        await audit.FlushAsync(ct);
        await audit.RecordAsync(actor, "License.Reveal", id, "ReleaseAuthorized", correlation, ct);
        actor = await directory.ResolveAsync(actor.SubjectId, ct);
        await access.RequireAsync(actor, TargetKind.Resource, license.ResourceId, VaultPermission.ReadSecret, ct);
        return cipher.Decrypt<LicensePayload>(license.EnvelopeJson, id, license.SecretVersion, "license");
    }

    public async Task<IReadOnlyList<LicenseAssignment>> AssignmentsAsync(Actor actor, Guid licenseId, CancellationToken ct)
    {
        var license = await db.Licenses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == licenseId && !x.Deleted, ct) ?? throw new AccessDeniedException();
        await access.RequireAsync(actor, TargetKind.Resource, license.ResourceId, VaultPermission.Metadata, ct);
        return await db.Assignments.AsNoTracking().Where(x => x.LicenseId == licenseId).ToListAsync(ct);
    }

    public async Task<Guid> AssignLicenseAsync(Actor actor, LicenseAssignment assignment, string correlation, CancellationToken ct)
    {
        if (assignment.Seats < 1 || assignment.Seats > 1000000
            || (assignment.ResourceId is null) == string.IsNullOrWhiteSpace(assignment.UserId)
            || assignment.ResourceId is null && string.IsNullOrWhiteSpace(assignment.UserProvider))
            throw new VaultValidationException("Tilldela positiva platser till antingen en användare eller en resurs.");
        var license = await db.Licenses.SingleOrDefaultAsync(x => x.Id == assignment.LicenseId && !x.Deleted, ct) ?? throw new AccessDeniedException();
        await access.RequireAsync(actor, TargetKind.Resource, license.ResourceId, VaultPermission.Modify, ct);
        if (assignment.ResourceId is null)
            await ValidateSubjectAsync(assignment.UserProvider!, assignment.UserId!, SubjectKind.User, ct);
        if (assignment.ResourceId is { } target)
            await access.RequireAsync(actor, TargetKind.Resource, target, VaultPermission.Metadata, ct);
        assignment.Id = Guid.NewGuid(); assignment.CreatedAt = clock.GetUtcNow();
        return await audit.MutationAsync(actor, "License.Assign", license.Id, correlation, async token =>
        {
            await RequireFreshAsync(actor, TargetKind.Resource, license.ResourceId, VaultPermission.Modify, token);
            var used = await db.Assignments.Where(x => x.LicenseId == license.Id).SumAsync(x => (int?)x.Seats, token) ?? 0;
            if (used + assignment.Seats > license.Seats) throw new VaultValidationException("Licensen saknar lediga platser.");
            // Reserve the parent row with a revision update; concurrent allocations cannot both commit.
            license.Revision++;
            db.Assignments.Add(assignment);
            return assignment.Id;
        }, ct);
    }

    public async Task UnassignLicenseAsync(Actor actor, Guid assignmentId, string correlation, CancellationToken ct)
    {
        var assignment = await db.Assignments.SingleOrDefaultAsync(x => x.Id == assignmentId, ct) ?? throw new AccessDeniedException();
        var license = await db.Licenses.SingleAsync(x => x.Id == assignment.LicenseId, ct);
        await access.RequireAsync(actor, TargetKind.Resource, license.ResourceId, VaultPermission.Modify, ct);
        await audit.MutationAsync(actor, "License.Unassign", license.Id, correlation, async token =>
        { await RequireFreshAsync(actor, TargetKind.Resource, license.ResourceId, VaultPermission.Modify, token); db.Assignments.Remove(assignment); return true; }, ct);
    }

    private async Task<SecretEntry> EntryAsync(Guid id, CancellationToken ct) =>
        await db.Secrets.SingleOrDefaultAsync(x => x.Id == id && !x.Deleted, ct) ?? throw new AccessDeniedException();

    public async Task<IReadOnlyList<SecretView>> DeletedSecretsAsync(Actor actor, Guid resourceId, CancellationToken ct)
    {
        await access.RequireAsync(actor, TargetKind.Resource, resourceId, VaultPermission.Metadata, ct);
        return await db.Secrets.AsNoTracking().Where(x => x.ResourceId == resourceId && x.Deleted).OrderBy(x => x.Title)
            .Select(x => new SecretView(x.Id, x.Title, x.CurrentVersion, x.Revision, x.LdapProfileId)).ToListAsync(ct);
    }

    public async Task DeleteEmptyTargetAsync(Actor actor, TargetKind kind, Guid id, long revision, string correlation, CancellationToken ct)
    {
        await access.RequireAsync(actor, kind, id, VaultPermission.ManageAccess, ct);
        await audit.MutationAsync(actor, kind == TargetKind.Group ? "Group.Delete" : "Resource.Delete", id, correlation, async token =>
        {
            await RequireFreshAsync(actor, kind, id, VaultPermission.ManageAccess, token);
            if (kind == TargetKind.Group)
            {
                if (await db.Groups.AnyAsync(x => x.ParentId == id, token) || await db.Resources.AnyAsync(x => x.GroupId == id, token))
                    throw new VaultValidationException("Gruppen innehåller resurser eller undergrupper.");
                var group = await db.Groups.SingleAsync(x => x.Id == id, token); Revision(group.Revision, revision); db.Groups.Remove(group);
            }
            else
            {
                if (await db.Secrets.AnyAsync(x => x.ResourceId == id, token) || await db.Licenses.AnyAsync(x => x.ResourceId == id, token)
                    || await db.Assignments.AnyAsync(x => x.ResourceId == id, token))
                    throw new VaultValidationException("Resursen innehåller lösenord, licenser eller tilldelningar och kan inte tas bort.");
                var resource = await db.Resources.SingleAsync(x => x.Id == id, token); Revision(resource.Revision, revision); db.Resources.Remove(resource);
            }
            db.Grants.RemoveRange(await db.Grants.Where(x => x.TargetKind == kind && x.TargetId == id).ToListAsync(token));
            db.Owners.RemoveRange(await db.Owners.Where(x => x.TargetKind == kind && x.TargetId == id).ToListAsync(token));
            return true;
        }, ct);
    }

    public async Task SetLicenseDeletedAsync(Actor actor, Guid id, bool deleted, long revision, string correlation, CancellationToken ct)
    {
        var license = await db.Licenses.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AccessDeniedException();
        await access.RequireAsync(actor, TargetKind.Resource, license.ResourceId, VaultPermission.Modify, ct);
        await audit.MutationAsync(actor, deleted ? "License.Delete" : "License.Undelete", id, correlation, async token =>
        { await RequireFreshAsync(actor, TargetKind.Resource, license.ResourceId, VaultPermission.Modify, token); Revision(license.Revision, revision); license.Deleted = deleted; license.Revision++; return true; }, ct);
    }

    public async Task<IReadOnlyList<LicenseView>> DeletedLicensesAsync(Actor actor, Guid resourceId, CancellationToken ct)
    {
        await access.RequireAsync(actor, TargetKind.Resource, resourceId, VaultPermission.Metadata, ct);
        return await db.Licenses.AsNoTracking().Where(x => x.ResourceId == resourceId && x.Deleted).OrderBy(x => x.Product)
            .Select(x => new LicenseView(x.Id, x.ResourceId, x.Product, x.Vendor, x.PurchaseReference, x.Seats,
                db.Assignments.Where(a => a.LicenseId == x.Id).Sum(a => (int?)a.Seats) ?? 0, x.ExpiresAt, x.Revision)).ToListAsync(ct);
    }
    private async Task ValidateSubjectAsync(string provider, string id, SubjectKind kind, CancellationToken ct)
    {
        if (provider != directory.ProviderId || await directory.FindAsync(id, kind, ct) is null)
            throw new VaultValidationException("Identiteten finns inte i den konfigurerade katalogen.");
    }
    private async Task RequireFreshAsync(Actor actor, TargetKind kind, Guid target, VaultPermission permission, CancellationToken ct) =>
        await access.RequireAsync(await directory.ResolveAsync(actor.SubjectId, ct), kind, target, permission, ct);
    private static void Revision(long actual, long expected)
    { if (actual != expected) throw new VaultConflictException("Posten har ändrats. Ladda om före nytt försök."); }
    private static void Name(string value)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > 200) throw new VaultValidationException("Namn måste vara 1–200 tecken."); }
    private static void Length(string value, int max)
    { if (value is null || value.Length > max) throw new VaultValidationException("Ett fält överskrider tillåten längd."); }
    private static void ValidatePayload(SecretPayload payload)
    { Length(payload.Username, 512); Length(payload.Password, 65536); Length(payload.Notes, 65536); }
}
