# ZeroVDI — VDI Broker / Lifecycle / Infrastructure Audit

Scope: `KSol.ZeroVDI/RDP/*` broker + infra files, VDI/Proxmox/Connector models, controllers, `KSol.ZeroVDI.Connector`, DbContext, Program.cs, and the admin docs. All paths relative to `/Users/max/Git/ksol-rdpgw/KSol.ZeroVDI/` unless prefixed. Read-only; every claim cites file:line.

---

## 1. Broker model as implemented

**Pool kinds.** `VdiPoolKind.Dedicated|Floating` exists (`Models/VdiPool.cs:7-13`) and is editable (`Controllers/VdiPoolsController.cs:144`, `Views/VdiPools/Manage.cshtml:90-91` exposes `MaxSize`), but **Floating is a hard stub**: `RDP/VdiProvisioningService.cs:70-71` returns `"Floating pools are not yet available." // step 6`. `Leased`/`Returning` states (`Models/VdiInstance.cs:13-15`), `LastLeasedUtc` (`:57`), `FloatingResetMode` (`VdiPool.cs:35-40`, single value `DestroyRecreate`) and `MaxSize` (`:90`) have **zero** consumers outside the model/controller/view (grep). The admin doc `wwwroot/docs/admin/features/vdi-pools.md:13,27-28` describes floating leasing + "a reconcile loop refills the free pool" as if shipped — **docs overstate the product**. No reconcile loop exists anywhere (grep "reconcil" hits only comments: `VdiProvisioningService.cs:143`, `ProxmoxSyncService.cs:106`, `VdiPool.cs:38`, `VdiPoolsController.cs:285`, `ApplicationDbContext.cs:92`).

**Assignment scoping.** `VdiPoolAssignment` = user XOR group (`Models/VdiPoolAssignment.cs:19-27`), entitlement = direct ∪ group via `ResourceAccessService.CanAccessPoolAsync` (`RDP/ResourceAccessService.cs:59-67`). A pool id doubles as a connect target: `GetAuthorizedResourceAsync` synthesizes a non-persisted `RDPResource` for a pool (`:96-114`), and the dashboard hides the pool card once the user owns any non-Failed/non-Deprovisioning instance (`Controllers/HomeController.cs:66-71,88`).

**Lazy clone.** No pre-provisioning. Clone happens inside the connect readiness run (`RDP/VdiResourceResolver.cs:80-86` → `EnsureResourceForUserAsync`).

**VdiInstance state machine (as actually driven):**
| From | To | Driver |
|---|---|---|
| (new) | Provisioning | rows created before clone (`VdiProvisioningService.cs:142-169`) |
| Provisioning | Ready | clone task OK + notes + identity (`:192-193`) |
| Provisioning | Failed | clone start/wait failure (`:175-180` → `FailAsync :404-410`) |
| Ready | Deprovisioning | VM vanished at connect (`:96-102`) |
| any | Deprovisioning | admin Deprovision (`VdiPoolsController.cs:298`) then row deleted (`:333`) |
| Failed/Deprovisioning | (deleted) | `ClearStaleInstanceAsync` on next provision (`:385-402`) |
| Leased / Returning | — | **never entered** |

There is **no transition out of Provisioning on crash/restart** — see Bug #1.

