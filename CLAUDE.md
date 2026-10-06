# PingBoard — start here

PingBoard is an always-on ping monitor for Windows 11 (WinUI 3, .NET 10, unpackaged and
self-contained). This file is the entry point for any coding assistant or person picking the
project up cold; it is written so that nothing depends on a previous conversation. (It is called
`CLAUDE.md` because Claude Code looks for that name; [`AGENTS.md`](AGENTS.md) points here for other
tools.)

## Read in this order

1. [README.md](README.md) — what it does, and the design rationale behind it.
2. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — the source map, the data flow, and **the
   invariants you must not break**.
3. [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) — build, test, release, and how to check UI changes
   safely.
4. [docs/CONFIGURATION.md](docs/CONFIGURATION.md) — every config key, default and range.
5. [docs/STATUS.md](docs/STATUS.md) — what is **not** verified, known gaps, and decisions already
   made.
6. [docs/MIGRATION.md](docs/MIGRATION.md) — moving a board or the source to another machine.

## Commands

```bash
dotnet build PingBoard.slnx -c Release                                  # must be 0 warnings
dotnet run --project src/PingBoard.Harness -c Release -- --selftest     # must be all passing
dotnet run --project src/PingBoard.App
```

If `dotnet` is not found from an agent shell, it is in `C:\Program Files\dotnet` — prepend that to
`PATH`. CI (`.github/workflows/ci.yml`) runs the first two on every push; keep it green.

## The rules

- **`src/PingBoard.Core` references no UI type, ever.** It is a separate project so this is a
  compile-time fact; warnings are errors there.
- **The UI reads immutable snapshots, never live engine state.**
- **Sleep, a down network and a paused target are never recorded as a target failure.**
- **Nothing unbounded on a probe path.** History is a fixed ring.
- **Saves are atomic**, via `File.Move(overwrite: true)` — never `File.Replace`.
- **This repository is public.** No real hostnames, internal addresses, credentials or personal
  details in code, tests, docs, demo boards or screenshots. Use RFC 5737 addresses
  (`192.0.2.0/24`) and `example.com` for fakes.
- **Runtime files never go in git** (`*.state.ini`, `*.outages.csv`, `pingboard-events.csv*`, a
  `config.ini` at the repo root). They are ignored on purpose.
- **Line endings.** `.gitattributes` stores LF and checks out CRLF. Do not convert files by hand; a
  whole-file diff means something went wrong.

The full list, with the reason behind each, is in ARCHITECTURE.md.

## Definition of done

- `dotnet build PingBoard.slnx -c Release` has 0 warnings, and the self-tests all pass.
- A behaviour change has a self-test for the failure it prevents.
- The docs moved with the code: a new config key in `docs/CONFIGURATION.md`, a user-visible feature
  in the README, a new file or invariant in `docs/ARCHITECTURE.md`, a changed limitation in
  `docs/STATUS.md`. If the self-test count changed, update the places that quote it.
- CI is green after the push.
- Anything you could not verify is **said so**, in the commit and in `docs/STATUS.md` — not
  described as working.

## Hazards that have already bitten

- **Launching the app against a scratch board rewrites `%AppData%\PingBoard\ui-state.ini`**, which
  the installed copy reads on its next start. Back it up and restore it, or use
  `tools/retake-screenshots.ps1` as the model. Details: DEVELOPMENT.md → *The UI-state hazard*.
- **Never stop a process you did not start.** An installed copy may be monitoring a real board.
- **Screenshots are solid black on a locked desktop.** UI Automation still works; capture does not.
- **Do not tag or release casually.** Every installed copy offers a release as an update. Docs-only
  and tooling changes do not need one. The release procedure is in DEVELOPMENT.md.
- **Do not trust a doc over the code.** Where they disagree, the code is right and the doc is a bug
  to fix.