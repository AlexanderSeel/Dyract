# Protocol parser robustness and fuzzing

Dyract treats all QR/import text and all peer/network frames as untrusted input, even when the surrounding transport is encrypted or authenticated.

This document records the repository-side deterministic parser-fuzzing strategy. It is an engineering robustness suite, not a replacement for a coverage-guided native fuzzer, independent penetration test, or cryptographic review.

## Goals

The current fuzz/property tests target these invariants:

1. arbitrary malformed input must not crash a decoder;
2. oversized externally controlled input must be rejected before avoidable large decoding/deserialization allocations;
3. protocol domains must not silently cross-accept one another;
4. valid round-trips must remain valid while malformed mutations are rejected/fail closed;
5. binary frame decoders must safely handle truncation, arbitrary magic/version/length fields and oversized buffers;
6. failures must be reproducible from fixed seeds.

## Current parser boundaries

### Contact invitation QR/import

Codec:

```text
ContactInvitationCodec
```

Wire prefix:

```text
dyract://contact/v1/
```

Decoded JSON payload maximum:

```text
8192 bytes
```

The codec now derives the maximum possible Base64URL character count from that decoded ceiling and rejects a larger URI **before** Base64 decoding.

This closes an allocation-amplification path where an attacker-controlled QR/text value could previously force an unnecessarily large Base64 decode before the 8 KiB decoded-payload check ran.

Tests cover:

- valid invitation round-trip;
- oversized encoded URI rejection;
- deterministic random malformed strings;
- deterministic mutation of a valid encoded invitation;
- cross-domain rejection by the pairing codec.

### Pairing-response QR/import

Codec:

```text
ContactPairingCodec
```

Wire prefix:

```text
dyract://pair/v1/
```

Decoded JSON payload maximum:

```text
8192 bytes
```

The same pre-Base64 encoded-length bound is enforced.

Tests cover:

- oversized encoded URI rejection;
- deterministic random malformed strings;
- cross-domain rejection of contact-invitation payloads.

Valid signed capability creation/verification already has dedicated capability tests; the parser fuzz test intentionally does not duplicate cryptographic capability construction.

### Authenticated-session hello frames

Decoder boundary:

```text
AuthenticatedSessionHandshake.TryDecodeClientHello
AuthenticatedSessionHandshake.TryDecodeServerHello
```

The robustness suite sends 10,000 deterministic random binary buffers with sizes from zero through large malformed inputs. The property is that the `TryDecode...` APIs return a result without throwing for arbitrary attacker-controlled bytes.

This exercises pre-authentication binary input because a remote endpoint can send a malformed hello before application identity authentication succeeds.

### Reliable-message (`DYRM`) frames

Decoder boundary:

```text
PeerMessagingProtocol public Try*Decode* methods
```

The test source is generated from the actual public decoder signatures on `main` so the fuzz harness does not silently drift when decoder names change.

Current properties:

- 10,000 deterministic random binary frames;
- random frame sizes up to 64 KiB;
- explicit edge sizes including empty/truncated buffers and large 256 KiB buffers;
- every one-input public `Try*Decode*` boundary must return without throwing.

The normal messaging tests remain responsible for valid text/ACK semantics, identity scoping, duplicate behavior and collision rejection.

## Deterministic seeds

Current seeds are intentionally fixed so a CI failure can be reproduced exactly:

```text
0x44595241   QR malformed-input corpus     ("DYRA")
0x50415253   valid-QR mutation corpus      ("PARS")
0x53455353   session-hello binary corpus   ("SESS")
0x4459524D   DYRM binary corpus            ("DYRM")
```

Do not replace fixed seeds with wall-clock randomness in the normal CI suite. New randomly discovered failures should be minimized into a deterministic regression case or assigned a fixed additional seed.

## Why deterministic randomized tests instead of only example cases?

Example-based tests are still required for protocol semantics, but parsers tend to fail on combinations that reviewers do not think to hand-write:

- truncated headers at every byte boundary;
- invalid UTF-8/JSON/Base64 encodings;
- impossible length fields;
- arbitrary version/magic bytes;
- very large but syntactically shaped inputs;
- mutations near delimiter/prefix boundaries.

A deterministic random corpus expands the input surface while retaining reproducibility and CI stability.

## Allocation/size policy

Before parsing an externally supplied length-bearing representation, Dyract should bound the representation as early as possible.

Preferred order:

```text
raw transport/request size bound
        ↓
encoded representation size bound
        ↓
decode/base64/json/binary parse
        ↓
decoded payload semantic size bound
        ↓
cryptographic/identity validation
```

For HTTP endpoints, Kestrel/request DTO limits provide an outer boundary. QR/import codecs require their own bounds because they can also be invoked directly by the mobile app outside the server HTTP path.

## Current status

Repository-side deterministic parser robustness now covers:

- contact invitation import;
- pairing-response import;
- authenticated-session hello decoding;
- reliable-message (`DYRM`) public decoders.

This is a meaningful completed **parser robustness slice**, but it is not yet sufficient to mark the broader security-plan item `protocol fuzz/property tests` complete.

## Remaining fuzz/property work

### Authenticated encrypted session frames (`DYSE`)

Add property tests around the established session cipher/state API for:

- random ciphertext frames;
- truncated nonce/tag/header fields;
- changed protocol version/session identifier;
- sequence-number replay;
- sequence gaps/out-of-order input;
- one-bit ciphertext/tag mutations;
- wrong directional key/session;
- maximum-size and over-limit plaintext/ciphertext boundaries.

The existing adversarial session tests already cover several of these as examples; the next step is systematic randomized mutation/state-sequence coverage.

### State-machine sequences

Add generated operation sequences for:

```text
handshake -> data -> replay -> close/reconnect
message -> ACK -> duplicate message -> duplicate ACK
signal send -> fetch -> ACK -> expiry
capability valid -> revoke -> retry/new capability
```

Properties should assert that invalid transitions fail closed and cannot move durable state forward incorrectly.

### HTTP/API fuzzing

The repository still needs endpoint-level malformed JSON/body/boundary fuzzing for:

- registration;
- presence;
- capability revocation;
- resolve;
- signaling send/fetch/ACK.

The 64 KiB Kestrel body limit, JSON depth limit and semantic DTO validation are already present but should be exercised with adversarial generated requests.

### Coverage-guided tooling

Before public production use, evaluate adding a dedicated coverage-guided .NET fuzzing job/tool rather than relying only on deterministic random loops. Any such CI job must remain reproducible enough to persist/minimize newly discovered failures.

## Acceptance rule

A fuzz/property task is complete only when:

- the relevant untrusted parser/state boundary is actually invoked;
- valid semantic behavior remains covered by normal tests;
- malformed randomized input is bounded and exception-safe;
- a discovered defect is fixed in production code, not merely excluded from the corpus;
- CI executes the regression coverage successfully.

The QR pre-decode size bound is an example of this rule: the fuzz-hardening work identified a real allocation-order weakness and changed the production decoder rather than simply adding a test for the old behavior.
