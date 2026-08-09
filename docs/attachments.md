# Attachment protocol foundation

## Status

Dyract now has a transport-neutral attachment protocol foundation for bounded manifests, fixed-size chunks, resume planning and end-to-end SHA-256 verification.

This does **not** yet mean attachments can be sent by the shipping app. Production peer-session framing, durable partial-file state, mobile file access, cleanup and thumbnails remain open.

## Privacy boundary

Attachments remain device-owned data. The directory/signaling server must not become an attachment store or offline file mailbox.

The intended path is:

```text
sender local file
        ↓
validated attachment manifest
        ↓
authenticated encrypted peer session
        ↓
bounded direct/optional-relay chunks
        ↓
receiver local temporary file
        ↓
SHA-256 verification
        ↓
receiver-owned final local storage
```

No filename, content type, hash, chunk or attachment body belongs in normal directory metadata, push payloads or ordinary telemetry.

## Protocol v1 manifest

`AttachmentManifest` contains:

```text
Version
AttachmentId
FileName
ContentType
SizeBytes
ChunkSize
Sha256
```

Current v1 bounds:

```text
version:              1
attachment ID:        128-bit random lowercase hex
maximum size:         100 MiB
chunk size:           64 KiB fixed
maximum filename:     128 characters
maximum content type: 127 characters
SHA-256:              64 lowercase hex characters
```

The fixed v1 chunk size means chunk index and byte offset are deterministic and do not need an attacker-controlled arbitrary range model.

## Filename handling

A remote filename is metadata for display, not a trusted filesystem path.

The protocol rejects:

- empty/whitespace-only names;
- leading/trailing whitespace;
- `.` and `..`;
- path separators;
- control characters;
- common cross-platform reserved path characters such as `:`, `*`, `?`, `|`, `<`, `>` and quotes.

A future mobile receiver must still generate its own destination path and must not concatenate the remote filename into an application directory path.

## Content type handling

Protocol v1 accepts a bounded simple MIME type such as:

```text
image/jpeg
application/pdf
application/octet-stream
```

Parameters such as `; charset=...` are not part of the v1 manifest.

The remote content type is advisory metadata only. It must never be used as proof that file bytes are safe to execute, render with elevated privileges, or hand to another application.

## Chunk geometry

`AttachmentChunk` contains:

```text
Version
AttachmentId
ChunkIndex
Offset
Data
```

For a manifest, every chunk has one canonical offset:

```text
offset = chunkIndex * 65536
```

Every non-final chunk must contain exactly 64 KiB. The final chunk must contain exactly the remaining manifest bytes.

A chunk is rejected if its:

- version differs;
- attachment ID differs;
- index is out of range;
- offset is not canonical;
- payload length differs from the expected length.

This prevents overlapping arbitrary writes and overrun geometry from being accepted by the protocol helper.

## Resume model

The receiver can represent durable progress as a set of validated chunk indices.

`GetMissingRanges` converts received chunk indices into coalesced missing chunk ranges. Example:

```text
chunks:    0 1 2 3 4 5
received:  x   x x   x
missing:   [1,1] [4,1]
```

The eventual peer-session request frame should request only these canonical chunk-index ranges, not arbitrary byte offsets supplied by the remote peer.

Duplicate received indices are harmless. Out-of-range indices fail closed.

## Integrity verification

The sender manifest includes SHA-256 of the complete attachment.

The receiver must not promote a partial file to completed attachment state until:

1. exactly `SizeBytes` have been reconstructed;
2. every expected chunk has been received;
3. the complete-file SHA-256 matches the manifest.

`AttachmentProtocol.VerifySha256Async` streams the file and rejects both size mismatch and digest mismatch.

A SHA-256 match is an integrity check, not malware/content safety validation.

## Limits and denial-of-service considerations

The 100 MiB prototype limit and fixed 64 KiB chunks bound:

- manifest-declared allocation;
- chunk count;
- resume bitmap/range planning;
- individual peer application frames before encrypted-session framing overhead.

The receiver must still enforce local disk quotas and concurrency limits before accepting an attachment. A valid contact/session must not be allowed to exhaust all device storage.

Future transfer scheduling should cap simultaneously active attachments and temporary-file bytes per peer and globally.

## Still open

Before attachments are a shipping feature:

- define versioned attachment manifest/chunk/resume application frames inside authenticated `DYSE` sessions;
- integrate with the proven production `IPeerTransport`/frame sender;
- persist partial-receive chunk state and temporary file ownership durably;
- make duplicate chunks idempotent and changed-content collisions fail closed;
- enforce per-peer/global temporary-storage quotas;
- handle mobile file picker/provider permissions safely;
- generate local destination names rather than trusting remote paths;
- clean abandoned temporary files;
- add optional thumbnails without decoding untrusted content in a privileged path;
- test interruption/resume, low-disk behavior and malicious manifests on Android/iOS;
- keep push/directory metadata opaque and attachment-free.

## Repository acceptance

Repository-side attachment manifest validation, deterministic chunk geometry, missing-range planning, SHA-256 verification and basic safe filename/content-type bounds are implemented and regression-tested.

Transport framing, durable transfer state and mobile UX/storage remain unfinished and therefore stay in `plan.md`.
