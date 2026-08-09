# Dyract implementation plan

## 1. Product goal

Dyract is a direct-first, privacy-oriented messenger for Android and iPhone written primarily in C#/.NET 10. Each installation owns a cryptographic identity. Contacts, locally assigned names, conversations, message bodies and future attachments are device-owned data. A minimal central directory authenticates peers and carries short-lived reachability/signaling metadata; it is not the chat-message store.

The target is **minimum necessary infrastructure with no central conversation history**.

## 2. Architectural invariants

1. A Peer ID is an address, never a password or bearer secret.
2. Peer ID is derived from the identity public key.
3. Identity private keys are generated on-device and never uploaded.
4. Security-sensitive directory operations are signed and replay protected.
5. Knowing a Peer ID alone must not expose endpoint metadata.
6. Endpoint resolution/signaling requires target-issued authorization for the exact grantee.
7. Contacts, local names, conversations and message bodies remain local data.
8. Reachability uses short-lived leases, not permanent IP history.
9. Contact identity and reachability authorization are separate concepts.
10. Outgoing messages are committed locally before any transport attempt.
11. Direct transport is preferred; relay is an explicit optional policy.
12. Dyract authenticates the application peer independently of WebRTC/DTLS.
13. Wire protocols are versioned, bounded and replay aware.
14. Ordinary diagnostics do not expose raw Peer IDs, IPs, ICE strings, keys or message contents.
15. Configured shared infrastructure must fail closed rather than silently degrading to process-local security state.

## 3. Repository structure

```text
Dyract.slnx                 core/server/storage/transport/tests
Dyract.Mobile.slnx          shipping MAUI Android/iOS app
Dyract.TransportSpike.slnx  isolated Android WebRTC experiment

src/
  Dyract.App/               .NET MAUI client
  Dyract.Client/            directory/pairing/signaling helpers
  Dyract.Core/              identity/domain primitives
  Dyract.Crypto/            signatures + authenticated-session crypto
  Dyract.Protocol/          versioned wire contracts/proofs
  Dyract.Server/            minimal directory/signaling service
  Dyract.Storage/           encrypted SQLite + migrations/outbox
  Dyract.Transport/         transport-neutral contracts

experiments/
  Dyract.Transport.FsWebRtcProbe/
  Dyract.Transport.AndroidHarness/

tests/
  Dyract.Tests/

docs/
  session-security.md
  reliable-messaging.md
  signaling.md
  transport-spike.md
  local-storage-migrations.md
  server-database-migrations.md
  capability-revocation.md
  redis-transient-state.md
  rate-limiting.md
  threat-model.md
  protocol-fuzzing.md
  device-compromise-recovery.md
  sbom.md
```

## 4. Phase 0 — identity and directory bootstrap

**Status: functional foundation implemented; deployment/security hardening remains.**

Implemented:

- [x] P-256 identity generation/signing.
- [x] Peer ID derived from public key.
- [x] challenge/response registration.
- [x] signed peer lookup.
- [x] timestamp + nonce replay protection.
- [x] request body/rate limits.
- [x] reusable .NET directory client.
- [x] in-memory identity store for local/test use.
- [x] optional PostgreSQL identity store.
- [x] ordered PostgreSQL schema migration ledger.
- [x] PostgreSQL advisory-lock serialization for concurrent migrators.
- [x] existing critical table-shape validation before adoption and on later startup.
- [x] PostgreSQL 18 integration CI.
- [x] optional Redis shared registration challenges.
- [x] optional Redis shared signed-request replay protection.
- [x] Redis startup availability check/fail-closed behavior.
- [x] Redis 8 integration CI.
- [x] layered process-local + Redis shared application request limiting.
- [x] ASP.NET integration tests.

Remaining:

- [ ] production secret management.
- [ ] Redis TLS/authentication/network deployment policy.
- [ ] privacy-aware structured logs/metrics/retention.
- [ ] production edge/network DDoS and abuse-control deployment/validation.
- [ ] server metadata backup/restore policy.

See `docs/server-database-migrations.md`, `docs/redis-transient-state.md` and `docs/rate-limiting.md`.

## 5. Phase 1 — contact authorization, presence and signaling

**Status: protocol/server feature set implemented with optional shared infrastructure.**

### Identity/contact exchange

```text
dyract://contact/v1/...
```

- [x] versioned invitation format.
- [x] PeerId/public-key binding validation.
- [x] local identity fingerprint presentation.
- [x] local-only display name.
- [x] copy/paste exchange.
- [x] QR rendering/scanning.
- [x] scanner accepts only structurally valid Dyract contact/pairing payloads.
- [x] QR scanning never bypasses normal cryptographic verification.
- [x] Android Release compilation of QR path.
- [x] iOS simulator Release compilation of QR/camera path.

