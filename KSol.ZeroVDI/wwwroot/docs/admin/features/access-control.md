# Access control

ZeroVDI grants access to resources and VDI pools through a single authorization gate,
`ResourceAccessService`. **Effective access = direct grants ∪ group grants.**

## Two ways to grant

| Grant type | Meaning |
|---|---|
| **Direct** | A specific user is granted a specific resource/pool. |
| **Via group** | A [group](../administration/users-and-groups) is granted; every member inherits it. |

A user can reach a resource if *either* path grants it. The dashboard, console, connection
authorization, and the WebSocket relay all consult the same service, so there is no way to reach a
resource you aren't authorized for.

## Where access is managed

Access is edited from **two object pages**, each showing both direct and via-group provenance:

- The **user** page — everything this user can reach.
- The **resource / pool** page — everyone who can reach this object.

Both use the shared access-picker UI. There is no standalone authorizations CRUD screen.

## Personal state vs. access

- **Access** can come from a group alone — a group-only user needs no per-user row to connect.
- **Personal state** (stored SSO credentials, per-user connection preferences) lives on the
  per-user row. When a group-only user saves credentials, a per-user row is created lazily *after*
  the access check.

## Auditing

Every grant and revoke is recorded in the [audit log](audit): `GroupAccessGranted/Revoked`,
direct grant/revoke, group membership changes, and group lifecycle events.

## Not yet available

External identity federation (AD/LDAP/OIDC group sync) is **not** implemented yet — groups are
managed inside ZeroVDI.

## Related

- [Users & groups](../administration/users-and-groups) · [Roles](../reference/roles)
