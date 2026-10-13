using System.Text.Json;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;

namespace Valvra.Web;

public sealed record AuditSnapshotPage(Guid Id,DateTimeOffset CapturedAt,DateTimeOffset ExpiresAt,IReadOnlyList<AuditEventView> Events);
public sealed class AuditSnapshotStore(TimeProvider? timeProvider=null)
{
    private sealed record Snapshot(string Provider,string SubjectId,AuditQuery Query,DateTimeOffset CapturedAt,DateTimeOffset ExpiresAt,byte[] Payload);
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    private readonly Dictionary<Guid,Snapshot> snapshots=[];
    private readonly object gate=new();
    private const int MaximumSnapshotBytes=8*1024*1024,MaximumCacheBytes=32*1024*1024;
    private static void Require(Actor actor){if(!actor.IsEnabled||!actor.IsAuditor)throw new AccessDeniedException();}
    public Guid Create(Actor actor,AuditQuery query,IReadOnlyList<AuditEventView> events)
    {
        Require(actor);
        var payload=JsonSerializer.SerializeToUtf8Bytes(events);
        if(payload.Length>MaximumSnapshotBytes)throw new VaultValidationException("Auditvyn är för stor. Begränsa datumintervallet eller filtren.");
        lock(gate){
            var now=clock.GetUtcNow();foreach(var id in snapshots.Where(x=>x.Value.ExpiresAt<=now).Select(x=>x.Key).ToArray())snapshots.Remove(id);
            var owned=snapshots.Where(x=>x.Value.Provider==actor.Provider&&x.Value.SubjectId==actor.SubjectId).OrderBy(x=>x.Value.CapturedAt).ToArray();
            foreach(var item in owned.Take(Math.Max(0,owned.Length-1)))snapshots.Remove(item.Key);
            while(snapshots.Count>=32||snapshots.Values.Sum(x=>x.Payload.Length)+payload.Length>MaximumCacheBytes)snapshots.Remove(snapshots.MinBy(x=>x.Value.CapturedAt).Key);
            var token=Guid.NewGuid();snapshots[token]=new(actor.Provider,actor.SubjectId,query with{Offset=0},now,now.AddMinutes(5),payload);return token;
        }
    }
    public AuditSnapshotPage Page(Actor actor,Guid token,AuditQuery query)
    {
        Require(actor);if(query.Offset is <0 or >1000000)throw new VaultValidationException("Ogiltig sidposition.");
        lock(gate){
            if(!snapshots.TryGetValue(token,out var snapshot)||snapshot.ExpiresAt<=clock.GetUtcNow()){snapshots.Remove(token);throw new VaultConflictException("Auditvyn har löpt ut. Läs aktuella händelser innan du fortsätter.");}
            if(snapshot.Provider!=actor.Provider||snapshot.SubjectId!=actor.SubjectId)throw new AccessDeniedException();
            if(snapshot.Query!=(query with{Offset=0}))throw new VaultValidationException("Filtren stämmer inte med auditvyn. Läs aktuella händelser.");
            // Deserialize inside the bounded cache lock: only one full snapshot
            // is materialized at a time, and callers cannot mutate cached bytes.
            var rows=JsonSerializer.Deserialize<AuditEventView[]>(snapshot.Payload)!;
            return new(token,snapshot.CapturedAt,snapshot.ExpiresAt,rows.Skip(query.Offset).Take(200).ToArray());
        }
    }
}
