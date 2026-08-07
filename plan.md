# Dyract implementation plan

## 1. Product goal

Dyract is a direct-first, privacy-oriented messenger for Android and iPhone written primarily in C#. Each installation owns a cryptographic peer identity. Contacts, user-assigned names, conversations, messages and attachments are device-owned data. A minimal central service authenticates peers and exposes short-lived reachability/signaling metadata; it is not the message store.

The goal is **minimum necessary infrastructure with no central conversation history**, not the unrealistic claim that a usable mobile messenger can be completely serverless.

## 2. Architectural invariants

These rules remain true unless an explicit architecture decision changes them:

1. A Peer ID is an address, never a password or bearer secret.
2. The Peer ID is derived from the identity public key.
3. The identity private key is generated on-device and is never uploaded.
4. Every security-sensitive directory operation is authenticated by a signature.
5. Knowing a Peer ID must not permit impersonation or endpoint discovery.
6. Contacts, local names, conversations and message bodies are local data.
7. Reachability is represented by short-lived leases, not permanent IP records.
8. Endpoint disclosure requires authorization signed by the target peer for the exact grantee.
9. Identity pinning and reachability authorization are separate operations.
10. Outgoing messages are committed locally before any transport attempt.
11. Direct transport is preferred; relay is an explicit fallback mode.
12. Wire/application protocols are versioned from the first release.
13. Dyract uses reviewed primitives/platform cryptography rather than inventing cryptographic algorithms.

## 3. Target architecture

```text
                         +---------------------------+
                         |      Dyract Directory     |
                         | ASP.NET Core / .NET 10    |
                         +---------------------------+
                         | identity registry         |
                         | presence leases           |
                         | capability verification   |
                         | ICE signaling (*)         |
                         | wake-up routing (*)       |
                         | rate limiting             |
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
               encrypted SQLite                 encrypted SQLite
               transactional outbox             transactional outbox
                      |                                 |
                      +==== authenticated P2P (*) =====+
                            ICE/STUN; optional TURN

(*) implementation in progress/planned
```

## 4. Repository structure

```text
Dyract.slnx             workload-free core/server/storage/transport/test solution
Dyract.Mobile.slnx      Android/iOS MAUI solution

src/
  Dyract.App/           .NET MAUI Android/iOS client
  Dyract.Client/        directory client + invitation/capability helpers
  Dyract.Core/          identity/domain primitives
  Dyract.Crypto/        identity cryptography/signature verification
  Dyract.Protocol/      versioned contracts + signed proof payloads
  Dyract.Server/        identity, presence and discovery service
  Dyract.Storage/       encrypted local SQLite repositories/outbox
  Dyract.Transport/     replaceable direct peer transport contracts

tests/
  Dyract.Tests/         unit + ASP.NET integration tests
```

## 5. Phase 0 — identity/directory bootstrap

**Status: implemented; production hardening remains.**

- [x] .NET 10 solution layout.
- [x] P-256 identity generation/signing.
- [x] Peer ID derived from public key.
- [x] Challenge/response registration.
- [x] Signed peer lookup.
- [x] Timestamp validation.
- [x] Replay-nonce protection.
- [x] Reusable directory client.
- [x] Unit + ASP.NET integration tests.
- [x] Request body limits and basic API rate limiting.
- [x] Async identity persistence abstraction.
- [x] In-memory identity persistence for local/test use.
- [x] Optional PostgreSQL identity persistence.
- [ ] Explicit PostgreSQL migrations instead of prototype schema bootstrap.
- [ ] PostgreSQL integration CI.
- [ ] Privacy-aware structured logging/metrics.

### Exit criteria

Met for the prototype: registered identities are cryptographically bound to their public keys; signed requests reject stale/replayed/tampered input; persistence can be in-memory or PostgreSQL.

## 6. Phase 1 — contact authorization and presence

**Status: prototype complete with HTTP authorization coverage.**

### Capability

```text
ContactCapability
  Version
  IssuerPeerId
  GranteePeerId
  CapabilityId
  IssuedUnixSeconds
  ExpiresUnixSeconds
  Signature
```

