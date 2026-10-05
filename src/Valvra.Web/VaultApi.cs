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
public sealed record SecretRequest(string Title, SecretPayload Payload, string? LdapProfileId, long Revision = 0);
public sealed record RevealRequest(int? Version = null, bool Copy = false);
public sealed record RestoreRequest(int Version, long Revision);
public sealed record DeleteRequest(bool Deleted, long Revision);
public sealed record LicenseRequest(Guid ResourceId, string Product, string Vendor, string PurchaseReference,
    int Seats, DateTimeOffset? ExpiresAt, LicensePayload Payload, long Revision = 0);

public static class VaultApi
{
    public static void MapVaultApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
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
                return Results.Ok(new { actor.DisplayName, actor.SubjectId, actor.IsAccessAdministrator, actor.IsAuditor,
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
                return Results.Ok(new { id = await vault.CreateSecretAsync(actor, resourceId, body.Title, body.Payload, body.LdapProfileId, ctx.TraceIdentifier, ctx.RequestAborted) });
            }));
        api.MapPost("/secrets/{id:guid}/update", async (Guid id, SecretRequest body, HttpContext ctx, CurrentActor current, VaultService vault, LdapTestOptions ldap) =>
            await current.RunAsync(ctx, async actor =>
            {
                ValidateProfile(body.LdapProfileId, ldap);
                await vault.UpdateSecretAsync(actor, id, body.Title, body.Payload, body.LdapProfileId, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted);
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
        api.MapPost("/licenses", async (LicenseRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(new { id = await vault.SaveLicenseAsync(actor, null, body.ResourceId, body.Product,
                body.Vendor, body.PurchaseReference, body.Seats, body.ExpiresAt, body.Payload, 0, ctx.TraceIdentifier, ctx.RequestAborted) })));
        api.MapPost("/licenses/{id:guid}/update", async (Guid id, LicenseRequest body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(new { id = await vault.SaveLicenseAsync(actor, id, body.ResourceId, body.Product,
                body.Vendor, body.PurchaseReference, body.Seats, body.ExpiresAt, body.Payload, body.Revision, ctx.TraceIdentifier, ctx.RequestAborted) })));
        api.MapPost("/licenses/{id:guid}/reveal", async (Guid id, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.RevealLicenseAsync(actor, id, ctx.TraceIdentifier, ctx.RequestAborted))));
        api.MapGet("/licenses/{id:guid}/assignments", async (Guid id, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await vault.AssignmentsAsync(actor, id, ctx.RequestAborted))));
        api.MapPost("/assignments", async (LicenseAssignment body, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => Results.Ok(new { id = await vault.AssignLicenseAsync(actor, body, ctx.TraceIdentifier, ctx.RequestAborted) })));
        api.MapPost("/assignments/{id:guid}/remove", async (Guid id, HttpContext ctx, CurrentActor current, VaultService vault) =>
            await current.RunAsync(ctx, async actor => { await vault.UnassignLicenseAsync(actor, id, ctx.TraceIdentifier, ctx.RequestAborted); return Results.NoContent(); }));

        api.MapGet("/audit", async (DateTimeOffset? from, DateTimeOffset? to, string? actorId, string? action,
            Guid? targetId, int? offset, HttpContext ctx, CurrentActor current, IAuditReader reader) =>
            await current.RunAsync(ctx, async actor => Results.Ok(await reader.ReadAsync(actor, new(from, to, actorId, action, targetId, offset ?? 0), ctx.RequestAborted))));
        api.MapGet("/audit/export", async (DateTimeOffset? from, DateTimeOffset? to, string? actorId, string? action,
            Guid? targetId, int? offset, HttpContext ctx, CurrentActor current, IAuditReader reader, AuditService audit) =>
            await current.RunAsync(ctx, async actor =>
            {
                var events = await reader.ReadAsync(actor, new(from, to, actorId, action, targetId, offset ?? 0), ctx.RequestAborted);
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
