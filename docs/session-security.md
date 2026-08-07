# Dyract authenticated session security

## Status

**Implemented as protocol code and covered by automated adversarial tests. Not yet externally reviewed or approved as the final production cryptographic protocol.**

WebRTC already encrypts a DataChannel in transit, but Dyract must not treat transport encryption alone as proof that the remote endpoint is the locally pinned Dyract identity. The application session therefore adds an identity-authenticated ephemeral key exchange above the DataChannel and encrypts application frames again with session-specific directional keys.

The current implementation lives in:

```text
src/Dyract.Crypto/Session/
  AuthenticatedSessionHandshake.cs
  AuthenticatedSessionKeys.cs
  AuthenticatedSessionCipher.cs

experiments/Dyract.Transport.FsWebRtcProbe/
  AuthenticatedExperimentalDataChannel.cs
  AuthenticatedDiagnosticSession.cs
```

The shared crypto code has no WebRTC dependency.

## Security goals

The current session layer is designed to provide:

- mutual binding to the locally pinned long-term Peer IDs/public identity keys;
- fresh ephemeral key agreement for every connection;
- forward secrecy with respect to later compromise of a long-term identity signing key, assuming ephemeral ECDH private keys were destroyed;
- initiator/responder and session-ID binding;
- transcript binding between hello and response;
- independent send and receive keys;
- authenticated application-frame confidentiality/integrity;
- strict replay/out-of-order rejection for the reliable ordered messaging channel;
- explicit protocol versioning and bounded packet/frame sizes;
- zeroing/disposal of derived key material where managed/runtime APIs permit it.

It is deliberately independent of TURN. If a future `AllowRelay` mode routes WebRTC through TURN, the relay still receives only WebRTC-encrypted traffic and Dyract application ciphertext.

## Long-term identity

Each installation has an existing P-256 ECDSA identity:

```text
PeerId = dyr_ + Base32(SHA256(identity public key))
```

Contacts pin the remote identity public key locally. Before a session starts, Dyract verifies that the supplied pinned public key derives exactly to the expected Peer ID.

The directory is therefore not allowed to silently substitute a different identity key for a contact.

## Handshake v1

The initiator generates for every connection:

```text
session id             128 random bits from the transport negotiation
initiator nonce        256 random bits
initiator ephemeral    NIST P-256 ECDH key pair
```

It sends a binary `DYSH` hello containing:

```text
magic / version / hello type
session id
initiator PeerId
responder PeerId
initiator nonce
initiator ephemeral public key (SPKI)
identity signature
```

The identity signature is ECDSA P-256 / SHA-256 in IEEE P1363 fixed-field format over the complete canonical unsigned hello.

The responder verifies:

1. packet structure/version/bounds;
2. expected session ID;
3. sender = pinned remote Peer ID;
4. receiver = local Peer ID;
5. pinned identity public key still derives to the expected Peer ID;
6. initiator identity signature.

The responder then generates its own 256-bit nonce and fresh P-256 ECDH key pair. Its signed response contains the same identity/session scope plus:

```text
SHA256(full initiator hello including signature)
```

The initiator verifies the responder identity signature and requires that hello hash to match its exact original hello. A response produced for another ephemeral hello cannot complete a different initiator state even if the Peer IDs and session ID were reused accidentally.

## Key derivation

Both peers import and validate the remote ephemeral public key as NIST P-256, then compute the raw ECDH shared secret.

The raw secret is **not** used directly as an encryption key.

The current derivation is conceptually:

```text
salt = SHA256(initiatorNonce || responderNonce)

transcriptHash = SHA256(
    fullSignedHello ||
    fullSignedResponse
)

info = ASCII("Dyract/session-keys/v1") || transcriptHash

keyMaterial = HKDF-SHA256(
    ikm  = rawECDHSharedSecret,
    salt = salt,
    info = info,
    len  = 64 bytes
)
```

