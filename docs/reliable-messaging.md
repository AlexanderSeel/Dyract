# Reliable peer messaging

Dyract's reliable messaging layer is intentionally independent from the concrete peer transport. A WebRTC DataChannel, future native transport, or test loopback only supplies an authenticated byte channel. Message identity, durable receive semantics, delivery/read acknowledgements and retries live above that boundary.

## State flow

```text
local compose
    |
    v
Queued + outbox row committed atomically
    |
    | due delivery cycle
    v
network write attempted
    |                   \
    | success            \ failure
    v                     v
Sent                  Failed
    |                     |
    | no ACK yet          | bounded retry
    +----------+----------+
               |
               v
           resend same
     MessageId / CreatedAt / text
               |
               v
       receiver durable insert
               |
          +----+----+
          |         |
        new       exact duplicate
          |         |
          +----+----+
               |
          delivery ACK
               |
               v
          Delivered
               |
       explicit user read
               |
               v
             Read
```

A successful socket/DataChannel write is **not** delivery. Only a valid ACK from the authenticated intended recipient removes the sender's outbox item. A message becomes `Read` only from an explicit peer-scoped read acknowledgement; delivery itself never implies that the user read the message.

## `DYRM` application frames

`Dyract.Protocol.PeerMessagingProtocol` defines the versioned application message envelope.

Current frame types:

```text
1  text message
2  delivery ACK
3  read ACK
```

Common fields:

```text
magic       DYRM
version     1
frame type
MessageId   lowercase 128-bit hex
sender      exact Dyract PeerId
recipient   exact Dyract PeerId
timestamp   Unix milliseconds
payload length
payload
```

Text payloads are strict UTF-8, bounded to 32,768 characters and 128 KiB encoded size. Delivery and read ACKs have no body.

`DYRM` is expected to travel **inside** the authenticated encrypted application session (`DYSE`). A decoded frame is still checked against the authenticated session identities:

```text
frame.sender    == authenticated remote PeerId
frame.recipient == local PeerId
```

The wire timestamp is intentionally millisecond precision.

## Sender durability

`SqliteLocalStore.QueueOutgoingTextAsync` already commits the outgoing message and outbox row in one SQLite transaction before network activity begins.

The delivery side is handled by:

```text
IOutboxDeliveryQueue
SqliteOutboxDeliveryQueue
OutboxDeliveryWorker
IPeerApplicationFrameSender
```

`SqliteOutboxDeliveryQueue` reconstructs a due message from durable data including the original:

- MessageId,
- SenderPeerId,
- RecipientPeerId,
- CreatedAt,
- encrypted text,
- attempt count,
- next-attempt time.

Retries therefore encode the same logical message rather than manufacturing a new message ID or timestamp.

### Successful network write

After `IPeerApplicationFrameSender.SendAsync` returns successfully:

- message state becomes `Sent`;
- attempt count increments;
- an ACK-wait retry is scheduled;
- the outbox row remains.

Current ACK-wait backoff begins at 10 seconds and is capped at 2 minutes.

### Failed network write

A failed send:

- marks the local message `Failed`;
- increments the attempt count;
- schedules bounded exponential retry beginning at 2 seconds;
- retains the outbox row.

Only a privacy-safe failure code such as:

```text
send:TimeoutException
send:InvalidOperationException
```

is persisted. Raw exception text is deliberately not stored because transport exceptions can contain candidate addresses, IPs, ports or other network metadata.

## Receiver idempotency

`SqliteIncomingMessageStore.StoreIncomingTextAsync` performs the durable receive operation.

A newly received message is accepted only when:

- MessageId is canonical;
- sender and recipient PeerIds are valid and distinct;
- sender is a locally saved contact;
- the authenticated session has already validated sender/recipient scope;
- text is within configured bounds.

The message is inserted as `Incoming / Text / Delivered` and its body is encrypted using the same local-data encryption system as other stored message content.

### Duplicate handling

`MessageId` is the idempotency key.

If the same MessageId arrives again, it is considered an acceptable retransmission **only if** the existing durable row matches the incoming message's:

- sender,
- recipient,
- direction,
- message type,
- original CreatedAt,
- exact text.

An exact duplicate creates no second chat message and returns `IsNew = false`.

If a duplicate MessageId carries different content or scope, Dyract rejects it as a collision/protocol violation rather than silently overwriting data.

## Delivery ACK semantics

`PeerMessageProcessor` emits a delivery ACK only **after** durable incoming storage succeeds.

If an exact duplicate is received because a prior ACK was lost, the duplicate is not inserted again, but another ACK is emitted. This makes sender retries safe.

Network ACK processing uses `IOutgoingDeliveryStore.MarkOutgoingDeliveredAsync`, which requires the exact stored peer scope:

```text
stored sender    == local PeerId
stored recipient == authenticated remote PeerId
```

The older generic `ILocalStore.MarkDeliveredAsync(messageId, ...)` must not be used for peer-network ACK processing because a bare MessageId is not a sufficient authorization boundary.

A valid ACK:

- transitions the original outgoing message to `Delivered`;
- records its delivery time once;
- removes the outbox row.

A repeated valid ACK is idempotent. An ACK from a different peer cannot clear the item.

## Read receipt semantics

Read receipts are explicit and peer scoped. `PeerReadReceiptService` marks an already-delivered incoming message read only after the caller's UX policy decides that the user has actually observed it, then creates a `PeerReadAckFrame` for the original sender.

The durable stores enforce exact scope in both directions:

```text
incoming mark-read:
reader == local recipient
sender == authenticated remote sender

outgoing read ACK:
stored sender == local sender
reader == authenticated remote recipient
```

