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

## Where the grants live

Granting and revoking happen on the two object pages above. There is also a read-only overview of
every direct (user, resource) grant at `/admin/authorizations` — handy for an audit sweep, but it has
no nav link and no edit actions on purpose, so each grant has exactly one place it is managed.

## Federation

Group membership can be driven by your identity provider instead of by hand: the provider's group
claim is matched against ZeroVDI group **names**, and matching groups are joined automatically. Only
memberships federation created are withdrawn again when the directory changes, so hand-made
assignments are never touched. See [Identity federation](identity-federation).

Direct LDAP/Active Directory binding (without an OIDC provider in front) is not implemented.

## Related

- [Users & groups](../administration/users-and-groups) · [Roles](../reference/roles) ·
  [Identity federation](identity-federation)