The target signs the capability for one exact grantee. The server verifies it during resolve without storing a friendship/contact graph.

- [x] Target-signed capability generation.
- [x] Capability bound to issuer and exact grantee.
- [x] Capability expiry verification.
- [x] Wrong-grantee rejection.
- [x] Mobile import verification against pinned issuer key.
- [x] Versioned `dyract://pair/v1/...` representation.
- [ ] Explicit pre-expiry revocation/rotation mechanism.

### Presence

```text
PeerPresence
  PeerId
  connection candidates
  published at
  expires at
```

- [x] Signed publish/update.
- [x] Signed removal.
- [x] Maximum two-minute lease.
- [x] Automatic expiry.
- [x] No permanent endpoint/IP history in prototype store.
- [x] Candidate validation/count limits.
- [x] Capability-protected `/api/v1/peer/resolve`.
- [x] HTTP tests for authorized and wrong-grantee resolve.
- [ ] Redis/TTL store when horizontal scaling is required.

## 7. Phase 2 — local mobile foundation

**Status: functional offline/local messaging foundation implemented.**

### Identity persistence

- [x] First-run identity creation.
- [x] Identity stored through MAUI `SecureStorage`.
- [x] Android Keystore-backed storage through MAUI.
- [x] iOS Keychain storage through MAUI.
- [x] Do not silently replace unreadable identity material.
- [x] Reinstall treated as a new identity until recovery exists.
- [x] Android app backup disabled for current privacy model.
- [x] Peer ID display/copy.
- [x] Human-readable identity fingerprint display.
- [ ] Non-exportable platform-native identity-key evaluation.
- [ ] Secure Enclave evaluation on iOS.
- [ ] Explicit encrypted identity export/recovery.
- [ ] Identity reset/recovery UX.

The current shared identity key remains exportable in application memory because it is represented as PKCS#8 material. That is an explicit prototype boundary.

### Encrypted local database

`Dyract.Storage` is implemented with SQLite behind `ILocalStore`.

- [x] `Dyract.Storage` project.
- [x] Schema/version bootstrap.
- [x] Contacts repository.
- [x] Conversations/messages repository.
- [x] Transactional outbox.
- [x] UUIDv7 message/conversation identifiers where applicable.
- [x] Independent 256-bit local-data key through SecureStorage.
- [x] AES-256-GCM encryption of contact names/capabilities/message text/outbox errors.
- [x] Wrong-key decryption tests.
- [x] SQLite native dependency pinned to a non-vulnerable compatible bundle.
- [ ] Formal schema migrations beyond prototype v1.
- [ ] Attachments and attachment cleanup/storage policy.
- [ ] Optional stronger full-file metadata protection if required by threat model.

The SQLite file is **not** claimed to be completely opaque: operational metadata such as Peer IDs and timestamps remains visible while user-content fields are encrypted.

### Contact onboarding and pairing

Identity exchange and endpoint authorization are separate:

```text
dyract://contact/v1/...   -> pin PeerId + public key

dyract://pair/v1/...      -> signed reachability authorization
```

- [x] Versioned contact invitation codec.
- [x] PeerId/public-key binding validation.
- [x] Fingerprint presentation.
- [x] Local-only display name.
- [x] Reciprocal pairing response generation/import.
- [x] Pairing response stored encrypted.
- [x] Wrong grantee/tampered/expired response tests.
- [x] Copy/paste onboarding flow.
- [ ] QR rendering/scanning.
- [ ] Capability revocation/renewal UX.

### Mobile directory integration

- [x] HTTPS-only directory origin setting.
- [x] Reject directory URLs with credentials/path/query/fragment.
- [x] Identity registration from MAUI app.
- [x] Capability-protected reachability check for paired contacts.
- [x] Re-verify saved capability before resolve.
- [x] Pin directory-returned public key against saved contact key.
- [x] Validate malformed server timestamp/key responses.
- [ ] Automatic background presence publication once real ICE candidates exist.

### UX slice

