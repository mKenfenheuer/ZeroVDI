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

## Host certificate

The host's TLS certificate is pinned on the first successful connection (trust on first use) and shown
on the resource's **Backend & VM** tab with its SHA-256 fingerprint. A later connection that presents a
different certificate is refused until an administrator clicks **Forget pinned certificate** there. See
[Security](security#host-certificate-pinning-trust-on-first-use).

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

### Session brokers and redirection

If the host answers with an RDP **Server Redirection** — what a Windows RD Connection Broker farm or a
load balancer in front of a desktop pool sends to name the machine that should actually serve this
user — ZeroVDI reconnects to that machine, carrying the broker's routing token and any one-time
credentials it handed back. GNOME Remote Desktop's "Remote Login" uses the same mechanism to hand a
session off on the *same* machine, which needs no special handling.

Because the target name arrives from the host rather than from your configuration, it is followed
under rules: loopback, link-local (including the cloud metadata address), multicast and unspecified
addresses are always refused, and `Redirection:AllowedTargetHosts` narrows it to a list of names,
addresses or domain suffixes you name. `Redirection:FollowTargetHost: false` disables cross-host
redirects entirely. Both the followed and the refused case are [audited](audit) (`SessionRedirected`,
`SessionRedirectRefused`).

A redirected leg lands on a machine whose certificate was never pinned for this resource — it is a
different machine — so the [host certificate](#host-certificate) pin is not enforced on that leg. The
broker itself is pinned and is what vouches for the target, which is the trust chain the Windows
client follows too.

## Related

- [Access control](access-control) — who can use a resource.
- [Device policy](device-policy) — central limits that override per-resource console options.
- [VDI pools](vdi-pools) — auto-provisioned resources instead of hand-published ones.
