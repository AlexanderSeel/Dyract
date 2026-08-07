# Dyract transport spike

## Status

**Decision state: experimental — Android compile proof complete; physical-device runtime proof still required.**

Dyract now has:

- capability-protected short-lived peer reachability,
- short-lived authenticated signaling for offer/answer/candidate exchange,
- typed and versioned transport-negotiation payloads,
- a replaceable `IPeerTransport` / `IPeerConnection` abstraction,
- a production-safe `IPeerSignalingGateway` abstraction,
- an Android FsWebRTC peer-session experiment,
- an Android negotiation coordinator that bridges WebRTC SDP/ICE to Dyract signaling,
- a directory-driven diagnostic harness for initiator/responder testing,
- `DirectOnly` vs `AllowRelay` policy,
- encrypted local messages and transactional outbox.

The FsWebRTC package remains isolated under `experiments/`; the shipping MAUI application references only Dyract transport contracts. The next milestone is a real data-only peer channel on physical devices. Library promotion must follow physical-device evidence rather than compile success alone.

## Current Android compile proof

The Android experiment is compile-proven against `FsWebRTC.Bindings.Maui.Android 0.9.3.15` on .NET 10 for:

- `PeerConnectionFactory` initialization,
- STUN `IceServer` and `RTCConfiguration`,
- `PeerConnection` creation,
- offer/answer creation,
- local/remote SDP application,
- local and remote ICE candidates,
- `PeerConnection.IObserver`, `ISdpObserver` and `DataChannel.IObserver`,
- outgoing and incoming DataChannels,
- binary DataChannel send/receive,
- bounded async receive queues,
- connection/ICE/gathering state callbacks,
- directory-driven offer/answer/trickle-ICE exchange,
- remote ICE buffering until remote SDP is installed,
- close/disposal flow for the native binding surface.

Binding-specific findings are intentionally kept in the experiment rather than leaked into Dyract's shared transport API. In particular, generated Java binding shapes are treated as package-version-specific implementation details.

## Transport requirements

A candidate must support the following without weakening Dyract's existing security boundaries:

1. Android and iOS from .NET MAUI / .NET 10.
2. WebRTC-style ICE candidate gathering.
3. STUN server-reflexive candidates.
4. TURN relay candidates when enabled.
5. DataChannel support; audio/video is not required for the messaging MVP.
6. Trickle ICE or an equivalent incremental candidate flow.
7. Offer/answer exchange that can be carried as opaque Dyract signaling payloads.
8. Inbound and outbound connection establishment.
9. Network-change behavior that can be observed/restarted by Dyract.
10. A license suitable for unrestricted distribution of a general-purpose messenger.
11. No requirement to send Dyract message plaintext or long-term private keys into third-party infrastructure.

## Candidate A — FsWebRTC native MAUI bindings

Current packages evaluated for the spike:

```text
FsWebRTC.Bindings.Maui.Android  0.9.3.15
FsWebRTC.Bindings.Maui.iOS      current 0.9.3.x line
```

### Strengths

- MIT licensed.
- Current packages target .NET 10 mobile TFMs.
- Android package binds a native `libwebrtc.aar`.
- iOS package binds native WebRTC framework artifacts.
- Native libwebrtc behavior is close to the mobile WebRTC implementation family used broadly in production applications.
- Good conceptual fit for ICE + DataChannel without requiring Dyract to use browser UI/media APIs.

### Risks

- Young bindings with comparatively small adoption/download numbers.
- Platform APIs differ between Android and iOS, so Dyract will need a shared adapter behind `IPeerTransport`.
- Native lifecycle/resource disposal must be tested carefully.
- Android and iOS need independent physical-device validation.
- Upgrade compatibility needs explicit testing because generated/native binding surfaces can change.

### Spike position

**Preferred first physical-device experiment**, not yet a production dependency.

## Candidate B — SIPSorcery

Current evaluated line:

```text
SIPSorcery 10.0.12
```

### Strengths

- Mature .NET real-time communications project.
- WebRTC, ICE, STUN, SDP and data-channel related functionality in a mostly managed .NET stack.
- Broad adoption compared with the newer MAUI binding packages.
- Useful reference implementation for protocol behavior even if it is not selected for the mobile app.

### Blocking concern for Dyract

The current package license is presented as BSD 3-Clause with an additional usage prohibition. Because Dyract is intended as a generally distributable messenger, legal/license suitability must be explicitly approved before adding the package as a shipping dependency.

Therefore SIPSorcery must **not** be silently adopted just because its managed API is convenient.

### Spike position

Useful comparison/reference candidate. Do not add as a production dependency without license review.

## Signaling contract already available

Dyract's directory carries transport negotiation as short-lived opaque signals:

```text
POST /api/v1/signal/send
POST /api/v1/signal/fetch
POST /api/v1/signal/ack
```

Supported logical signal types:

```text
offer
answer
candidate
end-of-candidates
close
```

Security/bounds:

