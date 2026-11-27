using Microsoft.EntityFrameworkCore;
using Valvra.Core;

namespace Valvra.Infrastructure.Services;

public sealed record ContentSearchHit(string Kind, string Title, Guid ResourceId, string ResourceName, Guid Id);
public sealed record ContentSearchPage(IReadOnlyList<ContentSearchHit> Items, bool HasMore);

public sealed partial class VaultOperations
{
    public async Task<ContentSearchPage> SearchContentAsync(Actor actor, string? query, CancellationToken ct, bool accountsOnly = false, int offset = 0)
    {
        var term = query?.Trim().ToLowerInvariant() ?? "";
        if ((!accountsOnly && term.Length == 0) || term.Length > 200 || offset < 0)
            throw new VaultValidationException("Ange ett sökord på 1–200 tecken.");

        var accounts = db.Secrets.Where(x => !x.Deleted && x.Title.ToLower().Contains(term));
        var licenses = db.Licenses.Where(x => !accountsOnly && !x.Deleted && x.Product.ToLower().Contains(term));
        var candidates = await accounts.Select(x => x.ResourceId).Union(licenses.Select(x => x.ResourceId)).ToListAsync(ct);
        var accountScope = new List<Guid>();
        var licenseScope = new List<Guid>();
        foreach (var id in candidates)
        {
            var permissions = await access.EffectiveAsync(actor, TargetKind.Resource, id, ct);
            if (permissions.HasFlag(VaultPermission.Metadata))
            {
                accountScope.Add(id);
                licenseScope.Add(id);
            }
            else if (actor.IsEnabled && actor.IsLicenseAdministrator && !await access.IsExcludedAsync(actor, TargetKind.Resource, id, ct))
                licenseScope.Add(id);
        }

        var hits = accounts.Where(x => accountScope.Contains(x.ResourceId))
            .Select(x => new { x.Id, x.ResourceId, x.Title, Kind = "account" })
            .Concat(licenses.Where(x => licenseScope.Contains(x.ResourceId))
                .Select(x => new { x.Id, x.ResourceId, Title = x.Product, Kind = "license" }));
        const int limit = 50;
        var items = await (from hit in hits
                           join r in db.Resources on hit.ResourceId equals r.Id
                           orderby hit.Title, r.Name, hit.ResourceId, hit.Kind, hit.Id
                           select new ContentSearchHit(hit.Kind, hit.Title, r.Id, r.Name, hit.Id)).Skip(offset).Take(limit + 1).ToListAsync(ct);
        return new(items.Take(limit).ToArray(), items.Count > limit);
    }
}
