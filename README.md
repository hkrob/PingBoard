# PingBoard

A always-on ping monitor for Windows 11. Watches a list of hosts, shows what is up and what is
down, and keeps enough history to tell you *how* something is failing rather than just that it is.

Built on **WinUI 3** (Windows App SDK 2.3.1) and **.NET 10**, deployed unpackaged and
self-contained — copy the folder and run it, nothing to install.

---

## Build and run

Requires the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). No Visual Studio needed.

```bash
dotnet run --project src/PingBoard.App
```

Release build you can copy anywhere:

```bash
dotnet publish src/PingBoard.App -c Release -r win-x64 -o dist
```

Point it at a specific board:

```bash
dist/PingBoard.App.exe --config C:\path\to\board.ini
```

`--config` also keys the single-instance guard, so two different boards can run side by side while
relaunching the same one just surfaces the window already open. `--minimized` starts in the tray
with no window, which is what the autostart entry uses.

### Building the installer

`dist/` runs as-is if you just copy it. For something to hand to someone else, there is an
[Inno Setup](https://jrsoftware.org/isinfo.php) script:

```bash
dotnet publish src/PingBoard.App -c Release -r win-x64 -o dist
"%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\PingBoard.iss
```

It installs per user into `%LocalAppData%\Programs`, so there is no UAC prompt to install or
update, and the optional "start when I sign in" checkbox writes the same `HKCU` value the in-app
toggle does — one setting, not two that can disagree.

The output is unsigned, so SmartScreen will warn until you sign it. That is a property of the
certificate, not of the packaging: no installer format avoids it.

### The headless engine

All the networking lives in `PingBoard.Core`, which references no UI type at all. That is enforced
structurally — it is a separate project — so the part that has to be correct can be driven and
stress-tested without XAML in the way.

```bash
dotnet run --project src/PingBoard.Harness -- --selftest        # 135 assertions
dotnet run --project src/PingBoard.Harness -- board.ini --seconds 300
```

The harness prints the same columns as the UI, plus a forced-GC memory line every 60s.

---

## Configuration

A plain `.ini` you can hand-edit and copy between machines.

```ini
[Settings]
IntervalMs=2000
TimeoutMs=2000
RollingWindow=300          ; probes kept per target for Loss %, avg/min/max and History
FailuresBeforeDown=3       ; consecutive failures before alerting
MaxConcurrent=32
PreferIPv4=true
DnsCacheSeconds=300
LogEnabled=true
LogPath=pingboard-events.csv

[Target:gateway]
Address=10.1.10.1

[Target:homeassistant]
Address=10.1.10.12
Probe=tcp
Port=8123
IntervalMs=5000            ; any [Settings] key can be overridden per target

[Target:old-nas]
Address=nas.local
Enabled=false              ; paused
```

### Tabs

Targets can be grouped into tabs. Membership lives on the target; the section carries the tab's own
state, so renaming a tab does not mean editing every member.

```ini
[Tab:LAN]
Enabled=true
Order=0

[Tab:WAN]
Enabled=false              ; every target in this tab stops being probed
Order=1

[Target:gateway]
Address=10.1.10.1
Tab=LAN
```

**A tab is a view, not a scheduler.** Targets are probed regardless of which tab is on screen — the
tabs you are *not* watching are exactly where an outage goes unnoticed. `Enabled=false` is the
separate, explicit way to stop probing a group, and it reuses `Paused`, which is already excluded
from the rolling loss figures. Disabling a tab overnight therefore does not corrupt its statistics
the way counting the silence would.

Each tab shows a live tally (`WAN · 2 down`) so a problem cannot hide behind an unselected tab, and
the strip is hidden entirely while there is only one group. A board that never used tabs
round-trips unchanged.

Counters live in a sidecar (`board.state.ini`), so the file you edit stays free of churning
numbers. Deleting the sidecar resets all statistics; there is a menu item for the same thing.

`Probe=tcp` exists because plenty of hosts and most corporate firewalls drop ICMP silently, which
would otherwise read as a permanently dead target. A completed TCP handshake also proves more than
an echo reply does, and a *refused* connection is reported separately from a timeout — it means the
host is up and the port is closed.

### Alerting

A tray balloon only reaches you while you are sitting in front of the machine — which is exactly
when you would have noticed the board turn red anyway. An optional `[Alerts]` section sends
transitions somewhere that reaches you when you are not.

```ini
[Alerts]
WebhookEnabled=true
WebhookUrl=https://hooks.example.com/abc      ; POSTs JSON; ntfy, Home Assistant, Discord, Slack
MinIntervalSeconds=60                          ; suppress repeat alerts per target; 0 disables
NotifyOnRecovery=true

EmailEnabled=false
SmtpHost=smtp.example.com
SmtpPort=587
SmtpUser=me@example.com
SmtpPassword=                                  ; DPAPI-encrypted on the next save
EmailFrom=me@example.com
EmailTo=me@example.com
```

Delivery happens on a background worker behind a bounded queue that drops oldest. That is the
whole point: an unreachable SMTP server blocks for its full TCP timeout, and sending inline from
the probe path would mean an outage on the *alerting* side degrading the thing raising the alerts.

`SmtpPassword` and `WebhookAuthorization` are encrypted with DPAPI under the current Windows user,
so a config that ends up in a sync folder, a backup or a repo carries no usable credential. The
flip side is deliberate: copy the file to another machine and the secret must be re-entered there.
A password typed in as plaintext by hand still works, and is encrypted the next time the board
saves.

A sink that fails is reported in the status bar. An alerting path that breaks quietly is the worst
state this app can be in — the board looks healthy and you believe you will be told when it is not.

---

## What the columns mean

**Status · IP · Hostname · Last OK · Last NOK · OK/NOK** are shown by default, along with **RTT**,
**Loss %** and **History**. Right-click the Columns button for avg/min/max, consecutive failures,
uptime and probe type.

**Loss %** is the one worth reading day to day. It is a rolling window over the last N probes; the
lifetime OK/NOK count is dragged down forever by an outage three days ago and stops describing the
present.

**History** is a sparkline of recent probes — bar height is RTT, failures are full-height blocks.
It is what turns a number into a diagnosis: "Loss 4%" tells you something is wrong, but four evenly
spaced drops means periodic, and one solid block means a single outage. Different problems.

Status is never conveyed by colour alone — every row carries a glyph, a text label and a colour.
Hovering shows the raw `IPStatus`, which distinguishes "nothing answered" from "a router actively
said it could not deliver".

Closing the window hides to the tray; **Exit** is on the tray menu. Notifications fire only on
transitions (down / recovered with duration), never on individual failed probes, and each one
replaces the last rather than stacking up a transcript of how you got here.

**Start with Windows** is in the ⚙ menu — a per-user `HKCU\...\Run` entry, so no elevation and no
scheduled task. It launches with `--minimized`, straight to the tray. A monitor you have to
remember to start is a monitor that is not running on the morning something breaks.

The **Theme** submenu carries Follow Windows / Light / Dark, plus **Matrix** — green phosphor on a
black plate, monospaced throughout. Failure states stay chromatically distinct there rather than
collapsing into shades of green, because a board you cannot read at a glance has lost the one
thing it is for.

**Mute** silences desktop notifications for an hour, twelve hours, or until switched back on. It
does *not* touch webhook or email alerting: those exist to reach you when you are away from this
machine, and silencing them because someone quietened a popup would invert the intent. The mute
survives a restart — an indefinite one that quietly lifted itself would be the worst kind of bug —
and shows in the status bar throughout, because a monitor you forgot you silenced is more dangerous
than one that never alerted.

**Zoom** is Ctrl+scroll, or Ctrl+`+` / Ctrl+`-` / Ctrl+`0`. It scales the column widths, row height
and font sizes that layout already reads rather than applying a render transform — WinUI has no
`LayoutTransform`, and scaling pixels would blur the text and drift the header out of alignment with
the rows.

**Columns fit their content** by default, remeasured every couple of seconds and only moved when a
width changes by more than a few pixels. Both guards matter: latency and "Last OK" change several
times a second, and a column that resizes on every tick moves under the cursor and makes the board
unusable. Text is measured rather than estimated per character — the board runs in three faces and
scales with zoom, so an estimate would be wrong by a different amount in each. Toggle it, or fit
once, from the ⚙ menu.

**About** (the ⓘ button) carries the version and a link to the project, and checks GitHub for a
newer release. It also checks quietly at startup, at most once a day, and says nothing unless there
is something to say — a tool that interrupts you at launch to report it is already up to date has
spent your attention on nothing. Turn it off in the ⚙ menu.

An update is **offered, never installed**. Silently replacing the binary of something you are
relying on to watch your network is not a decision this code gets to make: it tells you, and you
choose. The download URL is restricted to GitHub over HTTPS, because it arrives in a network
response and a downloader that will run whatever it is handed is a remote code execution
primitive.

**Filtering** is a text box over name, IP and hostname plus a status filter. Filtered-out targets
keep being probed and keep alerting; the status bar always says how many of the total you are
seeing, because counts that silently describe a subset are how someone concludes all is well while
looking at three of forty hosts.

### Failure traces

When a target crosses the down threshold, PingBoard traces the path and stores where it broke.
Expanding a row shows the latency graph and, beneath it, the hops:

```
path breaks after hop 2 (38.34.167.2), 7 probed
 1  10.4.0.1  49 ms
 2  38.34.167.2  49 ms
 3  *
```

That is the difference between knowing something broke and knowing whose problem it is — and it has
to be captured at the moment of failure, because by the time anyone reaches the machine the path has
usually healed. Right-click a row to trace on demand. Traces also append to a sibling
`<log>.traces.txt`; a dozen hop lines crammed into one CSV cell would ruin the thing the CSV is good
at. `TraceOnFailure`, `TraceMaxHops` and `TraceHopTimeoutMs` are in `[Settings]`.

Two guards worth knowing: only down transitions trace, so a permanently dead host costs one trace
rather than one per second; and at most two run at once, skipped rather than queued, because when an
uplink drops every target crosses the threshold within seconds of the others and forty simultaneous
traces would flood a network already in trouble.

### The latency graph

Expanding a row also plots the rolling window: one bar per probe scaled to that target's own peak,
failures as full-height blocks, with min/avg/max gridlines. The sparkline tells you the *shape* of a
problem in 44 bars; the graph tells you *how much*, against what baseline. A link that normally sits
at 8 ms and is now at 40 ms is not down and loses no packets — every other column reads healthy, and
only a plot scaled to its own history shows it.

---

## Design notes

The decisions that matter, and why.

**Sleep, resume and NIC loss never count as failures.** The single most important guard here.
`SystemWatcher` watches `PowerModeChanged` and `NetworkAvailabilityChanged`; while suspended every
target reads `Suspended`, counters freeze and no notification fires. Without it, closing a laptop
lid for an hour manufactures thousands of failures, wrecks the rolling loss figures and produces an
alert storm on wake — and a monitor that cries wolf after every sleep gets ignored.

**Fixed-size ring buffers, never a growing list.** A `List<ProbeResult>` appended at 1 Hz across 40
targets grows ~100 MB/day. Measured over 7 minutes at ~48 probes/sec (≈20,000 probes) the managed
heap stayed flat at 0.6 MB and handle count was unchanged.

**One scheduler, staggered — not N timers.** A single `PeriodicTimer` drives everything, each
target phase-offset across its interval. Probes that are still outstanding are skipped rather than
stacked, so a dead host with a long timeout cannot accumulate work; a `SemaphoreSlim` caps total
concurrency.

**Monotonic clock for durations, wall clock only for display.** Mixing them means an NTP correction
or a DST rollover silently corrupts every elapsed time on screen.

**DNS is a separate failure mode.** Names resolve once and cache with a TTL, re-resolving on expiry
or after repeated failures so a DHCP change is still picked up. A name that stops resolving shows
`DNS FAIL`, not `TIMEOUT` — a different problem at a different layer.

**Probe rate is decoupled from render rate.** Probes complete on threadpool threads and mutate the
engine; a single 4 Hz timer pulls immutable snapshots to the UI. Marshalling every result to the
dispatcher would mean 40+ hops/sec to redraw text nobody can read that fast.

**Atomic config writes.** Content goes to a temp file, is flushed to disk, then renamed over the
destination with `File.Move(overwrite: true)` — `MoveFileEx`, a true atomic rename. `File.Replace`
was tried first and rejected: it moves the destination to the backup and *then* renames the temp
in, so a kill between those steps leaves no config at all. A torture test that hard-killed a writer
mid-save hit exactly that window. Loading also recovers from `.bak` and clears orphaned `.tmp`.

### WinUI 3 rough edges worth knowing

Three cost real time and are easy to hit again:

1. **A `Window` is not a `FrameworkElement`.** `x:Bind` with a converter inside a `DataTemplate`
   cannot compile when the XAML root is a Window. The board therefore lives in `BoardView`, a
   `UserControl`, and `MainWindow` is an empty shell.

2. **`dotnet publish` drops the app's `resources.pri`.** Compiled XAML lives there, so the
   published app dies at `InitializeComponent` while the identical build in `bin\` runs perfectly.
   `PingBoard.App.csproj` has an explicit copy target with an `Error` guard so this fails loudly at
   build time rather than silently at startup.

3. **A custom `Panel` must not touch layout state from inside layout.** Adding children, calling
   `Measure`, or setting `TextBlock.Text` from `ArrangeOverride` invalidates layout from within
   layout. WinUI responds with `LayoutCycleException` and *abandons the pass*, which freezes the
   entire window and kills hit-testing — while the process still reports `Responding = true` and
   sits at idle CPU. There is no spinning to give it away, and if `UnhandledException` marks it
   handled the app limps on looking merely broken. `LatencyGraph` therefore creates its shapes and
   computes all its text in a property-changed callback, and `ArrangeOverride` only assigns brushes
   and calls `Arrange`. Cost: two wrong diagnoses before reading the crash log properly.

4. **Toast registration fails under self-contained deployment.**
   `AppNotificationManager.Register()` needs `Microsoft.WindowsAppRuntime.Insights.Resource.dll`,
   which ships with the installed framework runtime and is not in the self-contained payload or any
   NuGet package. Rather than give up portability, notifications fall back to a tray balloon —
   which Windows 10/11 renders as an ordinary toast in the notification centre anyway.

There is also no `DataGrid` in WinUI 3 (the Community Toolkit dropped it at v8.0), so the board is
a virtualized `ListView` with a hand-built header. `ColumnLayout` is a single shared object both
the header and the row template bind to, which makes header/row misalignment structurally
impossible. Hidden columns collapse to zero width *and* `Visibility.Collapsed` — zero width alone
is not enough, because a `TextBlock` arranged into it still paints and bleeds over its neighbour.

Similarly there is no tray support, so `TrayIcon` builds on `Shell_NotifyIcon` directly rather than
taking `H.NotifyIcon.WinUI`, whose latest stable release predates the Windows App SDK 2.0 breaking
changes.

---

## Layout

```
src/PingBoard.Core/      engine — no UI references, ever
src/PingBoard.App/       WinUI 3 front end
src/PingBoard.Harness/   headless driver and self-tests
```

## Not included

SNMP, a service mode, and multi-machine sync. Each is a reasonable next step; none belonged in the
first build. Traceroute, latency graphing and webhook/email alerting were on this list and have
since landed.

## Licence

MIT — see [LICENSE](LICENSE).
