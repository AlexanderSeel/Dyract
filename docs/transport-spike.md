# Dyract transport spike

## Status

**Decision state: experimental — no production transport library selected yet.**

Dyract now has:

- capability-protected short-lived peer reachability,
- short-lived authenticated signaling for offer/answer/candidate exchange,
- a replaceable `IPeerTransport` / `IPeerConnection` abstraction,
- `DirectOnly` vs `AllowRelay` policy,
- encrypted local messages and transactional outbox.

The next milestone is to prove a real data-only peer channel on physical Android and iPhone devices. Library choice must follow the physical-device evidence rather than drive the architecture.

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

Dyract's directory now carries transport negotiation as short-lived opaque signals:

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

Fetch does not delete a signal. The recipient ACKs only after the transport adapter has accepted/processed it. Expired negotiation items are purged automatically.

This is intentionally **not a message mailbox**. Chat messages continue to live in the local encrypted outbox until a peer transport exists.

## Proposed first experiment

### Step 1 — Android native DataChannel proof

Create an experimental implementation behind:

```text
IPeerTransport
IPeerConnection
```

Do not wire the transactional message outbox into it yet.

Proof target:

```text
Android A
  -> create offer
  -> Dyract signaling
Android B
  -> receive offer
  -> create answer
  -> Dyract signaling
A/B
  -> trickle ICE candidates
  -> DataChannel opens
  -> exchange fixed diagnostic byte frames
```

### Step 2 — publish real candidates

Map gathered host/server-reflexive/relay candidates into the existing signed presence lease so a paired peer can query current reachability before starting a session.

If the chosen WebRTC API handles all ICE candidates exclusively inside offer/trickle negotiation, adapt the directory representation rather than forcing the library into the current bootstrap candidate shape.

### Step 3 — Android network matrix

At minimum:

| A | B | Expected evidence |
|---|---|---|
| same Wi-Fi | same Wi-Fi | direct host path |
| Wi-Fi A | Wi-Fi B | STUN/direct where NAT permits |
| Wi-Fi | cellular | direct vs relay result |
| cellular | cellular | CGNAT behavior |
| IPv6 capable | IPv6 capable | IPv6 candidate result |

Record only connection outcome/category by default. Do not log Peer IDs, SDP, IP addresses or ICE candidates in ordinary telemetry.

### Step 4 — iPhone

Repeat with iOS and then Android ↔ iPhone.

An iOS project compiling is not sufficient evidence; the test must run on a physical iPhone because background/network behavior is part of the risk.

### Step 5 — TURN policy

Compare:

```text
DirectOnly
AllowRelay
```

`DirectOnly` must fail cleanly when only relay connectivity is possible. `AllowRelay` may use TURN but must clearly remain end-to-end encrypted at the Dyract application-session layer.

## Not part of the transport spike

Do not conflate the following with successful WebRTC connectivity:

- Dyract peer identity authentication,
- forward-secret application session keys,
- message ACK semantics,
- retry scheduling,
- read receipts,
- mobile wake-up.

A DataChannel becoming `open` proves reachability, not that the Dyract security/session protocol is complete.

## Exit criteria

The spike is complete only when:

1. two physical Android devices establish a data channel through Dyract signaling;
2. host/STUN candidate behavior is recorded across multiple network combinations;
3. relay behavior is tested separately;
4. Android ↔ iPhone succeeds or a documented blocker is identified;
5. lifecycle/disposal/network-change behavior is understood;
6. the chosen library's license is acceptable;
7. the implementation remains behind `IPeerTransport` with no message/business logic coupled to the library API.
