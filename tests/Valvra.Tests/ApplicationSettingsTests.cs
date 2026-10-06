using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Identity;
using Valvra.Infrastructure.Security;
using Valvra.Infrastructure.Services;
using Valvra.Web.Administration;
using Xunit;

namespace Valvra.Tests;

internal sealed class MemoryApplicationSettings : IApplicationSettingsStore
{
    private DirectorySettings current = new(1, "windows", "active-directory", "ad.example.test", 636, "DC=example,DC=test", "", "", 10);
    private readonly object gate = new();
    public DirectorySettings Read() { lock (gate) return current with { }; }
    public DirectorySettings Update(DirectorySettings candidate)
    { lock (gate) { if (candidate.Revision != current.Revision) throw new VaultConflictException("Posten har ändrats. Ladda om före nytt försök."); return current = candidate with { Revision = current.Revision + 1 }; } }
}
internal sealed class FakeDirectoryProbe : IDirectoryConfigurationProbe
{
    public bool Fail { get; set; }
    public int Calls { get; private set; }
    public Task<DirectoryProbeResult> TestAsync(DirectorySettings candidate, string subjectId, string query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Calls++;
        if (Fail) throw new VaultValidationException("Directory test failed.");
        return Task.FromResult(new DirectoryProbeResult("ad", subjectId, [new("ad", "example-person", "Example person", SubjectKind.User)], [new("ad", "example-group", "Example group", SubjectKind.Group)]));
    }
}
public sealed class ApplicationSettingsTests
{
    [Fact]
    public async Task SettingsAndRoleApisDenyOrdinaryUsersIncludingFakeDirectoryAdminClaims()
    {
        await using var host = new TestWebHost(); using var user = host.Client("outsider"); await host.AddCsrfAsync(user);
        foreach (var path in new[] { "/api/settings/directory", "/api/settings/providers", "/api/settings/roles", "/api/settings/people?query=test" })
            Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync(path)).StatusCode);
        var settings = host.Services.GetRequiredService<IApplicationSettingsStore>().Read();
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/settings/directory", new SettingsUpdateRequest(settings, "test"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/settings/roles", new GlobalRoleRequest("ad", "outsider", SubjectKind.User, GlobalRole.AccessAdministrator, 0))).StatusCode);
    }
    [Fact]
    public async Task SafeSettingsRoundTripTestsBeforeApplyingAndProtectsAgainstStaleWrites()
    {
        await using var host = new TestWebHost(); using var admin = host.Client("user"); await host.AddCsrfAsync(admin);
        var json = await admin.GetStringAsync("/api/settings/directory"); Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", json, StringComparison.OrdinalIgnoreCase);
        var old = JsonSerializer.Deserialize<DirectorySettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var candidate = old with { UserSearchBaseDn = "OU=Users,DC=example,DC=test", GroupSearchBaseDn = "OU=Groups,DC=example,DC=test" };
        using var saved = await admin.PostAsJsonAsync("/api/settings/directory", new SettingsUpdateRequest(candidate, "test"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(old.Revision + 1, (await saved.Content.ReadFromJsonAsync<DirectorySettings>())!.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/settings/directory", new SettingsUpdateRequest(candidate, "test"))).StatusCode);
        Assert.Contains(host.Transport.Events, x => x.Action == "Settings.DirectoryUpdate" && x.Outcome == "Committed");
    }
    [Fact]
    public async Task FailedConnectionTestLeavesSavedSettingsUntouched()
    {
        await using var host = new TestWebHost(); using var admin = host.Client("user"); await host.AddCsrfAsync(admin);
        var store = host.Services.GetRequiredService<IApplicationSettingsStore>(); var old = store.Read();
        ((FakeDirectoryProbe)host.Services.GetRequiredService<IDirectoryConfigurationProbe>()).Fail = true;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/settings/directory", new SettingsUpdateRequest(old with { Server = "new.example.test" }, "test"))).StatusCode);
        Assert.Equal(old, store.Read()); Assert.Contains(host.Transport.Events, x => x.Action == "Settings.DirectoryUpdate" && x.Phase == AuditPhase.Failed);
    }
    [Fact]
    public async Task GlobalRolesRejectGroupsAndDoNotImplicitlyExposeSecretsOrResourceMetadata()
    {
        await using var host = new TestWebHost(); using var admin = host.Client("user"); await host.AddCsrfAsync(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/settings/roles", new GlobalRoleRequest("ad", "group", SubjectKind.Group, GlobalRole.AccessAdministrator, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/settings/roles", new GlobalRoleRequest("ad", "outsider", SubjectKind.User, GlobalRole.SystemAdministrator, 0))).StatusCode);
        using var other = host.Client("outsider"); await host.AddCsrfAsync(other);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/settings/directory")).StatusCode);
        Assert.Equal("[]", await other.GetStringAsync("/api/resources"));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"/api/secrets/{Guid.NewGuid()}/reveal", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync("/api/settings/roles", new GlobalRoleRequest("ad", "outsider", SubjectKind.User, GlobalRole.AccessAdministrator, 1))).StatusCode);
    }
    [Fact]
    public async Task LastAdministratorCannotBeRemovedAndRoleUpdatesAreOptimistic()
    {
        await using var host = new TestWebHost(); using var admin = host.Client("user"); await host.AddCsrfAsync(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/settings/roles", new GlobalRoleRequest("ad", "user", SubjectKind.User, GlobalRole.None, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/settings/roles", new GlobalRoleRequest("ad", "user", SubjectKind.User, GlobalRole.Auditor, 0))).StatusCode);
    }
    [Theory]
    [InlineData("ldap://evil.example", "windows", "OU=Users,DC=example,DC=test")]
    [InlineData("ad.example.test", "missing", "OU=Users,DC=example,DC=test")]
    [InlineData("ad.example.test", "windows", "DC=other,DC=test")]
    public async Task UnsafeOrUnsupportedDirectorySettingsAreRejectedBeforeConnecting(string server, string login, string userBase)
    {
        await using var host = new TestWebHost(); using var admin = host.Client("user"); await host.AddCsrfAsync(admin);
        var old = host.Services.GetRequiredService<IApplicationSettingsStore>().Read();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/settings/directory", new SettingsUpdateRequest(old with { Server = server, LoginProviderId = login, UserSearchBaseDn = userBase }, "test"))).StatusCode);
        Assert.Equal(0, ((FakeDirectoryProbe)host.Services.GetRequiredService<IDirectoryConfigurationProbe>()).Calls);
    }
    [Fact]
    public async Task SettingsChangesRequireCsrf()
    {
        await using var host = new TestWebHost(); using var admin = host.Client("user");
        var old = host.Services.GetRequiredService<IApplicationSettingsStore>().Read();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/settings/directory", new SettingsUpdateRequest(old, "test"))).StatusCode);
        Assert.Equal(old, host.Services.GetRequiredService<IApplicationSettingsStore>().Read());
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("x")]
    public async Task MalformedSettingsRequestsFailBeforeProbing(string? query)
    {
        await using var host = new TestWebHost(); using var admin = host.Client("user"); await host.AddCsrfAsync(admin);
        var old = host.Services.GetRequiredService<IApplicationSettingsStore>().Read();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/settings/directory", new {settings = old, query})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/settings/directory/test", new {settings = (object?)null, query = "test"})).StatusCode);
        Assert.Equal(0, ((FakeDirectoryProbe)host.Services.GetRequiredService<IDirectoryConfigurationProbe>()).Calls);
    }
    [Fact]
    public async Task AuditOutagePreventsConfigurationAndRoleChanges()
    {
        await using var host = new TestWebHost(); using var admin = host.Client("user"); await host.AddCsrfAsync(admin);
        var store = host.Services.GetRequiredService<IApplicationSettingsStore>(); var old = store.Read(); host.Transport.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.PostAsJsonAsync("/api/settings/directory", new SettingsUpdateRequest(old with {Server="other.example.test"}, "test"))).StatusCode);
        Assert.Equal(old, store.Read());
        Assert.Equal(0, ((FakeDirectoryProbe)host.Services.GetRequiredService<IDirectoryConfigurationProbe>()).Calls);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.PostAsJsonAsync("/api/settings/roles", new GlobalRoleRequest("ad", "outsider", SubjectKind.User, GlobalRole.SystemAdministrator, 0))).StatusCode);
        using var scope = host.Services.CreateScope(); Assert.Single(await scope.ServiceProvider.GetRequiredService<VaultDbContext>().Grants.Where(x=>x.TargetKind==TargetKind.System).ToListAsync());
    }
    [Fact]
    public async Task RevokedRolesAreNotRestoredByDirectoryClaimsOnTheNextRequest()
    {
        await using var host = new TestWebHost(); using var admin = host.Client("user"); await host.AddCsrfAsync(admin);
        ((FakeDirectory)host.Services.GetRequiredService<IDirectoryProvider>()).ReportGlobalRoles=true;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/settings/roles", new GlobalRoleRequest("ad", "outsider", SubjectKind.User, GlobalRole.AccessAdministrator|GlobalRole.SystemAdministrator, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/settings/roles", new GlobalRoleRequest("ad", "user", SubjectKind.User, GlobalRole.None, 1))).StatusCode);
        var session = await admin.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.False(session.GetProperty("isAccessAdministrator").GetBoolean()); Assert.False(session.GetProperty("isSystemAdministrator").GetBoolean()); Assert.False(session.GetProperty("isAuditor").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/settings/directory")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/groups", new {name="Unauthorized", parentId=(Guid?)null})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/audit")).StatusCode);
    }
    [Fact]
    public async Task BootstrapIsAuditedPersonBoundAndCannotBeClaimedByAnotherInstaller()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var globals = new GlobalRoleService(f.Db,f.Integrity,f.Audit,new FakeDirectory(),f.Clock);
        await globals.BootstrapAsync(f.User,"setup",default); await globals.BootstrapAsync(f.User,"retry",default);
        var user = await globals.ApplyAsync(f.User,default); Assert.True(user.IsAccessAdministrator); Assert.True(user.IsSystemAdministrator); Assert.False(user.IsAuditor);
        var outsider = f.User with {SubjectId="outsider",GroupIds=new HashSet<string>{"user"}};
        var ordinary = await globals.ApplyAsync(outsider,default); Assert.False(ordinary.IsAccessAdministrator); Assert.False(ordinary.IsSystemAdministrator);
        await Assert.ThrowsAsync<AccessDeniedException>(()=>globals.BootstrapAsync(outsider,"race",default));
        Assert.Single(f.Transport.Events,x=>x.Action=="Installation.BootstrapAdministrator" && x.Outcome=="Committed");
        Assert.Equal(GlobalRole.AccessAdministrator|GlobalRole.SystemAdministrator,(await globals.ReadAsync(user,default)).Single().Roles);
        Assert.Equal(VaultPermission.None, PermissionEvaluator.Effective(ordinary,await f.Db.Grants.ToListAsync(),new HashSet<Guid>{f.GroupId},f.ResourceId,f.Clock.GetUtcNow()));
    }
    [Fact]
    public async Task TamperingWithGlobalRoleStorageFailsIntegrityVerification()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var globals = new GlobalRoleService(f.Db,f.Integrity,f.Audit,new FakeDirectory(),f.Clock);
        await globals.BootstrapAsync(f.User,"setup",default);
        await f.Db.Database.ExecuteSqlRawAsync("UPDATE Grants SET SubjectId = 'outsider' WHERE TargetKind = 2");
        await Assert.ThrowsAsync<VaultUnavailableException>(()=>globals.ApplyAsync(f.User with {SubjectId="outsider"},default));
    }
    [Fact]
    public void RegistryRejectsUnknownAndIncompatibleModulesAndRuntimeOptionsAreCopied()
    {
        var registry = ProviderRegistry.BuiltIn(); Assert.Throws<VaultValidationException>(()=>registry.Validate(new(){LoginProviderId="oidc"}));
        var incompatible = new ProviderRegistry([new(new("windows","Windows","another"),_=>new WindowsIdentityProvider())],
            [new(new("active-directory","AD","ad"),_=>new FakeDirectory())]);
        Assert.Throws<VaultValidationException>(()=>incompatible.Validate(new()));
        var options = new ActiveDirectoryOptions {Server="original",BaseDn="DC=example"}; var runtime = new DirectoryConfiguration(options);
        options.Server="altered"; Assert.Equal("original",runtime.Current.Server);
        var snapshot = runtime.Current; snapshot.BaseDn="mutated"; Assert.Equal("DC=example",runtime.Current.BaseDn);
        runtime.Apply(options); options.Server="again"; Assert.Equal("altered",runtime.Current.Server);
    }
    [Fact]
    public async Task BootstrapRetryMustDeliverItsPendingAuditBeforeFinishing()
    {
        await using var f=await VaultServiceTests.Fixture.CreateAsync();
        var globals=new GlobalRoleService(f.Db,f.Integrity,f.Audit,new FakeDirectory(),f.Clock);
        f.Transport.FailPhase=AuditPhase.Committed;
        await Assert.ThrowsAsync<VaultUnavailableException>(()=>globals.BootstrapAsync(f.User,"setup",default));
        await Assert.ThrowsAsync<VaultUnavailableException>(()=>globals.BootstrapAsync(f.User,"retry",default));
        f.Transport.FailPhase=null; await globals.BootstrapAsync(f.User,"retry",default);
        Assert.Single(f.Transport.Events,x=>x.Action=="Installation.BootstrapAdministrator" && x.Phase==AuditPhase.Committed);
        Assert.False(await f.Db.Audit.AnyAsync(x=>!x.Delivered));
    }
}
