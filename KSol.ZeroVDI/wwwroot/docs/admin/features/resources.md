# Resources

A **resource** is a single published machine that users can connect to — typically a Proxmox VM, but
it may be any reachable RDP host. Resources are managed at **Admin → Resources** (`/admin/resources`).

## What a resource holds

- **Identity & target:** name, host/address, RDP port, OS type, and the Proxmox backend + VMID it
  maps to (so ZeroVDI can power it on and read its IP).
- **Connection defaults:** a single set of console options (display, clipboard, audio, microphone,
  camera, …) applied when a user connects. This mirrors the in-console options popup.
- **SSO credentials (optional, per user):** stored username/password injected **server-side** at
  connect time so the user is signed straight into Windows. These are encrypted at rest and are
  **never** rendered into the browser.

## Power management

ZeroVDI can start a stopped VM on connect and shut it down on idle:

- **Start on connect:** the readiness flow powers the VM on and waits for it to become reachable
  before opening RDP.
- **Idle shutdown:** the idle reaper can power down resources with no active sessions.
- **Shutdown method:** SSH, IPMI, or Windows (`net rpc`) — configured per resource.

## Connecting

Users with [access](access-control) see the resource on their dashboard and open it in the browser
console. Power state, readiness phase, and (for Remote-Login hosts) reconnection are handled
automatically.

## Related

- [Access control](access-control) — who can use a resource.
- [Device policy](device-policy) — central limits that override per-resource console options.
- [VDI pools](vdi-pools) — auto-provisioned resources instead of hand-published ones.
