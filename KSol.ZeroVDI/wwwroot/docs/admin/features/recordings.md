# Recordings

ZeroVDI can record console sessions for audit. Recordings are **Admin/Auditor-only** and are never
visible to ordinary users. View and play them at **Admin → Recordings** (`/admin/recordings`).
Recording rules (what gets recorded) are managed at **Admin → Recording rules** (Admin only).

## Playback

Recordings play back in the browser. The play page shows **Encrypted** and **Tamper-evident** badges
plus a **Verify** button when those features are enabled.

## Encryption at rest

With `Recording:EncryptAtRest` enabled, track files are encrypted with **AES-256-CTR**:

- CTR mode is **seekable**, so HTTP range requests and player seeking still work — decryption happens
  on the fly when serving (including partial/range responses).
- The key is derived (PBKDF2) from the master keyring passphrase; each file has a random nonce header.
- CTR provides **confidentiality only** — integrity is a separate mechanism (below).

## Tamper-evidence

Each recording carries per-track SHA-256 hashes and an **append-only chain hash**
`H(prevChain | id | trackHashes)` computed over the on-disk bytes at mux time. Verification re-hashes
the files and reports **Intact**, **Tampered**, or **Missing**. The `/admin/recordings/verify/{id}`
action is audited as `RecordingVerified` with a success flag.

## Retention & auto-purge

A background sweep (every `Recording:RetentionSweepHours`, default 6) purges recordings by:

- **Age** — `Recording:RetentionDays` (older than N days), and/or
- **Total size** — `Recording:MaxTotalGB` (oldest-first until under the cap).

Both default to `0` (off). Purges are audited as `RecordingPurged`; manual deletes as
`RecordingDeleted`.

```jsonc
{
  "Recording": {
    "EncryptAtRest": true,
    "RetentionDays": 90,
    "MaxTotalGB": 500,
    "RetentionSweepHours": 6
  }
}
```

The sweep's last run is shown on **Admin → Operations**, along with the recordings' current size and
the free space on that volume.

## Related

- [Audit](audit) · [Sessions](sessions) · [Configuration](../getting-started/configuration)
