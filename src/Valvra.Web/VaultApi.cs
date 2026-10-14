using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Identity;
using Valvra.Infrastructure.Services;

namespace Valvra.Web;

public sealed record CreateGroupRequest(string Name, Guid? ParentId);
public sealed record CreateResourceRequest(string Name, Guid GroupId);
public sealed record RenameRequest(string Name, long Revision);
public sealed record MoveRequest(Guid DestinationGroupId, long Revision);
public sealed record SecretRequest(string Title, SecretPayload? Payload, string? LdapProfileId, long Revision = 0, SecretChange SecretChange = SecretChange.Replace);
public sealed record RevealRequest(int? Version = null, bool Copy = false);
public sealed record RestoreRequest(int Version, long Revision);
public sealed record DeleteRequest(bool Deleted, long Revision);
public sealed record GrantUpdateRequest(VaultPermission Permissions, DateTimeOffset? StartsAt, DateTimeOffset? ExpiresAt, long Revision);
public sealed record ExclusionRequest(TargetKind TargetKind, Guid TargetId, string Provider, string SubjectId);
public sealed record AccessRestoreRequest(long Revision);
public sealed record LicenseRequest(Guid ResourceId, string Product, string Vendor, string PurchaseReference,
    int Seats, DateTimeOffset? ExpiresAt, LicensePayload? Payload, long Revision = 0, SecretChange SecretChange = SecretChange.Preserve);