- [x] Identity screen.
- [x] Directory setup and registration status.
- [x] Contact import/pairing.
- [x] Contact list.
- [x] Conversation screen.
- [x] Locally queued text messages.
- [x] Reachability status check.
- [ ] QR contact exchange.
- [ ] Complete settings/security screen.
- [ ] Identity recovery/reset UX.

### Exit criteria

The **local/offline** part is reached: two installations can create identities, exchange identity/pairing data by copy/paste, persist contacts, open conversations and safely queue encrypted text locally. The remaining Phase 2 gap is primarily production UX/recovery/QR hardening, not the local data model.

## 8. Phase 3 — direct connectivity spike

**Status: started — transport abstraction and reachability descriptor validation implemented; concrete ICE transport pending.**

### Implemented transport foundation

`Dyract.Transport` now defines:

```text
IPeerTransport
  StartAsync
  ConnectAsync
  AcceptAsync

IPeerConnection
  PeerId
  State
  SendAsync
  ReceiveAsync
```

Modes:

```text
DirectOnly
AllowRelay
```

- [x] Replaceable `IPeerTransport` abstraction.
- [x] Inbound and outbound connection contracts.
- [x] DirectOnly vs AllowRelay policy.
- [x] Validate resolved lease before connection attempt.
- [x] Reject expired leases client-side.
- [x] Revalidate candidate address/protocol/port client-side.
- [x] DirectOnly strips relay candidates and fails cleanly if relay is the only option.
- [ ] Choose/spike concrete ICE/WebRTC-compatible implementation.
- [ ] Gather real host/server-reflexive candidates on Android/iOS.
- [ ] Add signaling API for offer/answer/trickle candidates where required.
- [ ] Establish first data-only peer channel.
- [ ] Publish gathered candidates through signed presence.
- [ ] Connect inbound accept path.

A current candidate for the spike is SIPSorcery because it supports .NET/WebRTC/ICE/data channels without native wrappers, but this is deliberately **not yet a production dependency**. Physical-device behavior will decide whether it is retained.

### Physical network matrix

- [ ] Android Wi-Fi ↔ Android same Wi-Fi.
- [ ] Android Wi-Fi ↔ Android different network.
- [ ] Android Wi-Fi ↔ mobile network.
- [ ] mobile ↔ mobile.
- [ ] Android ↔ iPhone.
- [ ] IPv4 NAT.
- [ ] CGNAT.
- [ ] IPv6.
- [ ] network change while connected.
- [ ] foreground/background transitions.
- [ ] TURN fallback where direct ICE fails.

Collect only privacy-minimized connection outcome telemetry; do not log peer IDs or candidate addresses by default.

### Exit criteria

A documented physical-device matrix establishes which network combinations support direct paths and when relay is required. That evidence drives the final product defaults for DirectOnly vs AllowRelay.

## 9. Phase 4 — authenticated encrypted peer sessions

Identity authentication and application-session encryption are separate from ICE transport.

- [ ] Ephemeral key agreement with forward secrecy.
- [ ] Long-term identity authentication of the transcript.
- [ ] Pinned identity verification during connection establishment.
- [ ] Sequence numbers/replay protection.
- [ ] Protocol-version negotiation.
- [ ] Downgrade protection.
- [ ] Session renewal/key rotation.
- [ ] Independent review of handshake/key schedule.

The transport layer must not be considered the E2E security layer merely because WebRTC/DTLS encrypts packets. Dyract should authenticate its own peer identity/session semantics independently.

## 10. Phase 5 — reliable messaging

```text
Queued -> Connecting -> Sent -> Delivered -> Read
                     \-> Retry/Failed
```

- [x] UUIDv7 sortable message IDs.
- [x] Transactional local message + outbox commit before network send.
- [ ] Outbox delivery worker.
- [ ] Retry with bounded exponential backoff.
- [ ] Idempotent receive by MessageId.
- [ ] Delivery ACK.
- [ ] Optional read receipt.
- [ ] Reconnect synchronization.
- [ ] Duplicate suppression.
- [ ] Clock-skew-safe ordering.

