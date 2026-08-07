using System.Security.Cryptography;

namespace Dyract.Core.Identity;

public readonly record struct PeerId
{
    public const string Prefix = "dyr_";
    private const int EncodedHashLength = 52;
    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public PeerId(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("Invalid Dyract Peer ID.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static PeerId FromPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.IsEmpty)
        {
            throw new ArgumentException("Public key must not be empty.", nameof(publicKey));
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(publicKey, hash);
        return new PeerId(Prefix + EncodeBase32(hash));
    }

    public static bool TryParse(string? value, out PeerId peerId)
    {
        if (IsValid(value))
        {
            peerId = new PeerId(value!);
            return true;
        }

        peerId = default;
        return false;
    }

    public override string ToString() => Value ?? string.Empty;

    private static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != Prefix.Length + EncodedHashLength ||
            !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = Prefix.Length; i < value.Length; i++)
        {
            if (!Base32Alphabet.Contains(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string EncodeBase32(ReadOnlySpan<byte> data)
    {
        var outputLength = (data.Length * 8 + 4) / 5;
        Span<char> output = outputLength <= 128
            ? stackalloc char[outputLength]
            : new char[outputLength];

        var buffer = 0;
        var bitsInBuffer = 0;
        var outputIndex = 0;

        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bitsInBuffer += 8;

            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                output[outputIndex++] = Base32Alphabet[(buffer >> bitsInBuffer) & 31];
            }

            buffer &= bitsInBuffer == 0 ? 0 : (1 << bitsInBuffer) - 1;
        }

        if (bitsInBuffer > 0)
        {
            output[outputIndex++] = Base32Alphabet[(buffer << (5 - bitsInBuffer)) & 31];
        }

        return new string(output[..outputIndex]);
    }
}
