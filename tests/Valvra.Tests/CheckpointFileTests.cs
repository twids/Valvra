using System.Security.Cryptography;
using Valvra.Infrastructure.Security;
using Xunit;

namespace Valvra.Tests;

public sealed class RequiresWindowsAttribute : FactAttribute
{ public RequiresWindowsAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires Windows DPAPI."; } }

public sealed class CheckpointFileTests
{
    [RequiresWindows]
    public async Task CheckpointIsDpapiProtectedAndTamperingIsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "valvra-checkpoint-test-" + Guid.NewGuid());
        try
        {
            var store = new FileIntegrityCheckpointStore(directory);
            using var signer = new TestIntegritySigner();
            await using (var lease = await store.AcquireAsync(default))
            {
                Assert.Null(await store.ReadAsync(default));
                var head = signer.Sign(Guid.NewGuid(), 42, new string('a', 64));
                await store.WriteAsync(new(head), default);
                var restored = (await store.ReadAsync(default))!;
                Assert.True(signer.Verify(restored.Current)); Assert.Equal(42, restored.Current.Generation);
                var path = Path.Combine(directory, "checkpoint.bin"); var bytes = await File.ReadAllBytesAsync(path);
                Assert.DoesNotContain(head.Hash, System.Text.Encoding.UTF8.GetString(bytes));
                bytes[bytes.Length / 2] ^= 1; await File.WriteAllBytesAsync(path, bytes);
                await Assert.ThrowsAsync<CryptographicException>(() => store.ReadAsync(default));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SeparateStoreInstancesShareExclusiveLeaseAndHonorCancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "valvra-lock-test-" + Guid.NewGuid());
        try
        {
            var first = new FileIntegrityCheckpointStore(directory); var second = new FileIntegrityCheckpointStore(directory);
            await using (var lease = await first.AcquireAsync(default))
            { using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.AcquireAsync(cancellation.Token)); }
            await using var next = await second.AcquireAsync(default);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
