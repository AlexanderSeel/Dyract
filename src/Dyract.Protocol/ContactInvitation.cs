using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dyract.Core.Identity;

namespace Dyract.Protocol;

public sealed record ContactInvitation(
    int Version,
    string PeerId,
    string PublicKey,
    long CreatedUnixSeconds);

public static class ContactInvitationCodec
{
    private const string Prefix = "dyract://contact/v1/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Encode(ContactInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        Validate(invitation);
        var json = JsonSerializer.SerializeToUtf8Bytes(invitation, JsonOptions);
        return Prefix + ToBase64Url(json);
    }

    public static bool TryDecode(string? value, out ContactInvitation? invitation, out string? error)
    {
        invitation = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "This is not a Dyract contact invitation.";
            return false;
        }

        try
        {
            var payload = FromBase64Url(value[Prefix.Length..]);
            if (payload.Length > 8192)
            {
                error = "Contact invitation is too large.";
                return false;
            }

            invitation = JsonSerializer.Deserialize<ContactInvitation>(payload, JsonOptions);
            if (invitation is null)
            {
                error = "Contact invitation payload is empty.";
                return false;
            }

            Validate(invitation);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            invitation = null;
            error = "Contact invitation is invalid or has been modified.";
            return false;
        }
    }

    public static string GetFingerprint(ContactInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        var key = Convert.FromBase64String(invitation.PublicKey);
        return GetFingerprint(key);
    }

    public static string GetFingerprint(ReadOnlySpan<byte> publicKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(publicKey, hash);
        var compact = Convert.ToHexString(hash[..10]);
        return string.Join('-', Enumerable.Range(0, 5).Select(index => compact.Substring(index * 4, 4)));
    }

    private static void Validate(ContactInvitation invitation)
    {
        if (invitation.Version != 1)
        {
            throw new ArgumentException("Unsupported contact invitation version.", nameof(invitation));
        }

        if (!PeerId.TryParse(invitation.PeerId, out var peerId))
        {
            throw new ArgumentException("Contact invitation PeerId is invalid.", nameof(invitation));
        }

        if (string.IsNullOrWhiteSpace(invitation.PublicKey) || invitation.PublicKey.Length > 8192)
        {
            throw new ArgumentException("Contact invitation public key is invalid.", nameof(invitation));
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(invitation.PublicKey);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Contact invitation public key is invalid.", nameof(invitation), exception);
        }

        if (publicKey.Length == 0 || PeerId.FromPublicKey(publicKey) != peerId)
        {
            throw new ArgumentException("Contact invitation public key does not match its PeerId.", nameof(invitation));
        }

        if (invitation.CreatedUnixSeconds <= 0)
        {
            throw new ArgumentException("Contact invitation creation time is invalid.", nameof(invitation));
        }
    }

    private static string ToBase64Url(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += normalized.Length % 4 switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url payload.")
        };

        return Convert.FromBase64String(normalized);
    }
}
