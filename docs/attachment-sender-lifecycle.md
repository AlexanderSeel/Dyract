# Attachment sender lifecycle

## Status

Dyract has an explicit durable sender-cancellation primitive and deliberately does **not** expire queued attachments solely because time has passed.

This is a repository/storage policy. The shipping mobile cancel button, progress UI and user-facing retry controls are still open.

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

## Explicit cancellation

`SqliteAttachmentSendMaintenance.CancelAsync` requires the exact:

```text
sender PeerId
recipient PeerId
attachment ID
```

The operation deletes only the matching `attachment_sends` parent row. SQLite foreign-key cascade then removes its encrypted `attachment_send_chunks` snapshot.

A wrong recipient/sender/attachment scope returns no deletion rather than broadening the match.

Cancellation does not contact the directory and does not attempt to revoke already delivered bytes from the recipient. It only stops this installation from retrying the queued sender snapshot.

## Interaction with completion

A valid final `DYAC` still owns the normal successful terminal transition. Once the sender store accepts a completion acknowledgement bound to the intended recipient, attachment ID and SHA-256, it deletes the same durable sender state automatically.

Cancellation is therefore a separate user-controlled terminal state, not an alternate acknowledgement.

## Interaction with reset

Destructive identity/local-data reset clears all queued sender snapshots regardless of age or recipient. This is part of the reset security boundary and is intentionally stronger than ordinary per-transfer cancellation.

## Mobile UX still required

Before attachments ship, the mobile layer should expose at least:

- pending/retrying state;
- explicit Cancel action with confirmation where appropriate;
- clear distinction between failed/retrying and completed;
- no implication that cancellation removes a file already received by the peer;
- optional future "retry now" control only when a production transport exists;
- any future time-based expiry as an explicit product setting/policy, never silent cleanup.

## Acceptance

Repository-side sender cancellation semantics are complete when:

- cancellation is exact-scope;
- encrypted chunk rows cascade-delete with the sender parent;
- wrong-scope cancellation cannot remove another transfer;
- no background cleanup silently expires sender snapshots;
- tests cover cancellation and retry-state disappearance after cancellation.

The mobile UX for invoking cancellation remains open.
