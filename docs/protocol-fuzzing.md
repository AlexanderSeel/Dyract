# Protocol parser robustness and fuzzing

Dyract treats QR/import text, HTTP request bodies and peer/network frames as untrusted input, even when the surrounding transport is encrypted or authenticated.

This document records the repository-side **deterministic** fuzz/property strategy. It is an engineering regression suite, not a replacement for coverage-guided fuzzing, an independent penetration test, or cryptographic review.

## Acceptance invariants

Repository fuzz/property coverage targets these rules:

1. arbitrary malformed input must fail closed without unexpected runtime exceptions;
2. externally controlled sizes must be bounded before avoidable large decoding/deserialization allocations;
3. protocol domains must not silently cross-accept one another;
4. accepted wire encodings must be canonical;
5. authentication failure must not advance receive/session state;
6. replay/downgrade/cross-session inputs must not become valid through parser/state-machine side effects;
7. deterministic seeds/corpora must make failures reproducible in CI.

## QR/import boundaries

### Contact invitation

```text
ContactInvitationCodec
dyract://contact/v1/...
```

Decoded JSON payload ceiling:

```text
8192 bytes
```

The codec rejects an oversized encoded URI before Base64URL decoding. Tests cover valid round-trip, oversized input, deterministic malformed strings, mutation of valid values and pairing-domain rejection.

### Pairing response

```text
ContactPairingCodec
dyract://pair/v1/...
```

The same pre-decode bound applies. Structurally valid QR data still goes through the normal cryptographic capability verification path; scanning is never an authorization bypass.

## Authenticated session handshake (`DYSH`)

The public pre-authentication boundary exercised by tests is the real responder API:

```text
AuthenticatedSessionResponder.Accept(...)
```

`ProtocolParserRobustnessTests` sends 10,000 deterministic random binary buffers into that boundary. Only expected format/authentication rejection is accepted; an unexpected exception or accidental authentication fails the test.

`ProtocolFuzzPropertyTests` adds signed-frame mutation properties:

- sampled single-byte/bit mutations across a valid client hello cannot authenticate;
- protocol-version downgrade mutations are rejected;
- a response generated for one SessionId cannot complete an initiator for another SessionId;
- role/identity/session binding remains enforced by normal adversarial handshake tests.

## Reliable-message frames (`DYRM`)

Boundary:

```text
PeerMessagingProtocol.TryDecode(...)
```

Repository properties now include:

- deterministic malformed/boundary binary corpora;
- hundreds of generated valid text/ACK frames with Unicode payloads;
- exact encode -> decode -> encode canonical equality for valid frames;
- one-bit mutation across every byte of a representative valid frame;
- a mutated frame must either reject with a bounded protocol error or decode to the exact canonical byte representation it already contains;
- normal reliable-messaging tests continue to cover sender/recipient scope, duplicate idempotency, ACK authorization and collision rejection.

## Authenticated encrypted application frames (`DYSE`)

`ProtocolFuzzPropertyTests` exercises the established `AuthenticatedSessionCipher` state API rather than a mock decoder.

Current properties:

- 64 sequential encrypted frames are mutated at deterministic positions;
- each authentication/format failure must leave the receive sequence unchanged so the original frame can still be accepted immediately afterward;
- truncated frames reject without consuming receive state;
- extended/trailing-byte frames reject without consuming receive state;
- a frame from Session A is rejected by Session B;
- the same frame is accepted by the correct session once and then rejected as replay.

These properties extend the existing example tests for header mutation, wrong identity/session, replay, out-of-order data and tampered ciphertext/tag.

## HTTP/API robustness

`DirectoryApiRobustnessTests` exercises every current `/api/v1` POST boundary with deterministic malformed JSON/binary bodies.

Covered endpoints include:

```text
identity/challenge
identity/register
peer/lookup
presence
presence/remove
capability/revoke
peer/resolve
signal/send
signal/fetch
signal/ack
```

Properties:

- malformed bodies must not produce an HTTP 5xx response;
- JSON-like malformed bodies exercise deeper model binding as well as immediate parse rejection;
- bodies above the 64 KiB outer ceiling are rejected with HTTP 413.

`DirectoryAbuseIntegrationTests` adds semantic abuse cases rather than random syntax only:

- an unregistered requester cannot lookup a known registered target;
- missing reachability capability does not leak a published candidate address/port;
- a capability issued for one grantee cannot be copied to another registered peer;
- an oversized signaling payload is rejected and never reaches the target inbox;
- request admission limits are exercised at the server boundary.

## Deterministic seeds

Normal CI uses fixed seeds rather than wall-clock randomness. Existing corpora include values such as:

```text
0x44595241   QR malformed-input corpus      ("DYRA")
0x50415253   valid-QR mutation corpus       ("PARS")
0x53455353   session binary corpus          ("SESS")
0x4459524D   DYRM parser corpus             ("DYRM")
0x0000D1A7   generated DYRM property corpus
0x00051A17   garbage DYRM property corpus
0x41504946   HTTP API malformed corpus      ("APIF")
```

Do not replace these with nondeterministic seeds in the required CI suite. A randomly discovered issue should be minimized into a deterministic regression input/seed.

## Size/allocation policy

Preferred validation order for untrusted data:

```text
raw transport/request size bound
        ↓
encoded representation size bound
        ↓
decode / JSON / Base64 / binary parse
        ↓
decoded semantic size bound
        ↓
identity / signature / authenticated-state validation
```

This matters because a parser that eventually rejects a 100 MiB attacker input can still be an availability vulnerability if it allocates/decodes the entire representation first.

## Current repository status

The deterministic repository fuzz/property PLAN item is considered implemented because current CI exercises:

- QR/import malformed input and pre-decode bounds;
- pre-authentication handshake garbage/mutations;
- handshake downgrade and cross-session binding;
- `DYRM` generated canonical round-trips and mutation corpus;
- `DYSE` authenticated mutation/replay/cross-session state properties;
- malformed HTTP/API request boundaries;
- semantic directory enumeration/capability-abuse regression cases.

This status means **repository deterministic regression coverage is implemented**. It does not mean Dyract has undergone independent fuzzing or security review.

## Still open before production

### Coverage-guided fuzzing

Evaluate a maintained coverage-guided .NET fuzzing tool/job for the core protocol assemblies. New crashes/findings need minimized reproducible corpus entries retained in the repository or CI artifact workflow.

### End-to-end production transport state sequences

Once a production peer transport is selected, run generated/adversarial sequences across the real connection lifecycle, for example:

```text
handshake -> encrypted data -> replay -> close -> reconnect
network transition -> reconnect -> stale frame/session attempt
message -> ACK -> duplicate message -> duplicate ACK
signal send -> fetch -> ACK -> expiry
capability valid -> revoke -> retry -> replacement capability
```

The current isolated FsWebRTC experiment is intentionally not used to claim production-transport fuzz acceptance.

### Independent testing

External API penetration testing, independent cryptographic review, mobile secure-storage review and broader security assessment remain separate mandatory PLAN items.

## Rule for future findings

A fuzz/property task is only accepted when the defect is fixed at the production boundary rather than excluded from the corpus. The earlier QR pre-decode bound and stale session parser test are examples of this discipline: the tests were aligned with the real exposed boundary instead of preserving dead helper APIs solely for the test suite.
