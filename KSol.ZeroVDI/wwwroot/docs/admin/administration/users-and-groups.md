# Users & groups

ZeroVDI uses ASP.NET Identity for accounts and adds first-class **groups** for scalable access
control.

## Users

Manage users at **Admin → Users** (`/admin/users`):

- Create and delete accounts.
- Assign [roles](../reference/roles).
- See per-user **MFA status**.
- Grant resources/pools directly, and view group-inherited access on the user's page.

User create/delete and role changes are [audited](../features/audit).

### Account security

The **Account** tab of a user's page carries the help-desk actions:

| Action | What it does |
|---|---|
| **Set password** | Sets a new password immediately, running the normal password rules. Optionally signs the user out everywhere and drops their open desktops (on by default). |
| **E-mail a reset link** | Sends the user a one-time link to choose their own password. Uses `App:PublicBaseUrl`. |
| **Sign out everywhere** | Invalidates every issued cookie and disconnects live console sessions — without changing the password. |
| **Unlock** | Clears a lockout caused by failed sign-in attempts (shown only while the account is locked). |
| **Disable / Enable account** | Blocks sign-in and ends open sessions while keeping the account, its grants and its stored credentials intact. The reversible alternative to deleting someone who has left. |

A disabled or force-signed-out user loses access within about two minutes at the latest — the auth
cookie is re-checked against their security stamp on that interval — and their live console sessions
are cut immediately.

### Lockout protection

ZeroVDI refuses the moves that would leave nobody able to administer it:

- The **last administrator** cannot have the Admin role removed, be deleted, or be disabled.
- **You cannot remove your own Admin role**, delete your own account, or disable yourself. Ask another
  administrator.

A role change now also takes effect immediately rather than at the end of the cookie's validation
interval, in both directions — a revoked administrator loses the admin area at once.

If a deployment somehow ends up with no administrator at all, the next start restores the Admin role
to `Bootstrap:AdminEmail` (and logs loudly that it did). That is a recovery path only; it no longer
re-grants the role on every start, which used to undo a deliberate demotion.

## Groups

Manage groups at **Admin → Groups** (`/admin/groups`, Admin only):

- Create / rename / delete a group.
- Add and remove members.
- Grant and revoke resources/pools to the whole group.

Every member of a group inherits the group's grants. Access is the **union** of direct and group
grants — see [access control](../features/access-control). Group lifecycle, membership, and access
changes are all audited (`GroupCreated/Updated/Deleted`, `GroupMemberAdded/Removed`,
`GroupAccessGranted/Revoked`).

## Federation

ZeroVDI can sign users in through an OpenID Connect provider and drive group membership from the
directory's group claim — see [Identity federation](../features/identity-federation).

Two things are worth knowing here:

- A directory group only matters once a ZeroVDI group of the **same name** exists. Creating that group
  is how you opt a directory group in; everything else in the directory is ignored.
- Memberships created by federation are withdrawn again when the directory stops asserting them.
  Memberships you add by hand on the group's Manage page are never touched by a sign-in, so the two
  can be mixed safely.

Direct LDAP/Active Directory binding (without an OIDC provider in front) is not implemented.

## Related

- [Access control](../features/access-control) · [Roles](../reference/roles) · [Security & MFA](../features/security) · [Identity federation](../features/identity-federation)
