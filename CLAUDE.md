# CLAUDE.md

Read [`AGENTS.md`](AGENTS.md) — it holds the repository map, the invariants that are easy to break,
and how to validate a change. Everything there applies here.

A few Claude Code specifics on top:

- **Build and test rather than guessing.** `dotnet build KSol.ZeroVDI.sln -c Release` compiles the
  Razor views too, so a broken view is a build error and worth catching before handing work back.
- **Do not syntax-check the browser client and call it verification.** `KSol.ZeroVDI/wwwroot/lib/rdpweb/`
  has no automated coverage; those changes are validated by connecting to a real desktop. Report what
  needs live-testing, against a Windows host (H.264) and a GNOME Remote Desktop host (RemoteFX
  Progressive) — they fail in different ways.
- **Commit only when asked.** The maintainer reviews and commits the working tree himself. When asked
  to commit: never commit to `main` without being told to, and follow the changelog-plus-version rule
  in `AGENTS.md` — every commit updates the changelog and the docs and bumps the version in both
  `KSol.ZeroVDI/package.json` and `KSol.ZeroVDI/KSol.ZeroVDI.csproj`.
- **`dotnet ef` works here** even though the installed tool is a major version ahead of the EF Core
  packages. Migrations go to `KSol.ZeroVDI/Data/Migrations/`.
- **Ask before deleting anything under `Data/`, `capture/` or a `dump/` directory.** Those hold real
  session data and encryption keys, not build output.
