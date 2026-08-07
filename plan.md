# Dyract implementation plan

## 1. Product goal

Dyract is a direct-first, privacy-oriented messenger for Android and iPhone written primarily in C#. Each installation owns a cryptographic peer identity. Contacts, locally assigned names, conversations, messages and attachments are device-owned data. A minimal central service authenticates peers and exposes short-lived reachability/signaling metadata; it is not the chat-message store.

The target is **minimum necessary infrastructure with no central conversation history**, not the unrealistic claim that a usable mobile messenger can be completely serverless.

## 2. Architectural invariants

1. A Peer ID is an address, never a password or bearer secret.
2. Peer ID is cryptographically bound to the identity public key.
3. Identity private keys are generated on-device and are never uploaded.
4. Security-sensitive directory operations are authenticated by signatures.
5. Knowing a Peer ID alone must not permit endpoint discovery.
6. Endpoint resolution requires target-issued authorization for the exact grantee.
7. Contacts, local names, conversations and message bodies remain local data.
8. Reachability uses short-lived leases, not permanent IP history.
9. Outgoing messages are committed locally before transport attempts.
10. Direct transport is preferred; relay is an explicit optional fallback.
11. Dyract authenticates the application peer independently of WebRTC/DTLS.
12. Application protocols are versioned and strictly bounded.
13. No raw peer IPs, ICE candidate strings, keys or message contents belong in ordinary diagnostics.

## 3. Current architecture

```text
                       Dyract Directory
                    ASP.NET Core / .NET 10
                 +--------------------------+
                 | identity registration    |
                 | capability verification  |
                 | short-lived presence     |
                 | ephemeral ICE signaling  |
                 | future wake routing      |
                 +------------+-------------+
                              |
                       no chat history
                              |
             +----------------+----------------+
             |                                 |
       Android / iOS                     Android / iOS
        .NET MAUI                         .NET MAUI
             |                                 |
      encrypted SQLite                  encrypted SQLite
      durable outbox                    durable receive
             |                                 |
             +==== authenticated P2P channel ==+
                    ICE/STUN; optional TURN
```

## 4. Repository structure

```text
Dyract.slnx             workload-free core/server/storage/transport/tests
Dyract.Mobile.slnx      shipping MAUI solution
Dyract.TransportSpike.slnx
                        Android FsWebRTC experiment

src/
  Dyract.App/           shipping .NET MAUI Android/iOS client
  Dyract.Client/        directory client and orchestration
  Dyract.Core/          identity/domain primitives
  Dyract.Crypto/        identity + authenticated-session crypto
  Dyract.Protocol/      versioned wire contracts
  Dyract.Server/        identity/presence/signaling service
  Dyract.Storage/       encrypted SQLite + reliable outbox state
  Dyract.Transport/     transport-independent contracts/models

experiments/
  Dyract.Transport.FsWebRtcProbe/
  Dyract.Transport.AndroidHarness/

docs/
  session-security.md
  reliable-messaging.md
```

## 5. Phase 0 — identity and directory bootstrap

**Status: prototype implemented; production hardening remains.**

- [x] .NET 10 solution layout.
- [x] P-256 identity generation/signing.
- [x] Peer ID derived from identity public key.
- [x] Challenge/response registration.
- [x] Signed requests with timestamps/nonces/replay protection.
- [x] Exact-peer lookup/resolve model.
- [x] Request-size and basic rate limits.
- [x] In-memory identity store for local/test use.
- [x] Optional PostgreSQL identity persistence.
- [ ] Explicit PostgreSQL migrations and PostgreSQL CI.
- [ ] Production structured logging/metrics and retention policy.
- [ ] Horizontal scaling / Redis-backed ephemeral state.

## 6. Phase 1 — contact authorization and presence

**Status: prototype complete.**

Identity exchange and endpoint authorization are separate:

```text
dyract://contact/v1/...   -> pin PeerId + public key
dyract://pair/v1/...      -> signed reachability authorization
```

- [x] Versioned contact invitation.
- [x] PeerId/public-key binding validation.
- [x] Human-verifiable fingerprint.
- [x] Target-signed capability bound to exact grantee.
- [x] Capability expiry/wrong-grantee/tamper validation.
- [x] Signed short-lived presence publication/removal.
- [x] Maximum two-minute presence lease.
- [x] Capability-protected peer resolve.
- [x] No permanent endpoint/IP history in prototype store.
- [x] Candidate count/shape validation.
- [ ] Pre-expiry capability revocation/rotation.
- [ ] Redis/TTL presence/signaling store when scaling requires it.

## 7. Phase 2 — local mobile foundation

**Status: functional local/offline foundation implemented.**

### Identity and local data