Physical camera/scanner runtime still needs device validation on both platforms.

### Reachability capability

```text
dyract://pair/v1/...
```

- [x] target-signed capability.
- [x] exact issuer/grantee binding.
- [x] random 128-bit capability ID.
- [x] default 30-day lifetime.
- [x] global 90-day maximum lifetime policy.
- [x] reciprocal pairing import/verification.
- [x] incoming capability encrypted locally.
- [x] outgoing/issued capability tracked separately and encrypted locally.
- [x] one still-valid tracked issued grant reused per contact.
- [x] pre-expiry signed revocation.
- [x] revocation blocks both `/peer/resolve` and `/signal/send`.
- [x] revoked capability IDs retained only until natural expiry.
- [x] server revocation metadata omits the grantee/contact graph.
- [x] per-issuer active revocation bound (512 prototype limit).
- [x] PostgreSQL-backed revocation persistence.
- [x] revocation survives process restart/fresh store instance.
- [x] multiple PostgreSQL-backed instances share the same revocation state.
- [x] revocation schema drift is rejected on server startup.

Without PostgreSQL, revocations use the in-memory development/test store and are intentionally process-local.

See `docs/capability-revocation.md`.

### Presence

- [x] signed publish/update.
- [x] signed removal.
- [x] maximum two-minute lease.
- [x] automatic/logical expiry.
- [x] candidate shape/count/address validation.
- [x] capability-protected peer resolve.
- [x] no permanent endpoint/IP history.
- [x] optional Redis shared presence leases.
- [x] cross-instance publish/get/remove integration tests.
- [x] hashed PeerId token in Redis presence key names.

### Ephemeral WebRTC signaling

- [x] capability-protected signal send.
- [x] signed target fetch + ACK.
- [x] offer/answer/candidate/end/close types only.
- [x] maximum 60-second signal TTL.
- [x] maximum 32 KiB signal payload.
- [x] bounded target inbox (64).
- [x] fetch retains items until explicit ACK.
- [x] signaling is not used as an offline chat queue.
- [x] revoked capabilities rejected for new signaling.
- [x] optional Redis shared signaling inbox.
- [x] atomic Redis expiry/capacity enforcement.
- [x] cross-instance non-destructive fetch and target-scoped ACK tests.
- [x] SHA-256-derived target token/cluster hash tag in Redis key names.

See `docs/signaling.md` and `docs/redis-transient-state.md`.

## 6. Phase 2 — local mobile foundation

**Status: Android and iOS shipping-app code both compile in Release CI; physical/runtime validation remains.**

### Identity/security

- [x] first-run identity generation.
- [x] MAUI SecureStorage persistence.
- [x] Android Keystore-backed secure storage.
- [x] iOS Keychain path implemented.
- [x] unreadable identity is not silently replaced.
- [x] Android app backup disabled.
- [x] identity fingerprint/Peer ID UI.
- [x] Android Release CI.
- [x] iOS `iossimulator-arm64` Release CI on macOS 26 / Xcode 26.6.
- [x] current Android and iOS shipping builds are warning-clean.
- [x] repository stolen-device/recovery security analysis.

Remaining:

- [ ] physical-device iOS runtime validation.
- [ ] physical-device QR/camera validation.
- [ ] non-exportable platform-native identity-key evaluation.
- [ ] Secure Enclave evaluation.
- [ ] encrypted identity export/recovery.
- [ ] identity reset/recovery UX.

See `docs/device-compromise-recovery.md`.

### Encrypted SQLite

- [x] contacts/conversations/messages/outbox.
- [x] independent 256-bit local-data key.
- [x] AES-256-GCM content-field encryption.
- [x] wrong-key tests.
- [x] SQLitePCLRaw native bundle updated to 3.0.5 and cross-platform CI validated.
- [x] formal append-only migration ledger.
- [x] migration 1 adopts historical v1.
- [x] real v1 -> v2 migration.
- [x] v2 adds encrypted per-contact issued-capability state.
- [x] existing encrypted contact data preserved through upgrade tests.
- [x] newer/malformed database versions rejected fail-closed.

Operational metadata such as Peer IDs and timestamps remains visible in SQLite; Dyract does not claim full-file opacity.

See `docs/local-storage-migrations.md`.

### Current mobile UX

- [x] identity + directory configuration.
- [x] register identity.
- [x] contact list.
- [x] contact QR display/scanning.
- [x] pairing QR display/scanning.
- [x] separate incoming/outgoing grant direction in contact UI.
- [x] revoke issued reachability grant.
- [x] local conversation screen.
- [x] locally queued text messages.
- [x] capability-protected reachability check.
- [x] compiled XAML bindings for contact/message list templates.

