# Configuration

ZeroVDI reads configuration from the standard ASP.NET Core sources: `appsettings.json`,
`appsettings.{Environment}.json`, environment variables, and user-secrets in development. This page
covers the most important sections; see the full [configuration keys reference](../reference/configuration-keys)
for everything.

## Secrets & data protection

```jsonc
{
  "DataProtection": {
    // Master passphrase for the AES-GCM keyring that encrypts stored VM credentials,
    // VDI identity secrets, and (optionally) recordings. KEEP THIS SAFE AND BACKED UP.
    "MasterKeyPassphrase": "change-me-to-a-long-random-value"
  }
}
```

> If you lose this passphrase you lose access to every encrypted secret (stored credentials,
> recordings). Store it in your secrets manager, not in source control.

## Multi-factor authentication

```jsonc
{
  "Mfa": {
    "RequireForAll": false,        // require MFA for every user
    "RequiredRoles": ["Admin"]     // or require it only for these roles (secure default)
  }
}
```

See [Security & MFA](../features/security) for how enforcement behaves.

## Public address

```jsonc
{
  "App": {
    // The address your users type. Password-reset and e-mail-change links, and the OpenID Connect
    // redirect URI, are built from this instead of the incoming Host header — which a forged request
    // could otherwise point at someone else's site. Set it in production.
    "PublicBaseUrl": "https://vdi.example.com"
  },
  // Reject requests carrying any other Host header.
  "AllowedHosts": "vdi.example.com"
}
```

Behind a reverse proxy, also list it in `ForwardedHeaders:KnownProxies` so the audit log and rate
limiting see real client addresses instead of the proxy's.

## E-mail

Password resets, e-mail-change confirmations and e-mail MFA codes all go through this. Leave
`Username`/`Password` empty for an unauthenticated relay.

```jsonc
{
  "Smtp": {
    "Host": "smtp.example.com",
    "Port": 587,
    "Username": "",
    "Password": "",
    "FromAddress": "zerovdi@example.com",
    "FromName": "ZeroVDI",
    "UseSsl": true
  }
}
```

Send a test message from **Admin → [Operations](../administration/operations)** once it is set.

## Single sign-on

Optional OpenID Connect federation — off unless `Enabled` is true *and* `Authority` and `ClientId`
are set. See [Identity federation](../features/identity-federation) for the full walkthrough.

```jsonc
{
  "Oidc": {
    "Enabled": true,
    "Authority": "https://login.example.com/realms/zerovdi",
    "ClientId": "zerovdi",
    "ClientSecret": "…",
    "GroupsClaim": "groups",
    "AdminGroups": [ "vdi-admins" ]
  }
}
```

## Rate limiting

Note the **flat** key names — these are what the code reads.

```jsonc
{
  "RateLimiting": {
    "AuthPermitLimit": 30, "AuthWindowSeconds": 60,  // Identity/login pages, per IP
    "WsPermitLimit": 45,   "WsWindowSeconds": 60     // WS relay handshake, per IP
  }
}
```

## Sessions

```jsonc
{
  "Sessions": {
    "MaxConcurrentPerUser": 0   // 0 = unlimited
  }
}
```

## Recordings

```jsonc
{
  "Recording": {
    "EncryptAtRest": true,   // AES-256-CTR using a key derived from the master passphrase
    "RetentionDays": 0,      // 0 = keep forever
    "MaxTotalGB": 0          // 0 = no size cap; otherwise oldest-first purge
  }
}
```

See [Recordings](../features/recordings).

## RDP host certificates

```jsonc
{
  "HostCertificates": {
    // Tofu  — pin on first connection and refuse a changed certificate (default)
    // Audit — pin and log mismatches, but allow the connection (useful while rolling out)
    // Off   — accept any certificate
    "Mode": "Tofu"
  }
}
```

See [Security & MFA](../features/security#host-certificate-pinning-trust-on-first-use).

## Session brokers

```jsonc
{
  "Redirection": {
    "FollowTargetHost": true,          // follow a redirect that names a different host
    "AllowedTargetHosts": []           // optional allow-list of names, addresses or .domain suffixes
  }
}
```

See [Resources → Session brokers and redirection](../features/resources).

## Backends

Proxmox backends can be configured in the database via the admin UI — see
[Backends](../administration/backends). They are not required in `appsettings.json`.

## Next steps

- [Configuration keys reference](../reference/configuration-keys)
- [Backends](../administration/backends)
