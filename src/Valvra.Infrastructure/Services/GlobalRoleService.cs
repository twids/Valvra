using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Security;
using Valvra.Infrastructure.Authorization;
using System.DirectoryServices.Protocols;

namespace Valvra.Infrastructure.Services;

public sealed record GlobalRoleView(Guid Id, string Provider, string SubjectId, GlobalRole Roles, long Revision, string? Name, string? SamAccountName=null);
public sealed record ExclusionView(Guid Id, TargetKind TargetKind, Guid TargetId, string TargetName, string Provider,
    string SubjectId, string? Name, string CreatedBy, DateTimeOffset CreatedAt, long Revision,
    string? SamAccountName=null,string? CreatedByName=null,string? CreatedBySamAccountName=null);
public sealed class GlobalRoleService(VaultDbContext db, VaultIntegrity integrity, AuditService audit, IDirectoryProvider directory, TimeProvider clock)
{
    private const GlobalRole All = GlobalRole.AccessAdministrator | GlobalRole.SystemAdministrator | GlobalRole.Auditor
        | GlobalRole.GlobalAdministrator | GlobalRole.LicenseAdministrator;
    private static bool Capability(GlobalRole roles, GlobalRole required) => roles.HasFlag(required) || roles.HasFlag(GlobalRole.GlobalAdministrator);
    private Task<List<AccessGrant>> Rows(CancellationToken ct) => db.Grants.Where(x => x.TargetKind == TargetKind.System
        && x.TargetId == integrity.InstallationId).ToListAsync(ct);
    public Task<Actor> ApplyAsync(Actor actor, CancellationToken ct) => integrity.RunAsync(async () =>
    {
        var roles = (await Rows(ct)).Where(x => x.SubjectKind == SubjectKind.User && x.Provider == actor.Provider
            && x.SubjectId == actor.SubjectId && x.StartsAt is null && x.ExpiresAt is null)
            .Aggregate(GlobalRole.None, (result, x) => result | (GlobalRole)(int)x.Permissions);
        return actor with { IsAccessAdministrator = actor.IsEnabled && Capability(roles, GlobalRole.AccessAdministrator),
            IsSystemAdministrator = actor.IsEnabled && Capability(roles, GlobalRole.SystemAdministrator),
            IsAuditor = actor.IsEnabled && Capability(roles, GlobalRole.Auditor),
            IsGlobalAdministrator = actor.IsEnabled && roles.HasFlag(GlobalRole.GlobalAdministrator),
            IsLicenseAdministrator = actor.IsEnabled && roles.HasFlag(GlobalRole.LicenseAdministrator) };
    }, ct);
    public async Task RequireAsync(Actor actor, GlobalRole required, CancellationToken ct)
    {
        var fresh = await ApplyAsync(actor, ct);
        if (!fresh.IsEnabled || (required switch {
            GlobalRole.AccessAdministrator => !fresh.IsAccessAdministrator,
            GlobalRole.SystemAdministrator => !fresh.IsSystemAdministrator,
            GlobalRole.GlobalAdministrator => !fresh.IsGlobalAdministrator,
            GlobalRole.LicenseAdministrator => !fresh.IsLicenseAdministrator,
            GlobalRole.Auditor => !fresh.IsAuditor,
            _ => true })) throw new AccessDeniedException();
    }
    public Task<IReadOnlyList<GlobalRoleView>> ReadAsync(Actor actor, CancellationToken ct) => integrity.RunAsync<IReadOnlyList<GlobalRoleView>>(async () =>
    {
        var fresh = await ApplyAsync(actor, ct);
        if (!fresh.IsAccessAdministrator && !fresh.IsSystemAdministrator) throw new AccessDeniedException();
        var result = new List<GlobalRoleView>();
        foreach (var row in await Rows(ct))
        {
            var person = row.Provider == directory.ProviderId ? await directory.FindAsync(row.SubjectId, SubjectKind.User, ct) : null;
            if (person is not null && (person.Provider != row.Provider || person.Id != row.SubjectId || person.Kind != SubjectKind.User)) throw new AccessDeniedException();
            result.Add(new(row.Id, row.Provider, row.SubjectId, (GlobalRole)(int)row.Permissions, row.Revision, person?.Name, person?.SamAccountName));
        }
        return result;
    }, ct);
    public Task<Guid> SetAsync(Actor actor, string provider, string subjectId, SubjectKind kind, GlobalRole roles, long revision, string correlation, CancellationToken ct) =>
        integrity.RunAsync(async () =>
        {
            await RequireAsync(actor, GlobalRole.AccessAdministrator, ct);
            if (kind != SubjectKind.User || string.IsNullOrWhiteSpace(subjectId) || subjectId.Length > 512 || provider != directory.ProviderId
                || (roles & ~All) != 0) throw new VaultValidationException("Globala rättigheter kan endast tilldelas personkonton hos den aktiva providern.");
            return await audit.MutationAsync(actor, "GlobalRoles.Update", integrity.InstallationId, correlation, async token =>
            {
                await RequireAsync(actor, GlobalRole.AccessAdministrator, token);
                var rows = await Rows(token); var existing = rows.SingleOrDefault(x => x.Provider == provider && x.SubjectId == subjectId);
                if ((existing?.Revision ?? 0) != revision) throw new VaultConflictException("Posten har ändrats. Ladda om före nytt försök.");
                // Reductions remain possible after an account is disabled or removed.
                if (existing is null || (roles & ~(GlobalRole)(int)existing.Permissions) != 0)
                {
                    var subject = await directory.FindAsync(subjectId, SubjectKind.User, token) ?? throw new VaultValidationException("Personkontot kunde inte verifieras.");
                    if (subject.Kind != SubjectKind.User || subject.Provider != provider || subject.Id != subjectId) throw new AccessDeniedException();
                }
                foreach (var essential in new[] { GlobalRole.AccessAdministrator, GlobalRole.SystemAdministrator })
                    if (existing is not null && Capability((GlobalRole)(int)existing.Permissions, essential) && !Capability(roles, essential))
                    {
                        var replacement = false;
                        foreach (var other in rows.Where(x => x.Id != existing.Id && x.Provider == directory.ProviderId && x.SubjectKind == SubjectKind.User
                            && x.StartsAt is null && x.ExpiresAt is null && (((GlobalRole)(int)x.Permissions).HasFlag(essential)
                                || ((GlobalRole)(int)x.Permissions).HasFlag(GlobalRole.GlobalAdministrator))))
                        {
                            try
                            {
                                var account = await directory.ResolveAsync(other.SubjectId, token);
                                if (account.IsEnabled && account.Provider == other.Provider && account.SubjectId == other.SubjectId) { replacement = true; break; }
                            }
                            catch (AccessDeniedException) { /* Deleted/disabled accounts are not replacements. */ }
                        }
                        if (!replacement) throw new VaultValidationException("Den sista administratören för denna rättighet kan inte tas bort.");
                    }
                if (existing is not null && roles == GlobalRole.None) { db.Grants.Remove(existing); return existing.Id; }
                if (existing is null && roles == GlobalRole.None) throw new VaultValidationException("Välj minst en global rättighet.");
                var grant = existing ?? new AccessGrant { TargetKind = TargetKind.System, TargetId = integrity.InstallationId,
                    SubjectKind = SubjectKind.User, Provider = provider, SubjectId = subjectId, CreatedAt = clock.GetUtcNow(), GrantedBy = actor.SubjectId };
                if (existing is null) db.Grants.Add(grant);
                grant.Permissions = (VaultPermission)(int)roles; grant.Revision++;
                return grant.Id;
            }, ct, System.Text.Json.JsonSerializer.Serialize(new { provider, subjectId, roles = (int)roles }));
        }, ct);
    public Task<IReadOnlyList<ExclusionView>> ExclusionsAsync(Actor actor, CancellationToken ct) => integrity.RunAsync<IReadOnlyList<ExclusionView>>(async () =>
    {
        await RequireAsync(actor, GlobalRole.AccessAdministrator, ct);
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var resources = await db.Resources.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var result = new List<ExclusionView>();
        var rows=await db.Exclusions.AsNoTracking().OrderBy(x=>x.CreatedAt).ToListAsync(ct);
        var people=new Dictionary<string,DirectorySubject>(StringComparer.Ordinal);
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(5));
        foreach(var subjectId in rows.Where(x=>x.Provider==directory.ProviderId).SelectMany(x=>new[]{x.SubjectId,x.CreatedBy}).Distinct().Take(100))
        {
            if(budget.IsCancellationRequested)break;
            try{var person=await directory.FindAsync(subjectId,SubjectKind.User,budget.Token);if(person is null)continue;
                if(person.Provider!=directory.ProviderId||person.Id!=subjectId||person.Kind!=SubjectKind.User)throw new AccessDeniedException();people[subjectId]=person;}
            catch(Exception error)when(error is VaultUnavailableException or LdapException or DirectoryOperationException){break;}
            catch(OperationCanceledException)when(!ct.IsCancellationRequested){break;}
        }
        ct.ThrowIfCancellationRequested();
        foreach (var row in rows)
        {
            var names = new List<string>();
            Guid? groupId = row.TargetKind == TargetKind.Group ? row.TargetId : resources.GetValueOrDefault(row.TargetId)?.GroupId;
            var visited = new HashSet<Guid>();
            while (groupId is { } id && visited.Add(id) && visited.Count <= 128 && groups.TryGetValue(id, out var group))
            { names.Insert(0, group.Name); groupId = group.ParentId; }
            if (row.TargetKind == TargetKind.Resource) names.Add(resources.GetValueOrDefault(row.TargetId)?.Name ?? row.TargetId.ToString());
            people.TryGetValue(row.SubjectId,out var person);people.TryGetValue(row.CreatedBy,out var creator);
            result.Add(new(row.Id,row.TargetKind,row.TargetId,string.Join(" / ",names),row.Provider,row.SubjectId,
                row.Provider==directory.ProviderId?person?.Name:null,row.CreatedBy,row.CreatedAt,row.Revision,
                row.Provider==directory.ProviderId?person?.SamAccountName:null,
                row.Provider==directory.ProviderId?creator?.Name:null,row.Provider==directory.ProviderId?creator?.SamAccountName:null));
        }
        return result;
    }, ct);

    public Task<IReadOnlyList<AuditEventView>> VisibleAuditAsync(Actor actor, IReadOnlyList<AuditEventView> rows, CancellationToken ct) => integrity.RunAsync<IReadOnlyList<AuditEventView>>(async () =>
    {
        var exclusions=await db.Exclusions.AsNoTracking().Where(x=>x.Provider==actor.Provider&&x.SubjectId==actor.SubjectId).ToListAsync(ct);
        if(exclusions.Count==0)return rows;
        var access=new AccessService(db,clock);var deniedResources=new HashSet<Guid>();var deniedGroups=new HashSet<Guid>();
        foreach(var resource in await db.Resources.AsNoTracking().ToListAsync(ct))
            if(await access.IsExcludedAsync(actor,TargetKind.Resource,resource.Id,ct))deniedResources.Add(resource.Id);
        foreach(var group in await db.Groups.AsNoTracking().ToListAsync(ct))
            if(await access.IsExcludedAsync(actor,TargetKind.Group,group.Id,ct))deniedGroups.Add(group.Id);
        foreach(var exclusion in exclusions)
            if(exclusion.TargetKind==TargetKind.Resource)deniedResources.Add(exclusion.TargetId);else deniedGroups.Add(exclusion.TargetId);
        // Unknown legacy scope cannot safely be attributed while exclusions exist. Never alter signed payloads.
        return rows.Where(x=>!x.SignatureValid || x.Event.Scope is {} scope
            && !(scope.ResourceId is {} resourceId&&deniedResources.Contains(resourceId))
            && !deniedGroups.Contains(scope.GroupId)&&!scope.GroupPath.Any(g=>deniedGroups.Contains(g.Id))).ToArray();
    },ct);

    public Task<string> AuditAccessStampAsync(Actor actor,CancellationToken ct)=>integrity.RunAsync(async()=>
    {
        var exclusions=await db.Exclusions.AsNoTracking().Where(x=>x.Provider==actor.Provider&&x.SubjectId==actor.SubjectId).OrderBy(x=>x.Id).ToListAsync(ct);
        if(exclusions.Count==0)return "";
        var groups=await db.Groups.AsNoTracking().OrderBy(x=>x.Id).Select(x=>new{x.Id,x.ParentId}).ToListAsync(ct);
        var resources=await db.Resources.AsNoTracking().OrderBy(x=>x.Id).Select(x=>new{x.Id,x.GroupId}).ToListAsync(ct);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new{exclusions,groups,resources})));
    },ct);

    public Task<Guid> ExcludeAsync(Actor actor, TargetKind kind, Guid target, string provider, string subjectId, string correlation, CancellationToken ct) => integrity.RunAsync(async () =>
    {
        await RequireAsync(actor, GlobalRole.AccessAdministrator, ct);
        var access = new AccessService(db, clock);
        await access.RequireAsync(await ApplyAsync(actor, ct), kind, target, VaultPermission.ManageAccess, ct);
        if (provider != directory.ProviderId || provider.Length > 128 || string.IsNullOrWhiteSpace(subjectId) || subjectId.Length > 512 || kind is not (TargetKind.Group or TargetKind.Resource))
            throw new VaultValidationException("Välj ett personkonto och en resurs eller resursgrupp.");
        var person = await directory.FindAsync(subjectId, SubjectKind.User, ct) ?? throw new VaultValidationException("Personkontot kunde inte verifieras.");
        if (person.Provider != provider || person.Id != subjectId || person.Kind != SubjectKind.User) throw new AccessDeniedException();
        return await audit.MutationAsync(actor,"Access.Exclude",target,correlation,async token =>
        {
            await RequireAsync(actor,GlobalRole.AccessAdministrator,token);
            await access.RequireAsync(await ApplyAsync(actor,token),kind,target,VaultPermission.ManageAccess,token);
            if(await db.Exclusions.AnyAsync(x=>x.TargetKind==kind&&x.TargetId==target&&x.Provider==provider&&x.SubjectId==subjectId,token))
                throw new VaultConflictException("Ett uttryckligt undantag finns redan för personen och destinationen.");
            var row=new AccessExclusion{TargetKind=kind,TargetId=target,Provider=provider,SubjectId=subjectId,
                CreatedBy=actor.SubjectId,CreatedAt=clock.GetUtcNow(),Revision=1};db.Exclusions.Add(row);return row.Id;
        },ct,System.Text.Json.JsonSerializer.Serialize(new{provider,subjectId,kind,target}));
    },ct);

    public Task RestoreAccessAsync(Actor actor, Guid id, long revision, string correlation, CancellationToken ct) => integrity.RunAsync(async () =>
    {
        await RequireAsync(actor,GlobalRole.AccessAdministrator,ct);
        var row=await db.Exclusions.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new AccessDeniedException();
        await audit.MutationAsync(actor,"Access.Restore",row.TargetId,correlation,async token=>
        {
            var fresh=await ApplyAsync(actor,token);
            await RequireAsync(fresh,GlobalRole.AccessAdministrator,token);
            if(!fresh.IsGlobalAdministrator)await new AccessService(db,clock).RequireAsync(fresh,row.TargetKind,row.TargetId,VaultPermission.ManageAccess,token);
            if(row.Revision!=revision)throw new VaultConflictException("Posten har ändrats. Ladda om före nytt försök.");
            db.Exclusions.Remove(row);return true;
        },ct,System.Text.Json.JsonSerializer.Serialize(new{row.Id,row.Provider,row.SubjectId,row.TargetKind,row.TargetId,row.Revision}));
    },ct);

    // Only the operator-authorized setup path calls this method. There is no
    // first-login endpoint; concurrent/partial retries cannot claim another user.
    public Task BootstrapAsync(Actor installer, string correlation, CancellationToken ct) => integrity.RunAsync(async () =>
    {
        if (!installer.IsEnabled || installer.Provider != directory.ProviderId) throw new AccessDeniedException();
        var rows = await Rows(ct);
        if (rows.Count > 0)
        {
            if (!rows.Any(x => x.Provider == installer.Provider && x.SubjectKind == SubjectKind.User && x.SubjectId == installer.SubjectId
                && ((GlobalRole)(int)x.Permissions).HasFlag(GlobalRole.AccessAdministrator | GlobalRole.SystemAdministrator))) throw new AccessDeniedException();
            await audit.FlushAsync(ct);
            return;
        }
        await audit.MutationAsync(installer, "Installation.BootstrapAdministrator", integrity.InstallationId, correlation, token =>
        {
            db.Grants.Add(new AccessGrant { TargetKind = TargetKind.System, TargetId = integrity.InstallationId, SubjectKind = SubjectKind.User,
                Provider = installer.Provider, SubjectId = installer.SubjectId,
                Permissions = (VaultPermission)(int)(GlobalRole.AccessAdministrator | GlobalRole.SystemAdministrator), Revision = 1,
                GrantedBy = installer.SubjectId, CreatedAt = clock.GetUtcNow() });
            return Task.FromResult(true);
        }, ct);
    }, ct);
}
