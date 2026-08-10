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
Dyract.slnx                 core/server/storage/transport/tests/fuzz harness
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

fuzz/
  Dyract.Protocol.Fuzz/     SharpFuzz/libFuzzer parser + authenticated-session state harness

docs/
  session-security.md
  reliable-messaging.md
  signaling.md
  attachments.md
  attachment-sender-lifecycle.md
  transport-spike.md
  local-storage-migrations.md
  server-database-migrations.md
  server-backup-restore.md
  capability-revocation.md
  redis-transient-state.md
  rate-limiting.md
  observability.md
  secret-management.md
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
- [x] production Redis TLS/authentication/non-admin/network-isolation startup policy.
- [x] production PostgreSQL/Redis connection-secret source enforcement.
- [x] privacy-aware structured application logs/metrics and retention policy.
- [x] server metadata backup/restore policy.
- [x] ASP.NET integration tests.

Remaining:

- [ ] production secret-manager deployment, access-control/rotation and revocation validation.
- [ ] production edge/network DDoS and abuse-control deployment/validation.
- [ ] production observability backend/access/retention configuration and validation.
- [ ] production PostgreSQL backup/PITR/retention configuration and restore-drill validation.

See `docs/server-database-migrations.md`, `docs/server-backup-restore.md`, `docs/redis-transient-state.md`, `docs/rate-limiting.md`, `docs/observability.md` and `docs/secret-management.md`.

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

**Status: shipping mobile foundation implemented; latest attachment file-lifecycle additions still require current Android/iOS CI and physical runtime validation.**

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
- [x] pre-attachment-lifecycle Android and iOS shipping builds were warning-clean.
- [x] repository stolen-device/recovery security analysis.
- [x] explicit two-step destructive identity/local-data reset.
- [x] reset remains reachable when an initialized identity is unreadable.
- [x] persisted pending-reset marker resumes interrupted reset before normal initialization.
- [x] reset rotates identity/local-data secrets and clears identity-bound SQLite/capability/directory/app-owned attachment state.

Remaining:

- [ ] current Android/iOS Release CI validation for the latest attachment lifecycle additions.
- [ ] physical-device iOS runtime validation.
- [ ] physical-device QR/camera validation.
- [ ] physical Android/iOS destructive-reset validation, including app-owned attachment files.
- [ ] non-exportable platform-native identity-key evaluation.
- [ ] Secure Enclave evaluation.
- [ ] encrypted identity export/recovery.

See `docs/device-compromise-recovery.md` and `docs/attachments.md`.

### Encrypted SQLite

- [x] contacts/conversations/messages/outbox.
- [x] independent 256-bit local-data key.
- [x] AES-256-GCM content-field encryption.
- [x] wrong-key tests.
- [x] SQLitePCLRaw native bundle updated to 3.0.5 and cross-platform CI validated.
- [x] formal append-only migration ledger.
- [x] migration 1 adopts historical v1.
- [x] migration 2 adds encrypted per-contact issued-capability state.
- [x] migration 3 adds encrypted durable partial attachment receive/chunk state.
- [x] migration 4 adds atomic per-peer/global attachment receive reservation quotas.
- [x] migration 5 adds encrypted durable attachment sender snapshots/outbox state and send quotas.
- [x] migration 6 adds encrypted bounded attachment completion receipts for final-ACK replay.
- [x] existing encrypted contact data preserved through current-version upgrade tests.
- [x] newer/malformed database versions rejected fail-closed.
- [x] transactional user-row reset preserves schema/migration ledger for in-process key rotation.

Operational metadata such as Peer IDs and timestamps remains visible in SQLite during normal use; Dyract does not claim full-file opacity or forensic secure erase after reset.

See `docs/local-storage-migrations.md` and `docs/device-compromise-recovery.md`.

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
- [x] recovery/security status screen with PeerId/fingerprint/protection/recovery state.
- [x] destructive reset confirmation/recovery flow.
- [x] compiled XAML bindings for contact/message list templates.
- [x] provider-safe attachment picker snapshots selected content immediately into the encrypted sender queue without persisting a filesystem/provider path.
- [x] per-contact pending attachment status/progress with Retry and explicit Cancel controls.
- [x] app-owned generated receive destination/capacity service registered without connecting production transport.

