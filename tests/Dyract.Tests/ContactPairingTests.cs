using Dyract.Client;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class ContactPairingTests
{
    [Fact]
    public void PairingResponse_RoundTripsAndVerifiesAgainstPinnedContactKey()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var time = new FixedTimeProvider(now);
        using var issuer = PeerIdentity.Generate();
        using var grantee = PeerIdentity.Generate();

        var capability = ContactCapabilityFactory.Create(
            issuer,
            grantee.PeerId.Value,
            TimeSpan.FromDays(30),
            time);
        var encoded = ContactPairingCodec.Encode(capability);

        Assert.True(ContactPairingCodec.TryDecode(encoded, out var decoded, out var decodeError), decodeError);
        Assert.NotNull(decoded);
        Assert.True(ContactCapabilityVerifier.TryVerify(
            decoded!,
            issuer.ExportPublicKey(),
            grantee.PeerId.Value,
            out var verificationError,
            time), verificationError);
    }

    [Fact]
    public void PairingResponse_ForAnotherGrantee_IsRejected()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var time = new FixedTimeProvider(now);
        using var issuer = PeerIdentity.Generate();
        using var intendedGrantee = PeerIdentity.Generate();
        using var otherGrantee = PeerIdentity.Generate();

        var capability = ContactCapabilityFactory.Create(
            issuer,
            intendedGrantee.PeerId.Value,
            TimeSpan.FromHours(1),
            time);

        Assert.False(ContactCapabilityVerifier.TryVerify(
            capability,
            issuer.ExportPublicKey(),
            otherGrantee.PeerId.Value,
            out var error,
            time));
        Assert.Contains("different Dyract identity", error);
    }

    [Fact]
    public void PairingResponse_WithTamperedExpiry_IsRejected()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var time = new FixedTimeProvider(now);
        using var issuer = PeerIdentity.Generate();
        using var grantee = PeerIdentity.Generate();

        var capability = ContactCapabilityFactory.Create(
            issuer,
            grantee.PeerId.Value,
            TimeSpan.FromHours(1),
            time);
        var tampered = capability with { ExpiresUnixSeconds = capability.ExpiresUnixSeconds + 3600 };

        Assert.False(ContactCapabilityVerifier.TryVerify(
            tampered,
            issuer.ExportPublicKey(),
            grantee.PeerId.Value,
            out var error,
            time));
        Assert.Contains("signature", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpiredPairingResponse_IsRejected()
    {
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        using var issuer = PeerIdentity.Generate();
        using var grantee = PeerIdentity.Generate();

        var capability = ContactCapabilityFactory.Create(
            issuer,
            grantee.PeerId.Value,
            TimeSpan.FromMinutes(10),
            new FixedTimeProvider(issuedAt));

        Assert.False(ContactCapabilityVerifier.TryVerify(
            capability,
            issuer.ExportPublicKey(),
            grantee.PeerId.Value,
            out var error,
            new FixedTimeProvider(issuedAt.AddMinutes(11))));
        Assert.Contains("expired", error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
