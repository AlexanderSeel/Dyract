using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Crypto.Session;
using Dyract.Protocol;
using SharpFuzz;

namespace Dyract.Protocol.Fuzz;

public static class Program
{
    private const byte MessagingTarget = 0;
    private const byte AttachmentTarget = 1;
    private const byte CompletionTarget = 2;
    private const byte HandshakeTarget = 3;
    private const byte EncryptedSessionTarget = 4;
    private const byte TargetCount = 5;
    private const string SessionId = "11111111111111111111111111111111";
    private const int MaximumMutations = 32;

    private static readonly Lazy<SessionFuzzFixture> SessionFixture = new(CreateSessionFixture);

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
        switch ((byte)(input[0] % TargetCount))
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
            case HandshakeTarget:
                FuzzHandshake(payload);
                break;
            case EncryptedSessionTarget:
                FuzzEncryptedSession(payload);
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

    private static void FuzzHandshake(ReadOnlySpan<byte> payload)
    {
        var fixture = SessionFixture.Value;
        var fuzzResponse = payload.Length > 0 && (payload[0] & 1) != 0;
        var mutations = payload.Length > 0 ? payload[1..] : ReadOnlySpan<byte>.Empty;

        if (fuzzResponse)
        {
            FuzzHandshakeResponse(fixture, mutations);
        }
        else
        {
            FuzzHandshakeHello(fixture, mutations);
        }
    }

    private static void FuzzHandshakeHello(SessionFuzzFixture fixture, ReadOnlySpan<byte> mutations)
    {
        using var initiator = AuthenticatedSessionInitiator.Create(
            fixture.Alice,
            fixture.Bob.PeerId,
            fixture.BobPublicKey,
            SessionId);
        var original = initiator.HelloPacket;
        var candidate = ApplyMutations(original, mutations);
        var changed = !candidate.AsSpan().SequenceEqual(original);

        try
        {
            var result = AuthenticatedSessionResponder.Accept(
                fixture.Bob,
                fixture.Alice.PeerId,
                fixture.AlicePublicKey,
                candidate,
                SessionId);
            using var keys = result.Keys;
            if (changed)
            {
                throw new InvalidOperationException("DYSH responder accepted a mutated signed hello.");
            }
        }
        catch (CryptographicException) when (changed)
        {
        }
        catch (ArgumentException) when (changed)
        {
        }
    }

    private static void FuzzHandshakeResponse(SessionFuzzFixture fixture, ReadOnlySpan<byte> mutations)
    {
        using var initiator = AuthenticatedSessionInitiator.Create(
            fixture.Alice,
            fixture.Bob.PeerId,
            fixture.BobPublicKey,
            SessionId);
        var response = AuthenticatedSessionResponder.Accept(
            fixture.Bob,
            fixture.Alice.PeerId,
            fixture.AlicePublicKey,
            initiator.HelloPacket,
            SessionId);
        using var responderKeys = response.Keys;

        var candidate = ApplyMutations(response.ResponsePacket, mutations);
        var changed = !candidate.AsSpan().SequenceEqual(response.ResponsePacket);
        try
        {
            using var initiatorKeys = initiator.Complete(candidate);
            if (changed)
            {
                throw new InvalidOperationException("DYSH initiator accepted a mutated signed response.");
            }
        }
        catch (CryptographicException) when (changed)
        {
        }
        catch (ArgumentException) when (changed)
        {
        }
    }

    private static void FuzzEncryptedSession(ReadOnlySpan<byte> payload)
    {
        var fixture = SessionFixture.Value;
        using var sender = new AuthenticatedSessionCipher(fixture.InitiatorKeys);
        using var receiver = new AuthenticatedSessionCipher(fixture.ResponderKeys);

        var firstPlaintext = "coverage-guided-dyse-frame-0"u8.ToArray();
        var secondPlaintext = "coverage-guided-dyse-frame-1"u8.ToArray();
        var firstFrame = sender.Encrypt(firstPlaintext);
        var secondFrame = sender.Encrypt(secondPlaintext);

        var mode = payload.Length == 0 ? 0 : payload[0] % 4;
        var instructions = payload.Length > 0 ? payload[1..] : ReadOnlySpan<byte>.Empty;
        var candidate = mode switch
        {
            0 => ApplyMutations(firstFrame, instructions),
            1 => secondFrame.ToArray(),
            2 => Truncate(firstFrame, instructions),
            3 => Extend(firstFrame, instructions),
            _ => throw new InvalidOperationException("Unsupported DYSE fuzz mode.")
        };

        var unchanged = candidate.AsSpan().SequenceEqual(firstFrame);
        if (unchanged)
        {
            EnsurePlaintext(firstPlaintext, receiver.Decrypt(candidate), "DYSE baseline frame changed plaintext.");
        }
        else
        {
            var rejected = false;
            try
            {
                var unexpected = receiver.Decrypt(candidate);
                CryptographicOperations.ZeroMemory(unexpected);
            }
            catch (CryptographicException)
            {
                rejected = true;
            }

            if (!rejected)
            {
                throw new InvalidOperationException("DYSE accepted a mutated/out-of-order frame.");
            }

            EnsurePlaintext(
                firstPlaintext,
                receiver.Decrypt(firstFrame),
                "DYSE rejection advanced receive state or changed plaintext.");
        }

        EnsurePlaintext(
            secondPlaintext,
            receiver.Decrypt(secondFrame),
            "DYSE follow-up frame failed after valid sequence progression.");

        try
        {
            var replay = receiver.Decrypt(firstFrame);
            CryptographicOperations.ZeroMemory(replay);
            throw new InvalidOperationException("DYSE replay was unexpectedly accepted.");
        }
        catch (CryptographicException)
        {
        }
    }

