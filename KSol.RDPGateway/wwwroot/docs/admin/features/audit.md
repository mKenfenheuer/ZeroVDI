# Audit log

ZeroVDI keeps a structured, append-only audit trail of security-relevant events. View it at
**Admin → Audit log** (`/admin/audit`), available to **Admin** and **Auditor** roles, with filtering
and paging.

## What is recorded

Each `AuditEvent` captures the actor, target, category, action, client IP, timestamp, and a JSON
detail payload. The logger is scoped and **never throws** — auditing failures can't break the action
being audited.

Recorded events include (non-exhaustive):

- **Authentication:** login success / failure / lockout, two-factor success/failure (with method).
- **MFA:** enrollment required, enabled/disabled/reset (authenticator and email).
- **Sessions:** connect, disconnect (with a `forced` flag), rejected-by-limit.
- **Credentials:** SSO credentials stored / cleared.
- **Users & access:** user create/delete, role change, direct and group access grant/revoke, group
  lifecycle and membership changes.
- **Device policy:** policy updated.
- **Recordings:** verified (with success flag), deleted, purged by retention.
- **VDI:** pool created/updated/deleted, assignment granted/revoked, instance lifecycle.

## Categories

Events are grouped by category (authentication, session, credential, user, access, policy, recording,
VDI, …) so you can filter the viewer down to what you need.

## Retention

The audit trail is append-only and is **not** automatically purged. Apply database-level retention if
your policy requires it.

## Related

- [Sessions](sessions) · [Recordings](recordings) · [Security & MFA](security)