**End-to-end connect sequence (dedicated pool, first connect):**
1. Dashboard card → `Home/Console/{poolId}` (`HomeController.cs:114-188`); auto-connect is pre-armed when the pool uses CloudInit+GenerateCredentials (`:180-186`).
2. Browser preflight `POST /connect/{poolId}/begin` (`Controllers/ConnectController.cs:60-70`) → `ConnectionReadinessService.Begin` spawns `Task.Run(RunReadinessAsync(..., CancellationToken.None))` (`RDP/ConnectionReadinessService.cs:45-64`).
3. `RunReadinessAsync` detects pool id (`VdiResourceResolver.cs:80`) → `EnsureResourceForUserAsync` (`:82`): fast path if a Ready instance exists and `VmStillExistsAsync` (`VdiProvisioningService.cs:74-79`); else per-(pool,user) `SemaphoreSlim` (`:82-83`) → `ProvisionDedicatedAsync`: clear stale row (`:122`) → backend (`:124`) → `ListTemplatesAsync` (`:129`) → `GetNextVmIdAsync` (`:134`) → insert `RDPResource`(Source=VdiClone)+`VdiInstance` (`:144-169`) → `CloneAsync` (`:172`) → `WaitForTaskAsync` ≤ max(300 s, 3×StartTimeout) (`:178-179`) → `SetNotesAsync` stamps `ksol-rdpgw-id` (`:184`) → `ApplyIdentityAsync` (`:190`: cloud-init ciuser/cipassword/sshkeys; generated creds also stored as the owner's SSO row `:317,334-348`) → Ready.
4. Resolver continues on the clone: `ListVmsAsync` for node self-heal (`VdiResourceResolver.cs:188-194`) → `GetStatusAsync`/`EnsureRunningAsync` (`:197-208`) → guest-agent poll ≤ max(60,StartTimeout) (`:212-233`) → IP poll + connector-aware RDP port probe ≤ max(60,StartTimeout) (`:237-258`) → persist IP/Running (`:270-272`) → `Ready(host,port,ResourceId=clone)` (`:279-280`).
5. Browser polls `/connect/{id}/status`, then opens `/ws/rdp/{poolId}`: `RdpWebSocketController.cs:92` authorizes, **re-runs the whole readiness** via `_resolver.ResolveAsync` (`:103`, `VdiResourceResolver.cs:52-58`), rebinds `id` to the clone (`:121`), loads SSO row on the clone and decrypts server-side (`:129-137`), accepts socket, enforces `Sessions:MaxConcurrentPerUser` (`:152-163`), picks transport (`:214`), builds `RdpRelaySession` with presupplied creds (`:216-226`), stamps activity (`:235`), registers session (`:245`), runs relay.
6. Disconnect: `finally` → `SessionTracker.Remove`, `OnDisconnectedAsync` (only stamps `LastActivityUtc`, `VdiResourceResolver.cs:290-302`), audit (`RdpWebSocketController.cs:262-269`). **No instance state change, no lease return, no logoff detection, no idle handling for clones** (see §3).

---

## 2. Provisioning correctness

**Clone API.** `nodes/{tplNode}/qemu/{tpl}/clone` with `newid,name,full,target,storage` (`RDP/ProxmoxClient.cs:185-197`). Task polled on the template node (`:221-257`) — correct (Proxmox runs clone tasks on the source node). Risks: `storage` is only valid for full clones and `target`≠source with Linked requires shared storage — neither combination is validated in `VdiPoolsController.Update` (`:145-149`); Proxmox will reject at connect time and the user sees "Failed to start cloning" (`VdiProvisioningService.cs:175-176`).

**VMID allocation race.** `GetNextVmIdAsync` is a pure read (`cluster/nextid` or a walk of `cluster/resources`, `ProxmoxClient.cs:131-161`) with **no reservation**. Two users provisioning concurrently in different (pool,user) locks get the same id; the second `CloneAsync` fails (Proxmox "already exists") → Failed row → user must retry. The Failed row points at the *other* user's live VMID; `SafeDestroyInstanceVmAsync` correctly refuses it via notes mismatch (`:249-256`), so the invariant holds, but there is no retry-with-next-id.

**Name collisions.** `Sanitize(Expand(NamePattern))` (`:139,415-428`) — Proxmox permits duplicate names; `{n}` is optional so two users whose names sanitize identically (`max.k`/`max_k`) collide silently. No DB uniqueness on clone names.

**Template validation.** Create checks `TemplateVmId > 0` only (`VdiPoolsController.cs:75-76`); Update checks nothing (`:143`). Provisioning re-checks existence via `ListTemplatesAsync` (`:129-132`) — good — but never checks that the template has a cloud-init drive, guest agent enabled, or is on `TargetStorage`.

**Identity.**
- `CloudInit`: sets `ciuser/cipassword/sshkeys` (`ProxmoxClient.cs:335-365`). **Hostname is never set** — `HostnamePattern` is stored/edited (`VdiPoolsController.cs:157`, `Manage.cshtml:105-106`) but has no consumer in `RDP/` (grep). Proxmox derives the cloud-init hostname from the VM name, so hostname == sanitized `NamePattern`, not `HostnamePattern`. Doc `vdi-pools.md:34` claims hostname injection.
- `GuestAgent`: **stub** — `LogDebug("not yet applied")` (`VdiProvisioningService.cs:302-303`). `DomainName/DomainOu/DomainJoinUser/ProtectedDomainJoinPassword` are stored (`VdiPoolsController.cs:161-170`) and never used. `GuestExecAsync`/`GetGuestExecStatusAsync` exist (`ProxmoxClient.cs:372-428`) with no caller. Doc `vdi-pools.md:36` presents domain-join as available.
- Generated creds: `VdiCredentialGenerator` (`RDP/VdiCredentialGenerator.cs:31-57`) is sound (CSPRNG, class guarantees). Risk to verify: Proxmox stores `cipassword` as a SHA-512 crypt hash; cloudbase-init's cloud-config user handling may not accept hashed passwords on Windows, which would make GenerateCredentials silently produce unusable SSO logins on Windows templates. Not provable from this repo — flag for a live test.
- Identity failure is **non-fatal**: `SetCloudInitAsync` false only logs (`:328-330`); instance still goes Ready with a shared/unknown password.

**IP discovery.** Guest agent only (`ProxmoxClient.cs:502-536`); first non-loopback IPv4 not starting `127.`/`169.` — a Windows host with Hyper-V/WSL/Docker vNICs (172.x) can report the wrong address first, and the retry loop re-asks the same question every 2 s (`VdiResourceResolver.cs:241-258`) until timeout. No DHCP-lease/static/DNS fallback; a template without qemu-guest-agent can never connect.

**Readiness probing / timeouts.** Guest-agent wait + IP wait are each bounded by max(60, StartTimeout) (`:212,237`) → worst case ~4 min before failure; on failure `PowerState=Stopped` is written although the VM is running (`:263`). RDP probe goes through `ConnectorPathSelector.IsReachableAsync` (`:326-331`) which **caches negative results for 60 s** (see Bug #3).

**Concurrency limits.** None — no cap on simultaneous clones per backend/storage; every entitled user hitting Connect at 9 am fires N parallel full clones.

**Retries.** None at any layer (clone, task poll, notes, cloud-init, HTTP).

**Cleanup of half-provisioned VMs.**
- Clone-wait timeout → `Failed` but the Proxmox task continues; VM finishes unstamped (notes only after wait success `:184`). `ClearStaleInstanceAsync` then deletes the tracking rows *without* touching the VM and says so (`:378-383`) → **leaked VM**; `ProxmoxSyncService` later adopts it as a plain Proxmox resource because no notes id / no VdiInstance matches (`RDP/ProxmoxSyncService.cs:108,137-147`).
- Process restart mid-clone → row stuck in `Provisioning` forever (Bug #1).
- `SetNotesAsync` return value ignored (`:184`) → an unstamped-but-Ready clone can **never** be safely destroyed (`SafeDestroy` → `NotFound`) → leaked on deprovision/pool delete.

**ksol-rdpgw-id invariant.** `SafeDestroyInstanceVmAsync` exists (`VdiProvisioningService.cs:223-284`): refuses without `RDPResourceId` (`:227-232`), verifies notes on the recorded node (`:238-248`), falls back to a cluster-wide notes scan (`:261-272`, N HTTP calls), swallows errors → `Error`. **Every destroy call site goes through it**: `ProxmoxClient.DestroyVmAsync` is invoked only from `VdiProvisioningService.cs:244,267`; `SafeDestroyInstanceVmAsync` is called from `VdiPoolsController.cs:199` (pool delete) and `:306` (deprovision). No other `DeleteVmAsync`/`DestroyVmAsync` callers (grep). Invariant holds. Minor: `DestroyVmAsync` maps a null status (backend error) to "already gone → true" (`ProxmoxClient.cs:292-294`), so a transient API failure logs "destroyed verified clone".

**Orphan reconciliation.** `ProxmoxSyncService` deliberately skips clones (`:103-109,229-239`) and prunes only `Source==Proxmox` (`:200-204`). Nothing reconciles `VdiInstance` ↔ inventory: no stuck-Provisioning sweep, no Failed cleanup, no adoption of stamped clones whose rows were lost, no detection of clones deleted in Proxmox (only at next connect via `VmStillExistsAsync :356-376`, which matches by VMID only, so a recycled VMID is a false positive).

---

## 3. Capacity & power

- **No spare/buffer/pre-provisioned instances, no max pool size enforcement** (`MaxSize` unused), no per-user/per-pool quotas, no storage/CPU capacity checks, no capacity reporting beyond the instance list on Manage (`VdiPoolsController.cs:104-121`).
- **IdleReaperService semantics** (`RDP/IdleReaperService.cs`): 5-min scan (`:13`), per-*resource* (not per instance/pool). Idle = no `SessionTracker` session for the resource (`:93`) AND `LastActivityUtc` older than backend `IdleTimeoutHours` (`:94-95`, min 1 h) with a full-window startup grace (`:162-170`). Action = backend `PauseAction` Suspend/Stop/Hibernate via `PauseAsync` (`:113`, `ProxmoxClient.cs:476-482`) — never destroy. Manual resources: hardcoded 24 h (`:16,132`) via `ResourceShutdownService`.
- **VDI clones are never reaped**: the Proxmox scan filters `r.Source == ResourceSource.Proxmox` (`:82`), manual scan `Manual` (`:127`); `VdiClone` (`Models/RDPResource.cs:17`) matches neither. Once started, a clone runs until an admin acts. Same omission in `ResourceStatusService.ResolveStateAsync` (`RDP/ResourceStatusService.cs:100`) so a clone's suspended/paused state is never observed.
- Floating reset: not implemented.
- Reaper side-effect: deletes DB rows for VMs whose notes carry the exclude marker (`:98-108`) — discovery's job leaking into the reaper.

---

## 4. Template lifecycle

Nothing exists: no template versioning, no record on `VdiInstance` of which template/snapshot it came from (`Models/VdiInstance.cs` has no such field), no rolling re-image, no maintenance mode, no "pause pool", no snapshot-based reset (grep for snapshot/rollback/maintenance/reimage finds no broker code). Changing `TemplateVmId` (`VdiPoolsController.cs:143`) silently affects only future clones; existing dedicated desktops drift forever.

---

## 5. Physical / manual resources

- **WoL** (`RDP/WakeOnLan.cs:8-22`): limited broadcast `255.255.255.255:9` only — no directed-subnet broadcast, no VLAN/interface choice; from a container this usually stays on the bridge. Malformed MAC throws (`:12`), caught upstream (`VdiResourceResolver.cs:123-124`). Readiness then waits a hardcoded 120 s (`:140`).
- **IPMI** (`RDP/IpmiClient.cs`): `ipmitool` subprocess, argv built safely, password via `IPMITOOL_PASSWORD` + `-E` (`:54-75`) — good. No process timeout (`:77-82`); `chassis power soft` for off (`:14-15`).
- **SSH** (`RDP/SshCommandService.cs:11-32`): SSH.NET with key from DB; **no host-key verification** (no `HostKeyReceived`), no passphrase support, `ConnectAsync(CancellationToken.None)` and synchronous `RunCommand` with no timeout (`:19-20`).
- **Windows** (`RDP/ResourceShutdownService.cs:86-119`): `net rpc shutdown -U user%password` puts the **password on the command line** (`:103-104`), visible in `ps`/`/proc` — inconsistent with the IPMI care taken. No timeout.
- Method inference for legacy rows (`:70-80`) is sensible. Secrets are `CredentialProtector`-wrapped (`Models/RDPResource.cs:141,146,153`).

---

## 6. Connectors

- **Enrollment**: admin mints a one-time token (`Controllers/ConnectorsController.cs:47`), encrypted at rest (`Data/ApplicationDbContext.cs:149`); agent redeems at `POST /agent/register` (`RDP/ConnectorAgentEndpoints.cs:22-44`) — linear scan + `string.Equals` (not constant-time, `:32-33`), no rate limit; long-lived token stored as SHA-256 (`RDP/ConnectorTokens.cs:22-23`). Regenerate invalidates (`ConnectorsController.cs:89-102`).
- **Framing/protocol**: JSON control frames `open-tcp|probe|ack|ping` (`RDP/ConnectorProtocol.cs`), duplicated byte-for-byte in the agent (`KSol.ZeroVDI.Connector/ConnectorMessage.cs`). **No version field** → no compat negotiation; drift is silent (`ConnectorHub.cs:88-90` just logs unknown types).
- **Auth/TLS**: bearer header **or `?token=` query** (`ConnectorAgentEndpoints.cs:94-103`, agent `Agent.cs:73-76`) — token lands in proxy/access logs. TLS enforced agent-side by refusing non-https (`Agent.cs:26-27,70-71`); **no mTLS, no cert pinning**. Server-side nothing prevents plaintext if someone terminates TLS in front.
- **Heartbeat**: agent pings every 20 s (`Agent.cs:115-126`). The hub ignores pings and its comment says "LastSeen is refreshed by the endpoint layer" (`ConnectorHub.cs:86-87`) — **false**: `LastSeenUtc` is written only on connect and on close (`ConnectorAgentEndpoints.cs:53-55,61-64`). Online status is purely in-memory `_connections` (`ConnectorHub.cs:27-30`). No server-side liveness timeout: a half-open control socket stays "online" until TCP notices; `OpenTcpAsync` then fails only after 15 s (`:129-130`).
- **Path selection**: RTT race direct vs every enabled/in-scope/online connector, cached 60 s per (host,port) (`RDP/ConnectorPathSelector.cs:22,48-57,85-114`). `AllowScope` (host literal or CIDR, `:141-169`) is applied **only** in the race; forced connectors bypass it (`:76-77`) and Proxmox-API tunnelling (`ProxmoxClient.cs:39-44`) never consults it. The agent enforces nothing — any host:port the gateway names is dialled (`Agent.cs:162-173`). A compromised gateway = open TCP pivot into every connector network.
- **Redundancy/failover**: none per resource — one `ForcedConnectorId` (`Models/RDPResource.cs:92`), and a backend has one `ConnectorId` (`Models/ProxmoxBackend.cs:54`). A mid-session connector drop kills the tunnel (`ConnectorHub.cs:73-74` faults everything); recovery relies on the browser auto-reconnect.
- **Reconnect**: agent exponential backoff 1→30 s (`Agent.cs:47-62`); a reconnect replaces the old control channel and faults its pending requests (`ConnectorHub.cs:41-44`).
- **What the agent sees**: raw TCP bytes; the gateway layers TLS+CredSSP on top (`RDP/IHostTransport.cs:5-8`, `ProxmoxClient.cs:37-38`), so RDP and Proxmox traffic are ciphertext at the agent and **no credentials transit the agent**. Good.
- Data-channel attach is scoped to the authenticated connector (`ConnectorHub.cs:99-108`), chanId is a random GUID — fine.

---

## 7. Proxmox client

- **Auth**: `PVEAPIToken` header (`ProxmoxClient.cs:51-52`); secret encrypted at rest (`ApplicationDbContext.cs:148`).
- **TLS**: `VerifyTls` **defaults to false** (`Models/ProxmoxBackend.cs:46`) and disables validation wholesale (`ProxmoxClient.cs:33-36`). No CA pinning option.
- **Connection handling**: a new `SocketsHttpHandler`+`HttpClient` per call, disposed after (`:28-54`, every method `using var client`) → no pooling, TCP+TLS handshake per request; sync and SafeDestroy make O(N) such calls.
- **Multi-node/cluster**: single `Host` URL per backend; cluster-wide calls go via `/cluster/resources` (`:64`), per-VM calls to `/nodes/{node}/…`. If the configured node is down the whole backend is down (no alternate endpoints).
- **Task polling**: 2 s poll of `tasks/{upid}/status` until `stopped`, `exitstatus=="OK"` (`:221-257`). No backoff, no jitter.
- **Error mapping**: every failure collapses to null/false/empty with a log line (e.g. `:84-88,156-160,199-204`); 403 (permissions) vs 5xx vs timeout are indistinguishable to callers, so users get "Contact an administrator" for everything.
- **Behaviour when Proxmox is down**: `ListVmsAsync` returns empty (`:87`) → sync early-returns without pruning (`ProxmoxSyncService.cs:83`, good); `VmStillExistsAsync` assumes exists (`VdiProvisioningService.cs:363,370-375`, good); resolver fails with a generic message after `GetStatusAsync` null → `EnsureRunningAsync` tries `StartAsync` anyway (`ProxmoxClient.cs:490-495`).
- **Kerberos config purpose**: `KerberosRealm/KdcHost` on the backend are for the *console's* CredSSP/NLA to the VMs, not for Proxmox (`Models/ProxmoxBackend.cs:82-94`, consumed at `RdpWebSocketController.cs:403-406`). They are **not in either `[Bind]` list nor copied in Edit** (`Controllers/ProxmoxBackendsController.cs:58,80,88-102`) and appear in no view → dead config unless set in the DB.
- **Required permissions**: documented at `wwwroot/docs/admin/administration/backends.md:17-26` (`VM.Clone, VM.Allocate, VM.Config.*, Datastore.AllocateSpace`; "standard VM read/power" for basics). Missing from the doc: `VM.Monitor`/`VM.Audit` for guest-agent calls, `VM.PowerMgmt`, and the `purge`/`destroy-unreferenced-disks` delete path (`ProxmoxClient.cs:267`).

---

## 8. Session tracking

- In-memory `ConcurrentDictionary` (`RDP/SessionTracker.cs:39`), explicitly single-instance (`:34-35`); **does not survive restart** — the reaper's startup grace (`IdleReaperService.cs:166`) is the only mitigation.
- Per-user cap `Sessions:MaxConcurrentPerUser` (`RdpWebSocketController.cs:152-163`), continuation legs exempt. Each WebSocket = a new session record (`:245`); there is no "reconnect to existing" — a browser reconnect adds a second record until the old relay's socket dies, so a cap of 1 can falsely reject the reconnect.
- Admin disconnect: `SessionsController.cs:37` → `ForceDisconnect` cancels the linked CTS (`SessionTracker.cs:86-91`, `RdpWebSocketController.cs:251-252`). Works.
- Session ↔ instance is not linked: `VdiInstance` never learns about sessions; `LastActivityUtc` is stamped only at connect/disconnect (`VdiResourceResolver.cs:288-302`), not during a session, so a 30-hour session followed by a disconnect is "idle" only from that moment — fine — but a session cut by a gateway restart leaves the old `LastActivityUtc`.

---

## 9. Data model critique

- `RDPResource.VdiInstanceId` is a bare string with **no FK** (`Models/RDPResource.cs:112`) while `VdiInstance.RDPResourceId` is FK SetNull (`ApplicationDbContext.cs:119-120`) → dangling pointers both ways after partial deletes.
- No unique index on `(ProxmoxBackendId, ProxmoxVmId)` for either `RDPResource` or `VdiInstance`; sync has explicit duplicate-collapse code because of it (`ProxmoxSyncService.cs:111-135`). No index on `RDPResource.Source/ProxmoxVmId` or `VdiInstance.ProxmoxVmId/State`, though sync queries `VdiInstances` per VM (`:236-238`).
- `VdiPoolAssignment` XOR is enforced only in the controller (`VdiPoolsController.cs:241-245`), not by a check constraint.
- `VdiInstance` lacks operational fields: clone UPID, provisioning started/failed timestamps, last error, template/snapshot version, last-session time → no way to resume/expire a provisioning after restart.
- `VdiPool→ProxmoxBackend` is Restrict (`ApplicationDbContext.cs:97-98`) but `ProxmoxBackendsController.DeleteConfirmed` (`:121-130`) does not catch the resulting `DbUpdateException` → 500. `RDPResource.ProxmoxBackendId` has no configured delete behaviour → default client-set-null orphans discovered resources on backend delete.
- No concurrency tokens anywhere; `ResourceStatusService` rewrites `PowerState` for every resource every 15 s (`RDP/ResourceStatusService.cs:72-92`) racing the resolver's `Starting/Running` writes.
- SQLite (`Program.cs:33`) with four background writers + requests: acceptable for a lab, single-writer contention and no HA path for production.
- Migrations: 30 in `Data/Migrations` over ~3 weeks including add/remove churn (`AddResourceProtocol` → `RemoveVncSpiceProtocol`, `AddBackendDefaultVncSpicePorts` then removal). Filtered unique index on `(PoolId, OwnerUserId)` (`ApplicationDbContext.cs:115-116`) is correct for SQLite partial indexes. Recommend squashing before 1.0.

---

## 10. Concurrency & failure

- **Double-click double-clone**: prevented in-process by the (pool,user) semaphore (`VdiProvisioningService.cs:82-83`) plus the readiness job de-dup (`ConnectionReadinessService.cs:40-45`). Not safe across gateway instances (no DB lock/row claim).
- **Two users, same VMID**: unprotected (§2).
- **Reaper/sync vs provisioner**: no conflict because both ignore clones — at the cost of no lifecycle at all (§3).
- **Pool delete during an in-flight provision**: Delete sees the Provisioning row, `SafeDestroy` → notes null → cluster scan → `NotFound` → rows deleted (`VdiPoolsController.cs:197-222`); the clone completes, is stamped with a now-deleted resource id, then the provisioner's `SaveChangesAsync` on the deleted row throws → orphan VM which sync later adopts *as* that stale GUID (`ProxmoxSyncService.cs:139-147`).
- **Deprovision with live session**: no session check (`VdiPoolsController.cs:290-341`); `DestroyVmAsync` hard-stops the VM (`ProxmoxClient.cs:296-309`) under the user.
- **Hosted services**: all three catch and log (`IdleReaperService.cs:52-59`, `ProxmoxSyncService.cs:50-54`, `ResourceStatusService.cs:56-60`) — a throw does not kill them. On-demand `SyncBackendAsync` (`ProxmoxBackendsController.cs:140`) can run concurrently with the timer on the same backend → duplicate rows (self-healed next run).
- **Cancellation**: the preflight and relay both pass `CancellationToken.None` (`ConnectionReadinessService.cs:52`, `VdiResourceResolver.cs:54`), so a user closing the tab cannot abort a clone (good for consistency) but nothing else can either; readiness work is bounded only by internal deadlines.
- **Idempotency**: `SetNotes` is idempotent; clone is not; `Deprovision` restores prior state on `Error` (`:307-314`) — good.
- **Relay double-resolve**: `/ws/rdp` re-runs the full readiness (`RdpWebSocketController.cs:103`) right after the preflight succeeded → 3–6 extra Proxmox calls per connect and a second 60 s negative-cache window (Bug #3).

---

## 11. Concrete bugs

1. **Stuck-in-Provisioning is unrecoverable** — `VdiProvisioningService.cs:74-104` skips only Failed/Deprovisioning; `ClearStaleInstanceAsync :387-389` clears only those two; after a gateway restart mid-clone the row stays `Provisioning`, the next connect inserts a second row and hits the unique `(PoolId, OwnerUserId)` index (`ApplicationDbContext.cs:115`) → `DbUpdateException` → "unexpected error" forever; meanwhile the dashboard shows the clone card (`HomeController.cs:66-68` excludes only Deprovisioning/Failed).
2. **VDI clones are never idle-reaped or status-polled** — `IdleReaperService.cs:82` / `:127`, `ResourceStatusService.cs:100` filter on `Proxmox`/`Manual`; `VdiClone` (`RDPResource.cs:17`) is excluded.
3. **Negative path caching stalls readiness** — `ConnectorPathSelector.cs:51-56` caches a `null` path for 60 s; the resolver's 2–3 s probe loops (`VdiResourceResolver.cs:148,163,253`) therefore see "unreachable" for a minute after the first miss, wasting most of the 120 s budgets and adding up to 60 s to every cold start.
4. **`HostnamePattern` is dead** — stored (`VdiPoolsController.cs:157`), never applied (no reference in `RDP/`); doc `vdi-pools.md:34` says hostname is injected.
5. **GuestAgent identity + domain join are stubs** — `VdiProvisioningService.cs:302-303`; `Domain*` fields (`VdiPool.cs:121-128`) unused; doc `vdi-pools.md:36` advertises them.
6. **Floating pools are a stub but selectable and documented** — `VdiProvisioningService.cs:70-71`; `vdi-pools.md:13,27`.
7. **Unchecked notes stamp** — `VdiProvisioningService.cs:184` ignores `SetNotesAsync`'s bool; an unstamped clone goes Ready and becomes undestroyable (`SafeDestroy` → NotFound) and thus a guaranteed leak on deprovision.
8. **Clone-timeout leak** — `WaitForTaskAsync` false (`:179-180`) → Failed, but the Proxmox task keeps running; `ClearStaleInstanceAsync :392-397` later deletes rows and explicitly abandons the VM.
9. **Kerberos backend fields unsettable** — absent from `ProxmoxBackendsController.cs:58,80` bind lists and Edit copy (`:88-102`), no view; consumed at `RdpWebSocketController.cs:403-406`.
10. **`LastSeenUtc` never refreshed by heartbeat** — `ConnectorHub.cs:86-87` says the endpoint does it; `ConnectorAgentEndpoints.cs:53,64` only write on connect/close.
11. **Resolver marks a running VM Stopped on probe timeout** — `VdiResourceResolver.cs:263`.
12. **`DestroyVmAsync` treats API failure as success** — `ProxmoxClient.cs:292-294` (null status → true) → `SafeDestroy` logs "destroyed verified clone" (`VdiProvisioningService.cs:245-247`).
13. **Windows shutdown password on argv** — `ResourceShutdownService.cs:103-104`.
14. **SSH without host-key verification / timeout** — `SshCommandService.cs:18-20`.
15. **Backend delete with pools → unhandled 500** — `ProxmoxBackendsController.cs:121-130` vs Restrict FK (`ApplicationDbContext.cs:97-98`).
16. **`VerifyTls` default false** — `ProxmoxBackend.cs:46`.
17. **Pool Update has no validation** — `VdiPoolsController.cs:141-153`: VMID range order, `TemplateVmId>0`, `Port`, `MaxSize`, Linked+`TargetStorage`/`TargetNode` combos all unchecked.
18. **VMID allocation without reservation** — `ProxmoxClient.cs:131-161` + `VdiProvisioningService.cs:134` (race → spurious Failed row).

---

## 12. Prioritized gap list (vs Citrix MCS / Horizon Instant Clones / AVD autoscale / Kasm)

**Critical**
- Reconciler/state-machine driver (Bug #1, #7, #8): a hosted `VdiReconcileService` that expires Provisioning rows (persist the UPID and resume `WaitForTaskAsync`), retries the notes stamp, destroys Failed clones via `SafeDestroy`, and detects out-of-band deletions. Every competitor has this as the core of the broker.
- Idle/power lifecycle for clones (Bug #2): include `VdiClone` in reaper + status service; add per-pool idle action (suspend/stop/destroy for floating), logoff detection via guest agent, and session-end hooks on `VdiInstance`.
- Floating pools + lease return + MaxSize enforcement (Bug #6) or remove the option and the doc claims until built.

**High**
- Negative-cache fix in `ConnectorPathSelector` (Bug #3) and stop double-resolving in the relay.
- Identity completion: apply `HostnamePattern`, implement GuestAgent rename/domain-join (`GuestExecAsync` is already there), verify cloudbase-init hashed-password behaviour, fail provisioning when identity fails (or mark instance "NeedsAttention").
- Pre-provisioning / buffer / power-on-boot-ahead: AVD autoscale and Horizon keep N spares; ZeroVDI's first connect = full clone + boot (minutes).
- Per-backend concurrency limiter for clones and a VMID reservation (claim row + retry-next-id).
- Proxmox client hygiene: pooled `HttpClient` per backend, distinguish 401/403/404/5xx, `VerifyTls` default true with CA option, surface Kerberos fields (Bug #9).
- Connector hardening: mTLS or at least drop the query-string token, enforce `AllowScope` on forced/API paths and agent-side, protocol version in `hello`, server-side heartbeat timeout, multiple connectors per resource with failover.

**Medium**
- Template lifecycle: record template VMID+snapshot per instance, "re-image on next logoff", pool maintenance/pause flag that blocks new provisions.
- Session model: persistent session table (survives restart, HA-ready), reconnect-to-existing instead of new record, instance-level last-session tracking.
- Safety around admin actions: refuse/confirm deprovision or pool delete while a session is live; handle backend delete FK error.
- Manual resource robustness: SSH host-key pinning + timeouts, `net rpc` password via `-A` authfile or env, directed-broadcast WoL, process timeouts for ipmitool/net.
- Data model: FK on `RDPResource.VdiInstanceId`, unique `(BackendId, VmId)`, indexes on `Source`, `State`, `ProxmoxVmId`; concurrency token on `RDPResource.PowerState`.

**Low**
- Docs truthfulness pass (`vdi-pools.md:13,27-28,34,36`, `connectors.md:87`), document full token permission set, squash migrations pre-1.0, name-collision handling with `{n}`, IP-selection heuristics for multi-NIC guests, `DestroyVmAsync` null-status mapping (Bug #12), reaper's exclude-row deletion moved to sync.
