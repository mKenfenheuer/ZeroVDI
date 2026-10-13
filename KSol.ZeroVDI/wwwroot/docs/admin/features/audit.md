# Audit log

ZeroVDI keeps a structured, append-only audit trail of security-relevant events. View it at
**Admin → Audit log** (`/admin/audit`), available to **Admin** and **Auditor** roles, with filtering
and paging.

## What is recorded

Each `AuditEvent` captures the actor, target, category, action, client IP, timestamp, and a JSON
detail payload. The logger is scoped and **never throws** — auditing failures can't break the action
being audited.

Recorded events include (non-exhaustive):

- **Authentication:** login success / failure / lockout, two-factor success / failure (with the
  method), MFA enrollment required.
- **MFA:** enabled / disabled / reset, for the authenticator and for e-mail.
- **Federation:** account provisioned from the identity provider (`ExternalUserProvisioned`), groups
  and roles synced from its claims (`ExternalGroupsSynced`, `ExternalRoleSynced`), sign-in refused
  (`ExternalLoginRejected`, with the reason).
- **Sessions:** connect, disconnect (with a `forced` flag), rejected by the concurrency limit
  (`SessionRejectedLimit`), refused by [device policy](device-policy) (`SessionRejectedPolicy`),
  followed or refused a broker redirect (`SessionRedirected`, `SessionRedirectRefused`).
- **Credentials:** SSO credentials stored / cleared.
- **Users & access:** user create / delete, role change, direct and group access grant / revoke,
  group lifecycle and membership changes.
- **Account administration:** password set by an administrator (`PasswordResetByAdmin`), reset link
  sent (`PasswordResetLinkSent`), signed out everywhere (`UserSignedOutEverywhere`), unlocked
  (`UserUnlocked`), disabled / enabled (`UserDisabled`, `UserEnabled`).
- **Recordings:** viewed (opening the player), verified (with a success flag), deleted, purged by
  retention.
- **Resources / VDI:** host certificate pinned / mismatch / reset, VDI instance reconciled
  (`VdiInstanceReconciled` with an `action`), pool created / updated / deleted, assignment granted /
  revoked, instance deprovisioned.
- **Administration:** device policy updated, appearance updated, and the
  [operations](../administration/operations) checks — `SmtpTested`, `OidcDiscoveryTested`,
  `BackendsTested` — recorded whether they passed or failed.

Creating, editing or deleting a resource, backend, connector or recording rule is **not** currently
audited; the grants and policy that govern access to them are.

## Categories

Every event carries one category, and the viewer filters on it: **Authentication**, **Session**,
**Credential**, **Resource**, **Authorization** (access grants, groups, roles), **User**,
**Backend**, **Recording**, and **Admin** (policy, appearance, the operations checks).

## Retention

The audit trail is append-only and is **not** automatically purged — there is no update or delete path
in the application, which is what makes it a compliance record. Apply database-level retention if your
policy requires it. The current event count is shown on
**[Admin → Operations](../administration/operations)**.

## Related

- [Sessions](sessions) · [Recordings](recordings) · [Security & MFA](security)
