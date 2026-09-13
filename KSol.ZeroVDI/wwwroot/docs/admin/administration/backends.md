# Backends

A **backend** is a Proxmox VE endpoint that ZeroVDI talks to for power control, IP discovery, and
(for VDI) cloning. Manage them at **Admin → Backends** (`/admin/proxmoxbackends`). You can register
more than one.

## Configuring a backend

Each backend needs:

- A **name** and the Proxmox **host URL**.
- API **token** credentials (token ID + secret). Token auth is preferred over a password.
- **Verify TLS certificate** — on by default. The token that manages every VM travels over this
  connection, so keep it on and give the cluster a certificate the gateway trusts (Proxmox supports
  ACME/Let's Encrypt or your own CA). Turn it off only for a self-signed lab cluster.

The admin dashboard probes every configured backend concurrently and shows online status, total VMs,
and running VMs per backend.

## Required token permissions

For basic publishing (power on/off, read IP): the standard VM read/power permissions.

For [VDI pools](../features/vdi-pools) (cloning), the token additionally needs:

- `VM.Clone`
- `VM.Allocate`
- `VM.Config.*`
- `Datastore.AllocateSpace`

## Templates

`ListVmsAsync` filters templates **out** of the normal VM listing; VDI pool template pickers use a
dedicated template listing. Make sure your template VM lives on the backend you select for the pool.

## Related

- [VDI pools](../features/vdi-pools) · [Resources](../features/resources)
