using Dyract.Crypto.Identity;
using Dyract.Protocol;

namespace Dyract.Client;

public static class ContactInvitationFactory
{
    public static string Create(PeerIdentity identity, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var invitation = new ContactInvitation(
            Version: 1,
            PeerId: identity.PeerId.Value,
            PublicKey: Convert.ToBase64String(identity.ExportPublicKey()),
            CreatedUnixSeconds: now.ToUnixTimeSeconds());

        return ContactInvitationCodec.Encode(invitation);
    }
}
