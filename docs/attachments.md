# Attachment protocol and mobile file lifecycle

## Status

Dyract now has a transport-neutral attachment foundation plus the shipping-app local file lifecycle needed around it:

- bounded manifests and canonical fixed-size chunks;
- versioned `DYRA` manifest/chunk/resume frames and manifest-bound `DYAC` completion;
- encrypted restart-safe receiver chunks and sender snapshots;
- verified receiver reconstruction before completion;
- bounded durable completion receipts for lost-final-ACK recovery;
- send/receive reservation quotas;
- MAUI picker -> encrypted sender snapshot queueing without persisting a provider path;
- Android/iOS app-owned generated receive destinations;
- mobile free-space admission checks when the platform can report capacity;
- pending/retry/waiting-for-final-confirmation/cancel sender UI;
- destructive reset of both attachment database state and app-owned staged/final files;
- a decoder-independent automatic-preview admission boundary for bounded, integrity-verified PNG/JPEG candidates.

This still does **not** mean attachments are delivered by the shipping app. The durable attachment outbox is intentionally not connected to the experimental FsWebRTC path. Production peer-transport integration remains gated by physical transport evidence.

Automatic thumbnail decoding is also not connected yet. `AttachmentPreviewPolicy` is the required pre-decode security boundary; see `docs/attachment-previews.md`.

## Privacy boundary

Attachments are device-owned data. The directory/signaling service must not become an attachment store or offline file mailbox.

The intended production path is:

```text
sender picker/provider
        ↓
stream inspection + SHA-256
        ↓
second provider read + exact snapshot verification
        ↓
encrypted durable attachment sender queue
        ↓
DYRA inside authenticated encrypted DYSE session
        ↓
bounded direct/optional-relay chunks
        ↓
encrypted durable receiver chunk state
        ↓
mobile free-space admission
        ↓
app-owned generated staging file
        ↓
whole-file size/chunk/SHA-256 verification
        ↓
app-owned final-file promotion
        ↓
bounded durable completion receipt
        ↓
manifest-bound DYAC completion ACK
```

`DYRA` and `DYAC` are protocol frames, not encryption layers. A production transport must carry them only inside the identity-authenticated encrypted `DYSE` session.

No filename, content type, hash, chunk, attachment body or thumbnail content belongs in normal directory metadata, push payloads or ordinary telemetry.

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

The fixed chunk size makes chunk index and byte offset deterministic rather than accepting attacker-controlled arbitrary byte ranges.

## Filename and content-type handling

A remote filename is display metadata, never a trusted local path.

The protocol rejects empty/non-canonical names, `.`/`..`, path separators, control characters and common reserved path characters. The mobile receiver generates the final path from the canonical attachment ID. It may retain only a conservative alphanumeric cosmetic extension from the display filename; an unsafe extension becomes `.bin`.

The remote content type is bounded simple MIME metadata. It is advisory only and is not proof that the file is safe to execute, preview or hand to another application.

## Sender picker and immutable snapshot

The shipping MAUI conversation page can select an attachment locally even though network delivery is not connected yet.

The picker result is treated as a provider handle, not as a guaranteed filesystem path. The app uses `OpenReadAsync()` and never stores `FileResult.FullPath` for retry.

Queueing is deliberately two-pass:

```text
OpenReadAsync #1
  ↓
AttachmentStreamSnapshot.InspectAsync
  ↓
bounded byte count + streaming SHA-256 + canonical manifest
  ↓
free-space admission when capacity is known
  ↓
OpenReadAsync #2
  ↓
AttachmentStreamSnapshot.ReadChunksAsync
  ↓
SqliteAttachmentSendStore.QueueAsync
  ↓
second whole-snapshot SHA-256 verification
  ↓
atomic encrypted queue commit
```

This matters for Android document providers and other sources where a picker result may not map to an ordinary path. It also prevents a source that changes between inspection and queueing from being committed under a stale manifest.

No plaintext sender temp file is created. Once queueing succeeds, retries are reconstructed only from the encrypted durable SQLite snapshot, so later provider permission/path changes do not affect retry correctness.

`AttachmentStreamSnapshot` clears its reusable internal read buffer after copying a chunk into the yielded `AttachmentChunk`. The yielded `chunk.Data` is caller-owned and is **not** cleared by the enumerator after `MoveNext`; consumers remain responsible for the lifetime of any plaintext copies they retain.

## Chunk geometry and DYRA

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

Every non-final chunk contains exactly 64 KiB. The final chunk contains exactly the remaining manifest bytes.

`AttachmentApplicationFrameProtocol` uses magic `DYRA` with frame types:

```text
1  manifest
2  chunk
3  resume/progress request
```

