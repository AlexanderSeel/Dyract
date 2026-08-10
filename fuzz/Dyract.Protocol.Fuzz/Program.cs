using System.Security.Cryptography;
using Dyract.Core.Identity;
using Dyract.Protocol;
using SharpFuzz;

namespace Dyract.Protocol.Fuzz;

public static class Program
{
    private const byte MessagingTarget = 0;
    private const byte AttachmentTarget = 1;
    private const byte CompletionTarget = 2;

    public static void Main(string[] args)
    {
        if (args is ["--generate-corpus", var outputDirectory])
        {
            GenerateCorpus(outputDirectory);
            return;
        }

        Fuzzer.LibFuzzer.Run(FuzzOne);
    }

    private static void FuzzOne(ReadOnlySpan<byte> input)
    {
        if (input.Length == 0)
        {
            return;
        }

        var payload = input[1..];
        switch ((byte)(input[0] % 3))
        {
            case MessagingTarget:
                FuzzMessaging(payload);
                break;
            case AttachmentTarget:
                FuzzAttachment(payload);
                break;
            case CompletionTarget:
                FuzzCompletion(payload);
                break;
        }
    }

    private static void FuzzMessaging(ReadOnlySpan<byte> payload)
    {
        if (!PeerMessagingProtocol.TryDecode(payload, out var frame, out _))
        {
            return;
        }

        var canonical = PeerMessagingProtocol.Encode(frame!);
        EnsureCanonical(payload, canonical, "DYRM");
    }

    private static void FuzzAttachment(ReadOnlySpan<byte> payload)
    {
        AttachmentApplicationFrame frame;
        try
        {
            frame = AttachmentApplicationFrameProtocol.Decode(payload);
        }
        catch (InvalidDataException)
        {
            return;
        }

        var canonical = AttachmentApplicationFrameProtocol.Encode(frame);
        EnsureCanonical(payload, canonical, "DYRA");
    }

    private static void FuzzCompletion(ReadOnlySpan<byte> payload)
    {
        AttachmentCompletionAcknowledgement acknowledgement;
        try
        {
            acknowledgement = AttachmentCompletionAcknowledgementProtocol.Decode(payload);
        }
        catch (InvalidDataException)
        {
            return;
        }

        var canonical = AttachmentCompletionAcknowledgementProtocol.Encode(acknowledgement);
        EnsureCanonical(payload, canonical, "DYAC");
    }

    private static void EnsureCanonical(ReadOnlySpan<byte> original, ReadOnlySpan<byte> canonical, string domain)
    {
        if (!original.SequenceEqual(canonical))
        {
            throw new InvalidOperationException($"{domain} decoder accepted a non-canonical frame.");
        }
    }

    private static void GenerateCorpus(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var sender = PeerId.FromPublicKey("dyract-fuzz-sender"u8);
        var recipient = PeerId.FromPublicKey("dyract-fuzz-recipient"u8);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var message = new PeerTextMessageFrame(
            "00112233445566778899aabbccddeeff",
            sender,
            recipient,
            timestamp,
            "coverage-guided seed");
        WriteSeed(outputDirectory, "dyrm-text", MessagingTarget, PeerMessagingProtocol.Encode(message));

        var attachmentBytes = new byte[] { 0x42 };
        var attachmentHash = SHA256.HashData(attachmentBytes);
        var manifest = AttachmentProtocol.CreateManifest(
            "seed.bin",
            "application/octet-stream",
            attachmentBytes.Length,
            attachmentHash,
            "00112233445566778899aabbccddeeff");
        WriteSeed(
            outputDirectory,
            "dyra-manifest",
            AttachmentTarget,
            AttachmentApplicationFrameProtocol.Encode(new AttachmentManifestApplicationFrame(manifest)));
        WriteSeed(
            outputDirectory,
            "dyra-chunk",
            AttachmentTarget,
            AttachmentApplicationFrameProtocol.Encode(new AttachmentChunkApplicationFrame(
                AttachmentProtocol.CreateChunk(manifest, 0, attachmentBytes))));
        WriteSeed(
            outputDirectory,
            "dyra-resume",
            AttachmentTarget,
            AttachmentApplicationFrameProtocol.Encode(new AttachmentResumeApplicationFrame(
                AttachmentProtocol.CurrentVersion,
                manifest.AttachmentId,
                new[] { new AttachmentChunkRange(0, 1) })));

        var completion = new AttachmentCompletionAcknowledgement(
            AttachmentProtocol.CurrentVersion,
            manifest.AttachmentId,
            manifest.Sha256);
        WriteSeed(
            outputDirectory,
            "dyac-completion",
            CompletionTarget,
            AttachmentCompletionAcknowledgementProtocol.Encode(completion));
    }

    private static void WriteSeed(string directory, string name, byte target, ReadOnlySpan<byte> payload)
    {
        var seed = new byte[payload.Length + 1];
        seed[0] = target;
        payload.CopyTo(seed.AsSpan(1));
        File.WriteAllBytes(Path.Combine(directory, name), seed);
    }
}
