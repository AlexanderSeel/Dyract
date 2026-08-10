# Attachment protocol foundation

## Status

Dyract now has a transport-neutral attachment foundation covering:

- bounded manifests;
- fixed-size canonical chunks;
- versioned `DYRA` manifest/chunk/resume application frames;
- restart-safe encrypted receive/chunk state;
- restart-safe encrypted sender snapshots and retry scheduling;
- peer-scoped progress and final completion acknowledgements;
- verified receiver reconstruction into caller-owned staging;
- bounded durable completed-receipt state for lost-final-ACK recovery;
- idempotent receive duplicates with changed-content collision rejection;
- bounded send/receive reservations and stale receive cleanup;
- end-to-end SHA-256 verification.

This does **not** yet mean attachments can be sent by the shipping app. Production peer-transport integration, platform-specific final-file promotion/free-space handling, sender-abandonment cleanup, mobile file access/destination UX and thumbnails remain open.

## Privacy boundary

Attachments remain device-owned data. The directory/signaling server must not become an attachment store or offline file mailbox.

The intended path is:

```text
sender local file
        ↓
validated immutable sender snapshot
        ↓
encrypted durable attachment outbox
        ↓
DYRA application payload
        ↓
authenticated encrypted DYSE session
        ↓
bounded direct/optional-relay chunks
        ↓
encrypted durable receiver chunk state
        ↓
caller-owned staging destination
        ↓
whole-file SHA-256 verification
        ↓
caller promotes verified staging to final local storage
        ↓
bounded durable completion receipt
        ↓
manifest-bound DYAC completion ACK
```

`DYRA` and `DYAC` are **not encryption layers**. A production peer must carry these payloads only inside the identity-authenticated encrypted `DYSE` session.

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
maximum chunks:       1600
maximum filename:     128 characters
maximum content type: 127 characters
SHA-256:              64 lowercase hex characters
```

The fixed chunk size means chunk index and byte offset are deterministic and do not need an attacker-controlled arbitrary range model.

## Filename handling

A remote filename is metadata for display, not a trusted filesystem path.

The protocol rejects empty/non-canonical names, `.`/`..`, path separators, control characters and common cross-platform reserved path characters. A mobile receiver must generate its own destination path and must not concatenate the remote filename into an application directory path.

## Content type handling

Protocol v1 accepts a bounded simple MIME type such as `image/jpeg`, `application/pdf` or `application/octet-stream`. Parameters are not part of v1.

The remote content type is advisory only. It is not proof that the file is safe to execute, render with elevated privileges or hand to another application.

## Chunk geometry

`AttachmentChunk` contains:

```text
Version
AttachmentId
ChunkIndex
Offset
Data
```

For a manifest:

```text
offset = chunkIndex * 65536
```

Every non-final chunk must contain exactly 64 KiB. The final chunk must contain exactly the remaining manifest bytes.

Manifest-scoped validation rejects a wrong version/attachment ID/index/offset/payload length. This prevents overlapping arbitrary writes and overrun geometry.

## DYRA application frames

`AttachmentApplicationFrameProtocol` uses the binary magic:

```text
DYRA
```

and v1 frame types:

```text
1  manifest
2  chunk
3  resume/progress request
```

The encoded-frame ceiling is 128 KiB. A maximum 64 KiB chunk plus `DYRA` metadata therefore remains comfortably below the current experimental 256 KiB raw DataChannel/session-frame boundary.

### Manifest frame

Carries the canonical 16-byte attachment ID, size, fixed chunk size, 32-byte SHA-256, and bounded UTF-8 filename/content type.

Decoded manifests immediately run full manifest validation.

### Chunk frame

Carries attachment ID, chunk index, byte offset, payload length and payload.

The decoder enforces structural bounds. The receiver must additionally call `AttachmentProtocol.ValidateChunk` against the already accepted manifest before persisting the chunk. A structurally valid frame is not sufficient by itself because canonical final-chunk length depends on the manifest.

### Resume/progress frame

Carries only ordered **missing chunk-index ranges**, not arbitrary byte ranges.

Ranges are bounded by the maximum protocol chunk count, must be positive, ordered and non-overlapping, and must then be validated against the specific accepted manifest with `ValidateResumeRequest`.

On the sender side this frame doubles as durable progress acknowledgement: chunks not listed as missing are marked acknowledged, listed ranges remain retryable, and retry attempts are reset because peer progress was observed.

A stale resume frame may cause redundant retransmission but cannot make the sender delete the immutable source snapshot.

The decoder rejects unsupported versions/types, invalid UTF-8, malformed/truncated frames and trailing bytes.

## DYAC final completion acknowledgement

`AttachmentCompletionAcknowledgementProtocol` uses:

```text
DYAC
```

A completion acknowledgement contains only:

```text
version
attachment ID
whole-file SHA-256
```

It is validated against the exact queued manifest. The sender accepts it only from the intended recipient scope and only when both attachment ID and hash match.

Reporting zero missing chunks is **not** final completion. The sender retains its encrypted snapshot and periodically sends a completion probe until a valid `DYAC` arrives. This keeps “all chunks arrived” distinct from “the receiver reconstructed and verified the complete attachment.”

## Restart-safe encrypted receive state

Schema migration 3 adds:

```text
attachment_receives
attachment_receive_chunks
```

`SqliteAttachmentReceiveStore` scopes all receive state by:

```text
(sender PeerId, attachment ID)
```

Sensitive values are protected with AES-256-GCM using the existing local-data key:

```text
filename
content type
whole-file SHA-256
chunk payload
```

Associated data binds each encrypted value to its peer/attachment/field or peer/attachment/chunk index. Operational geometry such as sender PeerId, attachment ID, size, chunk index and timestamps remains visible SQLite metadata, consistent with Dyract's existing local-storage disclosure boundary.

No plaintext temporary attachment file is introduced inside SQLite.

## Verified receiver staging and promotion boundary

`SqliteAttachmentReceiveStore.WriteVerifiedStagingAsync` accepts a **caller-owned writable staging stream**. For seekable destinations the stream must be empty and positioned at zero.

The method refuses to reconstruct while any canonical chunk range is missing. It then:

1. reads durable chunks in exact index order;
2. decrypts one chunk at a time;
3. validates stored chunk length/geometry;
4. writes sequentially to the caller-owned staging destination;
5. hashes the exact reconstructed bytes;
6. verifies exact total size/chunk count;
7. compares the complete SHA-256 against the manifest in fixed time;
8. returns a `VerifiedAttachmentStaging` token only after successful verification.

If verification/reconstruction fails and the destination is seekable, Dyract best-effort truncates the staging stream back to zero. Non-seekable/provider-specific cleanup remains the caller's responsibility.

`VerifiedAttachmentStaging` has no public constructor. The intended order is deliberately:

```text
WriteVerifiedStagingAsync
        ↓