The encoded-frame ceiling is 128 KiB. Structural decoding rejects malformed/truncated/trailing data, unsupported versions/types, invalid UTF-8 and negative chunk index/offset. Manifest-dependent final-chunk geometry is then checked against the already accepted manifest.

Resume/progress frames contain ordered missing **chunk-index ranges**, not arbitrary byte ranges. Ranges must be positive, ordered, non-overlapping and within the specific manifest.

## DYAC completion

`AttachmentCompletionAcknowledgementProtocol` uses magic `DYAC` and contains only:

```text
version
attachment ID
whole-file SHA-256
```

The sender accepts completion only from the intended recipient scope and only when attachment ID and SHA-256 match the queued manifest.

Zero missing chunks is not final completion. The sender keeps its encrypted source snapshot and can send a manifest completion probe until a valid `DYAC` is received.

## Restart-safe encrypted receive state

Schema migration 3 adds:

```text
attachment_receives
attachment_receive_chunks
```

Receive state is scoped by:

```text
(sender PeerId, attachment ID)
```

Sensitive values are AES-256-GCM protected under the local-data key:

```text
filename
content type
whole-file SHA-256
chunk payloads
```

Associated data binds each encrypted value to peer/attachment/field or peer/attachment/chunk index.

Peer IDs, attachment IDs, sizes, chunk indices and timestamps remain visible operational SQLite metadata. Dyract does not claim full local database opacity.

## Verified staging and completion boundary

`SqliteAttachmentReceiveStore.WriteVerifiedStagingAsync` reconstructs into a caller-owned writable staging stream only after all chunks exist. It:

1. reads durable chunks in canonical order;
2. decrypts and validates each stored chunk;
3. writes sequentially to staging;
4. hashes the exact reconstructed bytes;
5. verifies exact size/chunk count;
6. compares complete SHA-256 with the manifest;
7. returns a non-publicly-constructible `VerifiedAttachmentStaging` token.

The completion order is intentionally:

```text
WriteVerifiedStagingAsync
        ↓
platform/app-owned promotion
        ↓
MarkCompletedAsync
        ↓
durable completion receipt + active receive deletion
        ↓
DYAC may be emitted
```

Presence of every chunk alone cannot produce final completion.

## Mobile receive coordinator

`AttachmentReceiveFileCoordinator` now owns the transport-independent mobile completion orchestration:

```text
existing completion receipt?
  yes -> return stored DYAC; do not reconstruct
  no  -> continue

load active manifest
  ↓
query local available bytes
  ↓
reject if known capacity < manifest size
  ↓
create generated staging destination
  ↓
WriteVerifiedStagingAsync
  ↓
PromoteAsync
  ↓
MarkCompletedAsync
```

If verification, promotion or completion fails, the coordinator best-effort aborts staging and does not invent a completion acknowledgement.

A capacity provider may return `null` when a platform/provider cannot determine reliable available bytes. That does not imply infinite space: actual write failures still fail the operation without `DYAC`.

## App-owned Android/iOS destination

The shipping app registers `AppOwnedAttachmentStorage` as both:

```text
IAttachmentStorageCapacity
IAttachmentReceiveDestinationFactory
```

Files live below:

```text
FileSystem.AppDataDirectory/attachments/
    staging/
    received/
```

Final names are generated from the attachment ID, not the remote filename.

Android available capacity is queried from the app-data filesystem. iOS uses Foundation filesystem attributes. The iOS attachment root/final file is marked to skip cloud backup; failure to apply that exclusion prevents successful completion rather than silently backing up received attachment data.

### Crash/idempotence window

There is an unavoidable process-crash window between successful file promotion and `MarkCompletedAsync`.

The generated final path is deterministic for an attachment ID. If retry sees that final file already exists, promotion verifies its complete size/SHA-256 against the current manifest:

- exact match -> staging can be discarded and completion may proceed;
- mismatch -> fail closed; never overwrite silently.

This makes the promotion-before-DYAC window recoverable without trusting an unverified existing file.

## Durable completion receipts

Schema migration 6 adds:

```text
attachment_receive_completions
```

After successful promotion, `MarkCompletedAsync` atomically:

- persists a small encrypted completion receipt;
- deletes active receive state, cascading encrypted chunks;
- returns the manifest-bound `DYAC`.

The receipt retains no body, filename or MIME metadata. It stores only operational sender/attachment/times plus encrypted manifest fingerprint and SHA-256.

Bounds:

```text
retention:                    7 days
maximum receipts globally:    256
maximum receipts per sender:   64
```

An exact manifest replay can re-emit the stored final ACK after restart. Reusing the same sender/attachment ID with changed canonical manifest content fails closed.

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

