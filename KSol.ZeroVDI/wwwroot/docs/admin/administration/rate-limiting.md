# Rate limiting

ZeroVDI applies ASP.NET Core rate limiting on top of Identity lockout to slow brute-force and abuse.

## Policies

Two per-IP **fixed-window** policies are applied:

| Policy | Applies to | Default |
|---|---|---|
| `auth` | Identity / login pages | 10 requests / 60 s |
| `ws` | WebSocket relay handshake | 30 requests / 60 s |

Both are tunable in configuration:

```jsonc
{
  "RateLimiting": {
    "Auth": { "PermitLimit": 10, "WindowSeconds": 60 },
    "Ws":   { "PermitLimit": 30, "WindowSeconds": 60 }
  }
}
```

## Account lockout

Independently of rate limiting, **lockout-on-failure** is enabled for login, so repeated bad
passwords lock the account per the configured Identity lockout policy.

## Client IP

Rate limiting keys on the client IP, so make sure your reverse proxy forwards the real client address
(`X-Forwarded-For`) — see [Installation](../getting-started/installation).

## Related

- [Security & MFA](../features/security) · [Audit](../features/audit)
