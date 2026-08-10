using System.Security.Cryptography;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentCompletionAcknowledgementProtocolTests
{
    [Fact]
    public void CompletionAcknowledgement_RoundTripsAndBindsManifestHash()
    {
        var hash = SHA256.HashData("completion-ack"u8);
        var manifest = AttachmentProtocol.CreateManifest(
            "proof.bin",
            "application/octet-stream",
            123,
            hash,
            attachmentId: "00112233445566778899aabbccddeeff");
        var acknowledgement = new AttachmentCompletionAcknowledgement(
            AttachmentProtocol.CurrentVersion,
            manifest.AttachmentId,
            manifest.Sha256);

        var encoded = AttachmentCompletionAcknowledgementProtocol.Encode(acknowledgement);
        var decoded = AttachmentCompletionAcknowledgementProtocol.Decode(encoded);

        Assert.Equal(acknowledgement, decoded);
        AttachmentCompletionAcknowledgementProtocol.ValidateAgainstManifest(manifest, decoded);
    }

    [Fact]
    public void CompletionAcknowledgement_RejectsDifferentManifestHash()
    {
        var hash = SHA256.HashData("expected"u8);
        var manifest = AttachmentProtocol.CreateManifest(
            "proof.bin",
            "application/octet-stream",
            123,
            hash,
            attachmentId: "00112233445566778899aabbccddeeff");
        var acknowledgement = new AttachmentCompletionAcknowledgement(
            AttachmentProtocol.CurrentVersion,
            manifest.AttachmentId,
            new string('0', AttachmentProtocol.Sha256HexLength));

        Assert.Throws<InvalidDataException>(() =>
            AttachmentCompletionAcknowledgementProtocol.ValidateAgainstManifest(manifest, acknowledgement));
    }
}
