# Shared Redis transient state

Dyract can use Redis for the correctness-critical short-lived directory state that must be visible across multiple server instances.

Redis is optional. Without `ConnectionStrings:Redis`, Dyract keeps the existing in-memory implementations for local development and single-process testing.

With Redis configured, the server uses shared implementations for:

```text
registration challenges
signed-request replay nonces
presence leases
WebRTC signaling inboxes
```

Capability revocations are intentionally **not** stored here; PostgreSQL persists those because revocation must survive a directory restart until the capability's natural expiry.

Redis never acts as a chat-message queue. Message bodies and conversation history remain device-owned data.

## Configuration

ASP.NET configuration key:

```text
ConnectionStrings:Redis
```

Development example:

```text
redis.internal:6379,abortConnect=true
```

Production credentials/TLS settings belong in the deployment secret/configuration system, not in source control.

When Redis is configured, `RedisTransientStateInitializer` performs a startup ping. If the configured shared-state service is unavailable, application startup fails instead of silently falling back to process-local state.

This is deliberate: silently degrading a horizontally scaled directory to local replay/presence/signaling state would change security and correctness semantics.

## Production Redis policy

Production Redis has an explicit fail-closed deployment contract.

Application-enforced requirements in the `Production` environment are:

```text
ssl=true
password/ACL authentication present
allowAdmin=false
Dyract:Redis:NetworkIsolationConfirmed=true
```

For environment-variable based deployments, the network confirmation key is:

```text
Dyract__Redis__NetworkIsolationConfirmed=true
```

A representative production connection shape is:

```text
redis.private.example:6380,ssl=true,user=dyract,password=<secret>,allowAdmin=false,abortConnect=true
```

The actual password/ACL secret must come from protected deployment secret management. It must not be committed to application settings, repository files, container images, logs, or telemetry.

`RedisConnectionPolicy` validates TLS, authentication and non-admin access before the connection is created. The network-isolation flag is intentionally an **operator/deployment attestation**, not a claim that application code can inspect cloud firewall topology. Set it to `true` only after the following network controls are in place:

- Redis has no unrestricted public ingress;
- the endpoint is private/internal or protected by equivalent network controls;
- inbound Redis access is restricted to the Dyract directory workload/subnet/security identity as narrowly as the platform permits;
- firewall/security-group/private-endpoint policy is deployed and reviewed with the application deployment;
- TLS server certificate validation remains enabled; do not disable certificate validation to make a private endpoint connect;
- a dedicated Redis credential/ACL identity is used when the platform supports it;
- Dyract does not receive Redis administrative permissions and `allowAdmin` remains disabled;
- credential rotation can occur without embedding secrets in source or images;
- monitoring detects Redis connection failures, authentication failures and availability degradation without logging the connection string or transient values.

The application sets `AbortOnConnectFail=true` and uses client name `dyract-directory`. A configured Redis outage therefore fails startup/runtime operations rather than silently switching to process-local security state.

Redis contains TTL-bound correctness/security state. Production availability/failover should preserve the active dataset during normal failover. Operators must not treat routine `FLUSHDB`/cache replacement as harmless while directory instances are serving traffic, because doing so discards active replay markers, presence leases, registration challenges and signaling state.

Development and test environments intentionally do not require the production TLS/authentication/network attestation so local Redis service containers remain usable.

## Client library

Server package:

```text
StackExchange.Redis 3.0.17
```

## Registration challenges

Implementation:

```text
IRegistrationChallengeStore
RegistrationChallengeStore              in-memory fallback
RedisRegistrationChallengeStore         shared mode
```

Properties:

- two-minute TTL;
- 32-byte random challenge;
- random 128-bit challenge ID;
- challenge value binds PeerId + public key + challenge bytes + expiry;
- one-time consume after successful signature verification;
- a challenge created through one directory instance can be read/consumed through another;
- malformed/expired state fails closed.

Redis key shape:

```text
dyract:registration:<random-challenge-id>
```

The key itself contains no PeerId.

## Replay nonces

Implementation:

```text
IReplayNonceStore
ReplayNonceStore                         in-memory fallback
RedisReplayNonceStore                    shared mode
```

A signed request nonce is accepted with Redis `SET ... NX` and a five-minute TTL.

Redis key material is SHA-256 derived from:

```text
PeerId + "\n" + nonce
```

Key shape:

```text
dyract:replay:<sha256>
```

Raw Peer IDs and nonces are therefore not placed in Redis key names.

The same peer/nonce accepted through instance A is rejected through instance B. The same nonce remains independent for another PeerId.

## Presence leases

Implementation:

```text
IPresenceStore
PresenceStore                            in-memory fallback
RedisPresenceStore                       shared mode
```

