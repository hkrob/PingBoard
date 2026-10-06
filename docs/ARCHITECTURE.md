# Architecture

How PingBoard is put together, written for someone who has not seen the code before. For *why*
particular decisions were made, the README's [Design notes](../README.md#design-notes) carry the
long form; this page is the map.

PingBoard is about 18 000 lines: an engine (`PingBoard.Core`, ~6 600), a WinUI 3 front end
(`PingBoard.App`, ~8 300) and a headless driver with the self-test suite (`PingBoard.Harness`,
~2 900).

---

## The one-paragraph version

A single `ProbeScheduler` ticks every 250 ms and starts a probe for each target that is due. Each
probe runs off the UI thread, resolves the name through a cache, asks an `IProbe` (ICMP, TCP or
HTTP(S)) and hands the `ProbeResult` to the target. The target records it in a fixed-size ring and
its counters, and — only when the up/down/degraded/certificate *state changes* — returns a
`StateTransition`. The scheduler raises that as an event; the view model fans it out to the CSV log,
the outage journal, the alert sinks and (marshalled onto the UI thread) the tray notification. The
UI never reads live engine state: a 4 Hz timer pulls immutable snapshots into row view-models.

```
 board.ini ──ConfigStore──► Settings · TargetConfig[] · TabConfig[] · SiteConfig[] · AlertSettings
                                              │
                                              ▼
                       ProbeScheduler   (one PeriodicTimer, 250 ms; targets phase-staggered)
                                              │  per due target, on the thread pool
                       ┌──────────────────────┴───────────────────────┐
                       │ DnsCache ► IProbe (IcmpProbe│TcpProbe│HttpProbe) ► ProbeResult
                       └──────────────────────┬───────────────────────┘
                                              ▼
                       PingTarget.Record(...)   RingBuffer · counters · AvailabilityLog
                                              │  returns StateTransition? (Hard│Degraded│Certificate)
                                              ▼
                       ProbeScheduler.Transition event ─► MainViewModel.OnTransition
                          ├─ TransitionLog      CSV of every transition (+ .traces.txt)
                          ├─ TransitionJournal  in-memory outage list, "while you were away"
                          ├─ OutageStore        board.outages.csv, survives restarts
                          ├─ AlertDispatcher    webhook / SMTP, off the probe path
                          └─ DispatcherQueue ─► Transition event ─► toast / tray balloon

 UI thread:  MainViewModel._refreshTimer (250 ms visible / 1 s hidden)
                └─► TargetRow.Refresh()  ◄── PingTarget.Snapshot()  (immutable TargetSnapshot)
```

---

## Source map

### `src/PingBoard.Core` — the engine

> **Hard rule: this project references no UI type, ever.** It is a separate project so that is a
> compile-time fact, and `TreatWarningsAsErrors` is on. It targets `net10.0-windows` only for
> `SystemEvents` (sleep/resume).

| Area | File | What it holds |
|---|---|---|
| Probing | `Probes.cs` | `IProbe`; `IcmpProbe` (one `Ping` per target — `Ping` is not reentrant), `TcpProbe`, `HttpProbe` |
| | `ProbeResult.cs` | One immutable sample: status, RTT, monotonic tick, wall-clock time, raw `IPStatus` |
| | `TraceRoute.cs` | Path trace taken when a target goes down |
| | `CertificateCheck.cs` | Reads an HTTPS target's TLS certificate on a slow cadence |
| | `DnsCache.cs` | Forward and reverse DNS, TTL cache, hard per-lookup timeout |
| Scheduling | `ProbeScheduler.cs` | The timer, concurrency cap, in-flight guard, suspend/resume, quiet-hours handling |
| | `SystemWatcher.cs` | Sleep/resume and network-down detection; feeds `SetSuspended` |
| State | `PingTarget.cs` | `TargetConfig`, `TargetCounters`, `TargetSnapshot`, `PingTarget`, `StateTransition` — all mutable per-target state, behind one lock |
| | `TargetStatus.cs` | `TargetStatus`, `ProbeKind`, and the extension helpers that classify a status |
| | `RingBuffer.cs` | Fixed-capacity history with rolling stats (loss, min/avg/max, jitter) |
| | `AvailabilityLog.cs` | 24 h / 7 d / 30 d availability, as opposed to the last few minutes |
| | `Maintenance.cs` | Parses and evaluates `Sat 22:00-02:00`-style quiet hours |
| Records | `TransitionLog.cs` | Size-rotated CSV of transitions, plus the `.traces.txt` sibling |
| | `TransitionJournal.cs` | Recent transitions in memory, paired into outages |
| | `OutageStore.cs` | Durable outage record (`board.outages.csv`) with compaction |
| Alerting | `Alerts.cs` | `AlertSettings` and its validation |
| | `AlertDispatcher.cs` | Delivery to webhook / SMTP with per-target repeat suppression |
| | `ProtectedValue.cs` | DPAPI wrapper for the two stored secrets |
| Config | `Settings.cs` | Global defaults + `Validate()` clamps |
| | `ConfigStore.cs` | `ConfigStore` (the `.ini`) and `StateStore` (the `.state.ini` sidecar) |
| | `IniFile.cs` | Hand-rolled INI parser/writer; `SaveAtomic` and `LoadResilient` |
| | `TabConfig.cs`, `SiteConfig.cs` | Tab and site records |
| Other | `HostCatalog.cs` | The "Add well-known hosts" lists |
| | `Export.cs` | CSV export of what the board knows |
| | `UpdateCheck.cs` | Asks GitHub for the latest release; version comparison |

