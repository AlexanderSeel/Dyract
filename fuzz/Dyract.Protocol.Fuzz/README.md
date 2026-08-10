# Dyract protocol coverage-guided fuzzing

This project is an isolated SharpFuzz/libFuzzer harness for untrusted Dyract protocol boundaries. It targets .NET 10 and is part of `Dyract.slnx` so normal CI restores/builds the harness.

## Current targets

The first input byte selects a fuzz domain:

```text
0 -> DYRM reliable-message frame
1 -> DYRA attachment manifest/chunk/resume frame
2 -> DYAC attachment completion acknowledgement
3 -> DYSH authenticated-session handshake state
4 -> DYSE authenticated encrypted-session state
```

For `DYRM`, `DYRA` and `DYAC`, the remaining bytes are parser input. Accepted frames must re-encode byte-for-byte identically. Expected malformed-input rejection is suppressed only at the documented parser boundary; unexpected runtime exceptions and canonical mismatches escape as findings.

`DYSH` and `DYSE` are stateful targets rather than raw parser targets. Their fuzz input is a bounded mutation/state instruction stream applied to internally constructed valid sessions. The fixture uses synthetic process-local identities only; no application/user identity or corpus data is read.

### DYSH invariants

The harness creates a valid initiator/responder pair for each handshake attempt and fuzzes either the signed hello or signed response.

- unchanged internally generated packets must authenticate successfully;
- any changed signed hello must be rejected by `AuthenticatedSessionResponder.Accept`;
- any changed signed response must be rejected by `AuthenticatedSessionInitiator.Complete`;
- only the handshake API's expected `CryptographicException` / `ArgumentException` rejection contract is treated as expected;
- unexpected exceptions or acceptance of a changed signed packet are findings.

`AuthenticatedSessionInitiator.Complete` intentionally consumes/disposes initiator state after a completion attempt, so response fuzzing creates fresh initiator state for every input rather than incorrectly asserting reuse after failure.

### DYSE invariants

Each fuzz iteration creates fresh sender/receiver ciphers from synthetic authenticated session keys and produces two valid sequential frames. Input selects or mutates one of these conditions:

```text
0 -> bounded byte mutations of frame 0
1 -> frame 1 delivered before frame 0
2 -> truncated frame 0
3 -> extended frame 0
```

If the candidate differs from valid frame 0 it must be rejected. Immediately afterward, valid frame 0 and frame 1 must still decrypt in order. This proves the rejection did not advance receive sequence state. After both valid frames are accepted, replaying frame 0 must fail.

## Seed corpus

Generate deterministic fuzz instructions and valid parser seeds before a campaign:

```powershell
dotnet run --project fuzz/Dyract.Protocol.Fuzz/Dyract.Protocol.Fuzz.csproj -- --generate-corpus artifacts/fuzz/protocol-corpus
```

The generated corpus contains canonical `DYRM`, `DYRA` manifest/chunk/resume and `DYAC` frames plus state instructions for valid/mutated `DYSH` and baseline/out-of-order/truncated/extended `DYSE` paths. The repository already ignores `artifacts/`; generated campaign output is local by default.

The `DYSH`/`DYSE` corpus deliberately stores instructions rather than generated keys, nonces or ciphertext. Valid cryptographic state is constructed inside the harness, keeping corpus material reproducible in shape and independent of user secrets.

## Running libFuzzer

SharpFuzz 2.3.0 is pinned in the project. Use the maintained `libfuzzer-dotnet` driver and SharpFuzz `fuzz-libfuzzer.ps1` workflow described by the SharpFuzz project. Keep the native driver/release version recorded with campaign results rather than silently following an unpinned binary in CI.

A parser campaign should use a bounded maximum input length close to the largest parser frame (currently 128 KiB for `DYRA`). Session targets consume only a bounded mutation instruction prefix, so oversized inputs do not create unbounded cryptographic work. Retain crash artifacts outside ordinary logs.

## Findings workflow

For every finding:

1. reproduce it against the exact repository commit and recorded fuzzer/driver versions;
2. minimize the crashing input with libFuzzer/SharpFuzz tooling;
3. fix the production protocol/session boundary rather than suppressing the input in the harness;
4. add the minimized input or an equivalent deterministic regression test to the repository;
5. rerun the relevant deterministic tests plus a coverage-guided campaign;
6. do not include PeerIds, endpoint data, keys, message content or other real user data in corpus entries.

Binary minimized corpus files may be committed under a future `fuzz/corpus/regressions/` directory when they represent a fixed defect. Seed generation remains code-driven so the repository does not need opaque generated binaries merely to start a campaign.

## Scope still open

The repository now has coverage-guided parser targets and stateful `DYSH`/`DYSE` targets. It still does **not** cover a proven production transport lifecycle because no production transport has passed the physical-device gate yet.

A harness existing in the repository is not evidence that an external/long-running campaign has been executed. Campaign execution, duration/versions, corpus minimization evidence and security triage remain explicit release work. A scheduled workflow should be added only once the native fuzzer-driver acquisition/version is pinned and reviewable.
