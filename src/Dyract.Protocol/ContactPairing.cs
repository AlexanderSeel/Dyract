using System.Text.Json;
using Dyract.Core.Identity;

namespace Dyract.Protocol;

public static class ContactPairingCodec
{
    public const string Prefix = "dyract://pair/v1/";
    private const int MaximumPayloadBytes = 8192;
    private const int MaximumEncodedPayloadCharacters = ((MaximumPayloadBytes + 2) / 3) * 4;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Encode(ContactCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ValidateStructure(capability);
        var json = JsonSerializer.SerializeToUtf8Bytes(capability, JsonOptions);
        return Prefix + ToBase64Url(json);
    }

    public static bool TryDecode(string? value, out ContactCapability? capability, out string? error)
    {
        capability = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > Prefix.Length + MaximumEncodedPayloadCharacters ||
            !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "This is not a Dyract pairing response.";
            return false;
        }

        var encodedPayload = value[Prefix.Length..];
        if (encodedPayload.Length == 0 || encodedPayload.Length > MaximumEncodedPayloadCharacters)
        {
            error = "Pairing response payload is invalid or too large.";
            return false;
        }

        try
        {
            var payload = FromBase64Url(encodedPayload);
            if (payload.Length > MaximumPayloadBytes)
            {
                error = "Pairing response is too large.";
                return false;
            }

            capability = JsonSerializer.Deserialize<ContactCapability>(payload, JsonOptions);
            if (capability is null)
            {
                error = "Pairing response payload is empty.";
                return false;
            }

            ValidateStructure(capability);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            capability = null;
            error = "Pairing response is invalid or has been modified.";
            return false;
        }
    }

    public static void ValidateStructure(ContactCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        if (capability.Version != 1)
        {
            throw new ArgumentException("Unsupported contact capability version.", nameof(capability));
        }

        if (!PeerId.TryParse(capability.IssuerPeerId, out _) ||
            !PeerId.TryParse(capability.GranteePeerId, out _))
        {
            throw new ArgumentException("Pairing response contains an invalid PeerId.", nameof(capability));
        }

        if (capability.CapabilityId is not { Length: 32 } || !capability.CapabilityId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Pairing response capability ID is invalid.", nameof(capability));
        }

        if (capability.IssuedUnixSeconds <= 0 ||
            capability.ExpiresUnixSeconds <= capability.IssuedUnixSeconds)
        {
            throw new ArgumentException("Pairing response timestamps are invalid.", nameof(capability));
        }

        if (string.IsNullOrWhiteSpace(capability.Signature) || capability.Signature.Length > 2048)
        {
            throw new ArgumentException("Pairing response signature is invalid.", nameof(capability));
        }

        try
        {
            if (Convert.FromBase64String(capability.Signature).Length == 0)
            {
                throw new ArgumentException("Pairing response signature is empty.", nameof(capability));
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Pairing response signature is invalid.", nameof(capability), exception);
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
        normalized += (normalized.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url payload.")
        };

        return Convert.FromBase64String(normalized);
    }
}
