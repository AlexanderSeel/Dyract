# Attachment preview security boundary

## Status

Automatic attachment thumbnail decoding is **not implemented yet**. This document defines and enforces the repository-side admission boundary that must exist before a platform decoder is connected.

`AttachmentPreviewPolicy` is intentionally transport- and UI-neutral. It does not decode pixels. It turns only a narrow, bounded, integrity-verified completed attachment into a `VerifiedAttachmentPreviewSource` that a future reviewed platform decoder may consume.

Until a decoder is implemented behind this boundary, the shipping UI must use a generic attachment presentation rather than attempting to preview arbitrary remote content.

## Threat model

Every attachment is attacker-controlled input even when it arrived inside an authenticated peer session:

- the sender may be malicious or compromised;
- the declared MIME type may be false;
- the display filename and extension may be misleading;
- file-format metadata may be malformed;
- dimensions may be chosen to trigger excessive allocation or decompression work;
- complex formats can contain scripts, active content, external references, embedded files, animation or parser attack surface;
- a promoted local file could be corrupted after receive completion.

Authentication answers who sent the bytes. It does not make the bytes safe to decode.

## Admission rules

The first preview boundary supports only a deliberately narrow raster candidate set:

```text
image/png
image/jpeg
```

The following are **not** automatically previewable by this policy:

```text
image/svg+xml
text/html
application/pdf
GIF/WebP/HEIF and other raster/container formats
video/audio/document/archive formats
unknown application/octet-stream data
```

Unsupported content remains a normal attachment and should receive a generic icon. Preview rejection must not reject, delete or fail the underlying attachment.

### Mandatory checks before a decoder can receive bytes

`AttachmentPreviewPolicy.InspectCompletedAsync` requires a fresh readable stream positioned at the beginning of the completed file and applies these checks in order:

1. validate the canonical `AttachmentManifest`;
2. require an explicitly supported declared MIME type;
3. reject automatic preview sources larger than 8 MiB without reading them;
4. read exactly the manifest byte count and reject truncation or trailing growth;
5. recompute SHA-256 and require an exact fixed-time match with the manifest;
6. sniff the raster signature independently of filename/extension;
7. require the sniffed format to match the declared supported MIME type;
8. parse only bounded PNG/JPEG header structure needed for dimensions;
9. require width and height to be at most 8192 pixels each;
10. require total raster area to be at most 32,000,000 pixels.

Only after all checks pass is `VerifiedAttachmentPreviewSource` created. Its constructor is not public. The source retains the exact verified bytes and exposes them through a read-only stream; a future decoder must consume that stream instead of reopening a path that could introduce a verification-to-decode race.

Disposing the verified source clears its retained byte array.

## What the admission token does not claim

The policy is not a complete PNG/JPEG validator and does not make a native or managed image decoder trusted. A malicious file can pass basic structural admission and still be rejected by the eventual decoder.

The future decoder boundary must therefore additionally satisfy all of these requirements:

- consume only `VerifiedAttachmentPreviewSource`, never a remote/provider path;
- produce a bounded thumbnail, never a full-resolution decode by default;
- enforce output dimensions and allocation limits independently of source metadata;
- fail closed to a generic attachment icon on decode error;
- never execute scripts, active document content, external references or embedded payloads;
- never turn preview failure into attachment receive failure;
- avoid persisting thumbnail bytes outside app-owned storage unless an explicit local lifecycle is defined;
- keep attachment bytes, filenames, hashes and thumbnail contents out of ordinary telemetry, push metadata and directory/signaling state;
- receive platform-specific security review before being enabled in the shipping app.

## Completed-file ordering

Previewing belongs strictly after the existing verified receive lifecycle:

```text
all DYRA chunks durable
        ↓
WriteVerifiedStagingAsync
        ↓
whole-file size/chunk/SHA-256 verification
        ↓
app-owned final-file promotion
        ↓
MarkCompletedAsync / durable completion receipt
        ↓
open completed app-owned file
        ↓
AttachmentPreviewPolicy
        ↓
VerifiedAttachmentPreviewSource
        ↓
future reviewed bounded platform decoder
```

No preview parser or decoder should touch partial receive chunks, unverified staging data, sender provider handles or a remote filename-derived path.

The policy rechecks the completed file SHA-256 before exposing bytes to the future decoder. That provides defense against post-promotion corruption and ensures the decoder sees the same byte snapshot that was admitted.

## Filename and MIME handling

Filename extension is intentionally not used for preview trust. It remains display metadata only.

Declared MIME is also not trusted on its own. It is used only as a narrow opt-in allowlist and must agree with an independently detected PNG/JPEG signature. A mismatch results in a generic preview rejection.

This means, for example:

```text
photo.jpg + image/jpeg + PNG bytes -> no preview
photo.png + application/octet-stream + PNG bytes -> no automatic preview
image.svg + image/svg+xml -> no automatic preview
```

The attachment itself remains available according to the normal local attachment lifecycle.

## Repository acceptance for this boundary

Deterministic tests cover:

- admitted PNG dimensions and exact verified-byte handoff;
- admitted JPEG start-of-frame dimensions;
- declared MIME/signature mismatch;
- unsupported active/complex MIME rejected before reading source bytes;
- automatic-preview source-size limit rejected before reading source bytes;
- SHA mismatch/tampering;
- truncated source;
- trailing growth after the manifest snapshot;
- excessive single dimensions;
- excessive total pixel area;
- disposal preventing later access to retained verified bytes.

Actual Android/iOS thumbnail decoding, UI presentation and physical-device validation remain open PLAN work.
