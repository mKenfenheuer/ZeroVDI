# VDI pools

A **VDI pool** is a provisioning policy: *"members of these groups (or these users) each get a
desktop cloned from template **T** on backend **B**."* Instead of hand-publishing one resource per
machine, you define a pool once and ZeroVDI clones desktops on demand. Pools are managed at
**Admin → VDI Pools** (`/admin/vdi-pools`).

## Assignment models

| Kind | Behavior |
|---|---|
| **Dedicated** | Each assigned user gets their *own* persistent desktop, cloned on first connect and reused thereafter. |
| **Floating** | *Not yet available.* Planned: users lease a desktop from a shared free pool; on disconnect it is destroyed and re-cloned so the next user gets a pristine machine. The option is shown but cannot be saved. |

Cloning can be **Full** or **Linked**, per pool. A linked clone needs shared storage when the target
node differs from the template's node.

## Lifecycle (lazy provisioning)

Desktops are provisioned **on first connect**, surfaced through the normal readiness UI with a
*Creating your desktop…* step before *Starting*:

1. User opens the pool → `VdiProvisioningService` allocates a VMID (serialised across pools, skipping
   ids already claimed by in-flight or failed clones) and starts the clone task. The task id is stored
   on the instance.
2. ZeroVDI waits for the Proxmox clone task to finish, stamps the gateway binding id into the VM notes,
   then applies identity (below).
3. A backing resource + instance record is created and the desktop boots.
4. The existing start/IP/RDP flow connects the user.

Provisioned desktops take part in the same **idle policy** as discovered VMs: with *Auto-suspend idle
VMs* enabled on the backend, a clone with no session for the backend's idle timeout is suspended,
stopped or hibernated per the backend's pause action, and its power state is refreshed by the status
service like any other resource.

## Reconciliation

A background loop (`VdiReconcileService`, every 2 minutes) keeps instance records and Proxmox in step
when the connect-time path could not finish the job — a gateway restart mid-clone, an unreachable
backend, a clone task that outlived its wait, a VM deleted in the Proxmox UI:

- **Interrupted provisioning** — if the clone task finished, provisioning is completed (notes stamp,
  identity, Ready); if it failed or is unknown, the instance is marked *Failed* with the reason.
- **Failed / deprovisioning instances** — the VM is destroyed through the notes-verified path (never
  by VMID alone), then the rows are removed. A clone that finished after its wait is stamped first so
  it can be verified, so a slow clone no longer leaks a VM.
- **Ready instances whose VM vanished** — marked *Failed* with the reason; the user gets a fresh
  desktop on the next connect.
- **Missing notes stamp** — retried, because an unstamped clone cannot be destroyed safely.

Every action is audited as `VdiInstanceReconciled` (with `action` = `resumed-provisioning`,
`marked-failed`, `removed` or `vm-missing`). The pool page shows each instance's state and last error.
Deprovisioning a desktop or deleting a pool is refused while a session is active on it.

## Identity & customization

Each pool chooses how the clone gets its identity (`IdentityMode`):

- **CloudInit** — inject `ciuser` / `cipassword` / SSH keys before first boot. On Windows this is
  applied by **cloudbase-init** in the template. The clone's hostname is its VM name (from the name
  pattern).
- **None** — no customization; the template generalises itself.
- **GuestAgent** — *not yet implemented* (rename / domain join through the QEMU guest agent). The
  option and the domain-join fields are present but cannot be saved.

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
`VM.Config.*` (including `VM.Config.Options` for the notes stamp), `VM.PowerMgmt`, `VM.Monitor` and
`VM.Audit` (guest-agent queries), and `Datastore.AllocateSpace`. The template VM must exist on the
chosen backend and have the QEMU guest agent installed and enabled — the readiness flow discovers the
clone's IP through it.

## Related

- [Backends](../administration/backends) · [Access control](access-control) · [Resources](resources)
