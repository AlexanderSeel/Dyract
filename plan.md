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
```

## 4. Phase 0 — identity and directory bootstrap

**Status: prototype implemented; production infrastructure hardening remains.**

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
- [x] existing identity-table shape validation before adoption.
- [x] PostgreSQL 18 integration CI.
- [x] ASP.NET integration tests.

Remaining:

- [ ] production secret management.
- [ ] privacy-aware structured logs/metrics/retention.
- [ ] server metadata backup/restore policy.

See `docs/server-database-migrations.md`.

## 5. Phase 1 — contact authorization, presence and signaling

**Status: prototype feature set implemented.**

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

Prototype limitation: revocations are currently in-memory server state. Before horizontally scaled production use they must move to TTL-capable shared state so process restart cannot resurrect a revoked grant.

See `docs/capability-revocation.md`.

### Presence

- [x] signed publish/update.
- [x] signed removal.
- [x] maximum two-minute lease.
- [x] automatic expiry.
- [x] candidate shape/count/address validation.
- [x] capability-protected peer resolve.
- [x] no permanent endpoint/IP history in prototype store.

### Ephemeral WebRTC signaling

- [x] capability-protected signal send.
- [x] signed target fetch + ACK.
- [x] offer/answer/candidate/end/close types only.
- [x] maximum 60-second signal TTL.
- [x] maximum 32 KiB signal payload.
- [x] bounded target inbox.
- [x] fetch retains items until explicit ACK.
- [x] signaling is not used as an offline chat queue.
- [x] revoked capabilities rejected for new signaling.

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

Remaining:

- [ ] physical-device iOS runtime validation.
- [ ] physical-device QR/camera validation.
- [ ] non-exportable platform-native identity-key evaluation.
- [ ] Secure Enclave evaluation.
- [ ] encrypted identity export/recovery.
- [ ] identity reset/recovery UX.

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
- [x] Android transport harness performs handshake before probes.

Remaining:

- [ ] independent cryptographic review.
- [ ] fuzz/property tests.
- [ ] decide reconnect-level forward secrecy vs reviewed Noise/Double-Ratchet design.

See `docs/session-security.md`.

## 9. Phase 5 — reliable messaging

**Status: transport-neutral algorithm implemented/tested; shipping scheduler intentionally not connected to experimental transport.**

- [x] transactional message + outbox commit before send.
- [x] versioned `DYRM` text and delivery ACK frames.
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
- [x] experimental authenticated DataChannel message/ACK probe.

Remaining:

- [ ] production `IPeerApplicationFrameSender` selected from proven transport.
- [ ] lifecycle-safe mobile delivery scheduler.
- [ ] reconnect/session management around outbox worker.
- [ ] read receipts.
- [ ] long-offline synchronization strategy.
- [ ] presentation ordering under clock skew.

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
- [x] PostgreSQL table-shape adoption validation.
- [x] PostgreSQL advisory locking for concurrent application startup.
- [x] PostgreSQL 18 migration integration CI.
- [x] request/rate controls.
- [ ] Redis/shared TTL state for presence, replay, signaling and revocations.
- [ ] production STUN/TURN deployment decision.
- [ ] APNs/FCM integration.
- [ ] secret/key management.
- [ ] privacy-aware logs/metrics/retention.
- [ ] server metadata backup/restore policy.

See `docs/server-database-migrations.md`.

## 13. Phase 9 — security hardening

Before public production use:

- [ ] formal STRIDE + metadata/privacy threat model.
- [ ] API penetration testing.
- [ ] protocol fuzz/property tests.
- [ ] system-level replay/downgrade/session-collision tests.
- [ ] endpoint-enumeration/abuse tests.
- [ ] stolen-device/recovery analysis.
- [ ] SBOM/dependency automation.
- [ ] independent cryptographic review.
- [ ] mobile secure-storage review.

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
6. If Android transport is viable, implement the production Android transport/frame sender and lifecycle-safe outbox scheduler.
7. Select and implement the iOS WebRTC transport adapter, then run Android -> iPhone physical tests.
8. Move ephemeral server state, including capability revocations, to shared TTL infrastructure before horizontal production deployment.
9. Continue recovery UX, fuzzing, threat modeling and independent security review in parallel.
