# Status

What is solid, what is not proven, what is deliberately absent, and which decisions are already
made. Written for someone picking the project up without the history. It deliberately carries no
version number — the version is in `src/PingBoard.App/PingBoard.App.csproj`.

If you verify something listed as unverified, or fix a listed gap, **edit this page in the same
commit**. A status page that lags the code is worse than none.

---

## Complete

The feature set is described in the [README](../README.md): ICMP/TCP/HTTP(S) probes, tabs, sites and
tags with saved views, maintenance windows, degraded state, certificate expiry, rolling and
long-run availability, failure traces, the outage log, CSV export, webhook and email alerts, the
tray, autostart, a self-updating per-user installer, and thirty days of hourly latency history
with a 24h/7d/30d graph and an Hourly export. Nothing in that list is stubbed.

## Verified by automation

- **The engine**, by the self-test suite (see [DEVELOPMENT.md](DEVELOPMENT.md)): probe-result
  classification, the ring buffer and its rolling statistics, the concurrency ceiling following its
  setting, per-target overrides and thresholds, degraded/certificate/outage
  state machines, config round-tripping and hand-edit tolerance, atomic saves and `.bak` recovery,
  alert delivery against a local listener, secret protection, HTTP status judging, update
  version/release parsing, and the hourly latency record — what counts as a reply, the ~p95 and
  its bin error, grouping into 4-hour bars from merged histograms, slot reuse after thirty days,
  the `Latency` sidecar key round-tripping, a pre-1.12 sidecar loading without it, rejection of
  corrupt entries, and the Hourly export.
- **The 24h/7d/30d latency graph and the Hourly export, in the real app** (1.12, on a Windows 11
  VM, driven by UI Automation against a demo board seeded with 30 days of synthetic hourly
  history): every range drew with the right caption, the window stayed responsive (no
  `LayoutCycleException` freeze) through switching and collapse/expand, a second row opened on
  Live independently, and Export → Hourly wrote the CSV through the real file picker, with an
  hour of failures exporting empty latency columns.
- **Suspend/resume logic** — that counters freeze, failure streaks clear, nothing is announced while
  suspended, an outage announced before sleep stays open, and a recovery is alerted on wake.
- **Every announced episode closes** — a recovery inside a maintenance window or a muted tab, a
  degradation ended by a hard outage or by its thresholds being removed, a certificate warning read
  while quiet, a target's latch re-armed for an outage left open by the previous run (the target
  side only; see *Not verified*), and two unpaired downs in the journal.
- **Build and tests on every push to `main` and every pull request to `main`**, on `windows-latest`, via CI.
- **Memory.** An early soak (24 targets, ~48 probes/s, about 20 000 probes) held the managed heap
  flat at under 1 MB; the history rings are fixed-size by construction. It was measured on an early
  build — re-run the harness for a few minutes after any change to the engine's hot path.
  Re-run for 1.12, which added hourly latency to every probe's path: the demo board (8 targets,
  1 s interval) for 5 minutes on Windows 11 held the managed heap flat at 1.6 MB and the working
  set at 52–55 MB. The baseline is higher by construction — about 118 KB of preallocated hourly
  buckets per target.

## Not verified

These are believed to work and are not proven. Each has a way to settle it.

1. **Real sleep/resume and network-loss behaviour.** The logic above is tested, but the wiring
   from Windows' `PowerModeChanged` and `NetworkAvailabilityChanged` events to
   `ProbeScheduler.SetSuspended` (in `SystemWatcher.cs`) has never been exercised by a test, and the
   repository holds no record of a manual check. To settle it, with a board running:
   - **Sleep** the machine for ~5 minutes and wake it. Expect no burst of failures, no wave of
     notifications, targets reading `Suspended` and then recovering, and the NOK counters
     unchanged across the gap.
   - **Disable the network adapter** for ~60 seconds. Expect every target `Suspended` with a
     single banner explaining why, not a notification per target; re-enable and expect a clean
     recovery.

   This is the most important unverified claim: a monitor that cries wolf after every sleep gets
   ignored.
2. **A fully successful run of `tools/retake-screenshots.ps1`.** Its refusal on a locked desktop and
   its restore-after-failure path have **no automated test and no recorded manual run**; the successful capture path needs an unlocked desktop
   and has not yet been seen to complete.
3. **A native ARM64 build.** The project pins `win-x64`; it should run under emulation on ARM64, but
   neither that nor a native build has been tried.
4. **The scheduler's timing behaviour.** Phase-staggering targets across their interval and
   skipping — rather than stacking — a probe whose predecessor is still in flight hold by
   construction (see `ProbeScheduler.cs`), but no test drives the scheduler through them. The
   soak run is the evidence; a regression here would show as probe bursts or a growing task count.
