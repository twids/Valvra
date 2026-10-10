using System.Text;
using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Security;

namespace Valvra.Infrastructure.Auditing;

public sealed class AuditService(VaultDbContext db, IAuditTransport transport, TimeProvider clock, IAuditSigner signer, VaultIntegrity integrity)
{
    private AuditRecord Create(Actor actor, string action, Guid? target, AuditPhase phase, string outcome,
        string correlationId, Guid? operationId = null, string detailsJson = "{}", AuditScope? scope = null)
    {
        var value = new AuditEvent(Guid.NewGuid(), operationId ?? Guid.NewGuid(), clock.GetUtcNow(), actor.Provider,
            actor.SubjectId, action, target, phase, outcome, correlationId, detailsJson, integrity.InstallationId,
            Format: scope is null ? 2 : 3, Scope: scope);
        var signed = signer.Sign(value);
        return new AuditRecord
        {
            Id = value.Id, OperationId = value.OperationId, Timestamp = value.Timestamp, ActorProvider = value.ActorProvider,
            ActorId = value.ActorId, Action = value.Action, TargetId = value.TargetId, Phase = value.Phase, Outcome = value.Outcome,
            CorrelationId = value.CorrelationId, DetailsJson = value.DetailsJson, InstallationId = value.InstallationId,
            EventJson = Encoding.UTF8.GetString(signed.Payload), PayloadHash = signed.PayloadHash,
            SigningKeyId = signed.SigningKeyId, Signature = signed.Signature
        };
    }
    public Task RecordAsync(Actor actor, string action, Guid? target, string outcome, string correlationId, CancellationToken ct) =>
        integrity.RunAsync(async () =>
        {
            var scope = await AuditScopeCapture.CaptureAsync(db, target, null, ct);
            var record = Create(actor, action, target, AuditPhase.Event, outcome, correlationId, scope: scope);
            db.Audit.Add(record); await integrity.SaveAsync(ct); await DeliverAsync(record, ct);
        }, ct);

    public Task<T> MutationAsync<T>(Actor actor, string action, Guid? target, string correlationId,
        Func<CancellationToken, Task<T>> mutation, CancellationToken ct, string detailsJson = "{}", AuditScopeHint? scopeHint = null) =>
        integrity.RunAsync(async () =>
        {
            await FlushCoreAsync(ct);
            var before = await AuditScopeCapture.CaptureAsync(db, target, scopeHint, ct);
            var intent = Create(actor, action, target, AuditPhase.Intent, "Requested", correlationId, detailsJson: detailsJson, scope: before);
            db.Audit.Add(intent); await integrity.SaveAsync(ct); await DeliverAsync(intent, ct);
            T result; AuditRecord completed;
            try
            {
                result = await mutation(ct);
                var after = await AuditScopeCapture.CaptureAsync(db, target, scopeHint, ct) ?? before;
                completed = Create(actor, action, target, AuditPhase.Committed, "Committed", correlationId, intent.OperationId, detailsJson, after);
                db.Audit.Add(completed);
                await integrity.SaveAsync(ct);
            }
            catch
            {
                var uncertain = integrity.CommitUncertain;
                await integrity.RestartAsync(CancellationToken.None);
                var failed = Create(actor, action, target, uncertain ? AuditPhase.Event : AuditPhase.Failed,
                    uncertain ? "CommitUncertain" : "Failed", correlationId, intent.OperationId, detailsJson, before);
                db.Audit.Add(failed); await integrity.SaveAsync(CancellationToken.None); await DeliverAsync(failed, CancellationToken.None);
                throw;
            }
            await DeliverAsync(completed, ct);
            return result;
        }, ct);

    public Task FlushAsync(CancellationToken ct) => integrity.RunAsync(() => FlushCoreAsync(ct), ct);
    private async Task FlushCoreAsync(CancellationToken ct)
    {
        var pending = await db.Audit.Where(x => !x.Delivered).OrderBy(x => x.Timestamp).ThenBy(x => x.Id).Take(500).ToListAsync(ct);
        foreach (var record in pending) await DeliverAsync(record, ct);
        if (await db.Audit.AnyAsync(x => !x.Delivered, ct)) throw new VaultUnavailableException("Auditavstämning pågår.");
    }
    private async Task DeliverAsync(AuditRecord record, CancellationToken ct)
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<AuditEvent>(record.EventJson)
            ?? throw new VaultUnavailableException("Ogiltig audithändelse.");
        var value = new AuditEvent(record.Id, record.OperationId, record.Timestamp, record.ActorProvider,
            record.ActorId, record.Action, record.TargetId, record.Phase, record.Outcome, record.CorrelationId, record.DetailsJson, record.InstallationId,
            payload.Format, payload.Scope);
        var signed = new SignedAuditEvent(value, record.PayloadHash, record.SigningKeyId, record.Signature, Encoding.UTF8.GetBytes(record.EventJson));
        if (value.InstallationId != integrity.InstallationId || !signer.Verify(signed))
            throw new VaultUnavailableException("Audithändelsens signatur kan inte verifieras.");
        AuditReceipt receipt;
        try { receipt = await transport.SendAsync(signed, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new VaultUnavailableException("Auditmottagaren är inte tillgänglig. Operationen kan inte slutföras.", ex); }
        if (receipt.EventId != record.Id || receipt.Hash != signed.PayloadHash)
            throw new VaultUnavailableException("Ogiltig auditkvittens.");
        record.Delivered = true; record.ReceiptHash = receipt.Hash;
        db.Update(record);
        await integrity.SaveAsync(ct);
    }
}
