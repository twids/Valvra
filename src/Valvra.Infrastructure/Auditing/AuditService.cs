using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Data;

namespace Valvra.Infrastructure.Auditing;

public sealed class AuditService(VaultDbContext db, IAuditTransport transport, TimeProvider clock)
{
    public AuditRecord Create(Actor actor, string action, Guid? target, AuditPhase phase, string outcome,
        string correlationId, Guid? operationId = null) => new()
    {
        Id = Guid.NewGuid(), OperationId = operationId ?? Guid.NewGuid(), Timestamp = clock.GetUtcNow(),
        ActorProvider = actor.Provider, ActorId = actor.SubjectId, Action = action, TargetId = target,
        Phase = phase, Outcome = outcome, CorrelationId = correlationId
    };

    public async Task RecordAsync(Actor actor, string action, Guid? target, string outcome, string correlationId, CancellationToken ct)
    {
        var record = Create(actor, action, target, AuditPhase.Event, outcome, correlationId);
        db.Audit.Add(record);
        await db.SaveChangesAsync(ct);
        await DeliverAsync(record, ct);
    }

    public async Task<T> MutationAsync<T>(Actor actor, string action, Guid? target, string correlationId,
        Func<CancellationToken, Task<T>> mutation, CancellationToken ct, string detailsJson = "{}")
    {
        // Deliver older committed outcomes before accepting further protected mutations.
        await FlushAsync(ct);
        var intent = Create(actor, action, target, AuditPhase.Intent, "Requested", correlationId);
        intent.DetailsJson = detailsJson;
        db.Audit.Add(intent);
        await db.SaveChangesAsync(ct);
        await DeliverAsync(intent, ct);
        T result;
        AuditRecord completed;
        var commitAttempted = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            result = await mutation(ct);
            completed = Create(actor, action, target, AuditPhase.Committed, "Committed", correlationId, intent.OperationId);
            completed.DetailsJson = detailsJson;
            db.Audit.Add(completed);
            await db.SaveChangesAsync(ct);
            commitAttempted = true;
            await transaction.CommitAsync(ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            // A lost commit acknowledgement does not prove rollback. Preserve that ambiguity;
            // a durable Committed outbox record, when present, is the authoritative result.
            var failed = Create(actor, action, target,
                commitAttempted ? AuditPhase.Event : AuditPhase.Failed,
                commitAttempted ? "CommitUncertain" : "Failed", correlationId, intent.OperationId);
            failed.DetailsJson = detailsJson;
            db.Audit.Add(failed);
            await db.SaveChangesAsync(CancellationToken.None);
            await DeliverAsync(failed, CancellationToken.None);
            throw;
        }
        // A transport failure here leaves a durable committed outbox item for reconciliation.
        await DeliverAsync(completed, ct);
        return result;
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        var pending = await db.Audit.Where(x => !x.Delivered).OrderBy(x => x.Timestamp).Take(500).ToListAsync(ct);
        foreach (var record in pending) await DeliverAsync(record, ct);
        if (await db.Audit.AnyAsync(x => !x.Delivered, ct))
            throw new VaultUnavailableException("Auditavstämning pågår.");
    }

    private async Task DeliverAsync(AuditRecord record, CancellationToken ct)
    {
        AuditReceipt receipt;
        try
        {
            var auditEvent = new AuditEvent(record.Id, record.OperationId, record.Timestamp,
                record.ActorProvider, record.ActorId, record.Action, record.TargetId, record.Phase,
                record.Outcome, record.CorrelationId, record.DetailsJson);
            receipt = await transport.SendAsync(auditEvent, ct);
            if (receipt.Hash != AuditSignature.Hash(auditEvent)) throw new IOException("Audit receipt hash mismatch.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new VaultUnavailableException("Auditmottagaren är inte tillgänglig. Operationen kan inte slutföras.", ex);
        }
        if (receipt.EventId != record.Id || receipt.Hash.Length != 64)
            throw new VaultUnavailableException("Ogiltig auditkvittens.");
        record.Delivered = true;
        record.ReceiptHash = receipt.Hash;
        await db.SaveChangesAsync(ct);
    }
}
