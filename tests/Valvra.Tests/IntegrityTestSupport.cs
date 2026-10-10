using System.Security.Cryptography;
using System.Text.Json;
using Valvra.Infrastructure.Security;

namespace Valvra.Tests;

internal sealed class TestIntegritySigner : IIntegritySigner, IDisposable
{
    private readonly RSA key = RSA.Create(3072);
    public IntegrityHead Sign(Guid installationId, long generation, string hash)
    {
        var head = new IntegrityHead(installationId, generation, hash, "test-integrity-key", []);
        return head with { Signature = key.SignData(CertificateIntegritySigner.Payload(head), HashAlgorithmName.SHA256, RSASignaturePadding.Pss) };
    }
    public bool Verify(IntegrityHead head) => head.KeyId == "test-integrity-key"
        && key.VerifyData(CertificateIntegritySigner.Payload(head), head.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    public void Dispose() => key.Dispose();
}

internal sealed class MemoryCheckpointStore : IIntegrityCheckpointStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public IntegrityCheckpoint? Value { get; set; }
    public bool FailFinalization { get; set; }
    public bool FailPreparation { get; set; }
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
    { await gate.WaitAsync(ct); return new Lease(gate); }
    public Task<IntegrityCheckpoint?> ReadAsync(CancellationToken ct) => Task.FromResult(Clone(Value));
    public Task WriteAsync(IntegrityCheckpoint checkpoint, CancellationToken ct)
    {
        if (checkpoint.Prepared is not null && FailPreparation) { FailPreparation = false; throw new IOException("Injected preparation failure."); }
        if (checkpoint.Prepared is null && Value?.Prepared is not null && FailFinalization)
        { FailFinalization = false; throw new IOException("Injected finalization failure."); }
        Value = Clone(checkpoint); return Task.CompletedTask;
    }
    private static IntegrityCheckpoint? Clone(IntegrityCheckpoint? value) => value is null ? null
        : JsonSerializer.Deserialize<IntegrityCheckpoint>(JsonSerializer.SerializeToUtf8Bytes(value));
    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
}
