# Dyract protocol coverage-guided fuzzing

This project is an isolated SharpFuzz/libFuzzer harness for untrusted Dyract binary frame parsers. It targets .NET 10 and is part of `Dyract.slnx` so normal CI at least restores/builds the harness.

## Current targets

The first input byte selects a parser domain; the remaining bytes are passed directly to the production parser:

```text
0 -> DYRM reliable-message frame
1 -> DYRA attachment manifest/chunk/resume frame
2 -> DYAC attachment completion acknowledgement
```

Expected malformed-input rejection is suppressed only at the documented parser boundary. Unexpected runtime exceptions and canonical encode/decode mismatches are allowed to escape so libFuzzer records them as findings.

## Seed corpus

Generate deterministic valid seeds before a campaign:

```powershell
dotnet run --project fuzz/Dyract.Protocol.Fuzz/Dyract.Protocol.Fuzz.csproj -- --generate-corpus artifacts/fuzz/protocol-corpus
```

The generated corpus contains canonical `DYRM`, `DYRA` manifest/chunk/resume and `DYAC` frames. The repository already ignores `artifacts/`; generated campaign output is therefore local by default.

## Running libFuzzer

SharpFuzz 2.3.0 is pinned in the project. Use the maintained `libfuzzer-dotnet` driver and SharpFuzz `fuzz-libfuzzer.ps1` workflow described by the SharpFuzz project. Keep the native driver/release version recorded with campaign results rather than silently following an unpinned binary in CI.

A campaign should use a bounded maximum input length close to the largest parser frame (currently 128 KiB for `DYRA`) and retain crash artifacts outside ordinary logs.

## Findings workflow

For every finding:

1. reproduce it against the exact repository commit and recorded fuzzer/driver versions;
2. minimize the crashing input with libFuzzer/SharpFuzz tooling;
3. fix the production parser boundary rather than suppressing the input in the harness;
4. add the minimized input or an equivalent deterministic regression test to the repository;
5. rerun the relevant deterministic tests plus a coverage-guided campaign;
6. do not include PeerIds, endpoint data, keys, message content or other real user data in corpus entries.

Binary minimized corpus files may be committed under a future `fuzz/corpus/regressions/` directory when they represent a fixed defect. Seed generation remains code-driven so the repository does not need opaque generated binaries merely to start a campaign.

## Scope still open

This harness does not yet cover stateful `DYSH`/`DYSE` session sequences or a proven production transport lifecycle. Those require dedicated stateful fuzz targets rather than treating authenticated-session failures as generic parser exceptions.

A harness existing in the repository is not evidence that an external/long-running campaign has been executed. Campaign execution, corpus minimization evidence and security triage remain explicit release work.