No undelivered message body is uploaded to the directory.

## 11. Phase 6 — mobile wake-up/offline behavior

Pure IP reachability cannot guarantee WhatsApp-like delivery while a mobile app is suspended.

- [ ] APNs wake routing for iOS.
- [ ] FCM wake routing for Android.
- [ ] Minimum push-routing metadata only.
- [ ] No message body/contact display name/attachment in wake payload.
- [ ] Opaque wake signal that prompts Dyract reconnection.
- [ ] Graceful handling of throttling/unavailable push.

## 12. Phase 7 — attachments

```text
AttachmentManifest
  FileId
  Name
  MIME type
  Size
  SHA-256
  Chunk size
```

- [ ] Chunked direct transfer.
- [ ] Resume by missing ranges.
- [ ] Integrity verification.
- [ ] Transfer limits.
- [ ] Safe filename/content handling.
- [ ] Local cleanup policy.
- [ ] Local thumbnail generation.

## 13. Phase 8 — production directory infrastructure

Suggested production topology:

```text
ASP.NET Core API
PostgreSQL    durable identity/public-key registration
Redis         short-lived presence/nonces/signaling
APNs/FCM      wake routing
STUN          NAT discovery
TURN          optional encrypted packet relay
```

- [x] PostgreSQL identity store available when configured.
- [x] Basic rate/request-size controls.
- [ ] Explicit DB migrations + PostgreSQL CI.
- [ ] Horizontal scaling.
- [ ] Redis-backed ephemeral state.
- [ ] Broader abuse/DDoS strategy.
- [ ] Key/secret management.
- [ ] Privacy-aware operational logs/metrics.
- [ ] Retention specification.
- [ ] Backup/restore policy limited to server-owned metadata.

## 14. Phase 9 — security hardening

Required before public production use:

- [ ] Threat model (STRIDE + privacy/metadata analysis).
- [ ] API penetration test.
- [ ] Protocol fuzzing.
- [ ] Malformed-frame tests.
- [ ] Replay/downgrade tests.
- [ ] Endpoint-enumeration tests.
- [ ] Stolen-device analysis.
- [ ] Reinstall/key-loss/recovery analysis.
- [ ] Dependency/SBOM automation.
- [ ] Independent cryptographic review.
- [ ] Mobile secure-storage review.

## 15. Deferred features

Do not prioritize until one-to-one messaging is secure/reliable:

- group chat,
- multi-device synchronization,
- voice/video calls,
- public usernames/search,
- bots/channels,
- cloud message backup.

Each changes the privacy/security model materially.

## 16. MVP definition

The first usable MVP is reached when:

1. Android and iOS securely persist an identity.
2. Two users exchange/pin contact identities and reciprocal reachability authorization.
3. Both publish authenticated presence.
4. A direct ICE/STUN connection is attempted.
5. The peer channel is mutually identity-authenticated and application-E2E encrypted.
6. Text messages persist locally and retry after failures.
7. Delivery ACKs survive reconnects.
8. Background wake-up is best effort and contains no chat content.
9. Relay use can be enabled/disabled according to privacy mode.
10. Security review has no release-blocking issue.

## 17. Immediate next implementation tasks

1. Validate the latest directory/mobile/transport foundation in CI.
2. Build a concrete ICE/DataChannel spike behind `IPeerTransport` — SIPSorcery 10.0.12 is a candidate, not yet a commitment.
3. Add capability-protected ephemeral signaling for offer/answer/trickle ICE if the chosen implementation requires it.
4. Gather and publish real host/server-reflexive candidates from Android/iOS.
5. Prove Android↔Android and Android↔iPhone connectivity on physical devices.
6. Define and implement the authenticated forward-secret Dyract peer-session handshake.
7. Connect the transactional outbox to the peer transport with retries/ACKs.
8. Add macOS/iOS CI and iPhone build validation.
9. Add QR onboarding and capability renewal/revocation UX.
10. Add explicit PostgreSQL migrations and integration CI.