- [x] First-run identity creation.
- [x] MAUI SecureStorage integration.
- [x] Android Keystore / iOS Keychain-backed storage through MAUI.
- [x] Reinstall treated as a new identity until recovery exists.
- [x] Android app backup disabled for the current privacy model.
- [x] Encrypted SQLite repositories.
- [x] Contacts/conversations/messages/outbox repositories.
- [x] Independent local-data encryption key.
- [x] AES-256-GCM encryption of user-content fields.
- [x] UUIDv7 message/conversation IDs where applicable.
- [ ] Formal schema migrations beyond prototype v1.
- [ ] Identity export/recovery/reset UX.
- [ ] Evaluate non-exportable native identity keys / Secure Enclave.

### User flow

- [x] Identity screen.
- [x] Directory registration.
- [x] Contact invitation import.
- [x] Reciprocal pairing capability import.
- [x] Contact list.
- [x] Conversation screen.
- [x] Locally queued text messages.
- [x] Reachability check for paired contacts.
- [ ] QR rendering/scanning.
- [ ] Complete security/settings UX.

## 8. Phase 3 — direct connectivity spike

**Status: Android native WebRTC experiment implemented and CI-valid; physical-device evidence is now the gate.**

The shipping code remains transport-independent. The concrete Android spike is under `experiments/` using `FsWebRTC.Bindings.Maui.Android`.

Implemented:

- [x] Replaceable `IPeerTransport` / peer transport contracts.
- [x] DirectOnly vs AllowRelay policy in shared transport models.
- [x] Capability-protected ephemeral signaling.
- [x] Offer/answer/trickle ICE handling.
- [x] Android DataChannel creation/open handling.
- [x] Host/STUN candidate observation.
- [x] Privacy-safe observed candidate categories (`host`, `srflx`, `prflx`, `relay` + `udp`/`tcp`).
- [x] Privacy-safe selected ICE path through `RTCStats`.
- [x] Selected path resolved only from `transport.selectedCandidatePairId` and referenced candidate stats.
- [x] Raw candidate addresses/ports/stats IDs kept out of harness UI/logs.
- [x] RTCStats callback retained safely across caller timeout.
- [x] Android diagnostic APK produced by CI.

Still open:

- [ ] Android Wi-Fi ↔ Android same Wi-Fi physical proof.
- [ ] Android across different Wi-Fi/NAT networks.
- [ ] Wi-Fi ↔ cellular.
- [ ] cellular ↔ cellular / CGNAT characterization.
- [ ] IPv6 physical proof.
- [ ] network-change/reconnect behavior.
- [ ] foreground/background lifecycle behavior.
- [ ] TURN/AllowRelay experiment after DirectOnly evidence.
- [ ] iOS WebRTC runtime adapter and Android ↔ iPhone proof.

### FsWebRTC production blocker

Current Android builds report `XA0141`: the FsWebRTC native `libjingle_peerconnection_so.so` does not satisfy the Android 16 KB page-size requirement. FsWebRTC must **not** be promoted into the shipping messenger until this is fixed upstream/replaced and physical-device behavior is acceptable.

## 9. Phase 4 — authenticated encrypted peer sessions

**Status: implemented as transport-independent protocol code and exercised by the Android harness; independent review remains mandatory.**

Implemented:

- [x] Fresh ephemeral P-256 ECDH per session.
- [x] Long-term identity signatures over the handshake transcript.
- [x] Pinned PeerId/public-key verification.
- [x] Session-ID and initiator/responder role binding.
- [x] HKDF-SHA256 directional session keys.
- [x] AES-256-GCM `DYSE` application-session framing.
- [x] Monotonic sequence/replay/out-of-order rejection for the reliable ordered channel.
- [x] Protocol versioning and size bounds.
- [x] Adversarial automated tests for tampering/wrong identity/wrong session/replay.
- [x] Physical harness performs the authenticated handshake before protocol probes.
- [ ] Independent cryptographic review.
- [ ] Decide whether reconnect-level forward secrecy is sufficient or a reviewed Double-Ratchet/Noise-style design is required.
- [ ] Formal/fuzz/property testing of the final frozen wire protocol.

See `docs/session-security.md`.

## 10. Phase 5 — reliable messaging

**Status: transport-neutral reliability algorithm implemented and tested; shipping transport scheduling is intentionally not connected yet.**

```text
Queued -> send attempt -> Sent -> wait for peer ACK -> Delivered
             |                       |
             +---- Failed/retry -----+
```

Implemented:

- [x] Transactional local message + outbox commit before network send.
- [x] Versioned `DYRM` text and delivery-ACK frames.
- [x] Canonical lowercase MessageId validation.
- [x] Authenticated sender/recipient scope checks.
- [x] Durable incoming insert before ACK.
- [x] Idempotent receive by MessageId.
- [x] Exact-duplicate suppression.
- [x] Changed-content/scope collision rejection.
- [x] Duplicate ACK re-emission after lost ACK.
- [x] Exact-peer ACK authorization before clearing outbox.
- [x] Due-only outbox selection.
- [x] Deterministic retry of the original MessageId/CreatedAt/text.
- [x] ACK-timeout retry scheduling.
- [x] Bounded exponential failure backoff.
- [x] Privacy-safe persisted failure codes only.
- [x] Plaintext DYRM send buffer clearing after transport use.
- [x] Two-encrypted-database lost-first-ACK integration proof.
- [x] Experimental authenticated DataChannel `DYRM` message -> delivery ACK probe.

