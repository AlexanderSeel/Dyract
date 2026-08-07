# Dyract implementation plan

## 1. Product goal

Dyract is a direct-first, privacy-oriented messenger for Android and iPhone written primarily in C#. Each installation owns a cryptographic peer identity. Contacts, user-assigned names, messages, attachments and conversation state are stored locally. A minimal central service authenticates peers and helps them discover current reachability information; it is not the message store.

The product goal is not "zero servers". The goal is **minimum necessary infrastructure with no central conversation history**.

## 2. Architectural invariants

These rules should remain true unless a future architecture decision explicitly changes them:

1. A Peer ID is an address, never a password or bearer secret.
2. The Peer ID is derived from the identity public key.
3. The identity private key is generated on-device and never uploaded unencrypted.
4. Every security-sensitive directory operation is authenticated by a signature.
5. Knowing a valid Peer ID must not allow impersonation.
6. Contacts, display names, avatars, conversations and message bodies are local data.
7. Presence/reachability records are short-lived leases, not permanent IP records.
8. Endpoint disclosure requires authorization from the target peer.
9. P2P transport is preferred; relay is an explicit fallback rather than the primary path.
10. Undelivered messages remain in the sender's local outbox.
11. Protocols are versioned from the first release.
12. Cryptographic primitives come from reviewed platform/libraries; Dyract does not invent algorithms.

## 3. Target architecture

```text
                         +---------------------------+
                         |      Dyract Directory     |
                         | ASP.NET Core / .NET 10    |
                         +---------------------------+
                         | Identity registry         |
                         | Presence leases           |
                         | Capability verification   |
                         | ICE signaling             |
                         | Wake-up routing           |
                         | Rate limiting             |
                         +-------------+-------------+
                                       |
                               no chat history
                                       |
                      +----------------+----------------+
                      |                                 |
               +------+-------+                  +------+-------+
               | Alice device |                  |  Bob device  |
               | .NET MAUI    |                  | .NET MAUI    |
               +------+-------+                  +------+-------+
                      |                                 |
                      |  authenticated encrypted P2P   |
                      +=================================+
                         ICE/STUN; optional TURN relay
```

## 4. Repository/project structure

```text
Dyract.slnx             workload-free core/server/test solution
Dyract.Mobile.slnx      Android/iOS MAUI solution

src/
  Dyract.App/           .NET MAUI Android/iOS client
  Dyract.Core/          identity/domain primitives
  Dyract.Crypto/        long-term identity cryptography
  Dyract.Protocol/      wire contracts + canonical proof payloads
  Dyract.Client/        directory client used by mobile app
  Dyract.Server/        directory/signaling service
  Dyract.Transport/     peer transport abstractions (phase 3)
  Dyract.Storage/       SQLite/local repositories (phase 2)
tests/
  Dyract.Tests/         unit + ASP.NET integration tests
```

## 5. Phase 0 — identity/directory bootstrap

**Status: implemented; production hardening continues.**

### Scope

- [x] Create .NET 10 solution layout.
- [x] Generate identity key pair using platform cryptography.
- [x] Derive a stable Peer ID from the public key.
- [x] Implement challenge/response registration.
- [x] Implement signed peer lookup.
- [x] Add request timestamp validation.
- [x] Add replay-nonce protection.
- [x] Add reusable directory client.
- [x] Add unit tests.
- [x] Add ASP.NET integration tests around the API.
- [x] Add server rate limiting and request-size limits.
- [x] Put identity persistence behind an async interface.
- [x] Add optional PostgreSQL identity persistence.
- [ ] Replace prototype automatic PostgreSQL schema creation with explicit migrations.
- [ ] Add structured privacy-aware logging/metrics without sensitive payloads.
- [ ] Add PostgreSQL integration tests in CI.

### Exit criteria

Two generated identities can register against a local server. A registered peer can perform a correctly signed lookup of another registered peer. Modified, expired, replayed or unsigned requests are rejected. The API has bounded request size/basic rate limiting and can persist identity/public-key registrations in PostgreSQL when configured.

## 6. Phase 1 — contact authorization and presence

**Status: first implementation and HTTP authorization coverage complete.**

### Contact capability

The implemented capability is signed by the target/issuer and bound to one grantee:

```text
ContactCapability
  Version
  IssuerPeerId
  GranteePeerId
  CapabilityId       random 128-bit ID
  IssuedUnixSeconds
  ExpiresUnixSeconds
  Signature          issuer identity signature
```

The capability is held by the contact receiving permission. The server verifies it when presence is resolved but does not persist a friendship/contact graph.

A capability is reusable until expiry; each resolve request still requires a fresh requester signature, timestamp and nonce. Capability revocation before expiry is deliberately deferred and must be designed before production.

### Presence leases

```text
PeerPresence
  PeerId
  connection candidates
  last refresh
  expires at
```

Implemented requirements:

