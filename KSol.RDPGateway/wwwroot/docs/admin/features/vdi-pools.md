# VDI pools

A **VDI pool** is a provisioning policy: *"members of these groups (or these users) each get a
desktop cloned from template **T** on backend **B**."* Instead of hand-publishing one resource per
machine, you define a pool once and ZeroVDI clones desktops on demand. Pools are managed at
**Admin → VDI Pools** (`/admin/vdi-pools`).

## Assignment models

| Kind | Behavior |
|---|---|
| **Dedicated** | Each assigned user gets their *own* persistent desktop, cloned on first connect and reused thereafter. |
| **Floating** | Users lease a desktop from a shared free pool; on disconnect it is **destroyed and re-cloned** so the next user gets a pristine machine. |

Both kinds are configurable per pool. Cloning can be **Full** or **Linked**, also per pool.

## Lifecycle (lazy provisioning)

Desktops are provisioned **on first connect**, surfaced through the normal readiness UI with a
`Provisioning` phase before `Starting`:

1. User opens the pool → `VdiProvisioningService` allocates a VMID and clones the template.
2. ZeroVDI waits for the Proxmox clone task to finish, then applies identity (below).
3. A backing resource + instance record is created and the desktop boots.
4. The existing start/IP/RDP flow connects the user.

Floating leases are returned on disconnect; the VM is destroyed and a reconcile loop refills the
free pool.

## Identity & customization

Each pool chooses how the clone gets its identity (`IdentityMode`):

- **CloudInit** — inject `ciuser` / `cipassword` / hostname / SSH keys before first boot. On Windows
  this is applied by **cloudbase-init** in the template.
- **GuestAgent** — (fallback) rename / domain-join via the Proxmox guest agent.
- **None** — no customization.

### Generated credentials

Enable **Generate credentials** on a pool to derive a **unique** random username + password per
desktop owner (instead of a shared `ciuser`/`cipassword`):

- Username = sanitized owner name + a short hash of their account ID.
- Password = 20 characters meeting Windows complexity, generated with a CSPRNG.

The generated pair is injected via cloud-init **and** stored as the owner's SSO credentials so the
gateway logs them straight in. As with all stored credentials, they are encrypted at rest and
**never shown in the browser**.

## Access

Pool access uses the same direct-or-group model as resources via
[`ResourceAccessService`](access-control). A dedicated clone is **not** independently grantable —
access flows through the pool assignment and ownership.

## Proxmox requirements

The API token used by the backend needs clone-related permissions: `VM.Clone`, `VM.Allocate`,
`VM.Config.*`, and `Datastore.AllocateSpace`. The template VM must exist on the chosen backend.

## Related

- [Backends](../administration/backends) · [Access control](access-control) · [Resources](resources)