Still open:

- [ ] Production `IPeerApplicationFrameSender` implementation selected from the proven transport.
- [ ] Mobile lifecycle/background delivery scheduler.
- [ ] Reconnect/session management around the worker.
- [ ] Read receipts.
- [ ] Long-offline synchronization strategy.
- [ ] Clock-skew-aware presentation ordering.
- [ ] Physical-device proof of DYRM ACK round trip across the network matrix.

See `docs/reliable-messaging.md`.

## 11. Phase 6 — mobile wake-up/offline behavior

**Status: not implemented.**

Pure P2P cannot guarantee delivery while mobile operating systems suspend both apps.

- [ ] APNs wake routing for iOS.
- [ ] FCM wake routing for Android.
- [ ] Opaque wake payload only; no message body/contact display name/attachment metadata.
- [ ] Best-effort reconnect after wake.
- [ ] Graceful handling of throttled/unavailable push.
- [ ] Document delivery guarantees for Strict DirectOnly vs AllowRelay/wake-enabled mode.

## 12. Phase 7 — attachments

**Status: not implemented.**

- [ ] Versioned attachment manifest.
- [ ] Chunked direct transfer.
- [ ] Resume by missing ranges.
- [ ] SHA-256 integrity verification.
- [ ] Transfer/size limits.
- [ ] Safe filename/content handling.
- [ ] Local cleanup policy and thumbnails.

## 13. Phase 8 — production infrastructure

- [x] PostgreSQL identity store available when configured.
- [x] Basic API rate/request-size controls.
- [ ] Explicit DB migrations + PostgreSQL CI.
- [ ] Redis-backed ephemeral state for horizontal scaling.
- [ ] Production STUN/TURN deployment decision.
- [ ] APNs/FCM integration.
- [ ] Secret/key management.
- [ ] Privacy-aware logs/metrics/retention.
- [ ] Backup/restore limited to server-owned metadata.

## 14. Phase 9 — security hardening

Required before public production use:

- [ ] Formal threat model including metadata/privacy analysis.
- [ ] API penetration test.
- [ ] Protocol fuzzing/property tests.
- [ ] Replay/downgrade/session-collision tests at system level.
- [ ] Endpoint-enumeration/abuse tests.
- [ ] Stolen-device and recovery analysis.
- [ ] Dependency/SBOM automation.
- [ ] Independent cryptographic review.
- [ ] Mobile secure-storage review.

## 15. Deferred until one-to-one messaging is proven

- group chat;
- multi-device synchronization;
- voice/video calls;
- public usernames/search;
- bots/channels;
- cloud message backup.

Each materially changes the privacy/security model.

## 16. MVP definition

The first usable MVP requires:

1. Android and iOS securely persist identity and local encrypted data.
2. Two users exchange/pin identity and reciprocal reachability authorization.
3. Both can publish short-lived authenticated presence/signaling state.
4. A direct ICE/STUN connection is attempted and its path is privacy-safely observable.
5. The peer channel completes the Dyract authenticated encrypted application session.
6. Text messages are locally durable and use the tested retry/dedup/ACK semantics.
7. Mobile lifecycle integration retries queued messages after reconnect/wake.
8. Background wake-up is best effort and contains no chat content.
9. Relay use is an explicit policy choice.
10. Android/iOS physical matrices and security review have no release-blocking issue.

## 17. Immediate next tasks

Do **not** add more transport-dependent product code until the physical Android spike is measured.

1. Install the latest Android transport harness on two physical Android devices.
2. Run same-Wi-Fi DirectOnly first with STUN blank.
3. Record observed candidate categories, **selected ICE path**, authenticated-session result, DYRT RTT and DYRM ACK RTT.
4. Repeat on different Wi-Fi/NAT, Wi-Fi ↔ cellular, cellular ↔ cellular and IPv6.
5. Exercise close/retry and network transition behavior.
6. Decide whether FsWebRTC remains viable based on physical behavior and the Android 16 KB native-library blocker.
7. If viable, create the production Android `IPeerApplicationFrameSender`/transport adapter and connect the existing outbox worker behind lifecycle-safe scheduling.
8. Add the corresponding iOS transport adapter and Android ↔ iPhone matrix.
9. Only after transport viability: implement APNs/FCM wake routing and optional TURN fallback.
10. In parallel, continue QR onboarding, recovery UX, database migrations and security/fuzz review work because these do not depend on the final transport choice.
