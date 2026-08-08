# Production Redis security policy

Dyract uses Redis only for short-lived directory coordination state:

```text
registration challenges
signed-request replay nonces
presence leases
WebRTC signaling inboxes
shared application rate-limit counters
```

Redis is **not** a chat/message-history store.

## Startup policy enforced by the application

When `ConnectionStrings:Redis` is configured and the ASP.NET Core environment is `Production`, Dyract validates the parsed StackExchange.Redis options before creating the connection.

Required:

```text
ssl=true
password/ACL secret present
allowAdmin=false
```

An insecure configured production Redis connection causes application startup to fail rather than silently degrading to an insecure or process-local security mode.

Development/test environments intentionally permit the local CI/developer Redis service without TLS/authentication.

## Example production shape

Use protected deployment configuration rather than committing connection secrets:

```text
ConnectionStrings__Redis=redis.internal.example:6380,ssl=true,password=<secret>,abortConnect=true
```

The literal secret must come from the platform secret store/environment integration, not source control, container image layers, issue trackers or normal application logs.

## Why TLS is mandatory

Redis carries short-lived security/reachability coordination data. Without transport encryption, a network observer positioned between Dyract and Redis could potentially observe or alter:

- registration challenge state;
- replay-protection state;
- current presence candidates;
- WebRTC signaling payloads;
- rate-limit counters.

These datasets are deliberately short lived, but they are still security-sensitive operational state.

## Why authentication is mandatory

An unauthenticated Redis endpoint would allow any network principal able to reach it to interfere with shared directory state.

Dyract currently supports the normal StackExchange.Redis password/ACL-secret connection-string path. If deployment later adopts certificate-only or managed-identity/token authentication, the application policy must be extended deliberately before that mode is accepted in Production.

Do not disable the startup check merely to accommodate a different authentication mechanism.

## Administrative commands

The Dyract application connection does not require Redis administrative commands. `allowAdmin=true` is rejected in Production to reduce accidental privilege.

Operational administration should use a separately controlled operator identity/channel.

## Network policy still required outside the application

Application code cannot prove that a Redis service is privately routed or correctly firewalled.

Production deployment must additionally enforce and validate:

1. Redis is not exposed to the public internet;
2. inbound access is limited to intended Dyract directory workloads/operator paths;
3. outbound directory access is limited to the configured Redis service where platform controls support it;
4. DNS/private-endpoint routing resolves to the intended service;
5. server certificate validation is not disabled;
6. Redis credentials are separately scoped/rotatable from PostgreSQL credentials;
7. backup/persistence settings match the fact that most Redis state is intentionally ephemeral;
8. monitoring does not export raw Redis keys/values into broad telemetry;
9. operator/admin access is audited and separated from the application credential;
10. failover/restart behavior is tested for the selected managed/self-hosted topology.

This network deployment validation remains an open PLAN item until a production hosting environment is chosen.

## Metadata rules

Redis key names intentionally avoid directly exposing raw PeerIds or client IPs where practical:

- presence target identity is represented by a SHA-256-derived token in the key;
- signaling target hash tags use a SHA-256-derived token;
- replay keys derive opaque hashes from requester + nonce;
- global rate-limit client partition keys hash the client address before storage.

Presence/signaling **values** still necessarily contain the short-lived protocol data required for reachability/negotiation. TTLs and application validation remain mandatory.

Never add message bodies, attachments, contact names, private keys or recovery material to Redis.

## Validation coverage

Repository tests prove:

- Development accepts the local unauthenticated CI Redis configuration;
- Production rejects missing TLS;
- Production rejects missing authentication secret;
- Production rejects administrative access;
- Production accepts a TLS + authenticated + non-admin option set;
- the existing Redis 8 integration job validates challenge/replay/presence/signaling/rate-limit behavior against a real Redis service.

## Current acceptance boundary

The **application-side production Redis TLS/authentication policy** is implemented.

The broader **network/private-endpoint/firewall deployment policy** remains open because it depends on the chosen production environment and must be validated there rather than inferred from source code.
