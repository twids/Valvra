using Microsoft.EntityFrameworkCore;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Security;
using Xunit;

namespace Valvra.Tests;

public sealed class IntegrityCancellationTests
{
    [Fact]
    public async Task CancellationBeforePreparationRollsBackWithoutLeavingAPreparedHead()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); using var request = new CancellationTokenSource();
        var generation = f.Checkpoints.Value!.Current.Generation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Integrity.RunAsync(async () =>
        {
            (await f.Db.Resources.SingleAsync()).Name = "Cancelled";
            request.Cancel(); await f.Integrity.SaveAsync(request.Token);
        }, request.Token));
        Assert.Null(f.Checkpoints.Value!.Prepared); Assert.Equal(generation, f.Checkpoints.Value.Current.Generation);
        Assert.Equal("Resource", (await f.Vault.ResourcesAsync(f.User, default)).Single().Name);
    }

    [Fact]
    public async Task ClientDisconnectAfterPreparationCompletesTheExactAuthorizedCommit()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); using var request = new CancellationTokenSource();
        var store = new DisconnectAtPreparation(f.Checkpoints, request);
        var integrity = new VaultIntegrity(f.Db, store, f.IntegritySigner, f.IntegrityOptions);
        await integrity.RunAsync(async () =>
        {
            (await f.Db.Resources.SingleAsync()).Name = "Committed";
            await integrity.SaveAsync(request.Token);
        }, request.Token);
        Assert.True(request.IsCancellationRequested); Assert.Null(f.Checkpoints.Value!.Prepared);
        Assert.False(integrity.CommitUncertain);
        Assert.Equal("Committed", (await f.Vault.ResourcesAsync(f.User, default)).Single().Name);
    }

    [Fact]
    public async Task DisconnectedAuditWriteRemainsSignedAndDeliverableOnTheNextRequest()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); using var request = new CancellationTokenSource();
        var integrity = new VaultIntegrity(f.Db, new DisconnectAtPreparation(f.Checkpoints, request), f.IntegritySigner, f.IntegrityOptions);
        var audit = new AuditService(f.Db, f.Transport, f.Clock, f.AuditSigner, integrity);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audit.RecordAsync(f.User, "Session.Identified", null, "Success", "disconnect", request.Token));
        Assert.Null(f.Checkpoints.Value!.Prepared);
        await f.Audit.FlushAsync(default);
        Assert.Contains(f.Transport.Events, x => x.Action == "Session.Identified" && x.CorrelationId == "disconnect");
        Assert.Single(await f.Vault.ResourcesAsync(f.User, default));
    }

    private sealed class DisconnectAtPreparation(MemoryCheckpointStore inner, CancellationTokenSource request) : IIntegrityCheckpointStore
    {
        private bool armed = true;
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken ct) => inner.AcquireAsync(ct);
        public Task<IntegrityCheckpoint?> ReadAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return inner.ReadAsync(ct); }
        public async Task WriteAsync(IntegrityCheckpoint checkpoint, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); await inner.WriteAsync(checkpoint, ct);
            if (armed && checkpoint.Prepared is not null) { armed = false; request.Cancel(); }
        }
    }
}