Remaining:

- [ ] recovery/security settings screen.
- [ ] accessibility/polish/localization.
- [ ] physical iOS UX/runtime validation.

## 7. Phase 3 — direct connectivity spike

**Status: Android compile-level spike is mature; physical-device matrix is now the real gate.**

Transport-neutral foundation:

- [x] `IPeerTransport` / `IPeerConnection` contracts.
- [x] DirectOnly vs AllowRelay policy.
- [x] resolved lease/candidate validation.
- [x] relay candidates stripped in DirectOnly mode.

Experimental FsWebRTC Android path:

- [x] .NET 10 Android binding compile proof.
- [x] native PeerConnection factory/configuration.
- [x] STUN/ICE configuration.
- [x] offer/answer/trickle signaling coordinator.
- [x] DataChannel creation/accept.
- [x] actual DataChannel OPEN gating.
- [x] asynchronous SDP/ICE observers.
- [x] binary-only bounded DataChannel frames.
- [x] privacy-safe observed ICE categories.
- [x] privacy-safe selected ICE path via RTCStats.
- [x] RTCStats callback lifetime safe across caller timeout.
- [x] standalone physical-device Android harness/APK.
- [x] encrypted `DYRT` ping/pong probe.
- [x] encrypted `DYRM` message/ACK probe.

Do **not** move FsWebRTC into the shipping app yet.

### Physical matrix still required

- [ ] Android same Wi-Fi -> same Wi-Fi.
- [ ] Android different Wi-Fi/NAT.
- [ ] Wi-Fi -> cellular.
- [ ] cellular -> Wi-Fi.
- [ ] cellular -> cellular/CGNAT.
- [ ] IPv6.
- [ ] network transition while connected.
- [ ] foreground/background lifecycle.
- [ ] close/reconnect behavior.
- [ ] TURN/AllowRelay after DirectOnly evidence.
- [ ] iOS WebRTC transport adapter.
- [ ] Android -> iPhone.

Record only candidate classes/transport, selected path, connection stage and RTTs; never raw candidate/IP data.

### FsWebRTC production blocker

Current Android harness builds expose `XA0141`: the bundled `libjingle_peerconnection_so.so` does not satisfy Android's 16 KiB page-size requirement. The native probe itself is warning-clean; the remaining warning comes from the FsWebRTC native library packaged by the harness. FsWebRTC remains experimental until this is fixed upstream/replaced and physical behavior is acceptable.

## 8. Phase 4 — authenticated encrypted peer sessions

**Status: implemented as transport-independent protocol code; independent cryptographic review remains mandatory.**

- [x] ephemeral P-256 ECDH per session.
- [x] long-term ECDSA identity signatures.
- [x] pinned PeerId/public-key verification.
- [x] session ID + role binding.
- [x] transcript binding.
- [x] HKDF-SHA256 directional keys.
- [x] AES-256-GCM `DYSE` frames.
- [x] monotonic sequence/replay/out-of-order rejection.
- [x] protocol version/size bounds.
- [x] adversarial tests for tampering/wrong identity/wrong session/replay.
- [x] deterministic repository fuzz/property tests for handshake, `DYSE` and `DYRM` boundaries.
- [x] Android transport harness performs handshake before probes.

Remaining:

- [ ] independent cryptographic review.
- [ ] coverage-guided/external fuzzing beyond deterministic repository regression properties.
- [ ] decide reconnect-level forward secrecy vs reviewed Noise/Double-Ratchet design.

See `docs/session-security.md` and `docs/protocol-fuzzing.md`.

## 9. Phase 5 — reliable messaging

**Status: transport-neutral reliability/catch-up algorithm implemented/tested; shipping scheduler intentionally not connected to experimental transport.**

- [x] transactional message + outbox commit before send.
- [x] versioned `DYRM` text, delivery ACK and read ACK frames.
- [x] canonical MessageId validation.
- [x] authenticated sender/recipient scope.
- [x] durable receive before ACK.
- [x] idempotent duplicate receive.
- [x] changed-content collision rejection.
- [x] duplicate ACK re-emission after lost ACK.
- [x] exact-peer ACK authorization.
- [x] due-only outbox selection.
- [x] deterministic retry of same MessageId/CreatedAt/text.
- [x] ACK-timeout retry.
- [x] bounded failure backoff.
- [x] privacy-safe persisted failure codes.
- [x] two-database lost-first-ACK proof.
- [x] explicit durable peer-scoped read receipts.
- [x] presentation ordering under clock skew uses local receive time for incoming messages.
- [x] latest-message limiting uses the same clock-skew-safe presentation order.
- [x] bounded multi-page long-offline catch-up from the sender-owned durable outbox.
- [x] per-activation catch-up budget prevents unbounded reconnect drains.
- [x] experimental authenticated DataChannel message/ACK probe.

