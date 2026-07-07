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

## Rate limiting

```jsonc
{
  "RateLimiting": {
    "Auth": { "PermitLimit": 10, "WindowSeconds": 60 },  // Identity/login pages, per IP
    "Ws":   { "PermitLimit": 30, "WindowSeconds": 60 }   // WS relay handshake, per IP
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

## Backends

Proxmox backends can be configured in the database via the admin UI — see
[Backends](../administration/backends). They are not required in `appsettings.json`.

## Next steps

- [Configuration keys reference](../reference/configuration-keys)
- [Backends](../administration/backends)