Presence remains a maximum two-minute lease enforced by the HTTP endpoint.

Stored value:

```text
PeerId
connection candidates
updated timestamp
natural expiry
```

The Redis TTL is the remaining lease duration, and reads also validate the embedded logical expiry. Corrupt or logically expired lease state is removed and treated as unavailable.

Redis key shape:

```text
dyract:presence:<sha256(peer-id)>
```

The active lease value necessarily contains the PeerId and current reachability candidates because the directory must return them to an authorized peer, but raw Peer IDs are not exposed in the key namespace.

## Signaling inbox

Implementation:

```text
ISignalStore
SignalStore                              in-memory fallback
RedisSignalStore                         shared mode
```

Signaling remains intentionally short-lived and is not an offline chat queue.

Protocol constraints remain:

```text
maximum pending signals / target: 64
maximum fetch batch:              20
maximum signal lifetime:          60 seconds
maximum payload:                  32 KiB UTF-8
```

### Redis layout

Each target is represented by a SHA-256-derived hash tag so related keys remain colocated when Redis Cluster is used:

```text
dyract:signal:{<sha256(peer-id)>}:items
dyract:signal:{<sha256(peer-id)>}:order
dyract:signal:{<sha256(peer-id)>}:expiry
```

The structures are:

```text
items   HASH        signalId -> serialized transient envelope
order   SORTED SET  signalId scored by creation time
expiry  SORTED SET  signalId scored by natural expiry
```

### Atomic operations

Redis Lua scripts provide the multi-key atomicity needed for the inbox.

Enqueue performs one atomic operation:

```text
purge expired IDs
        ↓
check target pending-count < 64
        ↓
write envelope
        ↓
write creation-order score
        ↓
write expiry score
        ↓
apply key TTL up to latest active expiry
```

This prevents two directory instances from independently observing spare capacity and overflowing the bounded inbox.

Fetch performs:

```text
purge expired IDs
        ↓
read up to 20 oldest IDs
        ↓
return envelopes
```

Fetch is **non-destructive**. The target explicitly ACKs signal IDs after processing them.

ACK performs:

```text
purge expired IDs
        ↓
remove only requested IDs from this target's inbox
        ↓
delete empty inbox structures
```

Because the target identity selects the Redis inbox, an ACK issued for another target cannot remove somebody else's signal.

## Metadata boundary

Redis may temporarily contain the metadata necessary to establish a direct connection:

- registration challenge identity/key material for at most two minutes;
- active presence candidates for at most two minutes;
- WebRTC signaling envelopes for at most 60 seconds;
- opaque replay markers for five minutes.

It must not contain:

```text
contact display names
contact lists
conversation history
chat message bodies
attachments
long-lived endpoint history
```

WebRTC signaling payloads can inherently include network candidate information. Their TTL is therefore intentionally short and they are deleted on ACK/expiry.

## Redis outage behavior

When Redis is explicitly configured, Dyract treats it as required infrastructure.

Startup:

- the production connection policy must pass;
- Redis must connect and respond to ping;
- otherwise startup fails.

Runtime:

- store failures propagate as request failures;
- Dyract does not silently switch to in-memory state;
- this avoids accepting replays or losing cross-instance signaling/presence semantics during a partial outage.

Production deployment should combine this with appropriate Redis availability, monitoring and controlled failover consistent with the production policy above.

## CI validation

`.github/workflows/ci.yml` runs a dedicated Redis 8 service container and executes `RedisTransientStateTests`.

The suite proves:

- registration challenge created by one store instance is readable by another;
- challenge consume is visible across instances;
- expired challenge fails closed;
- presence published by one instance is visible/removable through another;
- logical presence expiry fails closed;
- replay nonce accepted by one instance is rejected by another;
- replay nonce remains PeerId-scoped;
- signal sent through one instance is fetched through another;
- signal fetch remains non-destructive until ACK;
- ACK through one instance removes state seen by another;
- ACK cannot remove another target's signal;
- expired signals are not returned;
- the 64-item target inbox limit is enforced atomically across instances.

`RedisConnectionPolicyTests` additionally covers development allowance plus production rejection of missing TLS, missing authentication, admin access and missing network-isolation attestation.

The normal core suite still exercises all in-memory fallbacks without requiring Redis.

## Remaining infrastructure work

The repository-side production Redis TLS/authentication/network policy is defined and startup-enforced. Remaining production infrastructure work includes:

- deployment secret management and credential rotation integration;
- privacy-aware metrics/log retention;
- distributed/global edge abuse controls beyond application-layer limiting;
- production STUN/TURN decision and deployment;
- APNs/FCM wake infrastructure;
- backup/recovery policy for durable PostgreSQL metadata.