A valid read ACK transitions the outgoing message to `Read`, records `ReadAt`, preserves/establishes delivery time, and removes any still-present outbox row. That last rule allows a read ACK to safely supersede a lost delivery ACK. Wrong-peer read acknowledgements cannot change the message.

Read receipt generation is transport-neutral; the shipping app still needs the proven production frame sender/lifecycle scheduler before read ACK bytes can be delivered over the real peer connection.

## Presentation ordering under clock skew

A remote peer controls the `CreatedAt` carried in its text frame, so that timestamp cannot safely define local chat order. `SqliteLocalStore.GetMessagesAsync` therefore uses a local presentation timestamp:

- outgoing messages: local `created_utc`;
- incoming messages: local receive time stored in `delivered_utc`, falling back to `created_utc` only for legacy/incomplete rows.

Both the limited "latest N" selection and the final ascending presentation order use this rule. A remote device whose clock is days behind or ahead therefore cannot push a newly received message outside the current conversation window merely through clock skew. The original remote `CreatedAt` is still retained on the message for protocol identity/audit semantics.

## Long-offline catch-up

Dyract does not introduce a central offline message mailbox. The durable sender outbox is the synchronization source for one-to-one messages that have not yet been acknowledged.

If Bob is offline for hours or days while Alice sends messages, Alice retains those exact messages locally. When a production transport session can be re-established, `OutboxDeliveryWorker.ProcessDueBacklogAsync` can drain the due backlog in bounded pages:

```text
reconnect / foreground / successful wake
        |
        v
read due outbox page (default 50)
        |
        v
send each exact DYRM message
        |
        +-- success -> move ACK retry into the future
        +-- failure -> move failure retry into the future
        +-- concurrent ACK -> row stays removed
        |
        v
read next due page
        |
        v
stop when page is short or activation budget is exhausted
```

The default activation budget is 500 messages, with an allowed batch size up to 500. This prevents an unbounded reconnect loop from monopolizing battery, CPU or network. `OutboxBacklogDrainResult.BudgetExhausted` tells the future lifecycle scheduler that the current activation consumed its explicit budget; a later foreground/wake/reconnect opportunity can continue draining remaining due rows.

The design relies on existing reliability properties rather than a second synchronization protocol:

- unacknowledged messages remain durable on the sender;
- each retry preserves MessageId, CreatedAt and text;
- the receiver durably inserts before ACK;
- duplicates are idempotent and re-ACKed;
- successful/failed attempts are moved out of the immediate due set, so the next page advances through older backlog;
- no message body is uploaded to the directory, Redis, PostgreSQL, APNs or FCM.

This is intentionally one-device-to-one-device synchronization. If the sender device is lost or its local data is deleted before delivery, Dyract has no cloud history from which to reconstruct the message. Multi-device synchronization and cloud backup remain separate deferred features because they materially change the privacy model.

The transport/lifecycle trigger remains open: the shipping app must invoke the bounded drain only after a production peer session is proven and integrated.

## Lost-ACK proof

`ReliableMessagingEndToEndTests` uses two separate encrypted SQLite databases to model Alice and Bob.

The test sequence is:

```text
Alice queues message M
Alice sends M to Bob
Bob durably stores M
Bob creates ACK(M)
first ACK is deliberately dropped
Alice keeps M in outbox
ACK timeout expires
Alice resends exact M
Bob recognizes exact duplicate M
Bob still has one durable message
Bob creates ACK(M) again
Alice processes ACK(M)
Alice marks M Delivered
Alice removes M from outbox
```

The test uses one shared simulated clock for sender retry scheduling and both receiver timestamp validators, matching the single-time-domain behavior expected on actual devices while keeping the scenario deterministic.

`ReadReceiptTests` additionally covers durable explicit read state, peer-scoped read ACK processing, wrong-peer rejection, and a read ACK superseding a lost delivery ACK. `MessagePresentationOrderingTests` covers a remote clock skewed far into the past while ensuring the latest-message limit still follows local receive order. `OutboxBacklogDrainTests` covers multi-page catch-up and the hard per-activation message budget.

## Concurrency behavior

An ACK can race a sender attempt. The queue update is written defensively:

- a valid ACK may remove the outbox while a send is finishing;
- a later attempt update that finds no outbox row returns `false` rather than recreating it;
- `OutboxDeliveryWorker` reports this as `ChangedConcurrently`.

The worker never resurrects a message already acknowledged as delivered/read.

## Current boundary

The reliability algorithm is implemented and covered independently of the concrete network implementation.

Implemented:

- transactional store-before-send;
- versioned text/delivery-ACK/read-ACK wire format;
- authenticated peer scope validation;
- idempotent durable receive;
- duplicate collision rejection;
- duplicate delivery-ACK re-emission;
- exact-peer delivery ACK processing;
- explicit durable peer-scoped read receipts;
- due outbox selection;
- deterministic resend of the same logical message;
- bounded ACK/failure retry scheduling;
- privacy-safe persisted failure codes;
- lost-first-ACK end-to-end integration test;
- clock-skew-safe local presentation ordering and latest-message selection;
- bounded multi-page long-offline backlog catch-up without a server message store.

Still intentionally open:

- production `IPeerApplicationFrameSender` implementation;
- long-running mobile delivery scheduler / lifecycle integration;
- reconnect/session management around the worker;
- physical-device proof over the experimental authenticated WebRTC path.

The shipping app must not start the outbox worker against FsWebRTC until the physical Android transport matrix succeeds and the current FsWebRTC Android 16 KB native-library blocker is resolved or the transport dependency changes.
