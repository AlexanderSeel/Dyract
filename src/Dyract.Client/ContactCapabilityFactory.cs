using System.Security.Cryptography;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Protocol;

namespace Dyract.Client;

public static class ContactCapabilityFactory
{
    public static ContactCapability Create(
        IPeerIdentitySigner issuer,
        string granteePeerId,
        TimeSpan lifetime,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        if (!PeerId.TryParse(granteePeerId, out var grantee))
        {
            throw new ArgumentException("Grantee PeerId is invalid.", nameof(granteePeerId));
        }

        if (lifetime <= TimeSpan.Zero || lifetime > ContactCapabilityPolicy.MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                $"Capability lifetime must be positive and no longer than {ContactCapabilityPolicy.MaximumLifetime.TotalDays:0} days.");
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var expires = now.Add(lifetime);
        var capabilityId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var proof = ProofPayload.ForContactCapability(
            issuer.PeerId.Value,
            grantee.Value,
            capabilityId,
            now.ToUnixTimeSeconds(),
            expires.ToUnixTimeSeconds());

        var signature = Convert.ToBase64String(issuer.Sign(proof));

        return new ContactCapability(
            Version: 1,
            IssuerPeerId: issuer.PeerId.Value,
            GranteePeerId: grantee.Value,
            CapabilityId: capabilityId,
            IssuedUnixSeconds: now.ToUnixTimeSeconds(),
            ExpiresUnixSeconds: expires.ToUnixTimeSeconds(),
            Signature: signature);
    }
}
