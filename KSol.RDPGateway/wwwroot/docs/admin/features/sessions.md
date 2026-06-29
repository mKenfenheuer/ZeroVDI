# Sessions

ZeroVDI tracks every live console session in an in-memory registry (`SessionTracker`). Admins and
Auditors can view live sessions at **Admin → Active sessions** (`/admin/sessions`); Admins can
force-disconnect.

## What a session records

Each `ActiveSession` captures the user, target resource, host, client IP, and start time, plus a
cancellation token wired to the relay loop.

## Force-disconnect

An Admin can terminate any session immediately. The relay's run-token is linked to the session, so
disconnecting aborts the byte loop and tears the connection down. The action is audited as
`SessionForceDisconnected`.

## Concurrency limits

`Sessions:MaxConcurrentPerUser` caps how many simultaneous sessions a single user may hold
(`0` = unlimited). Continuation legs (e.g. a redirect reconnect) are exempt so they don't count
against the user mid-connect. Rejected attempts are audited as `SessionRejectedLimit`.

```jsonc
{ "Sessions": { "MaxConcurrentPerUser": 2 } }
```

## Limitation

The registry is **in-memory**, so it is accurate for a single instance only. Cross-node session
limits and a shared view require the high-availability work on the roadmap.

## Related

- [Audit](audit) · [Recordings](recordings)