- [x] Signed publish/update presence requests.
- [x] Signed presence deletion.
- [x] Maximum two-minute lease TTL; client defaults to 90 seconds.
- [x] Automatic expiry during store access/publication.
- [x] No permanent endpoint/IP history in the prototype application store.
- [x] Capability-authorized endpoint lookup via `/api/v1/peer/resolve`.
- [x] Request timestamp and replay-nonce validation.
- [x] Candidate count/address/protocol/port validation.
- [x] Unit tests for signed capability/proof and presence expiry behavior.
- [x] ASP.NET end-to-end registration/presence/capability authorization tests.
- [ ] Redis or equivalent ephemeral store when horizontal scaling requires it.
- [ ] Capability revocation/rotation mechanism.

Current candidate model:

```text
kind      host | srflx | relay
protocol  udp | tcp
address   IPv4 | IPv6
port      1..65535
priority  >= 0
```

Loopback, unspecified, multicast and broadcast addresses are rejected. A maximum of eight candidates is accepted per lease.

### Exit criteria

Peer A can publish a short-lived endpoint lease, and an authenticated Peer B can resolve that lease only when it presents a valid capability signed by Peer A specifically for Peer B. Knowing Peer A's Peer ID alone does not disclose endpoint candidates. HTTP integration coverage verifies both authorized and wrong-grantee cases.

## 7. Phase 2 — local mobile foundation

**Status: started — MAUI shell and secure first-run identity flow implemented.**

`Dyract.App` targets Android and iOS through .NET MAUI. It is kept in `Dyract.Mobile.slnx` so the core/server CI does not require mobile workloads.

### Identity persistence

- [x] First-run identity creation.
- [x] Persist PKCS#8 identity through MAUI `SecureStorage`.
- [x] Android Keystore-backed encrypted storage through MAUI SecureStorage.
- [x] iOS Keychain storage through MAUI SecureStorage.
- [x] Do not silently replace an unreadable identity.
- [x] Define reinstall as a new identity until explicit recovery exists.
- [x] Disable Android app backup for the current privacy model.
- [ ] Evaluate non-exportable platform-native identity keys.
- [ ] Evaluate Secure Enclave-backed identity where appropriate.
- [ ] Add separate human-readable fingerprint/safety-number display.
- [ ] Design explicit encrypted identity export/recovery.

The current key is securely stored at rest but remains exportable in application memory because the shared `PeerIdentity` implementation imports/exports PKCS#8 key material. This is an explicit first implementation boundary, not the final mobile key design.

### Local database

Use SQLite behind repository interfaces. Initial entities:

```text
IdentityMetadata
Contact
Conversation
Message
OutboxItem
Attachment
KnownPeerEndpoint
CryptoSession
Settings
```

Never treat local display names as directory identity attributes.

- [ ] Create `Dyract.Storage` project.
- [ ] Add SQLite schema/versioning.
- [ ] Contacts repository.
- [ ] Conversation/message repository.
- [ ] Transactional outbox repository.
- [ ] Local database encryption/key strategy.

### Contact invitation representation

Before QR UI is added, define a versioned portable invitation format containing at least:

```text
protocol version
PeerId
identity public-key/fingerprint data needed for verification
contact authorization bootstrap data
optional human-readable checksum
```

The current durable contact capability is grantee-bound. Initial contact onboarding therefore needs an explicit pairing/invitation design rather than weakening endpoint authorization into a globally reusable capability.

### UX slice

- [x] First-run identity screen.
- [x] Show/copy Peer ID.
- [ ] Directory server configuration/registration state.
- [ ] QR/contact exchange.
- [ ] Contact list.
- [ ] Empty conversation screen.
- [ ] Basic settings/security screen.
- [ ] Explicit identity reset/recovery UI.

### Exit criteria

Two phones can create identities, exchange contact invitations and retain contacts locally across app restarts. **Not reached yet:** identity creation is implemented; contact pairing and local contact storage remain.

## 8. Phase 3 — direct connectivity spike

This is the highest-risk technical milestone and should be proven before building a polished chat experience.

### Transport abstraction

```csharp
public interface IPeerTransport
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<IPeerConnection> ConnectAsync(
        PeerConnectionDescriptor peer,
        CancellationToken cancellationToken);
}
```

Planned transport strategy:

1. LAN/direct candidate where possible.
2. ICE with STUN for NAT traversal.
3. Optional TURN relay when direct establishment fails.

The existing presence candidate contract is a bootstrap representation, not a commitment to invent a custom ICE protocol. The transport spike should use a mature ICE/WebRTC-compatible implementation where practical and adapt the directory signaling contract around that implementation.

### Test matrix

- [ ] Wi-Fi to same Wi-Fi.
- [ ] Wi-Fi to different home network.
- [ ] Wi-Fi to mobile network.
- [ ] Mobile network to mobile network.
- [ ] IPv4 NAT.
- [ ] CGNAT.
- [ ] IPv6.
- [ ] network changes while connected.
- [ ] app foreground/background transitions.

Collect connection outcome telemetry only in a privacy-preserving form; do not log peer identities or candidate addresses by default.

### Exit criteria

A documented connectivity matrix shows where direct connections work and where a relay is required. This result drives the final Direct-only vs Normal mode product decision.

## 9. Phase 4 — authenticated encrypted peer sessions

Identity authentication and message/session encryption are separate concerns.