`SqliteAttachmentSendStore.QueueAsync` requires exactly one canonical ordered chunk sequence, hashes the complete queued plaintext snapshot and commits only if that hash matches the manifest.

Encrypted sender fields include filename, content type, whole-file hash, chunk payloads and the bounded failure token. Due time, attempts, acknowledgement bits, Peer IDs, sizes and chunk indices remain operational SQLite metadata.

## Sender pending/retry/cancel UX

`SqliteAttachmentSendStatusStore` provides a bounded projection for one exact sender/recipient pair. The conversation UI shows:

```text
Pending delivery
Retry scheduled
Retry scheduled after a delivery error
Waiting for final confirmation
```

It also shows acknowledged/total chunk progress. Raw internal Peer IDs are not displayed by this attachment status UI.

`RetryNowAsync` schedules the existing immutable snapshot immediately without replacing content or resetting acknowledged chunks. If every chunk is already acknowledged, Retry schedules the final-confirmation probe.

`CancelAsync` deletes only the exact sender/recipient/attachment transfer and cascades its encrypted chunk rows. The UI requires confirmation.

There is no silent sender expiry. Sender state remains until:

1. valid recipient-scoped `DYAC`;
2. explicit user cancellation;
3. destructive identity/local-data reset.

See `docs/attachment-sender-lifecycle.md`.

## Quotas and free-space policy

SQLite reservation quotas remain:

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

These quotas prevent unbounded durable reservation but are not a substitute for real filesystem capacity.

The mobile sender preflights capacity before copying a selected file into the encrypted snapshot. The receive coordinator preflights capacity before reconstructing verified staging. Capacity is checked against at least the manifest body size; physical low-disk validation remains required because filesystem metadata can change immediately after a check and encrypted SQLite/staging overhead also consumes space.

## Cleanup and reset

Receiver cleanup:

```text
inactive partial receive retention: 14 days
completion receipt retention:        7 days
```

Sender snapshots do not expire on time alone.

Destructive installation reset now removes:

- attachment sender rows/chunks;
- active receive rows/chunks;
- completion receipts;
- app-owned attachment staging files;
- app-owned promoted received files;
- the rest of identity-bound local database state;
- identity/local-data secrets.

If app-owned file deletion fails, the pending-reset marker remains, so a later startup resumes the destructive reset instead of declaring success with leftover files.

## Automatic preview admission boundary

Remote content is never considered safe merely because the peer session authenticated the sender or because a filename/MIME value says it is an image.

`AttachmentPreviewPolicy` is now the mandatory repository-side admission step before any future automatic raster decoder. It currently allows only `image/png` and `image/jpeg`, and only after:

- exact completed-file length and SHA-256 re-verification against the canonical manifest;
- independently detected PNG/JPEG signature matching the declared supported MIME;
- bounded header parsing;
- an 8 MiB automatic-preview source limit;
- an 8192-pixel per-dimension limit;
- a 32,000,000-pixel total-area limit.

The policy does **not** decode pixels. A successful result owns the exact verified byte snapshot as a `VerifiedAttachmentPreviewSource`; future platform decoding must consume that read-only source instead of reopening an unverified path. SVG, HTML, PDF and all other unsupported/complex formats fall back to a generic attachment presentation.

Preview rejection or decoder failure must never invalidate an otherwise valid received attachment. See `docs/attachment-previews.md` for the complete threat model and decoder requirements.

## Still open

Before attachments are a production feature:

- integrate `DYRA`/`DYAC` with the physically proven production peer transport inside authenticated `DYSE` sessions;
- implement reviewed Android/iOS bounded thumbnail decoding behind `AttachmentPreviewPolicy` and wire safe UI presentation;
- validate Android/iOS picker/provider behavior on physical devices;
- validate low-disk behavior under real filesystem pressure;
- validate interruption/restart around staging, promotion and the promotion-before-DYAC window;
- validate sender retry/cancel and destructive reset physically on both platforms;
- validate malicious frame/manifest handling through the real production transport lifecycle;
- keep push/directory metadata opaque and attachment-free.

## Repository acceptance

Repository-side acceptance now covers the protocol framing, encrypted durable send/receive state, verified staging, durable completion replay, sender retry/cancel lifecycle, stream-safe provider snapshotting, generated app-owned destination orchestration, free-space admission contracts, mobile pending attachment UI, reset coverage of app-owned attachment files, and a deterministic untrusted-content admission boundary that prevents unsupported/oversized/mismatched/tampered raster inputs from reaching a future decoder through the approved API.

The remaining PLAN items are transport integration, actual platform thumbnail decoding/UI, current mobile Release validation and physical Android/iOS validation. No claim is made that the new mobile attachment path has passed physical-device validation or that shipping P2P attachment delivery is connected.
