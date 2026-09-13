# Working on ZeroVDI

Notes for AI coding agents (and new contributors — most of this is not agent-specific). Read
[`CONTRIBUTING.md`](CONTRIBUTING.md) too; this file is the map and the landmines.

## What this is

A self-hosted VDI platform: an ASP.NET Core gateway that brokers Proxmox VE desktops and relays RDP
into a browser. The RDP client is implemented here from the wire up — there is no FreeRDP, no
`guacd`, no mstsc anywhere in the path. That is the single most important thing to understand before
changing anything under `RDP/` or `wwwroot/lib/rdpweb/`: the protocol code is load-bearing, subtle,
and cannot be reasoned about from first principles alone. Its comments carry the section numbers of
the Microsoft specifications ([`Spec/README.md`](Spec/README.md)) and, more importantly, the reasons
behind decisions that look wrong until you know why.

## Layout

| Path | What lives there |
|---|---|
| `KSol.ZeroVDI/Controllers/` | MVC controllers. `RdpWebSocketController` is the console relay entry point and the most security-sensitive file in the repo. |
| `KSol.ZeroVDI/RDP/` | The gateway half: the RDP client (`RdpHostConnection`, `CredSspClient`, `NtlmClient`), the relay (`RdpRelaySession`), the passive decoder used for recording (`RdpSession`, `RdpChannels`, `Zgfx`, `Mppc`), the Proxmox broker (`VdiProvisioningService`, `VdiReconcileService`), and the policy services. |
| `KSol.ZeroVDI/wwwroot/lib/rdpweb/` | The browser RDP client: `protocol.js` (connection sequence, channels), `rdpgfx.js` (RDPEGFX surfaces and frames), `decode-worker.js` (H.264 via WebCodecs, off the main thread), `progressive.js` / `clear.js` (codecs), `client.js` (canvas, input, session state), `input/` (keymap, scancodes). |
| `KSol.ZeroVDI/Models/`, `Data/` | EF Core entities and the DbContext. Migrations in `Data/Migrations/`. |
| `KSol.ZeroVDI/Views/` | Razor views, Tailwind classes, `_AdminLayout` / `_UserLayout`. |
| `KSol.ZeroVDI/wwwroot/docs/` | Documentation **served by the running app** (Admin → Docs, and the user guide). Not a side note: a stale page here is a user-facing bug. |
| `KSol.ZeroVDI.Connector/` | The connector agent (separate binary, .NET 10). |
| `KSol.ZeroVDI.Tests/` | xUnit tests. |

## Invariants — break these and something real goes wrong

**Never destroy a Proxmox VM by `(node, VMID)` alone.** Always go through
`VdiProvisioningService.SafeDestroyInstanceVmAsync`, which verifies the VM's `ksol-rdpgw-id` note
first. A VMID is reused by Proxmox; acting on a stale one has already wiped a machine that was not
ours.

**Stored credentials never reach the browser.** Not the username, not the domain, not the password,
not a hash. The gateway injects them into the RDP handshake server-side; the browser sends
placeholders so its frames are well-formed. Anything that puts a credential into a view model, a
JSON response, or a log is wrong.

**Authorization goes through `ResourceAccessService`.** Effective access is the union of direct
grants and group membership. There are four gates (dashboard, console page, connect endpoint,
WebSocket relay) and they must all ask the same service. A new entry point needs the same check.

**Recordings are Admin/Auditor only.** They contain other people's screens. They are never visible to
the user who was recorded.

**The relay hot path must not block.** `RdpStreamRecorder.Feed` copies and enqueues; the structural
decode runs on a background task. Blocking the server→client pump back-pressures the RDP host, which
pauses mid-frame and stalls the session.

**Per-channel decoder state is keyed by direction.** Both directions feed one `RdpSession` in wire
order; keying reassembly, MPPC or ZGFX contexts by channel id alone corrupts the stream (this was a
real bug — the browser's frame acknowledgements landed inside the host's pending GFX frame).

**The CSP has no `'unsafe-inline'` for scripts.** Inline `<script>` elements get a nonce
automatically (`TagHelpers/ScriptNonceTagHelper`). Inline `on*=` attributes cannot be covered by a
nonce at all — put the listener in `wwwroot/js/site.js` instead.

## Conventions

**Every change updates the changelog, the docs, and the version.** The changelog is
`KSol.ZeroVDI/wwwroot/docs/admin/changelog.md` (`CHANGELOG.md` at the root symlinks to it). Bump the
patch version in **both** `KSol.ZeroVDI/package.json` and `KSol.ZeroVDI/KSol.ZeroVDI.csproj` — they
must agree, and the operations page reports it. The repository sits on the *next* version, so a
change lands under a version number that has not shipped yet.

**Comments explain why, not what.** The existing code is dense with rationale: why an ack is deferred
until a frame has actually painted, why a scan is anchored to TPKT framing instead of a byte marker,
why a lockout expresses "disabled". Match that. A comment restating the code is noise; the one
recording a constraint you discovered is the most valuable line in the file.

**Match the surrounding style** rather than importing your own. Razor views use Tailwind utility
classes and the shared partials (`_ListToolbar`, `_ConfirmModal`, `_AccessPicker`). The JavaScript
client is ES5-flavoured prototype code, not modules.

**Audit anything an administrator does**, via `IAuditLogger`, with the actor, the target and
non-sensitive detail. Never put a secret in `Detail`.

## Validating a change

```bash
dotnet build KSol.ZeroVDI.sln -c Release      # Razor views compile here too — view errors are build errors
dotnet test  KSol.ZeroVDI.sln -c Release
```

- **There is no browser test harness.** Changes to `wwwroot/lib/rdpweb/` cannot be validated
  automatically — they are verified by connecting to a real host. Say plainly in your summary what you
  changed and what needs testing against which kind of host (Windows sends H.264, GNOME Remote Desktop
  sends RemoteFX Progressive, and they fail differently).
- **Do not run `node --check` or similar syntax-only checks** on the client files and present the
  result as validation. It proves nothing about a protocol change and the maintainer live-tests anyway.
- **Add tests where they are possible**: parsers, decoders against hostile input, policy decisions,
  pure predicates. `KSol.ZeroVDI.Tests` shows the shape — each test says in its name and its comment
  what breaks in production if the assertion fails.
- **Database changes**: `dotnet ef migrations add <Name> --project KSol.ZeroVDI`. Check the generated
  migration before committing it; a `DropColumn` that was not intended is easy to miss.

## Things that look like bugs but are not

- **HTTP/1.1 is forced on Kestrel.** NTLM is connection-bound and breaks over HTTP/2.
- **The GFX surface canvases use `alpha: false`.** Without it the previous frame ghosts through.
- **Arrow keys are sent with the extended flag** and share make codes with the numeric keypad. That is
  the protocol, not a mistake.
- **A far-future lockout means "disabled".** ASP.NET Identity has no enabled flag.
- **The bootstrap admin re-grant only fires when no administrator exists at all.** It used to run on
  every start, which silently undid a deliberate demotion.

## Reporting your work

Say what you changed, what you verified and how, and what you could not verify. If you found a real
problem outside the scope you were given, mention it — do not silently fix it, and do not silently
leave it.
