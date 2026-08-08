namespace Dyract.Protocol;

public static class ContactCapabilityPolicy
{
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(30);

    public const int CapabilityIdHexLength = 32;

    public static bool IsLifetimeAllowed(long issuedUnixSeconds, long expiresUnixSeconds)
    {
        try
        {
            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedUnixSeconds);
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresUnixSeconds);
            var lifetime = expiresAt - issuedAt;
            return lifetime > TimeSpan.Zero && lifetime <= MaximumLifetime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
