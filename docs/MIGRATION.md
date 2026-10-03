# Moving PingBoard to another machine

Three different things are involved, and each moves a different way:

| | What it is | How it moves |
|---|---|---|
| The **app** | the installed program | reinstall from the latest release |
| Your **board** | targets, tabs, sites, settings, history, outage log | copy a handful of files |
| **This machine's** settings | window position, *Start with Windows*, the remembered board, alert passwords | redo them on the new machine |

Nothing about a board lives in the registry or is tied to the installer, which is what makes moving
one a file copy rather than an export.

---

## 1. Find your board

The board is whichever `.ini` the app has open. **That is not necessarily `%AppData%\PingBoard`** —
only the *default* board lives there. A board opened with ⚙ → *Open config…*, or started with
`--config`, lives wherever that file is.

To find it, hover the file name in the title bar for the full path, or use
⚙ → *Open config folder*.

## 2. What to copy

A board is one `.ini` plus sidecar files that share its name and sit in the same folder. Copy them
together.

| File | Holds | Copy it? |
|---|---|---|
| `<board>.ini` | settings, targets, tabs, sites, alert sinks | always |
| `<board>.state.ini` | counters and probe history | to keep OK/NOK totals and the history graphs |
| `<board>.outages.csv` | the outage log | to keep outage history |
| `pingboard-events.csv`, `pingboard-events.csv.traces.txt` | transition log and failure traces | optional |
| `*.bak`, `*.tmp` | safety copies of the last save | leave behind |

The event log's name comes from `LogPath` under `[Settings]`; a relative path resolves next to the
`.ini`, so it travels with the board as long as you copy the whole folder.

Copying only the `.ini` gives you the full board layout with zeroed statistics.

> **A note on ⚙ → *Save config as…*.** It writes the settings, targets, alert sinks, tabs and sites,
> plus the counters and outage log, and then **switches the running app to the new file**. It does
> not copy the event log or the failure traces. A plain file copy after *Exit* (below) is the more
> complete route and leaves the running board alone.

## 3. What does *not* travel

- **Alert credentials.** The SMTP password and the webhook authorization header are encrypted with
  DPAPI to the Windows user profile that wrote them. On another machine or account they normally
  cannot be decrypted and load as empty. This is deliberate: a config that ends up in a backup, a sync folder or a repo carries
  no usable secret. The webhook URL, SMTP host and addresses do travel. Re-enter the secrets in
  ⚙ → *Settings…* → *Alerting*, then press *Send a test alert* to confirm. If you do not use
  alerting, ignore this.
- **Start with Windows.** It is a per-user `HKCU\...\Run` value pointing at the installed
  executable. Turn it on again in the ⚙ menu.
- **The remembered board and window layout.** They live in `ui-state.ini` under
  `%AppData%\PingBoard` and are per machine. Do not copy it: it records window coordinates for the
  old monitor layout.
- **The installed app.** Install it fresh.

## 4. Procedure

**On the old machine**

1. **Exit PingBoard from the tray menu.** Closing the window only hides it to the tray. *Exit*
   writes the final counters, so the files you copy are complete and at rest.
2. Copy the board's files (section 2) to the new machine, keeping them together in one folder.

**On the new machine**

3. Download the installer from the
   [latest release](https://github.com/hkrob/PingBoard/releases/latest). It installs per user, so
   it needs no admin rights. It is unsigned, so SmartScreen will warn: *More info* → *Run anyway*.
   Install the latest release rather than an older build — an older version may not understand
   settings written by a newer one.
4. Put the board in a permanent folder (not *Downloads* or a temp folder). The app refers to the
   board by its path.
5. Start PingBoard, choose ⚙ → *Open config…* and pick the `.ini`. Confirm your tabs, targets and
   history appear and the title bar shows the right file name.
6. If you use alerting, re-enter the credentials (section 3).
7. If you want it, turn on ⚙ → *Start with Windows*.
8. Leave the old machine alone until the new board has been running healthily for a while. A target
   that was reachable from the old network but not the new one is the network, not the migration.

> **Remembering the open board.** From 1.11.17 the board you open is remembered immediately. In
> earlier versions it was only recorded when the window was hidden to the tray or the app was
> exited, so a restart, sign-out or crash could reopen the *previous* board and leave the one you
> chose unmonitored. If you are on an older build, open the board explicitly after every restart —
> or pass `--config <path>` — until you update.

Do not run two PingBoards against the same board files at once. The counters have a single writer;
two instances overwrite each other's totals.

---

## 5. Moving the source

If you develop PingBoard, clone it — do not copy the folder.

```bash
git clone https://github.com/hkrob/PingBoard.git
```

**Clone outside any file-sync folder** (Resilio, Dropbox, OneDrive and similar). A synced `.git`
corrupts easily when two machines have it open, and a synced working tree drags `bin/`, `obj/` and
installer output across with it.

Toolchain:

```bash
winget install Microsoft.DotNet.SDK.10
winget install JRSoftware.InnoSetup       # only needed to build the installer
```

No Visual Studio is needed, and no separate Windows App SDK install — the app is self-contained.

Verify the checkout before changing anything:

```bash
dotnet build PingBoard.slnx -c Release                                   # expect 0 warnings
dotnet run --project src/PingBoard.Harness -c Release -- --selftest      # expect 446 passed
```

Runtime files are kept out of git on purpose — `.gitignore` covers `*.state.ini`,
`*.outages.csv`, `pingboard-events.csv*` and a `/config.ini` at the repo root. A real board belongs
in `%AppData%\PingBoard` or wherever you keep it, never in source control.

The project pins `win-x64`. An ARM64 machine runs that build under emulation; a native build needs
the `RuntimeIdentifier` and `Platforms` in `PingBoard.App.csproj` changed, which has not been tried.