public static class VaultApi
{
    public static void MapVaultApi(this WebApplication app)
    {
        app.MapImportApi();
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/directory/active",async(HttpContext ctx,CurrentActor current,VaultService vault)=>
            await current.RunAsync(ctx,async actor=>Results.Ok(await vault.ActivePeopleAsync(actor,null,ctx.RequestAborted))));
        api.MapGet("/settings/people/active",async(HttpContext ctx,CurrentActor current,VaultService vault,GlobalRoleService globals)=>
            await current.RunAsync(ctx,async actor=>{await globals.RequireAsync(actor,GlobalRole.AccessAdministrator,ctx.RequestAborted);return Results.Ok(await vault.ActivePeopleAsync(actor,null,ctx.RequestAborted));}));
        api.MapGet("/licenses/{id:guid}/people/active",async(Guid id,HttpContext ctx,CurrentActor current,VaultService vault)=>
            await current.RunAsync(ctx,async actor=>Results.Ok(await vault.ActivePeopleAsync(actor,id,ctx.RequestAborted))));
        api.MapGet("/settings/exclusions",async(HttpContext ctx,CurrentActor current,GlobalRoleService roles)=>
            await current.RunAsync(ctx,async actor=>Results.Ok(await roles.ExclusionsAsync(actor,ctx.RequestAborted))));
        api.MapPost("/settings/exclusions",async(ExclusionRequest body,HttpContext ctx,CurrentActor current,GlobalRoleService roles)=>
            await current.RunAsync(ctx,async actor=>Results.Ok(new{id=await roles.ExcludeAsync(actor,body.TargetKind,body.TargetId,body.Provider,body.SubjectId,ctx.TraceIdentifier,ctx.RequestAborted)})));
        api.MapPost("/settings/exclusions/{id:guid}/restore",async(Guid id,AccessRestoreRequest body,HttpContext ctx,CurrentActor current,GlobalRoleService roles)=>
            await current.RunAsync(ctx,async actor=>{await roles.RestoreAccessAsync(actor,id,body.Revision,ctx.TraceIdentifier,ctx.RequestAborted);return Results.NoContent();}));
        api.MapPost("/targets/{kind}/{id:guid}/delete", async (TargetKind kind, Guid id, DeleteRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.DeleteEmptyTargetAsync(actor, kind, id, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));
        api.MapGet("/resources/{resourceId:guid}/deleted-secrets", async (Guid resourceId, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.DeletedSecretsAsync(actor, resourceId, ctx.RequestAborted))));
        api.MapGet("/resources/{resourceId:guid}/deleted-licenses", async (Guid resourceId, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.DeletedLicensesAsync(actor, resourceId, ctx.RequestAborted))));
        api.MapPost("/licenses/{id:guid}/deleted", async (Guid id, DeleteRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.SetLicenseDeletedAsync(actor, id, body.Deleted, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));
        api.MapGet("/session", async (HttpContext ctx, CurrentActor current, IAntiforgery csrf, AuditService audit) =>
            await current.RunAsync(ctx, async actor =>
            {
                await audit.RecordAsync(actor, "Session.Identified", null, "Success", ctx.TraceIdentifier, ctx.RequestAborted);
                return Results.Ok(new { actor.DisplayName, actor.SamAccountName, actor.Provider, actor.SubjectId, actor.IsAccessAdministrator, actor.IsAuditor, actor.IsSystemAdministrator, actor.IsGlobalAdministrator, actor.IsLicenseAdministrator,
                    applicationVersion = BuildInformation.Version, build = BuildInformation.Build,
                    csrfToken = csrf.GetAndStoreTokens(ctx).RequestToken });
            }));

        api.MapGet("/groups", async (HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.GroupsAsync(actor, ctx.RequestAborted))));
        api.MapPost("/groups", async (CreateGroupRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(new { id = await vault.CreateGroupAsync(actor, body.Name, body.ParentId, ctx.TraceIdentifier, ctx.RequestAborted) })));
        api.MapPost("/groups/{id:guid}/rename", async (Guid id, RenameRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.RenameGroupAsync(actor, id, body.Name, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));
        api.MapGet("/resources", async (HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.ResourcesAsync(actor, ctx.RequestAborted))));
        api.MapPost("/resources", async (CreateResourceRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(new { id = await vault.CreateResourceAsync(actor, body.GroupId, body.Name, ctx.TraceIdentifier, ctx.RequestAborted) })));
        api.MapPost("/resources/{id:guid}/rename", async (Guid id, RenameRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.RenameResourceAsync(actor, id, body.Name, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));

        api.MapGet("/resources/{resourceId:guid}/secrets", async (Guid resourceId, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.SecretsAsync(actor, resourceId, ctx.RequestAborted))));
        api.MapPost("/resources/{resourceId:guid}/secrets", async (Guid resourceId, SecretRequest body, HttpContext ctx, CurrentActor current, VaultService vault, LdapTestOptions ldap) =>
            await current.RunAsync(ctx, async actor =>
            {
                ValidateProfile(body.LdapProfileId, ldap);
                if(body.Payload is null || body.SecretChange != SecretChange.Replace)throw new VaultValidationException("Ange alla hemliga fält för det nya lösenordet.");
                return Results.Ok(new { id = await vault.CreateSecretAsync(actor, resourceId, body.Title, body.Payload, body.LdapProfileId, ctx.TraceIdentifier, ctx.RequestAborted) });
            }));
        api.MapPost("/secrets/{id:guid}/update", async (Guid id, SecretRequest body, HttpContext ctx, CurrentActor current, VaultService vault, LdapTestOptions ldap) =>
            await current.RunAsync(ctx, async actor =>
            {
                ValidateProfile(body.LdapProfileId, ldap);
                await vault.UpdateSecretAsync(actor, id, body.Title, body.Payload, body.LdapProfileId, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted, body.SecretChange);
                return Results.NoContent();
            }));
        api.MapPost("/secrets/{id:guid}/reveal", async (Guid id, RevealRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.RevealSecretAsync(actor, id, body.Version,
                body.Copy ? "Secret.Copy" : "Secret.Reveal", ctx.TraceIdentifier, ctx.RequestAborted))));
        api.MapGet("/secrets/{id:guid}/versions", async (Guid id, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.VersionsAsync(actor, id, ctx.RequestAborted))));
        api.MapPost("/secrets/{id:guid}/restore", async (Guid id, RestoreRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.RestoreVersionAsync(actor, id, body.Version, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));
        api.MapPost("/secrets/{id:guid}/deleted", async (Guid id, DeleteRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.SetDeletedAsync(actor, id, body.Deleted, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));
        api.MapPost("/secrets/{id:guid}/ldap-test", async (Guid id, HttpContext ctx, CurrentActor current, CredentialTestService tester) =>
            await current.RunAsync(ctx, async actor => Results.Ok(new { result = await tester.TestAsync(actor, id, ctx.TraceIdentifier, ctx.RequestAborted) })));
        api.MapGet("/ldap-profiles", (LdapTestOptions ldap) => Results.Ok(ldap.Profiles.Select(x => new { x.Id, x.Name })));

        api.MapGet("/access/{kind}/{id:guid}", async (TargetKind kind, Guid id, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.AccessAsync(actor, kind, id, ctx.RequestAborted))));
        api.MapPost("/grants", async (AccessGrant body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(new { id = await vault.GrantAsync(actor, body, ctx.TraceIdentifier, ctx.RequestAborted) })));
        api.MapPost("/grants/{id:guid}/revoke", async (Guid id, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.RevokeAsync(actor, id, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));
        api.MapPost("/grants/{id:guid}/update", async (Guid id, GrantUpdateRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.UpdateGrantAsync(actor,id,body.Permissions,body.StartsAt,body.ExpiresAt,body.Revision,ctx.TraceIdentifier,ctx.RequestAborted); return Results.NoContent(); }));
        api.MapPost("/owners", async (ResourceOwner body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(new { id = await vault.AddOwnerAsync(actor, body, ctx.TraceIdentifier, ctx.RequestAborted) })));
        api.MapPost("/owners/{id:guid}/remove", async (Guid id, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.RemoveOwnerAsync(actor, id, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));
        api.MapGet("/directory", async (string query, SubjectKind kind, HttpContext ctx, CurrentActor current, VaultService vault, IDirectoryProvider directory) =>
            await current.RunAsync(ctx, async actor =>
            {
                if (!actor.IsAccessAdministrator && !(await vault.GroupsAsync(actor, ctx.RequestAborted)).Any(x => x.CanManage)
                    && !(await vault.ResourcesAsync(actor, ctx.RequestAborted)).Any(x => x.CanManage)) throw new AccessDeniedException();
                return Results.Ok(await directory.SearchAsync(query, kind, ctx.RequestAborted));
            }));
        api.MapPost("/move/{kind}/{id:guid}/preview", async (TargetKind kind, Guid id, MoveRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.PreviewMoveAsync(actor, kind, id, body.DestinationGroupId, ctx.RequestAborted))));
        api.MapPost("/move/{kind}/{id:guid}", async (TargetKind kind, Guid id, MoveRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.MoveAsync(actor, kind, id, body.DestinationGroupId, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));

        api.MapGet("/resources/{resourceId:guid}/licenses", async (Guid resourceId, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.LicensesAsync(actor, resourceId, ctx.RequestAborted))));
        api.MapGet("/licenses/destinations",async(HttpContext ctx,CurrentActor current,VaultService vault)=>
            await current.RunAsync(ctx,async actor=>Results.Ok(await vault.LicenseDestinationsAsync(actor,ctx.RequestAborted))));
        api.MapGet("/licenses/overview",async(int? offset,HttpContext ctx,CurrentActor current,VaultService vault)=>
            await current.RunAsync(ctx,async actor=>Results.Ok(await vault.LicenseOverviewAsync(actor,offset??0,ctx.RequestAborted))));
        api.MapPost("/licenses", async (LicenseRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(new { id = await vault.SaveLicenseAsync(actor, null, body.ResourceId, body.Product,
                body.Vendor, body.PurchaseReference, body.Seats, body.ExpiresAt, body.Payload, 0, ctx.TraceIdentifier, ctx.RequestAborted, body.SecretChange) })));
        api.MapPost("/licenses/{id:guid}/update", async (Guid id, LicenseRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(new { id = await vault.SaveLicenseAsync(actor, id, body.ResourceId, body.Product,
                body.Vendor, body.PurchaseReference, body.Seats, body.ExpiresAt, body.Payload, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted, body.SecretChange) })));
        api.MapPost("/licenses/{id:guid}/reveal", async (Guid id, RevealRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.RevealLicenseAsync(actor, id, ctx.TraceIdentifier, ctx.RequestAborted, body.Version))));
        api.MapGet("/licenses/{id:guid}/versions", async (Guid id, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.LicenseVersionsAsync(actor, id, ctx.RequestAborted))));
        api.MapPost("/licenses/{id:guid}/restore", async (Guid id, RestoreRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.RestoreLicenseVersionAsync(actor, id, body.Version, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));
        api.MapGet("/licenses/{id:guid}/assignments", async (Guid id, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.AssignmentsAsync(actor, id, ctx.RequestAborted))));
        api.MapGet("/licenses/{id:guid}/people",async(Guid id,string query,HttpContext ctx,CurrentActor current,VaultService vault)=>
            await current.RunAsync(ctx,async actor=>Results.Ok(await vault.LicensePeopleAsync(actor,id,query,ctx.RequestAborted))));
        api.MapPost("/assignments", async (LicenseAssignment body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(new { id = await vault.AssignLicenseAsync(actor, body, ctx.TraceIdentifier, ctx.RequestAborted) })));
        api.MapPost("/assignments/{id:guid}/remove", async (Guid id, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.UnassignLicenseAsync(actor, id, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));

        api.MapGet("/audit", async (DateTimeOffset? from, DateTimeOffset? to, string? actorId, string? action,
            Guid? targetId, int? offset, Guid? resourceId, Guid? groupId, bool? includeSubgroups, string? actorProvider,Guid? snapshotId, HttpContext ctx, CurrentActor current, IAuditReader reader,AuditSnapshotStore snapshots,GlobalRoleService globals) =>
            await current.RunAsync(ctx, async actor => {
                var query=new AuditQuery(from,to,actorId,action,targetId,offset??0,resourceId,groupId,includeSubgroups??true,actorProvider);
                if(query.Offset is <0 or >1000000)throw new VaultValidationException("Ogiltig sidposition.");
                var stamp=await globals.AuditAccessStampAsync(actor,ctx.RequestAborted);
                var token=snapshotId??snapshots.Create(actor,query,await globals.VisibleAuditAsync(actor,await reader.ReadSnapshotAsync(actor,query,ctx.RequestAborted),ctx.RequestAborted),stamp);
                var page=snapshots.Page(actor,token,query,stamp);ctx.Response.Headers["X-Valvra-Audit-Snapshot"]=page.Id.ToString();
                ctx.Response.Headers["X-Valvra-Audit-Captured"]=page.CapturedAt.ToString("O");ctx.Response.Headers["X-Valvra-Audit-Expires"]=page.ExpiresAt.ToString("O");return Results.Ok(await globals.VisibleAuditAsync(actor,page.Events,ctx.RequestAborted));
            }));
        api.MapGet("/audit/targets", async (HttpContext ctx, CurrentActor current, IAuditReader reader, IDirectoryProvider directory,GlobalRoleService globals) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await AuditPresentation.NamesAsync(actor,AuditReader.BuildTargets(await globals.VisibleAuditAsync(actor,await reader.ReadSnapshotAsync(actor,new AuditQuery(null,null,null,null,null),ctx.RequestAborted),ctx.RequestAborted)),directory,ctx.RequestAborted))));
        api.MapGet("/audit/export", async (DateTimeOffset? from, DateTimeOffset? to, string? actorId, string? action,
            Guid? targetId, int? offset, Guid? resourceId, Guid? groupId, bool? includeSubgroups, string? actorProvider,Guid? snapshotId, HttpContext ctx, CurrentActor current, IAuditReader reader, AuditService audit,AuditSnapshotStore snapshots,GlobalRoleService globals) =>
            await current.RunAsync(ctx, async actor =>
            {
                var query=new AuditQuery(from,to,actorId,action,targetId,offset??0,resourceId,groupId,includeSubgroups??true,actorProvider);
                var stamp=await globals.AuditAccessStampAsync(actor,ctx.RequestAborted);
                var events=snapshotId is {} token?snapshots.Page(actor,token,query,stamp).Events:await reader.ReadAsync(actor,query,ctx.RequestAborted);
                events=await globals.VisibleAuditAsync(actor,events,ctx.RequestAborted);
                await audit.RecordAsync(actor, "Audit.Export", null, "Success", ctx.TraceIdentifier, ctx.RequestAborted);
                return Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(events), "application/json", "valvra-audit.json");
            }));
        api.MapGet("/health", async (HttpContext ctx, CurrentActor current, Valvra.Infrastructure.Data.VaultDbContext db, AuditService audit) =>
            await current.RunAsync(ctx, async actor =>
            {
                if (!actor.IsAccessAdministrator) throw new AccessDeniedException();
                await audit.RecordAsync(actor, "Health.Check", null, "Requested", ctx.TraceIdentifier, ctx.RequestAborted);
                return Results.Ok(new { vaultDatabase = await db.Database.CanConnectAsync(ctx.RequestAborted),
                    pendingAuditEvents = await db.Audit.CountAsync(x => !x.Delivered, ctx.RequestAborted) });
            }));
    }

    private static void ValidateProfile(string? id, LdapTestOptions options)
    { if (id is not null && !options.Profiles.Any(x => x.Id == id)) throw new VaultValidationException("LDAP-profilen är inte godkänd."); }
}