5. **Carrying open outages across a restart, end to end.** `PingTarget.RestoreOpenOutage` is
   self-tested; its caller, `MainViewModel.ResumeOpenOutages`, is App code and has only been
   compiled. To settle it: with the outage log on, let a target go down and be announced, close
   PingBoard, bring the host back, start PingBoard — expect one "recovered" notification and the
   outage closed in the log, with no second "down".
6. **Toast notifications as such.** `AppNotificationManager.Register()` fails under self-contained
   deployment (it needs a resource DLL that ships only with the installed framework runtime), so the
   app always falls back to a tray balloon, which Windows 11 renders as an ordinary toast. The
   toast code path itself is therefore not exercised in the shipped configuration.

## Known gaps

- **Screenshots** cover the board, the right-click menu, the 7-day latency graph and the Matrix
  theme. None shows the tab strip, tag/site saved views, the settings or target dialogs, or the
  outage log. The current set was taken on a Windows 11 VM at 300% scale by a one-off UI
  Automation driver; **`tools/retake-screenshots.ps1` still produces only the original three**
  (with a *Live* graph, and no menu shot), so running it would replace `board-detail.png` with a
  different view and leave `board-menu.png` alone. Extending it means seeding a history sidecar
  for the graph shot, as the README's caption describes.
- **The installer is unsigned**, so SmartScreen warns on first run.
- **The update check is unauthenticated** — GitHub allows 60 requests an hour per IP, shared by
  everything behind one router — and a private fork would see a 404. The app words both clearly.
- **The README is long.** It has grown by feature. [CONFIGURATION.md](CONFIGURATION.md) and
  [ARCHITECTURE.md](ARCHITECTURE.md) now carry the reference material, so the README could be
  trimmed to an overview with links.
- **Alert delivery** is a static `Authorization` header for the webhook and plain SMTP login for
  email. There is no OAuth, retry queue or delivery history.

## Deliberately not included

SNMP, a Windows-service mode, and multi-machine sync. Each is a reasonable next step; none belonged
in the first build. Code signing and a native ARM64 build are likewise open.

---

## Decisions already made

Reopen one only with a reason that was not available when it was made. The reasoning lives in the
README's [Design notes](../README.md#design-notes) and in the doc comments of the files named.

| Decision | Why | Where |
|---|---|---|
| **WinUI 3**, not WPF | Native Mica/Acrylic, Windows 11 controls, per-monitor DPI, first-class title bar. The cost is no `DataGrid` and no tray support | README design notes |
| **`ListView` + a shared `ColumnLayout`**, not a third-party grid | A ping board has tens of rows; one object that header and rows both bind to makes misalignment structurally impossible | `ColumnLayout.cs` |
| **Hand-rolled tray** on `Shell_NotifyIcon`, with `DllImport` | The usual WinUI tray package predates Windows App SDK 2.0's breaking changes; `LibraryImport` cannot marshal those structs | `TrayIcon.cs` |
| **Hand-rolled INI parser** | `GetPrivateProfileString` is ANSI-quirky and wants absolute paths; the file must stay hand-editable | `IniFile.cs` |
| **Atomic saves via `File.Move(overwrite: true)`**, never `File.Replace` | `Replace` leaves a window with no config file at all | `IniFile.SaveAtomic` |
| **Secrets in DPAPI, bound to the user profile** | A config in a backup or repo must carry no usable credential. Not portable — by design | `ProtectedValue.cs` |
| **One `Ping` per target**, never `ping.exe` | `Ping` is not reentrant; a process per probe is ~5 MB each | `Probes.cs` |
| **ICMP plus TCP and HTTP(S)** | Plenty of hosts and firewalls drop ICMP silently, which would read as a dead target | README |
| **Degraded thresholds off by default** | "Too slow" is a statement about one link; a global default is a guess about someone else's network | `Settings.cs` |
| **Quiet suppresses the announcement, not the probe — and never the closing half of one already made** | A host still down when maintenance ends must alert then, not never; a host announced down before the window must still report its recovery, or the outage stays open in the log | `ProbeScheduler.RecordAndNotify`, `PingTarget.Record` |
| **Counters are lifetime, but the rolling window is the figure to read** | A lifetime ratio is dragged down for ever by an old outage | README |
| **Soft transitions reuse the CSV `event` column** | A new column would put every older row under the wrong heading | `TransitionLog.cs` |
| **Unpackaged, self-contained, per-user Inno Setup install** | Copy-and-run, no runtime prerequisite, no UAC to install or update | `installer/PingBoard.iss` |
| **GitHub Releases as the update channel**, installer verified by SHA-256 | No server to run; the digest is published by GitHub | `UpdateCheck.cs`, `UpdateInstaller.cs` |