using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dyract.Core.Identity;

namespace Dyract.Server.Services;

public interface IRegistrationChallengeStore
{
    ValueTask<RegistrationChallenge> CreateAsync(
        PeerId peerId,
        byte[] publicKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask<RegistrationChallenge?> GetAsync(
        string challengeId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryConsumeAsync(
        string challengeId,
        CancellationToken cancellationToken = default);
}

public sealed class RegistrationChallengeStore : IRegistrationChallengeStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, RegistrationChallenge> _challenges = new(StringComparer.Ordinal);

    public ValueTask<RegistrationChallenge> CreateAsync(
        PeerId peerId,
        byte[] publicKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                return ValueTask.FromResult(challenge);
            }
        }
    }

    public ValueTask<RegistrationChallenge?> GetAsync(
        string challengeId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_challenges.TryGetValue(challengeId, out var challenge))
        {
            return ValueTask.FromResult<RegistrationChallenge?>(null);
        }

        if (challenge.ExpiresAt > now)
        {
            return ValueTask.FromResult<RegistrationChallenge?>(
                challenge with
                {
                    PublicKey = challenge.PublicKey.ToArray(),
                    ChallengeBytes = challenge.ChallengeBytes.ToArray()
                });
        }

        _challenges.TryRemove(challengeId, out _);
        return ValueTask.FromResult<RegistrationChallenge?>(null);
    }

    public ValueTask<bool> TryConsumeAsync(
        string challengeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_challenges.TryRemove(challengeId, out _));
    }
}

public sealed record RegistrationChallenge(
    string Id,
    PeerId PeerId,
    byte[] PublicKey,
    byte[] ChallengeBytes,
    DateTimeOffset ExpiresAt);