Remaining:

- [ ] production `IPeerApplicationFrameSender` selected from proven transport.
- [ ] lifecycle-safe mobile delivery scheduler.
- [ ] reconnect/session management around outbox worker.

See `docs/reliable-messaging.md`.

## 10. Phase 6 — mobile wake/offline behavior

**Status: not implemented.**

- [ ] FCM wake routing for Android.
- [ ] APNs wake routing for iOS.
- [ ] opaque wake payload only.
- [ ] no message/contact/attachment content in push metadata.
- [ ] reconnect after best-effort wake.
- [ ] documented behavior when wake is throttled/unavailable.

## 11. Phase 7 — attachments

**Status: not implemented.**

- [ ] versioned manifest.
- [ ] chunked direct transfer.
- [ ] resume by missing ranges.
- [ ] SHA-256 integrity verification.
- [ ] transfer limits and safe filenames/content handling.
- [ ] local cleanup/thumbnails.

## 12. Phase 8 — production infrastructure

- [x] optional PostgreSQL identity store.
- [x] ordered PostgreSQL schema migrations.
- [x] PostgreSQL table-shape adoption/startup validation.
- [x] PostgreSQL advisory locking for concurrent application startup.
- [x] PostgreSQL-backed metadata-minimized capability revocations.
- [x] PostgreSQL 18 migration/revocation integration CI.
- [x] optional Redis shared registration challenges.
- [x] optional Redis shared replay protection.
- [x] optional Redis shared presence leases.
- [x] optional Redis atomic signaling inboxes.
- [x] Redis 8 cross-instance integration CI.
- [x] configured Redis startup fail-closed check.
- [x] process-local request/rate controls.
- [x] Redis shared fixed-window application rate limiting across directory instances.
- [x] shared limiter client-partition hashing and middleware routing tests.
- [ ] production Redis TLS/authentication/network policy.
- [ ] production edge/network DDoS/WAF/global abuse-control deployment and validation.
- [ ] production STUN/TURN deployment decision.
- [ ] APNs/FCM integration.
- [ ] secret/key management.
- [ ] privacy-aware logs/metrics/retention.
- [ ] server metadata backup/restore policy.

See `docs/server-database-migrations.md`, `docs/redis-transient-state.md` and `docs/rate-limiting.md`.

## 13. Phase 9 — security hardening

Before public production use:

- [x] repository STRIDE + metadata/privacy threat model.
- [ ] independent threat-model/security review.
- [ ] API penetration testing.
- [x] deterministic repository protocol fuzz/property tests.
- [x] repository/session-level replay, downgrade and cross-session isolation regression tests.
- [ ] end-to-end production-transport replay/downgrade/session-collision validation.
- [x] repository endpoint-enumeration/capability-abuse integration tests.
- [x] stolen-device/recovery analysis.
- [x] SBOM/dependency automation.
- [ ] coverage-guided/external fuzzing and minimized corpus workflow.
- [ ] independent cryptographic review.
- [ ] mobile secure-storage review.

See `docs/threat-model.md`, `docs/protocol-fuzzing.md`, `docs/device-compromise-recovery.md` and `docs/sbom.md`.

## 14. Deferred until one-to-one messaging is proven

- group chat;
- multi-device synchronization;
- voice/video calls;
- public usernames/search;
- bots/channels;
- cloud message backup.

Each changes the privacy/security model materially.

## 15. Immediate next tasks

Transport-dependent product work remains gated by physical evidence.

1. Run the latest Android physical harness on two real devices, same Wi-Fi first.
2. Record observed candidate categories, selected path, authenticated-session result, `DYRT` RTT and `DYRM` ACK RTT.
3. Repeat across NAT/cellular/IPv6/network transitions.
4. Decide FsWebRTC viability versus the 16 KiB native-library blocker.
5. Validate the shipping iOS UI/QR/SecureStorage path on a physical iPhone.
6. If Android transport is viable, implement the production Android transport/frame sender, reconnect/session ownership and lifecycle-safe outbox/backlog scheduler.
7. Select and implement the iOS WebRTC transport adapter, then run Android -> iPhone physical tests.
8. Define/enforce production Redis TLS/authentication/network policy and edge abuse-control deployment before horizontal public deployment.
9. Add recovery/security settings UX without introducing weak/plaintext identity export.
10. Continue platform-native non-exportable key evaluation, coverage-guided fuzzing, privacy-aware observability, independent threat/security review and cryptographic review in parallel.
