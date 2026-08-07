# Dyract Android Transport Harness

This is a **physical-device diagnostic app**, not the shipping Dyract messenger.

Its purpose is to prove direct WebRTC DataChannel connectivity through Dyract's authenticated, capability-protected signaling layer and to prove that the application session binds the channel to the locally pinned peer identity before FsWebRTC is considered for production use.

## What it proves

The harness currently exercises:

- its own Android SecureStorage identity;
- the normal Dyract identity registration API;
- `dyract://contact/v1/...` identity invitations;
- signed `dyract://pair/v1/...` reachability capabilities;
- short-lived Dyract signaling endpoints;
- experimental FsWebRTC Android ICE/WebRTC transport;
- DataChannel OPEN-state gating;
- a signed ephemeral Dyract identity handshake;
- directional HKDF-derived session keys;
- AES-256-GCM protected `DYSE` application-session frames;
- encrypted `DYRT` ping/pong probes;
- encrypted versioned `DYRM` text/delivery-ACK probes;
- privacy-safe observed ICE candidate summaries;
- privacy-safe selected ICE path reporting through WebRTC `RTCStats`.

The harness does **not** use the shipping app database. Its `DYRM` message probe is intentionally not persisted as chat history. Durable incoming-message deduplication, outbox retry and ACK state transitions are tested separately by the core/SQLite reliability tests.

The application-session protocol is documented in `docs/session-security.md`. Reliable messaging semantics are documented in `docs/reliable-messaging.md`. Both are still prototype code and require independent security/cryptographic review before production use.

## Build

```bash
dotnet workload install maui-android
dotnet build experiments/Dyract.Transport.AndroidHarness/Dyract.Transport.AndroidHarness.csproj --configuration Release
```

Application ID:

```text
app.dyract.transportharness
```

Successful Transport Spike CI runs publish a 14-day artifact named:

```text
dyract-android-transport-harness-apk
```

## Two-device setup

Use two physical Android devices, A and B.

### 1. Configure the directory

On both devices:

1. enter the same HTTPS Dyract directory origin;
2. tap **Configure + Register**;
3. verify each device reports a different Peer ID.

### 2. Exchange identity invitations

On A:

1. tap **Copy my contact invitation**;
2. transfer the resulting `dyract://contact/v1/...` value to B.

On B:

1. paste it into **Remote identity**;
2. tap **Load remote invitation**;
3. verify the shown fingerprint out-of-band for security-sensitive tests.

Repeat B -> A.

The invitation public key is pinned and later used by the application-session handshake. A successful WebRTC connection to a different identity must therefore not be accepted as a valid Dyract session.

### 3. Exchange signaling capabilities

After each side has loaded the other's identity invitation:

On A:

1. tap **Copy pairing response for remote**;
2. transfer the resulting `dyract://pair/v1/...` value to B.

On B:

1. paste it into **Remote permission to reach it**;
2. tap **Validate remote pairing response**.

Repeat B -> A.

A pairing response is bound to one exact grantee Peer ID. Do not reuse a response generated for another identity. Imported capability material is stored in platform SecureStorage by the harness.

## ICE / STUN and privacy-safe diagnostics

For same-LAN host-candidate testing, leave the STUN field empty.

For tests that require server-reflexive candidates, enter one or more STUN URIs separated by commas, for example:

```text
stun:your-stun-host.example:3478
```

The harness intentionally accepts only `stun:` URIs. TURN is excluded from this DirectOnly experiment so relay connectivity cannot hide a failed direct path.

The UI separates candidates merely **observed during negotiation** from the pair WebRTC actually selected. Example:

```text
Local candidates: host/udp, srflx/udp
Remote candidates: host/udp, srflx/udp
Selected path: host/udp -> srflx/udp
```

Observed candidate parsing discards address, port, foundation, priority, related address/port and other SDP material. Unknown future tokens become `unknown/unknown` instead of being echoed.

Selected-path reporting uses the WebRTC stats chain:

```text
transport.selectedCandidatePairId
        -> candidate-pair
        -> localCandidateId / remoteCandidateId
        -> candidateType / protocol
```

Only the final category/transport pair leaves the native binding layer. Raw stats IDs, IP addresses, ports and candidate objects are not exposed to the harness UI or ordinary log. If that exact chain cannot be resolved, the UI reports:

```text
Selected path: unavailable
```