Remaining:

- [ ] accessibility/polish/localization.
- [ ] physical iOS UX/runtime validation.
- [ ] physical Android/iOS attachment picker/provider/low-disk/promotion/retry/cancel validation.

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
- [x] coverage-guided stateful `DYSH`/`DYSE` targets using internally generated valid sessions and bounded mutation/state instructions.
- [x] bounded CI fuzz-harness smoke verifies valid/mutated handshake and encrypted-session sequence invariants without claiming a sustained campaign.
- [x] Android transport harness performs handshake before probes.

Remaining:

- [ ] independent cryptographic review.
- [ ] sustained/external coverage-guided campaign evidence for `DYSH`/`DYSE` and retained minimized findings/regressions.
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

**Status: transport-neutral wire/send/receive/retry/verified-completion and local mobile file lifecycle implemented; production transport, thumbnails and physical-device validation remain.**

Implemented:

- [x] versioned bounded attachment manifest model.
- [x] fixed 64 KiB canonical chunk geometry and validation.
- [x] resume planning through coalesced missing chunk-index ranges.
- [x] streaming SHA-256 whole-file size/integrity verification.
- [x] 100 MiB prototype transfer limit and 1600-chunk bound.
- [x] bounded display filename and simple MIME metadata validation.
- [x] remote filename explicitly treated as metadata, not a trusted local path.
- [x] versioned bounded `DYRA` manifest/chunk/resume application-frame codec for authenticated `DYSE` payloads.
- [x] structural `DYRA` chunk decoding rejects negative index/offset before manifest-scoped validation.
- [x] manifest-scoped chunk and resume validation after structural frame decoding.
- [x] versioned manifest-bound `DYAC` final completion acknowledgement.
- [x] encrypted peer-scoped durable partial receive/chunk state for restart-safe resume.
- [x] exact duplicate manifest/chunk idempotency and changed-content collision rejection.
- [x] atomic database-enforced active receive count/declared-byte quotas per peer and globally.
- [x] encrypted sender/recipient/attachment-scoped durable sender snapshot and retry state.
- [x] sender queue verifies canonical chunk order and whole-snapshot SHA-256 before atomic commit.
- [x] sender resume/progress handling persists acknowledged vs missing chunks and immediately reschedules missing ranges.
- [x] sender retains source snapshot after zero-missing progress until recipient-scoped hash-bound completion ACK.
- [x] transport-neutral attachment outbox worker with bounded send/ACK retry scheduling.
- [x] atomic database-enforced active send count/declared-byte quotas per recipient and globally.
- [x] verified reconstruction into empty caller-owned staging with exact size/chunk/SHA-256 checks.
- [x] non-public verification token keeps final completion separate from merely receiving every chunk.
- [x] bounded encrypted receiver-completed receipts survive restart and re-emit final `DYAC` on an exact manifest replay.
- [x] completed attachment-ID reuse with changed canonical manifest content fails closed.
- [x] receiver cleanup expires inactive partial receives after 14 days and completion receipts after 7 days.
- [x] completion receipts bounded to 64 per sender / 256 globally.
- [x] exact-scope explicit sender cancellation cascades deletion of its encrypted snapshot/chunks.
- [x] sender lifetime policy has no silent time-based expiry: state remains until `DYAC`, explicit cancellation or destructive reset.
- [x] provider-safe mobile picker uses stream reads, bounded inspection and a second exact snapshot/hash pass before encrypted queue commit; provider paths are not retained for retry.
- [x] mobile sender status projection exposes bounded pending/retry/final-confirmation progress for the selected contact.
- [x] explicit Retry Now reschedules the existing immutable snapshot without resetting acknowledged chunks; Cancel remains exact-scope and user-confirmed.
- [x] transport-neutral receive file coordinator enforces capacity -> verified staging -> promotion -> durable `DYAC` completion ordering.
- [x] generated app-owned Android/iOS staging/final destinations never use the remote filename as a path and recover the promotion-before-`DYAC` crash window by verifying an existing final file.
- [x] Android/iOS app-data capacity provider implemented; unknown capacity remains fail-by-write rather than pretending space is unlimited.
- [x] iOS app-owned attachment data is marked to skip cloud backup before completion is accepted.
- [x] attachment receive/send/status/maintenance/file-lifecycle services registered in shipping DI without connecting an unproven transport.
- [x] destructive identity reset clears partial/completed/sender attachment database state and app-owned staged/promoted files while retaining the resumable reset marker on file-removal failure.
- [x] repository tests cover stream snapshot integrity/change detection, sender status/retry/cancel state and receive capacity/promotion/final-ACK ordering.

