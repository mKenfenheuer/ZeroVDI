# Rate limiting

ZeroVDI applies ASP.NET Core rate limiting on top of Identity lockout to slow brute-force and abuse.

## Policies

Two per-IP **fixed-window** policies are applied:

| Policy | Applies to | Built-in default | Shipped `appsettings.json` |
|---|---|---|---|
| `auth` | Identity / login pages | 10 requests / 60 s | 30 / 60 s |
| `ws` | WebSocket relay handshake | 30 requests / 60 s | 45 / 60 s |

Both are tunable in configuration (flat keys — these are the names the code reads):

```jsonc
{
  "RateLimiting": {
    "AuthPermitLimit": 30, "AuthWindowSeconds": 60,
    "WsPermitLimit": 45,   "WsWindowSeconds": 60
  }
}
```

## Account lockout

Independently of rate limiting, **lockout-on-failure** is enabled for login, so repeated bad
passwords lock the account per the configured Identity lockout policy.

## Client IP

Rate limiting keys on the client IP, so make sure your reverse proxy forwards the real client address
(`X-Forwarded-For`) **and** that the proxy is listed in `ForwardedHeaders:KnownProxies` /
`KnownNetworks` (see [Configuration keys](../reference/configuration-keys)). If it is not, every user
appears to come from the proxy's address and shares one bucket: 30 login-page requests a minute from
the whole company lock the login page for everyone.

## Related

- [Security & MFA](../features/security) · [Audit](../features/audit)
