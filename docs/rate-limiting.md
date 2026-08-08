# Directory rate limiting and abuse controls

Dyract uses layered request admission for the central directory. Rate limiting is an availability/abuse mitigation; it is not an authentication mechanism and it does not replace signed request verification.

## Current layers

### 1. ASP.NET Core process-local limiter

Every directory instance keeps the existing fixed-window ASP.NET Core limiter:

```text
registration endpoints    30 requests/minute/client partition
peer operations          240 requests/minute/client partition
```

This provides a cheap local guard even when Redis is unavailable/not configured in zero-setup development mode.

By itself, a per-process limiter is insufficient for horizontal deployment: four instances could otherwise admit roughly four times the intended aggregate request rate for one client.

### 2. Redis global fixed-window limiter

When `ConnectionStrings:Redis` is configured, Dyract also registers `RedisGlobalRequestLimiter` behind:

```text
IGlobalRequestLimiter
```

The global limiter runs before the normal ASP.NET Core endpoint limiter. The effective production admission path is therefore:

```text
request
   |
   +-- request-body/shape outer checks
   |
   +-- Redis shared fixed-window limit
   |
   +-- ASP.NET Core local fixed-window limit
   |
   +-- signed protocol/authentication validation
   |
   +-- endpoint operation
```

The limits intentionally match the current local policy:

```text
Registration       30 / minute
PeerOperations    240 / minute
```

The shared layer covers `/api/v1` requests. Registration challenge/register use the registration bucket; other API v1 operations use the peer-operation bucket. `/health` is outside this application-level client limiter and should be protected appropriately by deployment/network policy.

## Redis key privacy

The limiter does not place the raw client partition string in Redis keys.

It computes:

```text
SHA-256(client partition key)
```

and stores counters using a shape equivalent to:

```text
dyract:ratelimit:<category>:<sha256>:<fixed-window-bucket>
```

This is metadata minimization, not anonymity. The application/network stack still necessarily sees the source address used to derive the partition.

## Atomicity and horizontal scaling

Each request increments its Redis bucket through one Lua script:

```text
INCR counter
if first value:
    apply TTL
return counter
```

Because the increment is atomic in Redis, two directory instances cannot independently consume separate copies of the same logical allowance.

A counter is accepted while:

```text
counter <= category permit limit
```

and rejected afterward until the next fixed window.

The Redis key is kept slightly longer than the one-minute logical window to avoid abrupt physical-key churn affecting retries/clock boundaries. The logical bucket number, not Redis key survival alone, determines the active window.

## Retry behavior

A rejected global request returns:

```text
HTTP 429 Too Many Requests
```

with the existing Dyract API error:

```text
rate_limited
```

and a `Retry-After` value derived from the remaining fixed-window duration.

The local ASP.NET limiter remains able to reject first/independently as defense in depth.

## Client partition source and reverse proxies

The current application partition is based on:

```text
HttpContext.Connection.RemoteIpAddress
```

Dyract deliberately does **not** parse arbitrary `X-Forwarded-For` values inside the limiter. A client can forge those headers unless a trusted reverse proxy/load balancer has already sanitized and converted them into the framework's effective remote address.

For production behind a proxy/CDN/load balancer:

1. configure trusted proxy networks/addresses explicitly;
2. enable ASP.NET Core forwarded-header processing only for those trusted hops;
3. ensure the edge removes/rebuilds untrusted forwarded-address headers;
4. verify `RemoteIpAddress` resolves to the intended client partition before relying on address-based limits.

Misconfigured forwarding can either collapse all users into the proxy address (self-DoS) or let attackers choose arbitrary partitions (limit bypass).

This deployment trust must be tested/documented before public production use.

## What this does not solve

Application rate limiting cannot stop all volumetric attacks because traffic must reach the application/Redis layer first.

Production still requires an edge/network abuse strategy such as:

- load balancer/CDN/WAF request limits;
- connection/concurrency limits;
- DDoS protection appropriate to the hosting environment;
- Redis connection/pool limits;
- endpoint-specific anomaly monitoring;
- privacy-aware operational metrics;
- possibly authenticated-peer quotas in addition to IP/network partitions.

Therefore the plan should distinguish:

```text
[x] shared application-level multi-instance rate limiting
[ ] production edge/global abuse-control deployment and validation
```

## Tests

Redis integration tests exercise the global limiter through two limiter instances connected to the same Redis service.

Properties covered:

- the registration allowance is shared across instances;
- request 31 in the same registration window is rejected after 30 accepts even when requests alternate between instances;
- a new logical window receives a new allowance;
- different client partitions are independent;
- registration and peer-operation categories are independent;
- Redis key names do not expose the raw client partition;
- the no-Redis implementation preserves zero-setup behavior;
- server DI selects `RedisGlobalRequestLimiter` when Redis is configured and `NoOpGlobalRequestLimiter` otherwise.

The dedicated Redis 8 CI job is the acceptance gate for the shared behavior.