Directional assignment:

```text
first  32 bytes = initiator -> responder
second 32 bytes = responder -> initiator
```

The responder assigns the same material in the opposite send/receive orientation.

Raw ECDH material, intermediate HKDF output, nonces held by handshake state, and exported session keys are cleared/disposed as soon as practical.

## Authenticated application frames

After handshake completion, plaintext Dyract application payloads are wrapped in `DYSE` frames.

Current frame layout:

```text
magic              4 bytes   "DYSE"
version            1 byte    1
sequence           8 bytes   unsigned big-endian
ciphertext length  4 bytes   unsigned big-endian
ciphertext         N bytes
GCM tag            16 bytes
```

Encryption:

```text
AES-256-GCM
```

Nonce construction for a directional key:

```text
nonce[0..4]  = transcriptHash[0..4]
nonce[4..12] = sequence as unsigned big-endian
```

Because initiator->responder and responder->initiator use different AES keys, the same sequence number may safely exist in both directions without nonce/key reuse.

Associated data is:

```text
frame header || full 32-byte transcript hash
```

This binds a valid frame to:

- frame version;
- sequence;
- declared ciphertext length;
- the exact authenticated session transcript.

## Sequence/replay policy

The initial send and receive sequence is zero.

For the current reliable ordered DataChannel profile:

```text
received sequence must equal expected sequence exactly
```

Therefore:

- replayed frames are rejected;
- skipped/out-of-order frames are rejected;
- a failed authentication does not advance the receive sequence;
- a valid later frame cannot hide a missing earlier frame.

If Dyract later introduces unordered/unreliable channels, they must use a different replay-window design rather than weakening this invariant.

## Size boundary

The experimental raw DataChannel frame limit is 256 KiB.

The authenticated-session plaintext maximum subtracts the `DYSE` header and GCM tag so a maximum encrypted frame still fits inside the raw transport limit. Large attachments must use a separate chunked transfer protocol rather than oversized session frames.

## Automated negative tests

Current tests cover at least:

- initiator/responder derive opposite directional keys;
- transcript hashes match on both peers;
- wrong pinned identity key / Peer ID binding rejected;
- tampered initiator identity signature rejected;
- tampered responder identity signature rejected;
- response from a different ephemeral hello rejected;
- wrong expected session ID rejected;
- bidirectional AES-GCM frame round-trip;
- replay rejected;
- out-of-order frame rejected without advancing receive state;
- ciphertext/tag tampering rejected;
- frame from a different authenticated session rejected;
- empty/oversized plaintext rejected.

These tests catch implementation regressions; they are not a substitute for cryptographic review.

## What this does not solve yet

The current handshake/frame layer is intentionally smaller than a Signal-style protocol. It does **not** yet provide:

- Double Ratchet message-key evolution;
- post-compromise security/recovery during a long-lived session;
- asynchronous prekeys for initiating E2E sessions while the recipient is offline;
- multi-device identity/device certificates;
- deniability properties;
- formal protocol verification;
- independent third-party cryptographic audit.

For the first direct messenger MVP, the design uses a fresh ephemeral authenticated session whenever peers reconnect. Before production release, Dyract should decide whether this reconnect-level forward secrecy is sufficient or whether the messaging layer should adopt a reviewed Noise/Double-Ratchet style construction.

## Production promotion requirements

Do not label the application protocol as security-reviewed merely because unit tests and device tests pass.

Before final production promotion:

1. freeze/version the handshake and frame wire formats;
2. obtain dedicated cryptographic/security review;
3. add fuzz/property tests for handshake/frame decoders;
4. verify behavior across reconnect/retry/session collision scenarios;
5. decide the ratcheting/post-compromise-security requirement;
6. ensure logging/crash reporting never captures handshake packets, identity private keys, derived session keys, plaintext, or decrypted application frames;
7. verify all supported Android/iOS runtime cryptography implementations behave consistently.
