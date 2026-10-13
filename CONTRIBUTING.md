# Contributing

Pull requests are welcome. A few things worth knowing before you spend time on one.

## Licensing

ZeroVDI is source-available, not open source — see [`LICENSE`](LICENSE). By submitting a
contribution you grant KSol.IT the right to ship it as part of the Software (Section 4 of the
licence). You may modify the code to prepare a contribution; running a modified build in production
needs a separate licence, so if you need a local change, **ask** rather than forking it privately:
**maximilian.kenfenheuer@ksol.it**.

## Before you start

Open an issue first for anything larger than a bug fix. The protocol code in particular has a lot of
hard-won behaviour behind it, and a change that looks like a simplification often is not — the
comments explaining *why* something is done the awkward way are load-bearing.

## Working on it

```bash
dotnet build KSol.ZeroVDI.sln -c Release
dotnet test  KSol.ZeroVDI.sln -c Release
dotnet run   --project KSol.ZeroVDI
```

- **Match the surrounding style.** Comments here explain *why*, not *what*; a comment restating the
  code is noise, and one recording a non-obvious constraint is the most valuable line in the file.
- **Add a test** when the logic can be tested without a live desktop — see
  [`KSol.ZeroVDI.Tests/`](KSol.ZeroVDI.Tests/) for what that looks like. Protocol decoders, policies
  and parsers all can be. Rendering and relay behaviour mostly cannot; say so in the PR and describe
  what you tested by hand, against which hosts.
- **Update the docs and the changelog** with the change. The documentation under
  `KSol.ZeroVDI/wwwroot/docs/` is served by the app, so a stale page is a user-facing bug, and
  `wwwroot/docs/admin/changelog.md` is the changelog (`CHANGELOG.md` at the root is a symlink to it).
- **Bump the version** in `KSol.ZeroVDI/package.json` *and* `KSol.ZeroVDI/KSol.ZeroVDI.csproj` — they
  must agree; the operations page reports it.
- **Database changes** need an EF migration (`dotnet ef migrations add <Name> --project KSol.ZeroVDI`).

## Things that will get a PR sent back

- Weakening a security control without saying so: re-adding `'unsafe-inline'` to the CSP, skipping the
  certificate pin, bypassing `ResourceAccessService` for an authorization check, logging a secret.
- Destroying a VM by `(node, VMID)` alone. Always go through `SafeDestroyInstanceVmAsync`, which
  verifies the `ksol-rdpgw-id` note first. This has eaten a VM before.
- Sending a stored credential to the browser. The gateway injects them server-side, always.
- A new inline `<script>` without checking it still runs under the nonce-based CSP, or a new inline
  `on*=` handler (a nonce cannot cover attributes — put the listener in `wwwroot/js/site.js`).

## Reporting security issues

Privately, please — see [`SECURITY.md`](SECURITY.md).
