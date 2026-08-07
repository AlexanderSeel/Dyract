using Dyract.Client;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class ContactInvitationTests
{
    [Fact]
    public void Invitation_RoundTripsAndBindsPublicKeyToPeerId()
    {
        using var identity = PeerIdentity.Generate();

        var encoded = ContactInvitationFactory.Create(identity);

        Assert.True(ContactInvitationCodec.TryDecode(encoded, out var invitation, out var error), error);
        Assert.NotNull(invitation);
        Assert.Equal(identity.PeerId.Value, invitation!.PeerId);
        Assert.Equal(identity.ExportPublicKey(), Convert.FromBase64String(invitation.PublicKey));
        Assert.Matches("^[0-9A-F]{4}(-[0-9A-F]{4}){4}$", ContactInvitationCodec.GetFingerprint(invitation));
    }

    [Fact]
    public void Invitation_WithDifferentPublicKeyForPeerId_IsRejected()
    {
        using var alice = PeerIdentity.Generate();
        using var mallory = PeerIdentity.Generate();
        var invalid = new ContactInvitation(
            1,
            alice.PeerId.Value,
            Convert.ToBase64String(mallory.ExportPublicKey()),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        Assert.Throws<ArgumentException>(() => ContactInvitationCodec.Encode(invalid));
    }
}
