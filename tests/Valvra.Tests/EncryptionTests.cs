using System.Security.Cryptography;
using System.Text.Json;
using Valvra.Core;
using Valvra.Infrastructure.Security;
using Xunit;

namespace Valvra.Tests;

public sealed class EncryptionTests
{
    [Fact]
    public void RoundTripDoesNotStorePlaintext()
    {
        using var protector = new TestKeyProtector();
        var cipher = new EnvelopeCipher(protector);
        var id = Guid.NewGuid();
        var payload = new SecretPayload("sensitive-user", "sensitive-password", "sensitive-notes");
        var envelope = cipher.Encrypt(payload, id, 1, "secret");
        Assert.DoesNotContain("sensitive", envelope);
        Assert.Equal(payload, cipher.Decrypt<SecretPayload>(envelope, id, 1, "secret"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("purpose")]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    public void ContextAndTamperingAreRejected(string mutation)
    {
        using var protector = new TestKeyProtector();
        var cipher = new EnvelopeCipher(protector);
        var id = Guid.NewGuid();
        var json = cipher.Encrypt(new SecretPayload("a", "b", "c"), id, 1, "secret");
        var envelope = JsonSerializer.Deserialize<CipherEnvelope>(json)!;
        if (mutation == "ciphertext") envelope.Ciphertext[0] ^= 1;
        if (mutation == "tag") envelope.Tag[0] ^= 1;
        json = JsonSerializer.Serialize(envelope);
        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt<SecretPayload>(json,
            mutation == "id" ? Guid.NewGuid() : id, mutation == "version" ? 2 : 1, mutation == "purpose" ? "license" : "secret"));
    }

    [Fact]
    public void NewVersionsHaveIndependentDataKeys()
    {
        using var protector = new TestKeyProtector();
        var cipher = new EnvelopeCipher(protector);
        var id = Guid.NewGuid();
        var first = JsonSerializer.Deserialize<CipherEnvelope>(cipher.Encrypt("password", id, 1, "secret"))!;
        var second = JsonSerializer.Deserialize<CipherEnvelope>(cipher.Encrypt("password", id, 2, "secret"))!;
        Assert.NotEqual(protector.Unwrap(first.KeyId, first.WrappedKey), protector.Unwrap(second.KeyId, second.WrappedKey));
    }

    [Fact]
    public void RewrapChangesProtectionKeyAndPreservesCiphertext()
    {
        using var protector = new TestKeyProtector();
        var cipher = new EnvelopeCipher(protector);
        var id = Guid.NewGuid();
        var original = cipher.Encrypt("password", id, 1, "secret");
        protector.Rotate();
        var rotated = cipher.Rewrap(original);
        Assert.Equal("password", cipher.Decrypt<string>(rotated, id, 1, "secret"));
        var before = JsonSerializer.Deserialize<CipherEnvelope>(original)!;
        var after = JsonSerializer.Deserialize<CipherEnvelope>(rotated)!;
        Assert.NotEqual(before.KeyId, after.KeyId);
        Assert.Equal(before.Ciphertext, after.Ciphertext);
        Assert.Equal(before.Tag, after.Tag);
    }
}

internal sealed class TestKeyProtector : IKeyProtector, IDisposable
{
    private readonly Dictionary<string, RSA> keys = [];
    public string ActiveKeyId { get; private set; } = "";
    public TestKeyProtector() => Rotate();
    public void Rotate() { ActiveKeyId = Guid.NewGuid().ToString(); keys.Add(ActiveKeyId, RSA.Create(3072)); }
    public byte[] Wrap(ReadOnlySpan<byte> key) => keys[ActiveKeyId].Encrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA256);
    public byte[] Unwrap(string keyId, ReadOnlySpan<byte> wrappedKey) => keys[keyId].Decrypt(wrappedKey.ToArray(), RSAEncryptionPadding.OaepSHA256);
    public void Dispose() { foreach (var key in keys.Values) key.Dispose(); }
}
