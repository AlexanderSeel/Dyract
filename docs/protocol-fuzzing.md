# Protocol parser robustness and fuzzing

Dyract treats QR/import text, HTTP request bodies and peer/network frames as untrusted input, even when the surrounding transport is encrypted or authenticated.

This document records both the required deterministic regression strategy and the repository-side coverage-guided fuzz harness. Neither replaces an independent penetration test or cryptographic review, and merely having a harness in the repository is not evidence that a long-running external campaign has been executed.

## Acceptance invariants

Repository fuzz/property coverage targets these rules:

1. arbitrary malformed input must fail closed without unexpected runtime exceptions;
2. externally controlled sizes must be bounded before avoidable large decoding/deserialization allocations;
3. protocol domains must not silently cross-accept one another;
4. accepted wire encodings must be canonical;
5. authentication failure must not advance receive/session state;
6. replay/downgrade/cross-session inputs must not become valid through parser/state-machine side effects;
7. deterministic seeds/corpora must make failures reproducible in CI or a recorded fuzz campaign.

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

A coverage-guided stateful `DYSH` target is still open because it needs stable identity/session setup and must distinguish expected authentication failure from true state-machine defects.

## Reliable-message frames (`DYRM`)

Boundary:

```text
PeerMessagingProtocol.TryDecode(...)
```

Repository properties include:

- deterministic malformed/boundary binary corpora;
- hundreds of generated valid text/ACK frames with Unicode payloads;
- exact encode -> decode -> encode canonical equality for valid frames;
- one-bit mutation across every byte of a representative valid frame;
- a mutated frame must either reject with a bounded protocol error or decode to the exact canonical byte representation it already contains;
- normal reliable-messaging tests continue to cover sender/recipient scope, duplicate idempotency, ACK authorization and collision rejection.

`DYRM` is also a current coverage-guided fuzz target.

## Attachment frames (`DYRA` / `DYAC`)

Coverage-guided parser targets now include:

```text
AttachmentApplicationFrameProtocol.Decode(...)               DYRA
AttachmentCompletionAcknowledgementProtocol.Decode(...)      DYAC
```

The harness enforces canonical decode -> encode equality for accepted frames. Only documented `InvalidDataException` parser rejection is suppressed; unexpected runtime exceptions escape to the fuzzer.

While creating the harness, this invariant exposed a structural asymmetry: `DYRA` chunk decoding accepted negative chunk indexes/offsets even though the encoder rejected them. The production decoder now rejects negative geometry immediately, before later manifest-scoped chunk validation, and a deterministic regression test covers that boundary.

Manifest-dependent canonical offset/final-chunk geometry is still validated by `AttachmentProtocol.ValidateChunk` after structural frame decoding; the structural decoder is not treated as authorization or full manifest acceptance.

## Authenticated encrypted application frames (`DYSE`)

`ProtocolFuzzPropertyTests` exercises the established `AuthenticatedSessionCipher` state API rather than a mock decoder.

Current deterministic properties:

- 64 sequential encrypted frames are mutated at deterministic positions;
- each authentication/format failure must leave the receive sequence unchanged so the original frame can still be accepted immediately afterward;
- truncated frames reject without consuming receive state;
- extended/trailing-byte frames reject without consuming receive state;
- a frame from Session A is rejected by Session B;
- the same frame is accepted by the correct session once and then rejected as replay.

These properties extend the existing example tests for header mutation, wrong identity/session, replay, out-of-order data and tampered ciphertext/tag.

A coverage-guided stateful `DYSE` target remains open; arbitrary ciphertext alone is low-value unless the harness can generate and mutate valid authenticated session sequences while preserving expected receive-state invariants.

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

## Coverage-guided SharpFuzz/libFuzzer harness

The repository now contains:

```text
fuzz/Dyract.Protocol.Fuzz/
```

The .NET 10 project pins SharpFuzz 2.3.0 and is included in `Dyract.slnx`, so ordinary core CI restores/builds the fuzz target even though normal CI does not run an unbounded fuzz campaign.

The first fuzz-input byte selects a parser domain:

```text
0 -> DYRM
1 -> DYRA
2 -> DYAC
```

The remaining bytes are passed directly to the production parser. Accepted frames must re-encode byte-for-byte identically. This preserves the same canonical-wire invariant as deterministic property tests while allowing coverage-guided mutation to explore parser branches.

A deterministic seed-corpus generator produces valid text, attachment manifest/chunk/resume and final completion frames into the ignored `artifacts/` tree before a campaign. Seed binaries therefore do not need to be checked in merely to bootstrap the fuzzer.

The campaign and finding workflow is documented in `fuzz/Dyract.Protocol.Fuzz/README.md`:

1. record repository commit plus SharpFuzz/native driver versions;
2. run against generated valid seeds with bounded maximum input length;
3. minimize any crashing input;
4. fix the production boundary rather than broadening the harness exception filter;
5. retain the minimized binary input or an equivalent deterministic regression test;
6. never use real PeerIds, addresses, keys, message content or other user data as fuzz corpus material.

Repository support for this harness is complete; actual long-running/scheduled campaign evidence remains a separate release/security task.

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

The repository-side coverage-guided harness is also implemented for `DYRM`, `DYRA` and `DYAC`, including deterministic seed generation and canonical round-trip invariants.

Neither status means Dyract has undergone an independent fuzzing campaign or security review.

## Still open before production

### External/long-running coverage-guided campaigns

Run and record sustained campaigns against the repository harness, minimize any findings and retain reproducible regression corpus entries or equivalent deterministic tests. A scheduled workflow may be added only when the native fuzzer-driver acquisition/versioning strategy is pinned and reviewable rather than silently downloading an unversioned binary.

### Stateful authenticated-session fuzzing

Add dedicated coverage-guided `DYSH`/`DYSE` state-machine targets that construct valid sessions and mutate meaningful handshake/encrypted-frame sequences while asserting that rejected inputs do not advance authentication/receive state.

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

A fuzz/property task is only accepted when the defect is fixed at the production boundary rather than excluded from the corpus. The QR pre-decode bound, stale-session parser test and negative `DYRA` chunk-geometry rejection are examples of this discipline: tests/harnesses are aligned with the real exposed boundary instead of preserving parser asymmetries or dead helper APIs solely for test convenience.