Requirements:

- [ ] Ephemeral session-key agreement with forward secrecy.
- [ ] Long-term identity authentication of the handshake.
- [ ] Peer identity fingerprint pinning.
- [ ] Detect and block unexpected identity-key changes.
- [ ] Session sequence numbers.
- [ ] Replay protection.
- [ ] Protocol-version negotiation and downgrade protection.
- [ ] Key rotation/session renewal design.

Before this phase is considered complete, the handshake and key schedule require independent security review.

## 10. Phase 5 — reliable messaging

### Message lifecycle

```text
Queued -> Connecting -> Sent -> Delivered -> Read
                     \-> Retry/Failed
```

Requirements:

- [ ] UUIDv7/sortable message identifiers.
- [ ] Local outbox transaction before network send.
- [ ] Idempotent receive by MessageId.
- [ ] Delivery ACK.
- [ ] Optional read receipt.
- [ ] Retry with bounded exponential backoff.
- [ ] Reconnect synchronization.
- [ ] Duplicate suppression.
- [ ] Clock-skew-safe ordering strategy.

No undelivered message body is uploaded to the Dyract directory.

## 11. Phase 6 — mobile wake-up/offline behavior

Pure IP reachability cannot provide WhatsApp-like delivery when the recipient app is suspended.

Implement best-effort wake-up routing:

- [ ] APNs for iOS.
- [ ] FCM for Android.
- [ ] Store only the minimum push-routing token required.
- [ ] Push payload contains no message body, sender display name or attachment.
- [ ] Wake event is an opaque prompt to re-establish Dyract connectivity.
- [ ] Handle push throttling/unavailability gracefully.

The user-facing delivery state must distinguish local queueing from actual peer delivery.

## 12. Phase 7 — attachments

Implement direct chunked transfer:

```text
AttachmentManifest
  FileId
  Name
  MIME type
  Size
  SHA-256
  Chunk size
```

- [ ] Resume by missing chunk ranges.
- [ ] Hash/integrity verification.
- [ ] Transfer size limits.
- [ ] Local storage cleanup policy.
- [ ] Safe filename/content handling.
- [ ] Thumbnail generation locally.

## 13. Phase 8 — production directory infrastructure

Move prototype state to production stores without expanding server knowledge.

Suggested services:

```text
ASP.NET Core API
PostgreSQL    durable identity/public-key registration
Redis         short-lived presence, nonces, signaling
APNs/FCM      wake routing
STUN          NAT discovery
TURN          optional encrypted packet relay
```

Requirements:

- [x] PostgreSQL-backed identity/public-key store available when configured.
- [x] Basic per-client API rate limiting and request-size limits.
- [ ] Explicit database migrations and PostgreSQL integration CI.
- [ ] Horizontal scaling.
- [ ] Redis-backed short-lived state.
- [ ] broader abuse/DDoS protection.
- [ ] key/secret management.
- [ ] privacy-preserving operational logs.
- [ ] data retention specification.
- [ ] backup/restore limited to server-owned metadata.

## 14. Phase 9 — security hardening

Required before public production use:

- [ ] Threat model (STRIDE-style plus privacy/metadata analysis).
- [ ] API penetration test.
- [ ] protocol fuzzing.
- [ ] malformed-frame tests.
- [ ] replay tests.
- [ ] downgrade tests.
- [ ] endpoint-enumeration tests.
- [ ] stolen-device analysis.
- [ ] reinstall/key-loss analysis.
- [ ] dependency/SBOM generation.
- [ ] independent cryptographic review.
- [ ] mobile secure-storage review.

## 15. Deferred features

Do not add these until one-to-one messaging is stable:

- group chat,
- multi-device identity synchronization,
- calls/video,
- public usernames/search,
- bots,
- channels,
- cloud message backup.

Each of these changes the privacy/security model substantially.

## 16. MVP definition

The first usable MVP is reached when:

1. Android and iOS generate and securely persist an identity.
2. Two users exchange contact authorization via QR/copy link.
3. Both register authenticated presence.
4. A direct connection is attempted using ICE/STUN.
5. The connection is mutually authenticated and end-to-end encrypted.
6. Text messages persist locally and retry when delivery is impossible.
7. Delivery ACKs work across reconnects.
8. Background wake-up is best effort and contains no chat content.
9. A relay can be enabled/disabled according to the selected privacy mode.
10. Security review has found no release-blocking flaw.

## 17. Immediate next implementation tasks

The next sequence is:

1. Validate the current server/core changes through CI and add PostgreSQL integration coverage/migrations.
2. Define a secure first-contact pairing/invitation protocol that upgrades into grantee-bound capabilities.
3. Connect the MAUI client to configurable directory registration/status.
4. Create `Dyract.Storage` and implement local contact + conversation + transactional outbox persistence.
5. Add explicit identity reset/recovery UX without silent key replacement.
6. Add capability revocation/rotation semantics.
7. Create `Dyract.Transport` abstractions and begin the ICE/STUN connectivity spike on physical Android/iPhone devices.
8. Add Redis-backed ephemeral presence/nonces/signaling only when multi-instance deployment requires it.