caller promotes/moves the verified staging object to final local storage
        ↓
MarkCompletedAsync(verification token)
        ↓
completion receipt committed + partial encrypted chunks deleted
        ↓
DYAC may be emitted
```

This prevents the storage API from issuing final completion merely because all chunks are present. Platform-specific promotion is still owned by the mobile/file-provider layer because Android/iOS providers do not share one universal atomic-rename contract.

If the process dies after verification but before completion commit, the durable encrypted chunks remain and the attachment must be verified again after restart. This is preferable to persisting an unverified “complete” bit.

## Durable receiver completion receipts

Schema migration 6 adds:

```text
attachment_receive_completions
```

After the caller has successfully promoted a verified staging destination, `MarkCompletedAsync` atomically:

- persists a small completion receipt;
- deletes the active receive parent row, cascading deletion of encrypted chunks;
- returns the manifest-bound `DYAC` payload.

The completion receipt intentionally retains no attachment body, filename or MIME metadata. It stores only:

```text
sender PeerId + attachment ID        operational scope
canonical manifest fingerprint       AES-256-GCM protected
whole-file SHA-256                   AES-256-GCM protected
completed/expires timestamps         operational metadata
```

The manifest fingerprint is SHA-256 of the canonical accepted `DYRA` manifest frame. If a completion-probe manifest is replayed after restart:

- exact fingerprint match -> `StoreManifestAsync` returns `Completed`, and the stored `DYAC` can be re-emitted;
- same sender/attachment ID with different manifest content -> fail closed as an ID collision.

This handles a lost final ACK without re-downloading chunks or retaining the attachment itself in the completion table.

Completion receipts are bounded:

```text
retention:                    7 days
maximum receipts globally:    256
maximum receipts per sender:   64
```

When count bounds are exceeded, the newest receipts are retained. A receipt older than its expiry is not considered authoritative and is removed on lookup/cleanup.

## Restart-safe encrypted sender state

Schema migration 5 adds:

```text
attachment_sends
attachment_send_chunks
```

Sender state is scoped by:

```text
(sender PeerId, recipient PeerId, attachment ID)
```

`SqliteAttachmentSendStore.QueueAsync` accepts a canonical asynchronous chunk sequence, encrypts each chunk into the database, hashes the exact queued plaintext snapshot, compares that hash with the manifest and commits the entire transfer atomically.

The queue operation fails closed if:

- a chunk is missing, repeated or out of canonical order;
- chunk geometry does not match the manifest;
- the complete queued snapshot hash differs from the manifest;
- the same sender/recipient/attachment ID is already queued;
- sender queue quotas are exceeded;
- initialization/storage fails.

A failed queue operation leaves no partial durable transfer.

Encrypted sender fields include:

```text
filename
content type
whole-file SHA-256
chunk payloads
bounded last-failure diagnostic token
```

Due-time, attempt count, acknowledgement bits, PeerIds, sizes and chunk indices remain visible operational SQLite metadata.

## Sender retry ownership

`AttachmentOutboxWorker` uses the same transport-neutral `IPeerApplicationFrameSender` abstraction as reliable text delivery. It is intentionally **not registered against a shipping transport yet**.

A due transfer sends:

- the manifest until the receiver has emitted a valid resume/progress frame;
- only currently missing/unacknowledged chunks after progress is known;
- the manifest again as a completion probe when no chunks are missing but final completion is not acknowledged.

Successful send attempts move the transfer to an ACK-timeout retry. Transport failures use bounded exponential retry. Progress acknowledgements reschedule missing work immediately.

Exact retry frames are derived from the immutable encrypted sender snapshot rather than re-reading a potentially changed source file.

A valid final `DYAC` removes the sender transfer and cascades deletion of its encrypted chunk snapshot.

## Duplicate and collision behavior

Active manifest registration is idempotent only if all manifest content matches the previously stored receive manifest for the same peer/attachment ID.

Receive chunk storage uses the primary key:

```text
(sender PeerId, attachment ID, chunk index)
```

An exact duplicate chunk returns `Duplicate`. If the same identity/index is presented with different plaintext bytes, the store fails closed with a collision error rather than replacing the existing chunk.

After verified completion, the durable manifest fingerprint applies the same collision rule without retaining individual chunks.

## Resume model

The receive store persists validated chunk indices. After process/store recreation, `GetMissingRangesAsync` reconstructs the missing ranges from durable state.

Example:

```text
chunks:    0 1 2 3 4 5
received:  x   x x   x
missing:   [1,1] [4,1]
```

Duplicate received indices are harmless. Out-of-range indices fail closed.

## Integrity verification

The sender manifest includes SHA-256 of the complete attachment.

The receiver does not create a completion receipt until the durable chunks have been reconstructed into caller-owned staging and the exact complete-file size and SHA-256 match the manifest.

`AttachmentProtocol.VerifySha256Async` remains available as a general streaming verifier; `WriteVerifiedStagingAsync` performs the same completion boundary while reconstructing encrypted receive chunks.

A SHA-256 match is an integrity check, not malware/content safety validation.

## Send/receive quotas

Migrations 4 and 5 add atomic SQLite triggers for active reservations:

```text
receive:
  maximum active receives globally:       16
  maximum active receives per sender:      4
  maximum declared active bytes globally: 512 MiB
  maximum declared active bytes per peer:  200 MiB

