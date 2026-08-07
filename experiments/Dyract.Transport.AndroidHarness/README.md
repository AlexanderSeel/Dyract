# Dyract Android Transport Harness

This is a **physical-device diagnostic app**, not the shipping Dyract messenger.

Its purpose is to prove direct WebRTC DataChannel connectivity through Dyract's existing authenticated, capability-protected signaling layer before FsWebRTC is considered for production use.

## What it uses

- its own Android SecureStorage identity;
- the normal Dyract identity registration API;
- normal `dyract://contact/v1/...` identity invitations;
- normal signed `dyract://pair/v1/...` pairing responses;
- the normal Dyract short-lived signaling endpoints;
- the experimental FsWebRTC Android peer session;
- binary `DYRT` diagnostic ping/pong frames.

It does **not** use the shipping app database and it does not send chat messages.

## Build

```bash
dotnet workload install maui-android
dotnet build experiments/Dyract.Transport.AndroidHarness/Dyract.Transport.AndroidHarness.csproj --configuration Release
```

The project targets Android only and has application ID:

```text
app.dyract.transportharness
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

### 3. Exchange reachability/signaling capabilities

After each side has loaded the other's identity invitation:

On A:

1. tap **Copy pairing response for remote**;
2. transfer the resulting `dyract://pair/v1/...` value to B.

On B:

1. paste it into **Remote permission to reach it**;
2. tap **Validate remote pairing response**.

Repeat B -> A.

A pairing response is grantee-bound. Do not reuse a response generated for another Peer ID.

### 4. STUN setting

For same-LAN host-candidate testing, leave the STUN field empty.

For tests that require server-reflexive candidates, enter one or more test STUN URIs separated by commas, for example:

```text
stun:your-stun-host.example:3478
```

The harness intentionally accepts only `stun:` URIs. TURN is excluded from this DirectOnly experiment so relay connectivity cannot hide a failed direct path.

### 5. Establish the session

On B first:

1. tap **Wait as responder**;
2. leave the app in the foreground during the current spike.

On A:

1. tap **Start initiator**;
2. wait for both devices to show `Connected`.

The initiator generates the 128-bit session ID automatically. The responder discovers the first valid short-lived offer from the pinned remote Peer ID and uses that session ID.

### 6. Verify the binary DataChannel

On A tap **Ping**.

Expected result:

```text
Pong received in <n> ms
```

The responder runs an automatic binary echo loop. The diagnostic frame is fixed-size and contains:

```text
magic      DYRT
version    1
type       ping / pong
token      16 random bytes
timestamp  Stopwatch timestamp
```

No chat payload is involved.

## Runtime matrix

Run at least:

| A | B | Goal |
|---|---|---|
| same Wi-Fi | same Wi-Fi | prove host/direct DataChannel |
| Wi-Fi | different Wi-Fi | observe NAT/STUN behavior |
| Wi-Fi | cellular | observe direct vs CGNAT failure |
| cellular | cellular | characterize CGNAT/direct failure |
| IPv6 network | IPv6 network | observe IPv6 direct behavior |

After a successful session, also test Wi-Fi -> cellular and cellular -> Wi-Fi transitions.

## Record

Record only:

- test network category;
- success/failure;
- offer-to-connected time;
- ping RTT;
- whether a reconnect/restart was necessary;
- clean close/retry behavior.

Do not put raw Peer IDs, SDP, IP addresses, candidate strings, keys, invitations, capabilities, or message content into ordinary logs/screenshots shared outside the test group.

## Current limitations

- foreground physical-device experiment only;
- Android only;
- DirectOnly / STUN only;
- no TURN;
- no mobile wake-up;
- no application-level authenticated/forward-secret Dyract session protocol yet;
- no transactional chat outbox delivery yet;
- no iOS runtime adapter yet.

A successful ping proves WebRTC reachability and DataChannel transport only.
