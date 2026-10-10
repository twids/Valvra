using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Security;

namespace Valvra.Infrastructure.Services;

public sealed record GlobalRoleView(Guid Id, string Provider, string SubjectId, GlobalRole Roles, long Revision, string? Name);
public sealed class GlobalRoleService(VaultDbContext db, VaultIntegrity integrity, AuditService audit, IDirectoryProvider directory, TimeProvider clock)
{
    private const GlobalRole All = GlobalRole.AccessAdministrator | GlobalRole.SystemAdministrator | GlobalRole.Auditor;
    private Task<List<AccessGrant>> Rows(CancellationToken ct) => db.Grants.Where(x => x.TargetKind == TargetKind.System
        && x.TargetId == integrity.InstallationId).ToListAsync(ct);
    public Task<Actor> ApplyAsync(Actor actor, CancellationToken ct) => integrity.RunAsync(async () =>
    {
        var roles = (await Rows(ct)).Where(x => x.SubjectKind == SubjectKind.User && x.Provider == actor.Provider
            && x.SubjectId == actor.SubjectId && x.StartsAt is null && x.ExpiresAt is null)
            .Aggregate(GlobalRole.None, (result, x) => result | (GlobalRole)(int)x.Permissions);
        return actor with { IsAccessAdministrator = actor.IsEnabled && roles.HasFlag(GlobalRole.AccessAdministrator),
            IsSystemAdministrator = actor.IsEnabled && roles.HasFlag(GlobalRole.SystemAdministrator),
            IsAuditor = actor.IsEnabled && roles.HasFlag(GlobalRole.Auditor) };
    }, ct);
    public async Task RequireAsync(Actor actor, GlobalRole required, CancellationToken ct)
    {
        var fresh = await ApplyAsync(actor, ct);
        if (!fresh.IsEnabled || (required switch {
            GlobalRole.AccessAdministrator => !fresh.IsAccessAdministrator,
            GlobalRole.SystemAdministrator => !fresh.IsSystemAdministrator,
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
            result.Add(new(row.Id, row.Provider, row.SubjectId, (GlobalRole)(int)row.Permissions, row.Revision, person?.Name));
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
                    if (existing is not null && ((GlobalRole)(int)existing.Permissions).HasFlag(essential) && !roles.HasFlag(essential))
                    {
                        var replacement = false;
                        foreach (var other in rows.Where(x => x.Id != existing.Id && x.Provider == directory.ProviderId && x.SubjectKind == SubjectKind.User
                            && x.StartsAt is null && x.ExpiresAt is null && ((GlobalRole)(int)x.Permissions).HasFlag(essential)))
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