Remaining:

- [ ] chunked direct-transfer integration through the proven production peer transport inside authenticated sessions.
- [ ] current Android/iOS Release CI validation for the latest platform-specific attachment lifecycle additions.
- [ ] thumbnails/previews with safe untrusted-content handling.
- [ ] physical Android/iOS picker/provider permission/lifecycle validation.
- [ ] physical Android/iOS interruption, resume, final-ACK-loss, low-disk, malicious-frame/manifest, staging/promotion, retry/cancel and reset validation.

See `docs/attachments.md` and `docs/attachment-sender-lifecycle.md`.

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
- [x] production Redis TLS/authentication/non-admin/network-isolation policy and startup enforcement.
- [x] production PostgreSQL/Redis credentials constrained to deployment secret-source settings.
- [x] privacy-aware application logs/metrics + repository retention policy.
- [x] server metadata backup/restore policy.
- [ ] production edge/network DDoS/WAF/global abuse-control deployment and validation.
- [ ] production STUN/TURN deployment decision.
- [ ] APNs/FCM integration.
- [ ] production secret-manager/access/rotation deployment validation and future TURN/push/backup secret extensions.
- [ ] production observability backend/access/retention deployment validation.
- [ ] production PostgreSQL backup/PITR/retention deployment and restore-drill validation.

See `docs/server-database-migrations.md`, `docs/server-backup-restore.md`, `docs/redis-transient-state.md`, `docs/rate-limiting.md`, `docs/observability.md` and `docs/secret-management.md`.

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
- [x] coverage-guided SharpFuzz/libFuzzer harness for `DYRM`/`DYRA`/`DYAC` plus stateful `DYSH`/`DYSE`, with deterministic seed/instruction generation and bounded CI smoke invariants.
- [ ] run/schedule external fuzz campaigns, record versions/duration, retain minimized regression corpus/findings and extend stateful coverage to the proven production transport lifecycle.
- [ ] independent cryptographic review.
- [ ] mobile secure-storage review.

See `docs/threat-model.md`, `docs/protocol-fuzzing.md`, `fuzz/Dyract.Protocol.Fuzz/README.md`, `docs/device-compromise-recovery.md` and `docs/sbom.md`.

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
5. Validate the shipping iOS UI/QR/SecureStorage/reset path on a physical iPhone and validate reset on Android.
6. If Android transport is viable, implement the production Android transport/frame sender, reconnect/session ownership and lifecycle-safe outbox/backlog scheduler.
7. Select and implement the iOS WebRTC transport adapter, then run Android -> iPhone physical tests.
8. Deploy and validate production edge/network DDoS/WAF/global abuse controls before horizontal public deployment.
9. Design and implement reviewed encrypted identity recovery/export/restore without plaintext/cloud-escrow key handling; destructive reset is complete.
10. Deploy/validate the production secret manager plus observability retention/access and PostgreSQL backup/PITR restore drills; run external coverage-guided fuzz campaigns and continue platform-native key evaluation plus independent security/cryptographic review in parallel.
11. Validate the new attachment picker/provider, free-space, staging/promotion, retry/cancel and reset lifecycle in current Android/iOS Release CI and on physical devices; implement thumbnails only after defining a safe untrusted-content decoding boundary.