send:
  maximum active sends globally:           16
  maximum active sends per recipient:       4
  maximum declared active bytes globally: 512 MiB
  maximum declared active bytes per peer:  200 MiB
```

These limits are enforced where parent transfer state is inserted, not only through process-local counters, so concurrent paths cannot independently bypass the reservation check.

They are prototype safety limits and may be tuned after real-device profiling. The separate per-attachment protocol maximum remains 100 MiB.

Actual free-space checks still belong to mobile integration because database reservation cannot know whether the OS/storage provider is near capacity.

## Cleanup policy

Receiver-side cleanup now distinguishes incomplete data from tiny completion receipts:

```text
inactive partial receive retention: 14 days
completion receipt retention:        7 days
```

`CleanupStaleAsync` deletes inactive partial parent rows (cascading chunk deletion) and expired completion receipts transactionally.

The cleanup policy deliberately does not guess whether a mobile destination/provider has enough free space. Free-space checks must happen before accepting/staging data in the platform integration layer.

Sender-side abandoned-outbox expiry is still open because automatically deleting a not-yet-delivered sender snapshot changes user-visible delivery guarantees and needs an explicit UX policy (cancel, retry indefinitely, or expire after a declared period).

## Reset behavior

Destructive Dyract identity/local-data reset deletes partial receive state, completion receipts and queued sender snapshots before rotating the local-data key. The resetter also tolerates databases from earlier versions where attachment tables do not yet exist.

## Still open

Before attachments are a shipping feature:

- integrate `DYRA`/`DYAC` with the proven production `IPeerTransport`/frame sender inside authenticated `DYSE` sessions;
- implement platform-specific promotion/generated destination behavior after `WriteVerifiedStagingAsync` succeeds;
- enforce actual mobile free-space checks in addition to reservation quotas;
- handle Android/iOS file picker/provider permissions safely;
- define user-visible sender cancellation/abandoned-outbox expiry policy;
- add optional thumbnails without decoding untrusted content in a privileged path;
- test interruption/resume, final-ACK loss, low-disk behavior, malicious frames/manifests, staging/promotion and reset on physical Android/iOS devices;
- keep push/directory metadata opaque and attachment-free.

## Repository acceptance

Repository-side manifest validation, canonical chunk geometry, `DYRA` manifest/chunk/resume framing, `DYAC` final-completion framing, encrypted restart-safe receive state, verified caller-owned staging, bounded durable completion receipts/final-ACK replay, receiver cleanup, encrypted restart-safe sender snapshots, peer scoping, sender retry scheduling, progress acknowledgement handling, exact-duplicate/collision handling, send/receive reservation quotas, SHA-256 verification and safe filename/content-type bounds are implemented with regression coverage.

Production transport integration and platform/mobile file lifecycle remain unfinished and therefore stay in `plan.md`.