```text
send            target capability + sender signature required
fetch           target identity signature required
ack             target identity signature required
signal TTL      <= 60 seconds
payload         <= 32 KiB UTF-8
pending inbox   <= 64 per target
fetch batch     <= 20
```

The transport layer adds local validation before native WebRTC sees a signal: 128-bit hex signal/session IDs, valid sender Peer ID, timestamp ordering, maximum 60-second envelope lifetime, supported payload version and type-specific payload checks.

Fetch does not delete a signal. A matching signal is ACKed only after it has been decoded and accepted by the negotiation coordinator. Malformed matching signals are ACKed as poison items so they cannot block the short-lived inbox indefinitely.

This is intentionally **not a message mailbox**. Chat messages continue to live in the local encrypted outbox until a peer transport exists.

## Implemented diagnostic flow

The isolated Android experiment can now represent this flow in code:

```text
Android A
  -> create DataChannel
  -> create + apply local offer
  -> typed Dyract offer signal
  -> authenticated directory signaling

Android B
  -> fetch + validate offer
  -> apply remote offer
  -> create + apply local answer
  -> typed Dyract answer signal

A / B
  -> trickle typed ICE candidate signals
  -> queue remote candidates until remote SDP exists
  -> apply candidates
  -> emit end-of-candidates when gathering completes
  -> wait for connected state
  -> exchange binary diagnostic frames
```

The directory harness polls at one-second intervals by default. This is diagnostic behavior only; wake-up/background delivery and production scheduling are separate concerns.

## Physical-device runtime matrix

Compile success is not an exit criterion. At minimum record the following on physical devices:

| A | B | Expected evidence |
|---|---|---|
| Android Wi-Fi | Android same Wi-Fi | direct host path, DataChannel round-trip |
| Android Wi-Fi | Android different Wi-Fi | STUN/direct where NAT permits |
| Android Wi-Fi | Android cellular | direct/CGNAT result |
| Android cellular | Android cellular | CGNAT behavior and clean DirectOnly failure where necessary |
| Android IPv6 | Android IPv6 | IPv6 candidate/path behavior |
| Android | iPhone | cross-platform offer/answer/ICE/DataChannel after iOS binding spike |

For every run record only diagnostic categories by default:

- offer-to-connected duration,
- candidate type used (`host`, `srflx`, `relay`),
- DataChannel binary frame round-trip success,
- reconnect/restart result after Wi-Fi/cellular transition,
- clean timeout/failure behavior,
- resource cleanup after close/retry.

Do not log Peer IDs, SDP text, IP addresses, ICE candidate bodies, message content or long-term keys in ordinary telemetry.

## Next runtime experiments

### 1. Android physical-device host

Create a small experimental Android host that composes:

```text
FsWebRtcAndroidPeerSession
FsWebRtcNegotiationCoordinator
FsWebRtcDirectoryHarness
IPeerSignalingGateway
```

It must remain under `experiments/` and must not move the FsWebRTC package into `Dyract.App` yet.

The host should expose only diagnostic actions/status:

```text
identity / peer id
configured directory
paired target peer
initiator / responder
session id
connection state
candidate category summary
send diagnostic frame
last frame round-trip latency
close / retry
```

### 2. Android network transitions

After a successful connection, switch Wi-Fi/cellular and observe whether the session survives, fails, or requires a controlled ICE/session restart. Do not hide failures with an automatic TURN fallback while validating `DirectOnly`.

### 3. iOS binding spike

Repeat compile/API discovery for the iOS FsWebRTC binding, then run Android ↔ iPhone on physical devices. An iOS compile alone is not sufficient evidence because lifecycle/background/network behavior is part of the risk.

### 4. TURN policy

Compare:

```text
DirectOnly
AllowRelay
```

`DirectOnly` must fail cleanly when only relay connectivity is possible. `AllowRelay` may use TURN but must clearly remain end-to-end encrypted at the Dyract application-session layer.

## Not part of the transport spike

Do not conflate successful WebRTC connectivity with:

- Dyract peer identity authentication at the application-session layer,
- forward-secret application session keys,
- message ACK semantics,
- retry scheduling,
- read receipts,
- transactional outbox delivery,
- mobile wake-up/background scheduling.

A DataChannel becoming open proves reachability, not that the Dyract security/session protocol is complete.

## Exit criteria

The spike is complete only when:

1. two physical Android devices establish a DataChannel through Dyract signaling;
2. binary diagnostic frames round-trip successfully;
3. host/STUN candidate behavior is recorded across the Android network matrix;
4. clean timeout/failure behavior is verified where direct connectivity is impossible;
5. Wi-Fi/cellular network-change behavior is understood;
6. relay behavior is tested separately when `AllowRelay` is enabled;
7. Android ↔ iPhone succeeds or a documented blocker is identified;
8. native lifecycle/disposal behavior survives repeated connect/close/retry cycles;
9. the chosen library's license is acceptable;
10. the implementation remains behind Dyract transport abstractions with no message/business logic coupled to the native library API.
