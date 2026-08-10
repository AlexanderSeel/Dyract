# Attachment sender lifecycle

## Status

Dyract now exposes the durable attachment sender lifecycle in the shipping mobile conversation UI while deliberately keeping delivery transport disconnected until the peer transport is proven.

The sender can:

- pick a file and snapshot it immediately into the encrypted attachment sender store;
- see pending/retrying/waiting-for-final-confirmation state for the current contact;
- request an immediate retry by moving the existing durable transfer's due time forward;
- explicitly cancel the exact transfer and delete its encrypted sender snapshot.

Dyract still does **not** expire queued attachments solely because time has passed.

## Why there is no silent automatic expiry

A queued attachment is an immutable encrypted sender-owned snapshot. Deleting it after an arbitrary timeout changes a user-visible delivery guarantee: the sender may believe the attachment is still pending while Dyract has permanently discarded the only retry snapshot.

Therefore the current rule is:

```text
queued attachment remains durable until one of:

1. valid recipient-scoped DYAC completion
2. explicit sender cancellation
3. destructive Dyract identity/local-data reset
```

There is currently no fourth "age exceeded" transition.

A future product decision may add an explicit expiry such as "cancel after N days", but that must be visible/configurable and must not be introduced as a hidden cleanup optimization.

## Immutable sender snapshot from the mobile picker

The shipping app uses the MAUI picker result as a short-lived provider handle rather than assuming it is a filesystem path.

The queue sequence is:

```text
pick file
  ↓
OpenReadAsync
  ↓
stream size + SHA-256 inspection under protocol bounds
  ↓
local free-space admission check when the platform reports capacity
  ↓
OpenReadAsync again
  ↓
canonical 64 KiB chunk replay into SqliteAttachmentSendStore
  ↓
second whole-snapshot SHA-256 check inside QueueAsync
  ↓
atomic encrypted sender snapshot commit
```

The application does not persist a picker URI/path for later retries. Exact retry data comes from encrypted SQLite chunks. If the provider-backed file changes between inspection and the second read, queueing fails closed rather than committing a manifest for different content.

## Pending status projection

`SqliteAttachmentSendStatusStore` exposes a bounded, exact sender/recipient projection for local UI. It decrypts only the selected transfer metadata required for presentation:

```text
filename
content type
whole-file SHA-256
bounded last-failure token
```

The projection also reports:

```text
attempt count
next attempt time
manifest acknowledged
acknowledged chunks / total chunks
```

The conversation UI converts that into these user-facing states:

```text
Pending delivery
Retry scheduled
Retry scheduled after a delivery error
Waiting for final confirmation
```

The UI does not display the raw sender/recipient Peer IDs used internally for exact-scope actions.

## Explicit retry

`SqliteAttachmentSendMaintenance.RetryNowAsync` requires the exact:

```text
sender PeerId
recipient PeerId
attachment ID
```

It changes only operational retry state:

- `next_attempt_utc` becomes now;
- the prior bounded failure token is cleared;
- `updated_utc` advances.

It does **not** replace the immutable snapshot, reset acknowledged chunks, or delete retry history. When all chunks are already acknowledged, Retry means "schedule the final-confirmation probe now" rather than retransmit acknowledged chunks.

The control is useful before transport is wired because it defines and tests the local lifecycle. It does not imply that a shipping peer transport currently consumes the due queue.

## Explicit cancellation

`SqliteAttachmentSendMaintenance.CancelAsync` requires the same exact scope.

The operation deletes only the matching `attachment_sends` parent row. SQLite foreign-key cascade then removes its encrypted `attachment_send_chunks` snapshot.

A wrong recipient/sender/attachment scope returns no deletion rather than broadening the match.

The mobile UI asks for explicit confirmation and states that Dyract does not silently expire pending attachments.

Cancellation does not contact the directory and does not attempt to revoke already delivered bytes from the recipient. It only stops this installation from retaining/retrying the queued sender snapshot.

## Interaction with completion

A valid final `DYAC` still owns the normal successful terminal transition. Once the sender store accepts a completion acknowledgement bound to the intended recipient, attachment ID and SHA-256, it deletes the same durable sender state automatically.

Cancellation is therefore a separate user-controlled terminal state, not an alternate acknowledgement.

## Interaction with reset

Destructive identity/local-data reset clears all queued sender snapshots regardless of age or recipient. It also deletes app-owned staged/final received attachment files before the reset marker is cleared.

If attachment-file removal fails, reset fails with the pending-reset marker retained, allowing startup to resume the destructive reset rather than silently leaving old file data while rotating identity state.

## Acceptance

Repository/mobile sender lifecycle acceptance now includes:

- exact-scope cancellation;
- encrypted chunk cascade deletion;
- wrong-scope cancellation isolation;
- no silent time-based sender expiry;
- bounded local sender-status projection;
- explicit Retry Now scheduling without snapshot replacement;
- pending/retry/final-confirmation UI;
- provider-safe immediate mobile snapshot queueing;
- tests for sender status, retry state, final-confirmation state and cancellation.

Still required before attachments are a production feature:

- connect the existing durable lifecycle to a physically proven authenticated peer transport;
- validate picker/provider interruption and retry/cancel behavior on physical Android/iOS devices;
- decide any future user-configurable expiry policy explicitly rather than through hidden cleanup.
