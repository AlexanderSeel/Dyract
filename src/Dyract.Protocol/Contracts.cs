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

public sealed record ContactCapability(
    int Version,
    string IssuerPeerId,
    string GranteePeerId,
    string CapabilityId,
    long IssuedUnixSeconds,
    long ExpiresUnixSeconds,
    string Signature);

public sealed record ConnectionCandidate(
    string Kind,
    string Protocol,
    string Address,
    int Port,
    int Priority);

public sealed record PublishPresenceRequest(
    string PeerId,
    ConnectionCandidate[] Candidates,
    long LeaseExpiresUnixSeconds,
    long TimestampUnixSeconds,
    string Nonce,
    string Signature);

public sealed record PublishPresenceResponse(
    string PeerId,
    long LeaseExpiresUnixSeconds);

public sealed record RemovePresenceRequest(
    string PeerId,
    long TimestampUnixSeconds,
    string Nonce,
    string Signature);

public sealed record ResolvePeerRequest(
    string RequesterPeerId,
    string TargetPeerId,
    ContactCapability Capability,
    long TimestampUnixSeconds,
    string Nonce,
    string Signature);

public sealed record ResolvePeerResponse(
    string PeerId,
    string PublicKey,
    bool IsReachable,
    ConnectionCandidate[] Candidates,
    long? LeaseExpiresUnixSeconds);

public sealed record ApiError(string Code, string Message);
