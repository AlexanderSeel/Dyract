using Dyract.Core.Identity;

namespace Dyract.Crypto.Identity;

/// <summary>
/// Public identity + signing operations required by Dyract protocols.
/// Implementations are not required to expose/export private-key material.
/// </summary>
public interface IPeerIdentitySigner
{
    PeerId PeerId { get; }

    byte[] ExportPublicKey();

    byte[] Sign(ReadOnlySpan<byte> payload);
}
