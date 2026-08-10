using System.Text;

namespace Dyract.Protocol;

public sealed record AttachmentCompletionAcknowledgement(
    int Version,
    string AttachmentId,
    string Sha256);

public static class AttachmentCompletionAcknowledgementProtocol
{
    private static readonly byte[] Magic = "DYAC"u8.ToArray();
    private const int AttachmentIdSize = 16;
    private const int Sha256Size = 32;
    private const int EncodedSize = 4 + 1 + AttachmentIdSize + Sha256Size;

    public static byte[] Encode(AttachmentCompletionAcknowledgement acknowledgement)
    {
        Validate(acknowledgement);

        var encoded = new byte[EncodedSize];
        Magic.AsSpan().CopyTo(encoded);
        encoded[4] = AttachmentProtocol.CurrentVersion;
        Convert.FromHexString(acknowledgement.AttachmentId).CopyTo(encoded, 5);
        Convert.FromHexString(acknowledgement.Sha256).CopyTo(encoded, 5 + AttachmentIdSize);
        return encoded;
    }

    public static AttachmentCompletionAcknowledgement Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != EncodedSize || !encoded[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Attachment completion acknowledgement is malformed.");
        }

        if (encoded[4] != AttachmentProtocol.CurrentVersion)
        {
            throw new InvalidDataException("Attachment completion acknowledgement version is not supported.");
        }

        var acknowledgement = new AttachmentCompletionAcknowledgement(
            encoded[4],
            Convert.ToHexString(encoded.Slice(5, AttachmentIdSize)).ToLowerInvariant(),
            Convert.ToHexString(encoded.Slice(5 + AttachmentIdSize, Sha256Size)).ToLowerInvariant());
        Validate(acknowledgement);
        return acknowledgement;
    }

    public static void ValidateAgainstManifest(
        AttachmentManifest manifest,
        AttachmentCompletionAcknowledgement acknowledgement)
    {
        AttachmentProtocol.ValidateManifest(manifest);
        Validate(acknowledgement);

        if (!string.Equals(acknowledgement.AttachmentId, manifest.AttachmentId, StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.Sha256, manifest.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Attachment completion acknowledgement does not match the manifest.");
        }
    }

    private static void Validate(AttachmentCompletionAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (acknowledgement.Version != AttachmentProtocol.CurrentVersion ||
            !IsCanonicalHex(acknowledgement.AttachmentId, AttachmentProtocol.AttachmentIdHexLength) ||
            !IsCanonicalHex(acknowledgement.Sha256, AttachmentProtocol.Sha256HexLength))
        {
            throw new InvalidDataException("Attachment completion acknowledgement fields are invalid.");
        }
    }

    private static bool IsCanonicalHex(string? value, int expectedLength)
    {
        if (value is null || value.Length != expectedLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