### `src/PingBoard.App` — the WinUI 3 front end

| Area | File | What it holds |
|---|---|---|
| Entry | `Program.cs` | Custom `Main`; single-instance redirection, keyed on the `--config` path; `--minimized` |
| | `App.xaml(.cs)` | Unhandled-exception wiring |
| | `CrashLog.cs` | `CrashLog` and `AppPaths` (the `%AppData%\PingBoard` locations) |
| Shell | `MainWindow.xaml(.cs)` | **An empty shell**: Mica backdrop, window placement, close-to-tray, theme on the root grid |
| | `BoardView.xaml(.cs)` | The whole board: toolbar, tab strip, header row, virtualized `ListView`, status bar |
| | `TrayIcon.cs` | `Shell_NotifyIcon` by hand — WinUI 3 has no tray support |
| | `Notifications.cs`, `NotificationMute.cs` | Toasts with a tray-balloon fallback; global desktop mute |
| | `Autostart.cs`, `UiState.cs`, `UpdateInstaller.cs` | Start with Windows (HKCU `Run`), `ui-state.ini`, installer download/launch |
| View models | `ViewModels/MainViewModel.cs` | Owns the engine; timers; load/save orchestration; tabs, sort, filters; the fan-out in `OnTransition` |
| | `ViewModels/TargetRow.cs` | One row: a display projection of one `PingTarget` snapshot |
| | `ViewModels/TabItem.cs` | One tab in the strip |
| | `ViewModels/ColumnLayout.cs` | **Single source of truth** for column widths/visibility/order, shared by header and rows |
| | `ViewModels/ColumnFitter.cs` | Measures how wide each column needs to be |
| Controls | `Controls/Sparkline.cs`, `Controls/LatencyGraph.cs` | Hand-drawn per-row history strip and the expanded latency chart |
| | `Controls/BoardPalette.cs`, `Controls/BrushKeyConverter.cs` | Resolve a status *key* to a brush at bind time, so theme switches recolour live |
| Theme | `Theme/Board.xaml`, `MatrixTheme.cs` | Light/dark/high-contrast status colours; the green-phosphor override |
| Dialogs | `TargetDialog`, `SettingsDialog` (XAML) | The two large forms |
| | `AddHostsDialog`, `ArrangeColumnsDialog`, `ExportDialog`, `OutageLogDialog`, `NewTabDialog`, `RenameTabDialog`, `TagFilterDialog`, `SiteFilterDialog`, `AboutDialog` | Smaller dialogs, built in code |

### `src/PingBoard.Harness` — headless driver and tests

`Program.cs` runs a board in a console with the same columns as the UI (`<board.ini> --seconds N`),
and has `--selftest` and `--updatecheck`. `SelfTest.cs` is the whole test suite: one file of
`Check(name, condition)` calls grouped into methods by area, no test framework. It runs in CI.

### Outside `src/`

`installer/PingBoard.iss` (Inno Setup), `.github/workflows/ci.yml`, `docs/`, `tools/`, `CLAUDE.md`.

---

## Threads and timing

| What | Cadence | Where |
|---|---|---|
| Scheduler tick | 250 ms `PeriodicTimer` | background; each probe is its own task on the thread pool |
| Probe per target | `IntervalMs`, staggered across the interval | skipped (not queued) if the previous probe is still in flight or the concurrency cap is reached |
| UI refresh | 250 ms visible; 1 s while the window is hidden (tray tally only) | UI thread, `DispatcherQueueTimer` |
| Counter/history autosave | every 60 s, on board switch, on exit | UI thread timer; writes are atomic |
| Column auto-fit | throttled to 2 s | UI thread |
| Certificate re-check | `CertCheckHours` | scheduler |
| Update check | once at startup, if enabled | background |

Probes complete on pool threads and mutate the engine; they **never touch the dispatcher**. The only
hop onto the UI thread on a probe path is the transition notification, which fires on state change
rather than per probe.

## Persistence

| File | Format | Written |
|---|---|---|
| `board.ini` | INI | on every edit; atomic temp-file-then-rename, previous kept as `.bak` |
| `board.state.ini` | INI; history as compact `status:rtt` pairs (timestamps dropped on purpose) | autosave + exit |
| `board.outages.csv` | append-only CSV, rewritten when it needs compaction | on each transition |
| transition CSV (`LogPath`) | CSV, rotated by size | on each transition |
| `ui-state.ini` | INI, per machine | when a UI preference changes |

