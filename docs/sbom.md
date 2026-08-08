# SBOM and dependency automation

Dyract generates Software Bills of Materials (SBOMs) as part of repository security validation.

The goal is to maintain a reproducible inventory of third-party .NET dependencies without conflating shipping code with the experimental WebRTC transport spike.

## Format

The repository uses:

```text
CycloneDX JSON 1.7
```

Generator:

```text
CycloneDX .NET 6.2.0
```

The generator is pinned as a repository-local .NET tool in:

```text
.config/dotnet-tools.json
```

Generated BOMs are validated with the official CycloneDX CLI before they are published as CI artifacts.

## SBOM surfaces

The workflow emits three BOMs:

```text
dyract-directory.cdx.json
```

Entry project:

```text
src/Dyract.Server/Dyract.Server.csproj
```

This recursively includes the production directory's referenced Dyract projects and NuGet dependencies.

```text
dyract-mobile-android.cdx.json
```

Entry project:

```text
src/Dyract.App/Dyract.App.csproj
```

Generated for `net10.0-android`. This covers the current shipping mobile application's shared and Android dependency graph, including QR, storage and client/transport-neutral libraries.

iOS is compiled independently in CI. The current MAUI project uses the same project/package declarations for Android and iOS, but platform-specific runtime assets can differ. Before a signed iOS production release, add an iOS-specific BOM generated on the macOS/Xcode release toolchain if the final packaging graph materially differs.

```text
dyract-transport-experiment.cdx.json
```

Entry project:

```text
experiments/Dyract.Transport.AndroidHarness/Dyract.Transport.AndroidHarness.csproj
```

This BOM intentionally separates the FsWebRTC experiment from shipping application dependencies. The current FsWebRTC native 16 KiB-page blocker therefore cannot be mistaken for an accepted shipping transport dependency merely because it appears in a repository-wide inventory.

## Generation

Workflow:

```text
.github/workflows/sbom.yml
```

The job:

1. checks out the repository;
2. installs .NET 10;
3. restores the pinned CycloneDX tool;
4. installs the MAUI Android workload;
5. restores the three production/experimental dependency graphs;
6. generates CycloneDX 1.7 JSON with recursive project references included;
7. validates every BOM with the official CycloneDX CLI;
8. creates SHA-256 checksums;
9. uploads the bundle as the `dyract-cyclonedx-sbom` GitHub Actions artifact.

The generated metadata version uses the current commit prefix:

```text
0.0.0+<12-char-commit-sha>
```

This makes an SBOM artifact traceable to the source snapshot that produced it without inventing an application release version before Dyract has a release process.

## Validation

A workflow is considered successful only when all generated BOMs pass official CycloneDX schema validation.

A file merely existing is not sufficient acceptance.

Current validation image is pinned to:

```text
cyclonedx/cyclonedx-cli:0.32.0
```

Checksums are stored beside the BOMs in:

```text
SHA256SUMS
```

The first full repository validation completed successfully in GitHub Actions run `31248702771`: all three dependency graphs restored, all three CycloneDX 1.7 BOMs generated, official validation passed, checksums were created, and the artifact upload succeeded.

## Artifact retention

CI SBOM bundles are currently retained for 30 days.

This is appropriate for branch/continuous validation, not yet a complete release-retention policy. Once Dyract publishes versioned releases, each release should retain or attach its corresponding validated SBOM for at least the supported lifetime of that release.

Do not commit automatically regenerated BOM JSON to `main` on every dependency change. That creates noisy bot commits and can leave source/SBOM synchronization ambiguous. The CI artifact is generated from the exact checked-out commit and is therefore the authoritative continuous-build inventory.

A future signed release pipeline may additionally attach the BOM to the GitHub Release and/or submit it to a vulnerability-management system.

## Dependency update automation

Configuration:

```text
.github/dependabot.yml
```

Dependabot monitors:

```text
NuGet packages
GitHub Actions
```

Minor/patch NuGet updates are grouped by production vs development dependency type where possible. Major updates remain separate so compatibility/security impact is explicit in review.

GitHub Actions dependency updates are grouped separately.

Dependency update PRs must still pass the repository's normal build/test/mobile/infrastructure checks as applicable. Dependabot is an update mechanism, not an automatic security approval.

## Security use

An SBOM answers:

```text
What third-party components and versions are present in this build graph?
```

It does not by itself answer:

```text
Is every component safe?
Is every vulnerability exploitable here?
Is the binary provenance trusted?
```

SBOMs should later feed:

- vulnerability scanning;
- incident response for newly disclosed CVEs;
- release provenance/attestation;
- dependency licensing review;
- upgrade planning;
- transport-binding risk review.

## Privacy

The SBOM describes software components, versions and dependency relationships. It must not include runtime user data, PeerIds, IP addresses, capabilities, keys, message contents or deployment secrets.

The workflow does not pass production credentials to the generator.

## Acceptance criteria

The repository-side SBOM automation is complete when:

- [x] generator version is pinned in the repository;
- [x] directory/server BOM is generated;
- [x] shipping Android app BOM is generated;
- [x] experimental transport BOM is generated separately;
- [x] recursive project dependencies are included;
- [x] CycloneDX 1.7 JSON is used;
- [x] official schema validation is required;
- [x] SHA-256 checksums are produced;
- [x] CI artifact upload is configured;
- [x] NuGet dependency update automation is configured;
- [x] GitHub Actions update automation is configured;
- [x] the workflow has executed successfully on GitHub Actions.

Repository-side SBOM/dependency automation is therefore complete. Release attachment/long-term retention and vulnerability-management ingestion remain release-process work rather than blockers for this automation milestone.
