namespace Dyract.Protocol;

public sealed record RegistrationChallengeRequest(string PublicKey);

public sealed record RegistrationChallengeResponse(
    string PeerId,
    string ChallengeId,
    string Challenge,
    long ExpiresUnixSeconds);

public sealed record RegisterPeerRequest(
    string PeerId,
    string PublicKey,
    string ChallengeId,
    string Signature);

public sealed record RegisterPeerResponse(
    string PeerId,
    long RegisteredUnixSeconds);

public sealed record PeerLookupRequest(
    string RequesterPeerId,
    string TargetPeerId,
    long TimestampUnixSeconds,
    string Nonce,
    string Signature);

public sealed record PeerLookupResponse(
    string PeerId,
    string PublicKey,
    long RegisteredUnixSeconds);

public sealed record ApiError(string Code, string Message);
