# Development

Everything needed to build, test, verify and release PingBoard. For how the code is organised, see
[ARCHITECTURE.md](ARCHITECTURE.md); for the config schema, [CONFIGURATION.md](CONFIGURATION.md).

## Prerequisites

| Needed for | Install |
|---|---|
| Everything | `winget install Microsoft.DotNet.SDK.10` and `git` |
| Building the installer only | `winget install JRSoftware.InnoSetup` (Inno Setup 6) |
| GitHub releases | `gh` (GitHub CLI), authenticated |

No Visual Studio, and no separate Windows App SDK install: the app is unpackaged and
self-contained, and `dotnet build` is enough for WinUI 3. If `dotnet` is not found from an agent or
CI-style shell, it lives in `C:\Program Files\dotnet` — put that on `PATH`.

## Build, test, run

```bash
dotnet build PingBoard.slnx -c Release                                  # must be 0 warnings
dotnet run --project src/PingBoard.Harness -c Release -- --selftest     # must be all passing
dotnet run --project src/PingBoard.App                                  # the app
```

CI runs the first two on every push and pull request to `main`
([`.github/workflows/ci.yml`](../.github/workflows/ci.yml)); keep it green. Building the
**solution** also builds the harness; building only the app does not.

> **Before launching the app against any board, read [The UI-state hazard](#the-ui-state-hazard)
> below.** It rewrites a file your installed copy depends on.

## The self-test suite

[`src/PingBoard.Harness/SelfTest.cs`](../src/PingBoard.Harness/SelfTest.cs) is the whole suite: a
single file of `Check("area: what holds", condition)` calls, grouped into one method per area. There
is no test framework — a failing check prints `FAIL` and the run exits non-zero. It is headless and
needs no UI; the network-touching tests use loopback and a local `HttpListener` (the webhook one),
so it runs in CI.

To add a check, put it in the method for its area, or add a method and call it from `Run()`. Test
the *behaviour that would be silently wrong* — the suite is strongest where it pins a decision (an
address surviving a failed probe, a muted target still alerting when the mute ends) rather than
where it re-reads a getter.

The count appears in [README.md](../README.md) and [MIGRATION.md](MIGRATION.md). Update both when it
changes.

## The headless harness

The same engine, in a console, with the same columns as the UI:

```bash
dotnet run --project src/PingBoard.Harness -c Release -- board.ini --seconds 300
```

It prints a managed-heap line every 60 s after a forced collection, which doubles as a leak check:
the history rings are fixed-size, so the heap should be flat. A line that climbs means something is
retaining results. `--updatecheck` runs the GitHub release check from a console.

## Checking UI changes

The app is a GUI, so a build that compiles and passes the tests has not been looked at. Two
techniques work without taking the keyboard focus from whoever is using the machine:

- **UI Automation** to drive it. Every button and each row's expander exposes an accessible name
  (`Add target`, `File, statistics and settings`, `Show latency graph and path trace`, …), so a
  script can invoke them. Icon-only buttons **must** carry an explicit `AutomationProperties.Name`:
  glyph content yields no accessible name, and a tooltip does not supply one.
- **`PrintWindow`** to capture it — [`tools/capture-window.ps1`](../tools/capture-window.ps1).

Capture needs an **unlocked, awake desktop**. While the workstation is locked Windows stops
compositing apps and every capture is a solid black frame; UI Automation still works. The tool
refuses to save a black frame rather than writing a screenshot that looks fine until opened. If the
window is merely covered by others, use `-Raise`: it lifts the window to the top *without
activating it* for the instant of the capture.

## The UI-state hazard

**Launching any build with `--config` rewrites `%AppData%\PingBoard\ui-state.ini`.** Since 1.11.17
the open board is remembered the moment it changes, so a test launch points that file at your test
board — and the installed copy reads it on its next start, which would then open the test board
instead of the real one. A theme change or window move writes there too.

So when testing against a scratch board: back up `ui-state.ini` (and `.bak`) first, restore after,
and compare hashes. [`tools/retake-screenshots.ps1`](../tools/retake-screenshots.ps1) does exactly
this in a `finally` block and is the model to copy. Also never stop a process you did not start —
an installed copy may be monitoring a real board.

## Screenshots

[`docs/demo.ini`](demo.ini) is a board of public hosts and RFC 5737 documentation addresses, with
notifications, logging and trace-on-failure off, so running it is safe and nothing about a real
network reaches an image. To regenerate `docs/screenshots/`:

```bash
dotnet build PingBoard.slnx -c Release
```

```bash
pwsh tools/retake-screenshots.ps1
```

It launches the demo board, lets it build ~150 s of history, drives the UI to expand a row and
switch theme, captures three images, and only then replaces the files in `docs/screenshots/`. It
needs an unlocked desktop, and the demo window is visible on screen for a few minutes. The first
two checks in the script (refusing when locked; restoring state after a failure) are tested; **a
fully successful capture run has not yet been confirmed** — see [STATUS.md](STATUS.md).

Screenshots must stay free of anything real: the demo board's hosts only, and the title bar shows
`demo.ini`.

## Releasing

A release is a version bump, a built installer, a tag and a GitHub release carrying that installer.
**Every installed copy offers a release as an update**, so do not cut one casually; docs-only and
tooling changes do not need one.

1. Pick the version and bump it in **both** places, which must match:
   `<Version>` in `src/PingBoard.App/PingBoard.App.csproj` and `AppVersion` in
   `installer/PingBoard.iss`.
2. Build and run the self-tests (above); both clean.
3. Publish the payload:
   ```bash
   dotnet publish src/PingBoard.App -c Release -r win-x64 -o dist
   ```
   The project has a target that copies the app's `.pri` into the publish folder and **fails the
   build** if it is missing — without it the published app dies at startup with a
   `XamlParseException` while the identical build in `bin\` runs fine.
4. Compile the installer (the path depends on how Inno Setup was installed):
   ```bash
   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\PingBoard.iss
   ```
   Output: `installer/output/PingBoard-<version>-setup.exe` (git-ignored, ~60 MB). The installer's
   `AppId` GUID must never change — it is how an upgrade recognises the existing install. It
   refuses to downgrade.
5. Commit (`1.x.y: what changed`, with a body that says *why*), push, wait for CI to pass.
6. Tag and publish, attaching the installer:
   ```bash
   gh release create vX.Y.Z installer/output/PingBoard-X.Y.Z-setup.exe --target <sha> --title "PingBoard X.Y.Z" --notes-file notes.md
   ```
   Past releases have a headline section describing the change in user terms, then an
   `## Install` section naming the asset (`gh release view v1.11.17` shows one).
7. Check it: the asset has a SHA-256 digest (`gh release view vX.Y.Z --json assets`), and **About →
   Check for updates** in an older install offers it. The in-app updater reads `releases/latest`,
   downloads that asset and verifies it against the published digest; it calls the public GitHub
   API, so a private fork would see a 404.

The installer is unsigned, so SmartScreen warns on first run. That is a property of the
certificate, not the packaging.

## Conventions

- **Line endings.** `.gitattributes` stores LF and checks out CRLF. Do not convert files by hand; a
  whole-file diff means something went wrong.
- **Runtime files never go in git** — `*.state.ini`, `*.outages.csv`, `pingboard-events.csv*`, and a
  `config.ini` at the repo root are ignored on purpose.
- **This repository is public.** No real hostnames, internal addresses, credentials or personal
  details in code, tests, docs, demo boards or screenshots.
- **Docs move with the code.** A new config key goes in [CONFIGURATION.md](CONFIGURATION.md); a
  user-visible feature in the README; a new invariant or file in
  [ARCHITECTURE.md](ARCHITECTURE.md). If the self-test count changes, update the places that quote
  it.
- **Commit messages** explain *why*. The history is the best record of decisions that do not fit in
  a comment.
- **No behaviour change without a test** for the failure it prevents.

## Troubleshooting

| Symptom | Cause |
|---|---|
| App vanishes with no dialog | Check `%AppData%\PingBoard\crash.log` — unhandled exceptions are logged there |
| Published app dies with `XamlParseException` | The `.pri` was not copied; the csproj target should have failed the publish |
| Screenshot is solid black | Workstation locked, window hidden to the tray or minimised, or covered (try `-Raise`) |
| Header and rows misalign | A column bound to something other than the shared `ColumnLayout` entry |
| Toasts never appear | Expected under self-contained deployment — `Notifications` falls back to a tray balloon |
| A new instance opens the wrong board | See [the UI-state hazard](#the-ui-state-hazard) |

The WinUI 3 traps behind several of these are written up in the README's
[rough edges](../README.md#winui-3-rough-edges-worth-knowing).