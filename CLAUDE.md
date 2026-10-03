# PingBoard — notes for contributors and coding agents

Read `README.md` first: it carries the design rationale and the WinUI 3 pitfalls. This file holds
only what you need in order not to break things.

## Layout, and the one hard rule

- `src/PingBoard.Core` — the engine. **It references no UI type, ever.** That is enforced by it
  being a separate project, and warnings are errors there.
- `src/PingBoard.App` — the WinUI 3 front end (unpackaged, self-contained, .NET 10).
- `src/PingBoard.Harness` — a headless driver and the self-test suite.

## Commands

```bash
dotnet build PingBoard.slnx -c Release                                  # must be 0 warnings
dotnet run --project src/PingBoard.Harness -c Release -- --selftest     # 446 passing
dotnet run --project src/PingBoard.App
```

CI (`.github/workflows/ci.yml`) runs the build and the self-test on `windows-latest`; keep it green.
Publishing and building the installer are in the README. If `dotnet` is not found from an agent
shell, it is installed in `C:\Program Files\dotnet` — prepend that to `PATH`.

## Conventions that bite

- **Line endings.** `.gitattributes` stores LF and checks out CRLF. Do not convert files by hand; a
  whole-file diff means something went wrong.
- **Runtime files never go in git.** `*.state.ini`, `*.outages.csv`, `pingboard-events.csv*` and a
  `config.ini` at the repo root are ignored on purpose. Real boards live in `%AppData%\PingBoard`
  or wherever the user keeps them.
- **This repository is public.** Do not add real hostnames, credentials or personal network
  details to code, tests, docs or screenshots.
- **The self-test count is quoted** in `README.md` and `docs/MIGRATION.md`. Update both when it
  changes.

## Releasing

- The version lives in two places that must match: `<Version>` in
  `src/PingBoard.App/PingBoard.App.csproj` and `AppVersion` in `installer/PingBoard.iss`.
- A release is a `vX.Y.Z` tag on the commit plus a GitHub release with
  `PingBoard-<version>-setup.exe` attached. The in-app updater reads `releases/latest`, downloads
  that asset and verifies its SHA-256 against the digest GitHub publishes.
- So **do not tag or release casually**: every installed copy will offer it as an update. Docs-only
  changes do not need a version bump.

## Checking UI changes without taking focus

Drive the running app with UI Automation rather than synthesised clicks, and capture it with
`PrintWindow` using `PW_RENDERFULLCONTENT` (flag `2`). WinUI's composition surface can come back
black when the window is occluded. Icon-only buttons need an explicit `AutomationProperties.Name` —
glyph content yields no accessible name, and a tooltip does not supply one.

## Moving the project

`docs/MIGRATION.md` covers moving a board and the source between machines. Clone outside any
file-sync folder.
