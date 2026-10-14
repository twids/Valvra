using System.Net;using System.Net.Http.Json;using System.Text.Json;
using Valvra.Core;using Valvra.Infrastructure.Auditing;using Valvra.Web;using Valvra.Web.Administration;using Xunit;
namespace Valvra.Tests;
public sealed class AuditSnapshotTests{
 private sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
 private static Actor Auditor(string id="user")=>new("ad",id,id,new HashSet<string>(),true,false,true);
 private static AuditEvent Event(int index)=>new(Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow.AddHours(-1).AddSeconds(index),"ad","user","Snapshot.Test",null,AuditPhase.Event,"Success","test");
 [Fact]public void CacheIsImmutableOwnerBoundExpiringAndRejectsFilterChanges(){
  var clock=new Clock();var store=new AuditSnapshotStore(clock);var group=new[]{new AuditScopeGroup(Guid.NewGuid(),"Original")};var view=new AuditEventView(Event(0) with{Scope=new(null,null,group[0].Id,group)},"hash","key",true);var query=new AuditQuery(null,null,null,null,null);
  var token=store.Create(Auditor(),query,[view]);group[0]=group[0] with{Name="Changed"};Assert.Equal("Original",Assert.Single(store.Page(Auditor(),token,query).Events).Event.Scope!.GroupPath[0].Name);
  Assert.Throws<AccessDeniedException>(()=>store.Page(Auditor("other"),token,query));Assert.Throws<AccessDeniedException>(()=>store.Page(Auditor() with{IsAuditor=false},token,query));
  Assert.Throws<VaultValidationException>(()=>store.Page(Auditor(),token,query with{Action="Different"}));clock.Now=clock.Now.AddMinutes(6);Assert.Throws<VaultConflictException>(()=>store.Page(Auditor(),token,query));
 }
 [Fact]public void ChangedAccessPolicyInvalidatesCapturedPagesBeforePagination(){
  var store=new AuditSnapshotStore();var query=new AuditQuery(null,null,null,null,null);
  var token=store.Create(Auditor(),query,Enumerable.Range(0,300).Select(i=>new AuditEventView(Event(i),"hash","key",true)).ToArray(),"before");
  Assert.Equal(200,store.Page(Auditor(),token,query,"before").Events.Count);
  Assert.Throws<VaultConflictException>(()=>store.Page(Auditor(),token,query,"after"));
  Assert.Throws<VaultConflictException>(()=>store.Page(Auditor(),token,query with{Offset=200},"before"));
 }
 [Fact]public void CacheRetainsOnlyTwoViewsPerOwnerAndRejectsOversizeSnapshots(){
  var store=new AuditSnapshotStore();var query=new AuditQuery(null,null,null,null,null);var first=store.Create(Auditor(),query,[]);store.Create(Auditor(),query,[]);var last=store.Create(Auditor(),query,[]);
  Assert.Throws<VaultConflictException>(()=>store.Page(Auditor(),first,query));Assert.Empty(store.Page(Auditor(),last,query).Events);
  Assert.Throws<VaultValidationException>(()=>store.Create(Auditor(),query,[new(Event(0) with{DetailsJson=new string('x',9*1024*1024)},"hash","key",true)]));
 }
 [Fact]public async Task PagingAndExportKeepExactEventsWhenNewMatchingEventsArrive(){
  await using var host=new TestWebHost();using var client=host.Client("user");await host.AddCsrfAsync(client);
  for(var i=0;i<300;i++)host.Transport.Events.Add(Event(i));
  using var first=await client.GetAsync("/api/audit?action=Snapshot.Test");first.EnsureSuccessStatusCode();var rows=await first.Content.ReadFromJsonAsync<JsonElement[]>();var token=first.Headers.GetValues("X-Valvra-Audit-Snapshot").Single();
  host.Transport.Events.Add(Event(5000));
  var second=await client.GetFromJsonAsync<JsonElement[]>($"/api/audit?action=Snapshot.Test&snapshotId={token}&offset=200");Assert.Equal(100,second!.Length);
  using var exported=await client.GetAsync($"/api/audit/export?action=Snapshot.Test&snapshotId={token}");exported.EnsureSuccessStatusCode();var values=await exported.Content.ReadFromJsonAsync<JsonElement[]>();
  Assert.Equal(rows!.Select(x=>x.GetProperty("event").GetProperty("id").GetGuid()),values!.Select(x=>x.GetProperty("Event").GetProperty("Id").GetGuid()));
  Assert.Empty(rows!.Select(x=>x.GetProperty("event").GetProperty("id").GetGuid()).Intersect(second!.Select(x=>x.GetProperty("event").GetProperty("id").GetGuid())));
  Assert.Contains(host.Transport.Events,x=>x.Action=="Audit.Export"&&x.Outcome=="Success");
  (await client.PostAsJsonAsync("/api/settings/roles",new GlobalRoleRequest("ad","outsider",SubjectKind.User,GlobalRole.Auditor,0))).EnsureSuccessStatusCode();using var other=host.Client("outsider");Assert.Equal(HttpStatusCode.Forbidden,(await other.GetAsync($"/api/audit?snapshotId={token}&action=Snapshot.Test")).StatusCode);
  (await client.PostAsJsonAsync("/api/settings/roles",new GlobalRoleRequest("ad","user",SubjectKind.User,GlobalRole.AccessAdministrator|GlobalRole.SystemAdministrator,1))).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"/api/audit/export?snapshotId={token}&action=Snapshot.Test")).StatusCode);
 }
 [Fact]public async Task SnapshotExportFailsClosedIfAuditAcknowledgementFails(){
  await using var host=new TestWebHost();using var client=host.Client("user");using var first=await client.GetAsync("/api/audit");first.EnsureSuccessStatusCode();var token=first.Headers.GetValues("X-Valvra-Audit-Snapshot").Single();host.Transport.Fail=true;
  Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.GetAsync($"/api/audit/export?snapshotId={token}")).StatusCode);
 }
}
