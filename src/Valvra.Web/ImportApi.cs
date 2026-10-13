using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Authorization;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Services;

namespace Valvra.Web;

public sealed record ImportOperation(string Key, string Kind, string Target, string Name, SecretPayload? Payload = null, int? Row = null);
public sealed record ImportBatchRequest(IReadOnlyList<ImportOperation> Operations);
public sealed record ImportOperationResult(string Key, Guid Id, bool Created, int? Row);
public sealed record ImportBatchResult(IReadOnlyList<ImportOperationResult> Results, string? ErrorCode = null, string? FailedKey = null, int? UncertainRow = null);

public static class ImportApi
{
    public static void MapImportApi(this WebApplication app)
    {
        app.MapPost("/api/imports/batch", async (ImportBatchRequest body, HttpContext ctx, CurrentActor current,
            GlobalRoleService globals, AccessService access, VaultService vault, VaultDbContext db, AuditService audit) =>
            await current.RunAsync(ctx, async actor =>
            {
                await globals.RequireAsync(actor, GlobalRole.AccessAdministrator, ctx.RequestAborted);
                if (body.Operations is null || body.Operations.Count is < 1 or > 12
                    || body.Operations.Any(x => x is null || string.IsNullOrWhiteSpace(x.Key) || x.Key.Length > 64
                        || x.Kind is not ("group" or "resource" or "secret") || string.IsNullOrWhiteSpace(x.Target) || x.Target.Length > 64
                        || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 200
                        || (x.Kind == "secret" ? x.Payload is null || x.Row is < 2 or > 1001 or null : x.Payload is not null || x.Row is not null))
                    || body.Operations.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != body.Operations.Count)
                    throw new VaultValidationException("Ogiltig importbatch.");

                var results = new List<ImportOperationResult>();
                var references = new Dictionary<string, Guid>(StringComparer.Ordinal);
                var titles = new Dictionary<Guid, HashSet<string>>();
                foreach (var operation in body.Operations)
                {
                    var ct = ctx.RequestAborted;
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await globals.RequireAsync(actor, GlobalRole.AccessAdministrator, ct);
                        var target = Guid.TryParse(operation.Target, out var guid) ? guid
                            : references.TryGetValue(operation.Target, out var reference) ? reference : throw new VaultValidationException("Ogiltig importreferens.");
                        var kind = operation.Kind == "secret" ? TargetKind.Resource : TargetKind.Group;
                        await access.RequireAsync(actor, kind, target, VaultPermission.Metadata | VaultPermission.Modify, ct);
                        Guid id; var created = true;
                        if (operation.Kind == "group")
                        {
                            await access.RequireAsync(actor, TargetKind.Group, target, VaultPermission.ManageAccess, ct);
                            var siblings = await db.Groups.AsNoTracking().Where(x => x.ParentId == target).Select(x => new {x.Id,x.Name}).ToListAsync(ct);
                            var matches = siblings.Where(x => Key(x.Name) == Key(operation.Name)).Take(2).ToArray();
                            if (matches.Length > 1) throw new VaultValidationException("Tvetydig importdestination.");
                            created = matches.Length == 0;
                            id = created ? await vault.CreateGroupAsync(actor, operation.Name, target, ctx.TraceIdentifier, ct) : matches[0].Id;
                        }
                        else if (operation.Kind == "resource")
                        {
                            await access.RequireAsync(actor, TargetKind.Group, target, VaultPermission.ManageAccess, ct);
                            var siblings = await db.Resources.AsNoTracking().Where(x => x.GroupId == target).Select(x => new {x.Id,x.Name}).ToListAsync(ct);
                            var matches = siblings.Where(x => Key(x.Name) == Key(operation.Name)).Take(2).ToArray();
                            if (matches.Length > 1) throw new VaultValidationException("Tvetydig importdestination.");
                            created = matches.Length == 0;
                            id = created ? await vault.CreateResourceAsync(actor, target, operation.Name, ctx.TraceIdentifier, ct) : matches[0].Id;
                        }
                        else
                        {
                            if (!titles.TryGetValue(target, out var known))
                                titles[target] = known = (await vault.SecretsAsync(actor, target, ct)).Select(x => Key(x.Title)).ToHashSet(StringComparer.Ordinal);
                            created = !known.Contains(Key(operation.Name));
                            id = created ? await vault.CreateSecretAsync(actor, target, operation.Name, operation.Payload!, null, ctx.TraceIdentifier, ct) : Guid.Empty;
                            known.Add(Key(operation.Name));
                        }
                        references.Add(operation.Key, id); results.Add(new(operation.Key, id, created, operation.Row));
                    }
                    catch (Exception failure) when (failure is AccessDeniedException or VaultValidationException or VaultConflictException or VaultUnavailableException)
                    {
                        if (failure is AccessDeniedException)
                            await audit.RecordAsync(actor, "Access.Denied", null, "Denied", ctx.TraceIdentifier, ct);
                        // Never return exception text or source values. An audit-ack failure may follow a durable write.
                        var code = failure switch { AccessDeniedException => "Denied", VaultValidationException => "Invalid", VaultConflictException => "Conflict", _ => "Unavailable" };
                        return Results.Ok(new ImportBatchResult(results, code, operation.Key, failure is VaultUnavailableException ? operation.Row : null));
                    }
                }
                return Results.Ok(new ImportBatchResult(results));
            })).RequireAuthorization().WithMetadata(new RequestSizeLimitAttribute(2 * 1024 * 1024));
    }
    private static string Key(string value) => value.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
}
