using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dyract.Core.Identity;

namespace Dyract.Server.Services;

public sealed class RegistrationChallengeStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, RegistrationChallenge> _challenges = new(StringComparer.Ordinal);

    public RegistrationChallenge Create(PeerId peerId, byte[] publicKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        while (true)
        {
            var challenge = new RegistrationChallenge(
                Guid.NewGuid().ToString("N"),
                peerId,
                publicKey.ToArray(),
                RandomNumberGenerator.GetBytes(32),
                now.Add(Lifetime));

            if (_challenges.TryAdd(challenge.Id, challenge))
            {
                return challenge;
            }
        }
    }

    public bool TryGet(string challengeId, DateTimeOffset now, out RegistrationChallenge challenge)
    {
        if (!_challenges.TryGetValue(challengeId, out challenge!))
        {
            return false;
        }

        if (challenge.ExpiresAt > now)
        {
            return true;
        }

        _challenges.TryRemove(challengeId, out _);
        challenge = null!;
        return false;
    }

    public bool TryConsume(string challengeId)
        => _challenges.TryRemove(challengeId, out _);
}

public sealed record RegistrationChallenge(
    string Id,
    PeerId PeerId,
    byte[] PublicKey,
    byte[] ChallengeBytes,
    DateTimeOffset ExpiresAt);
