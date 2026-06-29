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

## Groups

Manage groups at **Admin → Groups** (`/admin/groups`, Admin only):

- Create / rename / delete a group.
- Add and remove members.
- Grant and revoke resources/pools to the whole group.

Every member of a group inherits the group's grants. Access is the **union** of direct and group
grants — see [access control](../features/access-control). Group lifecycle, membership, and access
changes are all audited (`GroupCreated/Updated/Deleted`, `GroupMemberAdded/Removed`,
`GroupAccessGranted/Revoked`).

## Federation (not yet available)

External identity providers and AD/LDAP/OIDC group synchronization are **not** implemented yet;
groups are managed inside ZeroVDI. This is a planned roadmap item.

## Related

- [Access control](../features/access-control) · [Roles](../reference/roles) · [Security & MFA](../features/security)
