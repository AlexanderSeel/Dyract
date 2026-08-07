# Reliable peer messaging

Dyract's reliable messaging layer is intentionally independent from the concrete peer transport. A WebRTC DataChannel, future native transport, or test loopback only supplies an authenticated byte channel. Message identity, durable receive semantics, delivery acknowledgements and retries live above that boundary.

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
        remove outbox row
```

A successful socket/DataChannel write is **not** delivery. Only a valid ACK from the authenticated intended recipient removes the sender's outbox item.

## `DYRM` application frames

`Dyract.Protocol.PeerMessagingProtocol` defines the versioned application message envelope.

Current frame types:

```text
1  text message
2  delivery ACK
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

Text payloads are strict UTF-8, bounded to 32,768 characters and 128 KiB encoded size. Delivery ACKs have no body.

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

## Concurrency behavior

An ACK can race a sender attempt. The queue update is written defensively:

- a valid ACK may remove the outbox while a send is finishing;
- a later attempt update that finds no outbox row returns `false` rather than recreating it;
- `OutboxDeliveryWorker` reports this as `ChangedConcurrently`.

The worker never resurrects a message already acknowledged as delivered.

## Current boundary

The reliability algorithm is implemented and covered independently of the concrete network implementation.

Implemented:

- transactional store-before-send;
- versioned text/ACK wire format;
- authenticated peer scope validation;
- idempotent durable receive;
- duplicate collision rejection;
- duplicate ACK re-emission;
- exact-peer delivery ACK processing;
- due outbox selection;
- deterministic resend of the same logical message;
- bounded ACK/failure retry scheduling;
- privacy-safe persisted failure codes;
- lost-first-ACK end-to-end integration test.

Still intentionally open:

- production `IPeerApplicationFrameSender` implementation;
- long-running mobile delivery scheduler / lifecycle integration;
- reconnect/session management around the worker;
- read receipts;
- multi-message synchronization after long offline periods;
- clock-skew-aware presentation ordering;
- physical-device proof over the experimental authenticated WebRTC path.

The shipping app must not start the outbox worker against FsWebRTC until the physical Android transport matrix succeeds and the current FsWebRTC Android 16 KB native-library blocker is resolved or the transport dependency changes.
