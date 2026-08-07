# Dyract Android Transport Harness

This is a **physical-device diagnostic app**, not the shipping Dyract messenger.

Its purpose is to prove direct WebRTC DataChannel connectivity through Dyract's existing authenticated, capability-protected signaling layer **and** to prove the Dyract application session can bind that channel to the locally pinned peer identity before FsWebRTC is considered for production use.

## What it uses

- its own Android SecureStorage identity;
- the normal Dyract identity registration API;
- normal `dyract://contact/v1/...` identity invitations;
- normal signed `dyract://pair/v1/...` pairing responses;
- the normal Dyract short-lived signaling endpoints;
- the experimental FsWebRTC Android peer session;
- DataChannel OPEN-state gating;
- a signed ephemeral Dyract identity handshake over the opened DataChannel;
- directional HKDF-derived session keys;
- AES-256-GCM protected `DYRT` diagnostic ping/pong frames.

It does **not** use the shipping app database and it does not send chat messages.

The application-session protocol is documented separately in `docs/session-security.md`. It is implemented and adversarially tested, but is still experimental protocol code and has not yet received an independent cryptographic review.

## Build

```bash
dotnet workload install maui-android
dotnet build experiments/Dyract.Transport.AndroidHarness/Dyract.Transport.AndroidHarness.csproj --configuration Release
```

The project targets Android only and has application ID:

```text
app.dyract.transportharness
```

Successful Transport Spike CI runs also publish a 14-day artifact named:

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
3. verify the shown fingerprint out-of-band when doing a security-sensitive test.

Repeat B -> A.

The public key from this invitation is later used by the application-session handshake. A successful WebRTC connection to a different identity must therefore not be accepted as a valid Dyract session.

### 3. Exchange reachability/signaling capabilities

After each side has loaded the other's identity invitation:

On A:

1. tap **Copy pairing response for remote**;
2. transfer the resulting `dyract://pair/v1/...` value to B.

On B:

1. paste it into **Remote permission to reach it**;
2. tap **Validate remote pairing response**.

Repeat B -> A.

A pairing response is grantee-bound. Do not reuse a response generated for another Peer ID. The imported capability is stored in platform SecureStorage by the harness.

### 4. STUN setting

For same-LAN host-candidate testing, leave the STUN field empty.

For tests that require server-reflexive candidates, enter one or more test STUN URIs separated by commas, for example:

```text
stun:your-stun-host.example:3478
```

The harness intentionally accepts only `stun:` URIs. TURN is excluded from this DirectOnly experiment so relay connectivity cannot hide a failed direct path.

### 5. Establish WebRTC and the authenticated Dyract session

On B first:

1. tap **Wait as responder**;
2. leave the app in the foreground during the current spike.

On A:

1. tap **Start initiator**;
2. observe the connection stages on both devices.

The expected progression is conceptually:

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

The initiator generates the 128-bit session ID automatically. The responder discovers the first valid short-lived offer from the pinned remote Peer ID and uses that same session ID. The application handshake also binds both Peer IDs and the session ID into the signed transcript.

Do not treat a UI state equivalent to **WebRTC connected** as final success. The useful security milestone is **Authenticated session ready**.

### 6. Verify encrypted authenticated frames

After A reports that the authenticated session is ready, tap **Ping**.

Expected result:

```text
Authenticated pong received in <n> ms
```

The responder runs an automatic encrypted echo loop. Before the DataChannel sees the diagnostic payload, the `DYRT` frame is wrapped inside a `DYSE` authenticated-session frame using AES-256-GCM and the directional key derived from the signed ephemeral handshake.

The inner fixed-size diagnostic payload contains:

```text
magic      DYRT
version    1
type       ping / pong
token      16 random bytes
timestamp  Stopwatch timestamp
```

The encrypted session wrapper adds:

```text
magic              DYSE
version            1
sequence           uint64
ciphertext length  uint32
ciphertext         encrypted DYRT payload
GCM tag            16 bytes
```

No chat payload is involved.

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

For each network case distinguish these outcomes rather than recording only pass/fail:

```text
signaling failed
ICE / PeerConnection failed
PeerConnection connected but DataChannel did not open
DataChannel opened but identity handshake failed
authenticated session established but encrypted ping failed
authenticated encrypted ping succeeded
```

## Record

Record only:

- test network category;
- which connection stage was reached;
- offer-to-WebRTC-connected time;
- authenticated-session establishment result;
- authenticated ping RTT;
- whether a reconnect/restart was necessary;
- clean close/retry behavior.

Do not put raw Peer IDs, SDP, IP addresses, candidate strings, keys, invitations, capabilities, handshake packets, decrypted frames, or message content into ordinary logs/screenshots shared outside the test group.

## Current limitations

- foreground physical-device experiment only;
- Android only;
- DirectOnly / STUN only;
- no TURN;
- no mobile wake-up;
- authenticated session protocol is experimental and not independently reviewed yet;
- no Double Ratchet / post-compromise ratcheting yet;
- no asynchronous prekeys/offline E2E session bootstrap;
- no transactional chat outbox delivery over this transport yet;
- no iOS runtime adapter yet;
- current FsWebRTC Android native library has the documented Android 16 KB page-size `XA0141` production-promotion blocker.

A successful authenticated ping proves direct reachability, DataChannel transport, pinned identity authentication, ephemeral session-key agreement, and authenticated encryption for the diagnostic frame. It does **not** prove the complete messenger protocol, offline delivery, ratcheting, background wake-up, or production readiness.
