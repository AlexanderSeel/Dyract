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
8. P2P transport is preferred; relay is an explicit fallback rather than the primary path.
9. Undelivered messages remain in the sender's local outbox.
10. Protocols are versioned from the first release.
11. Cryptographic primitives come from reviewed platform/libraries; Dyract does not invent algorithms.
12. Endpoint disclosure should require contact capability authorization before production.

## 3. Target architecture

```text
                         +---------------------------+
                         |      Dyract Directory     |
                         | ASP.NET Core / .NET 10    |
                         +---------------------------+
                         | Identity registry         |
                         | Presence leases           |
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
Dyract.slnx
src/
  Dyract.Core/          identity/domain primitives
  Dyract.Crypto/        long-term identity cryptography
  Dyract.Protocol/      wire contracts + canonical proof payloads
  Dyract.Client/        directory client used by mobile app
  Dyract.Server/        directory/signaling service
  Dyract.App/           .NET MAUI app (phase 2)
  Dyract.Transport/     peer transport abstractions (phase 3)
  Dyract.Storage/       SQLite/local repositories (phase 2)
tests/
  Dyract.Tests/
  Dyract.IntegrationTests/ (later)
```

## 5. Phase 0 — identity/directory bootstrap

**Status: started in the first implementation.**

### Scope

- [x] Create .NET 10 solution layout.
- [x] Generate identity key pair using platform cryptography.
- [x] Derive a stable Peer ID from the public key.
- [x] Implement challenge/response registration.
- [x] Implement signed peer lookup.
- [x] Add request timestamp validation.
- [x] Add replay-nonce protection.
- [x] Add reusable directory client.
- [x] Add initial unit tests.
- [ ] Add integration tests around the ASP.NET API.
- [ ] Add structured logging/metrics without logging sensitive payloads.
- [ ] Add server rate limiting.
- [ ] Persist registered identities in PostgreSQL.

### Exit criteria

Two generated identities can register against a local server. A registered peer can perform a correctly signed lookup of another registered peer. Modified, expired, replayed or unsigned requests are rejected.

## 6. Phase 1 — contact authorization and presence

This phase turns the identity registry into a safe discovery service.

### Contact capability

A QR/contact invitation should contain a capability created by the target peer. The directory can validate the capability without storing a global friendship/contact graph.

Conceptually:

```text
ContactCapability
  version
  targetPeerId
  random capability secret or token material
  optional expiry
  target signature
```

Do not return IP/ICE candidates merely because a requester knows a Peer ID.

### Presence leases

Add a short-lived presence model:

```text
PeerPresence
  PeerId
  connection candidates
  protocol version
  last refresh
  expires at
```

Requirements:

- [ ] Signed publish/update/delete presence requests.
- [ ] 1-2 minute lease TTL.
- [ ] Automatic expiry.
- [ ] No permanent IP history in application storage.
- [ ] Redis or equivalent ephemeral store when scaling requires it.
- [ ] Capability-authorized endpoint lookup.

### Exit criteria

Peer A can publish a temporary endpoint lease. Authorized Peer B can obtain it. Unauthorized registered peers cannot retrieve the endpoint.

## 7. Phase 2 — local mobile foundation

Create `Dyract.App` as a .NET MAUI application targeting Android and iOS.

### Identity persistence

- [ ] First-run identity creation.
- [ ] Android Keystore-backed secret protection.
- [ ] iOS Keychain/Secure Enclave integration where appropriate.
- [ ] Identity fingerprint display.
- [ ] Export/recovery design before enabling identity backup.

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

### UX slice

- [ ] First-run identity screen.
- [ ] Show/copy Peer ID.
- [ ] QR contact exchange.
- [ ] Contact list.
- [ ] Empty conversation screen.
- [ ] Basic settings/security screen.

### Exit criteria

Two phones can create identities, exchange contact invitations and retain contacts locally across app restarts.

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

- [ ] Horizontal scaling.
- [ ] abuse protection/rate limiting.
- [ ] DDoS considerations.
- [ ] key/secret management.
- [ ] database migrations.
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
2. Two users exchange contact capability via QR/copy link.
3. Both register authenticated presence.
4. A direct connection is attempted using ICE/STUN.
5. The connection is mutually authenticated and end-to-end encrypted.
6. Text messages persist locally and retry when delivery is impossible.
7. Delivery ACKs work across reconnects.
8. Background wake-up is best effort and contains no chat content.
9. A relay can be enabled/disabled according to the selected privacy mode.
10. Security review has found no release-blocking flaw.

## 17. Immediate next implementation tasks

After the bootstrap PR:

1. Add ASP.NET integration tests for registration/lookup failure modes.
2. Add server-side rate limiting and request-size limits.
3. Replace in-memory identity persistence with PostgreSQL behind an interface.
4. Specify and implement contact capability tokens.
5. Add signed presence leases, but do not expose endpoints before capability checks are in place.
6. Create the MAUI shell and secure identity persistence.
7. Begin the ICE/STUN connectivity spike on two physical phones.