`IniFile.SaveAtomic` uses `File.Move(overwrite: true)` (an atomic `MoveFileEx`), **not**
`File.Replace`: `Replace` moves the destination aside and *then* renames the temp in, so a kill
between those steps leaves no config at all. `LoadResilient` recovers from `.bak` and discards an
orphaned `.tmp`. Full key listing: [CONFIGURATION.md](CONFIGURATION.md).

---

## Invariants — what not to break

Each of these exists because the opposite was tried, or would plainly fail in a tool left running
for weeks.

1. **`PingBoard.Core` references no UI type.** (Compile-time.)
2. **The UI reads immutable snapshots, never live state.** `PingTarget` guards all mutable state
   with one lock; `TargetSnapshot` is what leaves it.
3. **No unbounded collection on a probe path.** History is a preallocated ring; a growing list at
   1 Hz across dozens of targets is ~100 MB a day.
4. **Monotonic ticks (`Environment.TickCount64`) for every duration and schedule; wall clock for
   display only.** An NTP step or DST change must not corrupt elapsed times.
5. **Paused and Suspended are not failures.** They are excluded from the rolling figures, and a
   result that lands while the machine is suspending is discarded (`RecordAndNotify` returns early).
   Sleep/resume and a down NIC must never be recorded as a target outage.
6. **A probe never takes the loop down, and a slow probe is skipped, not stacked.** Each target has
   an in-flight guard; a `SemaphoreSlim` caps total concurrency.
7. **Report the address that was probed, never `reply.Address`.** On failure the reply address is
   whoever generated the ICMP error — often the local machine, or `0.0.0.0`.
8. **DNS failure is its own status.** `DnsFail` is not `Timeout`; they send you to different layers.
9. **Quiet suppresses the announcement, not the probe.** A muted tab or a maintenance window tells
   `Record` not to announce, and leaves the "already announced" flag clear — so a host still down
   when the quiet period ends alerts *then*, rather than never.
10. **Soft transitions reuse the CSV's `event` column** (`degraded`, `degraded_cleared`,
    `cert_expiring`). A new column would put every older row under the wrong heading.
11. **Secrets are DPAPI-protected on write and never logged.** An undecryptable value loads as empty.
12. **`TabConfig` and `TargetConfig` hold lists (`SelectedTags`, `SelectedSites`, `Tags`) and
    `Clone()` is shallow — assign a new list, never mutate one in place**, or the clone and the
    original change together.
13. **One `Ping` instance per target.** `Ping` is not reentrant; never shell out to `ping.exe`.
14. **Every `TcpClient` is disposed in a `finally`.** An abandoned half-open connect leaks a handle
    per probe.

WinUI 3 specifics — a `Window` is not a `FrameworkElement`, `dotnet publish` drops the app's
`.pri`, `x:Name` fields are private, theme must be applied to the *content root* for Mica to follow,
hidden columns need `Collapsed` as well as zero width, the tray needs `DllImport` not
`LibraryImport` — are explained in the README's
[WinUI 3 rough edges](../README.md#winui-3-rough-edges-worth-knowing).

---

## Making common changes

**Add a setting.** Property and default in `Settings.cs`, plus its clamp in `Validate()`; read *and*
write it in `ConfigStore.cs` using `nameof(Settings.X)`; add the control to `SettingsDialog.xaml` and
its read/write in `SettingsDialog.xaml.cs`; add a round-trip check to `SelfTest.cs`; add a row to
[CONFIGURATION.md](CONFIGURATION.md). A per-target override additionally goes on `TargetConfig`
(nullable, inheriting), a `…From(Settings)` accessor on `PingTarget`, and `TargetDialog`.

**Add a column.** Follow `Jitter` — it is threaded through `ColumnLayout.cs`, `TargetRow.cs`,
`BoardView.xaml`, `ColumnFitter.cs` and `MainViewModel.cs`. Header and rows must both bind to the
same `ColumnLayout` entry, or they drift.

**Add a probe kind.** A value in `ProbeKind`, an `IProbe` in `Probes.cs`, the factory in
`PingTarget`, parse/write in `ConfigStore`, and the choice in `TargetDialog`. Read how `Http` was
threaded through; it touched every one of those.

**Add a dialog.** Follow a small code-only one such as `NewTabDialog.cs`. Set `XamlRoot` before
`ShowAsync()`, and wrap `async void` handlers in `try/catch` that calls `CrashLog.Write` — an
unhandled exception in one can take the process down.

**Add an alert sink.** `AlertSettings` (+ its `Validate`), delivery in `AlertDispatcher`, the
`[Alerts]` keys in `ConfigStore`, the `SettingsDialog` section, and a delivery check in `SelfTest`
(the webhook one stands up a local listener).