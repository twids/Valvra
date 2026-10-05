using System.Security.Cryptography;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Xunit;

namespace Valvra.Tests;

public sealed class AuditSignatureTests
{
    private static AuditEvent Event() => new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "ad", "user",
        "Secret.Reveal", Guid.NewGuid(), AuditPhase.Event, "ReleaseAuthorized", "request");

    [Fact]
    public void SignatureVerifiesWithPublicKeyOnly()
    {
        using var privateKey = RSA.Create(3072);
        using var publicKey = RSA.Create();
        publicKey.ImportParameters(privateKey.ExportParameters(false));
        var signed = AuditSignature.Sign(Event(), "key-1", privateKey);
        Assert.True(AuditSignature.Verify(signed, publicKey));
    }

    [Fact]
    public void ModifiedEventAndRecomputedHashCannotForgeSignature()
    {
        using var key = RSA.Create(3072);
        var signed = AuditSignature.Sign(Event(), "key-1", key);
        var changed = signed.Event with { Outcome = "Tampered" };
        Assert.False(AuditSignature.Verify(signed with { Event = changed }, key));
        Assert.False(AuditSignature.Verify(signed with { Event = changed, PayloadHash = AuditSignature.Hash(changed) }, key));
    }

    [Fact]
    public void SameEventHasSameDeduplicationHashAcrossSignatureAttempts()
    {
        using var key = RSA.Create(3072);
        var value = Event();
        var first = AuditSignature.Sign(value, "key-1", key);
        var second = AuditSignature.Sign(value, "key-1", key);
        Assert.Equal(first.PayloadHash, second.PayloadHash);
        Assert.NotEqual(first.Signature, second.Signature);
    }
}
