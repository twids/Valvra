using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Services;
using Xunit;

namespace Valvra.Tests;

public sealed class AuditScopeTests
{
    [Fact]
    public async Task SecretLicenseAndPermissionEventsCaptureSignedResourceAndAncestry()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var child = await f.Vault.CreateGroupAsync(f.User, "Child", f.GroupId, "test", default);
        var resource = await f.Vault.CreateResourceAsync(f.User, child, "Child server", "test", default);
        var grant = await f.Vault.GrantAsync(f.User, new AccessGrant { TargetKind = TargetKind.Resource, TargetId = resource,
            Provider = "ad", SubjectId = "user", Permissions = VaultPermission.Metadata | VaultPermission.ReadSecret | VaultPermission.Modify }, "test", default);
        var secret = await f.Vault.CreateSecretAsync(f.User, resource, "Account", new("private-user", "private-password", "private-note"), null, "test", default);
        var license = await f.Vault.SaveLicenseAsync(f.User, null, resource, "Product", "Vendor", "Purchase", 2, null, new("private-key", "private-note"), 0, "test", default);
        await f.Vault.RevealSecretAsync(f.User, secret, null, "Secret.Reveal", "test", default);
        await f.Vault.RevealLicenseAsync(f.User, license, "test", default);
        await f.Vault.RevokeAsync(f.User, grant, "test", default);
        var relevant = f.Transport.Events.Where(x => x.TargetId == secret || x.TargetId == license || x.Action.StartsWith("Access.") && x.TargetId == resource).ToArray();
        Assert.NotEmpty(relevant);
        foreach (var e in relevant)
        {
            Assert.Equal(3, e.Format); Assert.Equal(resource, e.Scope!.ResourceId); Assert.Equal(child, e.Scope.GroupId);
            Assert.Equal(new[] { child, f.GroupId }, e.Scope.GroupPath.Select(x => x.Id));
            var json = Encoding.UTF8.GetString(AuditSignature.Serialize(e));
            Assert.DoesNotContain("private-password", json); Assert.DoesNotContain("private-key", json); Assert.DoesNotContain("private-user", json);
        }
    }

    [Fact]
    public async Task ResourceMoveKeepsOldEventsInOldGroupAndNewEventsInNewGroup()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var secret = await f.Vault.CreateSecretAsync(f.User, f.ResourceId, "Account", new("u", "p", "n"), null, "test", default);
        await f.Vault.RevealSecretAsync(f.User, secret, null, "Secret.Reveal", "before-move", default);
        var destination = await f.Vault.CreateGroupAsync(f.User, "New root", null, "test", default);
        await f.Vault.GrantAsync(f.User, new AccessGrant { TargetKind = TargetKind.Group, TargetId = destination,
            Provider = "ad", SubjectId = "user", Permissions = VaultPermission.Metadata | VaultPermission.ReadSecret | VaultPermission.Modify }, "test", default);
        await f.Vault.MoveAsync(f.User, TargetKind.Resource, f.ResourceId, destination, 1, "move", default);
        await f.Vault.RevealSecretAsync(f.User, secret, null, "Secret.Reveal", "after-move", default);
        var old = Query(group: f.GroupId); var next = Query(group: destination);
        Assert.True(old.MatchesScope(f.Transport.Events.Single(x => x.CorrelationId == "before-move")));
        Assert.False(next.MatchesScope(f.Transport.Events.Single(x => x.CorrelationId == "before-move")));
        Assert.True(next.MatchesScope(f.Transport.Events.Single(x => x.CorrelationId == "after-move")));
        Assert.True(old.MatchesScope(f.Transport.Events.Single(x => x.CorrelationId == "move" && x.Phase == AuditPhase.Intent)));
        Assert.True(next.MatchesScope(f.Transport.Events.Single(x => x.CorrelationId == "move" && x.Phase == AuditPhase.Committed)));
    }

    [Fact]
    public async Task GroupMoveAndRenameCannotRewriteExistingAncestorNamesOrMembership()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var child = await f.Vault.CreateGroupAsync(f.User, "Child", f.GroupId, "test", default);
        var resource = await f.Vault.CreateResourceAsync(f.User, child, "Server", "test", default);
        var before = f.Transport.Events.Single(x => x.Action == "Resource.Create" && x.Phase == AuditPhase.Committed);
        var destination = await f.Vault.CreateGroupAsync(f.User, "Other root", null, "test", default);
        await f.Vault.MoveAsync(f.User, TargetKind.Group, child, destination, 1, "move", default);
        await f.Vault.RenameGroupAsync(f.User, destination, "Renamed root", 1, "rename", default);
        Assert.Equal("Root", before.Scope!.GroupPath.Last().Name);
        Assert.True(Query(resource, f.GroupId).MatchesScope(before));
        Assert.False(Query(resource, destination).MatchesScope(before));
        Assert.False(Query(resource, f.GroupId, includeSubgroups: false).MatchesScope(before));
        Assert.True(Query(resource, child, includeSubgroups: false).MatchesScope(before));
    }

    [Fact]
    public async Task CreationAndDeletionIntentsKeepContextEvenWhenObjectDoesNotExist()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var resource = await f.Vault.CreateResourceAsync(f.User, f.GroupId, "Disposable", "create", default);
        await f.Vault.DeleteEmptyTargetAsync(f.User, TargetKind.Resource, resource, 1, "delete", default);
        var events = f.Transport.Events.Where(x => x.TargetId == resource).ToArray();
        Assert.Equal(4, events.Length);
        Assert.All(events, e => { Assert.Equal(resource, e.Scope!.ResourceId); Assert.Equal(f.GroupId, e.Scope.GroupId); });
    }

    [Fact]
    public async Task FailedMutationRetainsOriginalScope()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        await Assert.ThrowsAsync<VaultValidationException>(() => f.Audit.MutationAsync<bool>(f.User, "Test.Fail", f.ResourceId, "test",
            _ => throw new VaultValidationException("Synthetic failure."), default));
        var failed = f.Transport.Events.Single(x => x.Phase == AuditPhase.Failed);
        Assert.Equal(f.ResourceId, failed.Scope!.ResourceId); Assert.Equal(f.GroupId, failed.Scope.GroupId);
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("group")]
    [InlineData("ancestry")]
    [InlineData("name")]
    public async Task ContextTamperingInvalidatesSignature(string changed)
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        await f.Audit.RecordAsync(f.User, "Test", f.ResourceId, "Success", "test", default);
        var e = f.Transport.Events.Single(); var signed = f.AuditSigner.Sign(e);
        var scope = e.Scope!;
        var forged = changed switch
        {
            "resource" => scope with { ResourceId = Guid.NewGuid() },
            "group" => scope with { GroupId = Guid.NewGuid() },
            "ancestry" => scope with { GroupPath = [scope.GroupPath[0], new(Guid.NewGuid(), "Forged ancestor")] },
            _ => scope with { ResourceName = "Forged name" }
        };
        Assert.True(f.AuditSigner.Verify(signed));
        Assert.False(f.AuditSigner.Verify(signed with { Event = e with { Scope = forged } }));
        Assert.False(f.AuditSigner.Verify(signed with { Event = e with { Format = 2 } }));
    }

    [Fact]
    public async Task LegacyAndServiceWideEventsDoNotMatchScopedFilters()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        await f.Audit.RecordAsync(f.User, "Identity.Test", null, "Success", "test", default);
        var e = f.Transport.Events.Single();
        Assert.Equal(2, e.Format); Assert.Null(e.Scope);
        Assert.True(Query().MatchesScope(e)); Assert.False(Query(f.ResourceId).MatchesScope(e)); Assert.False(Query(group: f.GroupId).MatchesScope(e));
        Assert.True(f.AuditSigner.Verify(f.AuditSigner.Sign(e)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("null-node")]
    public async Task MalformedGroupContextIsRejectedWithoutCrashingVerifier(string shape)
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        await f.Audit.RecordAsync(f.User, "Test", f.ResourceId, "Success", "test", default);
        var e = f.Transport.Events.Single(); var signed = f.AuditSigner.Sign(e);
        AuditScopeGroup[] path = shape switch { "missing" => null!, "empty" => [], _ => [null!] };
        Assert.False(f.AuditSigner.Verify(signed with { Event = e with { Scope = e.Scope! with { GroupPath = path } } }));
    }

    [Theory]
    [InlineData("/api/audit?resourceId=11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/audit/export?groupId=11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/audit/targets")]
    public async Task ScopedAuditAndTargetChoicesStillRequireAuditorRole(string path)
    {
        await using var host = new TestWebHost(); using var client = host.Client("other");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task ApiAndExportUseIdenticalCombinedFiltersAndTargetsRetainDeletedObjects()
    {
        await using var host = new TestWebHost(); using var client = host.Client("user");
        using var scope = host.Services.CreateScope(); var vault = scope.ServiceProvider.GetRequiredService<VaultService>();
        var actor = new Actor("ad", "user", "User", new HashSet<string>(), true, true, true);
        var group = await vault.CreateGroupAsync(actor, "Root", null, "test", default);
        var child = await vault.CreateGroupAsync(actor, "Child", group, "test", default);
        var resource = await vault.CreateResourceAsync(actor, child, "Server", "test", default);
        var query = $"resourceId={resource}&groupId={group}&includeSubgroups=true&action=Resource.Create&actorId=user";
        var page = await client.GetFromJsonAsync<AuditEventView[]>("/api/audit?" + query);
        var exported = await client.GetFromJsonAsync<AuditEventView[]>("/api/audit/export?" + query);
        Assert.Equal(2, page!.Length); Assert.Equal(page.Select(x => x.Event.Id), exported!.Select(x => x.Event.Id));
        Assert.Empty((await client.GetFromJsonAsync<AuditEventView[]>("/api/audit?" + query.Replace("true", "false")))!);
        Assert.Empty((await client.GetFromJsonAsync<AuditEventView[]>($"/api/audit?resourceId={Guid.NewGuid()}"))!);
        await vault.DeleteEmptyTargetAsync(actor, TargetKind.Resource, resource, 1, "delete", default);
        var options = await client.GetFromJsonAsync<AuditTargetOptions>("/api/audit/targets");
        Assert.Contains(options!.Resources, x => x.Id == resource && x.Name == "Server");
        Assert.Contains(options.Groups, x => x.Id == group); Assert.Contains(options.Groups, x => x.Id == child);
    }

    private static AuditQuery Query(Guid? resource = null, Guid? group = null, bool includeSubgroups = true) =>
        new(null, null, null, null, null, ResourceId: resource, GroupId: group, IncludeSubgroups: includeSubgroups);
}
