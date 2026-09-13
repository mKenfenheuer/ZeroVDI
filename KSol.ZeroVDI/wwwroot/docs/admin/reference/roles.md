# Roles

ZeroVDI uses ASP.NET Identity roles to gate the admin area and audit surfaces.

| Role | Can do |
|---|---|
| **Admin** | Everything: manage resources, VDI pools, backends, connectors, users, groups, device policy, appearance, recording rules; run the operations checks; force-disconnect sessions; view audit and recordings. |
| **Auditor** | Read-only oversight: view active sessions, the audit log, recordings, and this documentation. Cannot change configuration or force-disconnect. |
| *(standard user)* | No admin access. Connects to the resources/pools they are [granted](../features/access-control). |

## Surface-by-role

| Area | Admin | Auditor | User |
|---|:---:|:---:|:---:|
| Dashboard / connect to granted resources | ✓ | ✓ | ✓ |
| Resources, VDI pools, backends, users, groups (manage) | ✓ | — | — |
| Device policy, recording rules | ✓ | — | — |
| Operations (health, test buttons) | ✓ | — | — |
| Active sessions (view) | ✓ | ✓ | — |
| Force-disconnect a session | ✓ | — | — |
| Audit log | ✓ | ✓ | — |
| Recordings | ✓ | ✓ | — |
| Documentation | ✓ | ✓ | — |

## Related

- [Users & groups](../administration/users-and-groups) · [Access control](../features/access-control)