The stats callback is retained until WebRTC actually delivers it. A UI timeout cancels only the caller's wait and does not prematurely dispose the native callback object.

## Establish WebRTC and the authenticated Dyract session

On B first:

1. tap **Wait as responder**;
2. leave the app in the foreground during the current spike.

On A:

1. tap **Start initiator**;
2. observe the connection stages on both devices.

Expected progression:

```text
WebRTC signaling / ICE
        ↓
PeerConnection Connected
        ↓
DataChannel OPEN
        ↓
Signed ephemeral DYSH handshake
        ↓
Pinned remote PeerId/public-key verification
        ↓
HKDF directional session keys
        ↓
Authenticated session ready
```

The initiator generates the session ID automatically. The responder accepts only a valid short-lived offer from the pinned remote Peer ID. The application handshake binds both Peer IDs and the session ID into the signed transcript.

Do not treat **WebRTC connected** as final success. The useful security milestone is **Authenticated session ready**.

## Protocol probes

### Ping

After A reports that the authenticated session is ready, tap **Ping**.

Expected result:

```text
Authenticated pong received in <n> ms
```

The inner `DYRT` frame is encrypted inside a `DYSE` application-session frame. Record the RTT, not packet contents.

### Message + ACK

On the initiator, tap **Message + ACK**.

Expected result:

```text
Authenticated DYRM delivery ACK received in <n> ms
```

This sends one real versioned `DYRM` text frame through the authenticated encrypted DataChannel. The responder validates peer scope and returns the corresponding delivery ACK. The probe text is not persisted as chat history.

This validates the application wire path only. The full durable reliability loop is tested separately:

```text
queue locally
  -> send original MessageId/CreatedAt
  -> ACK may be lost
  -> retry same message
  -> receiver deduplicates
  -> ACK again
  -> sender removes outbox only after valid peer ACK
```

## Runtime matrix

Run at least:

| A | B | Goal |
|---|---|---|
| same Wi-Fi | same Wi-Fi | prove host/direct authenticated DataChannel |
| Wi-Fi | different Wi-Fi | observe NAT/STUN behavior |
| Wi-Fi | cellular | observe direct vs CGNAT failure |
| cellular | cellular | characterize CGNAT/direct failure |
| IPv6 network | IPv6 network | observe IPv6 direct behavior |

After a successful session, also test Wi-Fi -> cellular and cellular -> Wi-Fi transitions.

For every case distinguish these outcomes rather than recording only pass/fail:

```text
signaling failed
ICE / PeerConnection failed
PeerConnection connected but DataChannel did not open
DataChannel opened but identity handshake failed
authenticated session established but DYRT ping failed
authenticated DYRT ping succeeded
DYRM message sent but delivery ACK failed
authenticated DYRM delivery ACK succeeded
```

## Record

Record only:

- network category for A and B;
- highest connection stage reached;
- observed local candidate categories;
- observed remote candidate categories;
- selected ICE path category, e.g. `host/udp -> srflx/udp`;
- whether selected path was unavailable;
- offer-to-WebRTC-connected time;
- authenticated-session establishment result;
- authenticated `DYRT` ping RTT;
- authenticated `DYRM` delivery-ACK RTT;
- reconnect/restart requirement;
- clean close/retry behavior.

Do **not** put raw Peer IDs, SDP, IP addresses, ports, candidate strings, stats IDs, keys, invitations, capabilities, handshake packets, decrypted frames, MessageIds or message content into ordinary logs/screenshots shared outside the test group.

## Current limitations

- foreground physical-device experiment only;
- Android only;
- DirectOnly / STUN only;
- no TURN;
- no mobile wake-up;
- authenticated session protocol is experimental and not independently reviewed yet;
- no Double Ratchet / post-compromise ratcheting yet;
- no asynchronous prekeys/offline E2E session bootstrap;
- shipping app outbox is not yet scheduled over this experimental transport;
- no iOS runtime adapter yet;
- current FsWebRTC Android native library has the documented Android 16 KB page-size `XA0141` production-promotion blocker.

A successful `DYRT` + `DYRM` physical test proves direct reachability, DataChannel transport, pinned identity authentication, ephemeral session-key agreement, authenticated encryption, the application message/ACK wire path and the privacy-safe selected ICE path diagnostic. It does **not** prove background delivery, TURN behavior, iOS interoperability, post-compromise ratcheting or production readiness.