    private static byte[] ApplyMutations(ReadOnlySpan<byte> original, ReadOnlySpan<byte> instructions)
    {
        var candidate = original.ToArray();
        if (instructions.IsEmpty || candidate.Length == 0)
        {
            return candidate;
        }

        var offset = 0;
        var mutations = 0;
        while (offset < instructions.Length && mutations < MaximumMutations)
        {
            int targetOffset;
            byte mask;
            if (instructions.Length - offset >= 3)
            {
                targetOffset = BinaryPrimitives.ReadUInt16BigEndian(instructions.Slice(offset, 2)) % candidate.Length;
                mask = instructions[offset + 2];
                offset += 3;
            }
            else
            {
                targetOffset = instructions[offset] % candidate.Length;
                mask = offset + 1 < instructions.Length ? instructions[offset + 1] : (byte)1;
                offset = instructions.Length;
            }

            candidate[targetOffset] ^= mask;
            mutations++;
        }

        return candidate;
    }

    private static byte[] Truncate(ReadOnlySpan<byte> original, ReadOnlySpan<byte> instructions)
    {
        if (original.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        var length = instructions.IsEmpty
            ? original.Length - 1
            : BinaryPrimitives.ReadUInt16BigEndian(PadToUInt16(instructions)) % original.Length;
        return original[..length].ToArray();
    }

    private static byte[] Extend(ReadOnlySpan<byte> original, ReadOnlySpan<byte> instructions)
    {
        var extraLength = instructions.IsEmpty ? 1 : Math.Clamp(instructions[0] % 17, 1, 16);
        var extended = new byte[original.Length + extraLength];
        original.CopyTo(extended);
        for (var index = 0; index < extraLength; index++)
        {
            extended[original.Length + index] = index + 1 < instructions.Length
                ? instructions[index + 1]
                : (byte)0x42;
        }

        return extended;
    }

    private static ReadOnlySpan<byte> PadToUInt16(ReadOnlySpan<byte> input)
    {
        if (input.Length >= 2)
        {
            return input[..2];
        }

        return new byte[] { 0, input[0] };
    }

    private static void EnsurePlaintext(ReadOnlySpan<byte> expected, byte[] actual, string message)
    {
        try
        {
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException(message);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
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

        WriteSeed(outputDirectory, "dysh-hello-baseline", HandshakeTarget, new byte[] { 0 });
        WriteSeed(outputDirectory, "dysh-response-baseline", HandshakeTarget, new byte[] { 1 });
        WriteSeed(outputDirectory, "dyse-baseline", EncryptedSessionTarget, new byte[] { 0 });
        WriteSeed(outputDirectory, "dyse-out-of-order", EncryptedSessionTarget, new byte[] { 1 });
        WriteSeed(outputDirectory, "dyse-truncated", EncryptedSessionTarget, new byte[] { 2 });
        WriteSeed(outputDirectory, "dyse-extended", EncryptedSessionTarget, new byte[] { 3 });
    }

    private static SessionFuzzFixture CreateSessionFixture()
    {
        var alice = PeerIdentity.Generate();
        var bob = PeerIdentity.Generate();
        var alicePublicKey = alice.ExportPublicKey();
        var bobPublicKey = bob.ExportPublicKey();
        try
        {
            using var initiator = AuthenticatedSessionInitiator.Create(
                alice,
                bob.PeerId,
                bobPublicKey,
                SessionId);
            var response = AuthenticatedSessionResponder.Accept(
                bob,
                alice.PeerId,
                alicePublicKey,
                initiator.HelloPacket,
                SessionId);
            var initiatorKeys = initiator.Complete(response.ResponsePacket);
            return new SessionFuzzFixture(
                alice,
                bob,
                alicePublicKey,
                bobPublicKey,
                initiatorKeys,
                response.Keys);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(alicePublicKey);
            CryptographicOperations.ZeroMemory(bobPublicKey);
            alice.Dispose();
            bob.Dispose();
            throw;
        }
    }

    private static void WriteSeed(string directory, string name, byte target, ReadOnlySpan<byte> payload)
    {
        var seed = new byte[payload.Length + 1];
        seed[0] = target;
        payload.CopyTo(seed.AsSpan(1));
        File.WriteAllBytes(Path.Combine(directory, name), seed);
    }

    private sealed class SessionFuzzFixture(
        PeerIdentity alice,
        PeerIdentity bob,
        byte[] alicePublicKey,
        byte[] bobPublicKey,
        AuthenticatedSessionKeys initiatorKeys,
        AuthenticatedSessionKeys responderKeys)
    {
        public PeerIdentity Alice { get; } = alice;
        public PeerIdentity Bob { get; } = bob;
        public byte[] AlicePublicKey { get; } = alicePublicKey;
        public byte[] BobPublicKey { get; } = bobPublicKey;
        public AuthenticatedSessionKeys InitiatorKeys { get; } = initiatorKeys;
        public AuthenticatedSessionKeys ResponderKeys { get; } = responderKeys;
    }
}
