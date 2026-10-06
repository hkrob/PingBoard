# Configuration reference

Every key PingBoard reads, with its default and the range it is clamped to. The source of truth is
the code — [`Settings.cs`](../src/PingBoard.Core/Settings.cs),
[`Alerts.cs`](../src/PingBoard.Core/Alerts.cs), `TargetConfig` in
[`PingTarget.cs`](../src/PingBoard.Core/PingTarget.cs),
[`TabConfig.cs`](../src/PingBoard.Core/TabConfig.cs),
[`SiteConfig.cs`](../src/PingBoard.Core/SiteConfig.cs) and
[`ConfigStore.cs`](../src/PingBoard.Core/ConfigStore.cs) — and this page is derived from them. If
you add or change a key, change it here in the same commit.

For the *meaning* of the features behind these keys, see the [README](../README.md).

---

## Files

A **board** is one `.ini` plus sidecars that share its name and sit in the same folder.

| File | Written by | Holds |
|---|---|---|
| `<board>.ini` | you, and the app on every edit | settings, targets, tabs, sites, alert sinks |
| `<board>.state.ini` | the app, every 60 s and on exit | counters, probe history, availability |
| `<board>.outages.csv` | the app | the outage log, so it survives a restart |
| `<LogPath>` (default `pingboard-events.csv`) | the app | every up/down/degraded transition |
| `<LogPath>.traces.txt` | the app | the traceroute taken when a target went down |
| `*.bak`, `*.tmp` | the app | safety copy of the previous save / an interrupted save |

A relative `LogPath` resolves **next to the `.ini`**, not next to the program, so a board moved
between machines keeps its log with it.

Per-machine UI preferences are *not* part of the board. They live in
`%AppData%\PingBoard\ui-state.ini` — see [the last section](#ui-stateini).

## Conventions

- Keys and section names are case-insensitive. Section names may contain spaces (`[Target:Backup site]`).
- A line starting with `;` or `#` is a comment. A trailing comment needs whitespace before the `;`.
- Booleans accept `true`/`false`, `yes`/`no`, `on`/`off`, `1`/`0`.
- Lists (`Tags`, `SelectedTags`, `SelectedSites`) are comma-separated; blanks and case-insensitive
  duplicates are dropped.
- **Out-of-range numbers are clamped, not rejected**, so a hand-edited typo cannot stop the app
  starting. The clamp is in the "Range" column.
- Saves are atomic (temp file, flush, rename) and keep the previous version as `.bak`; loading
  recovers from the `.bak` if the main file is missing or unreadable.
- A `[Target:...]` with no `Address`, or a duplicate name, is skipped on load rather than
  half-loaded.

---

## `[Settings]`

Global defaults. Every numeric probe setting can be overridden per target.

| Key | Default | Range | Meaning |
|---|---|---|---|
| `IntervalMs` | `2000` | 250 – 3 600 000 | Time between probes of one target |
| `TimeoutMs` | `2000` | 100 – 60 000 | Per-probe wait. Pulled down to `IntervalMs` if larger, since a longer timeout would permanently skip ticks against a dead host |
| `PayloadBytes` | `32` | 0 – 65 500 | ICMP payload size |
| `Ttl` | `64` | 1 – 255 | ICMP TTL |
| `RollingWindow` | `300` | 10 – 10 000 | Probes kept per target for Loss %, avg/min/max, jitter and the graphs. A fixed, preallocated ring |
| `PreferIPv4` | `true` | | Pick the A record when a name has both |
| `DnsCacheSeconds` | `300` | 1 – 86 400 | How long a resolved name is trusted |
| `MaxConcurrent` | `32` | 1 – 512 | Ceiling on simultaneous in-flight probes |
| `FailuresBeforeDown` | `3` | 1 – 100 | Consecutive failures before a target is declared down |
| `FailuresBeforeReresolve` | `3` | 1 – 100 | Consecutive failures before the name is re-resolved ahead of the DNS TTL |
| `ResumeSettleMs` | `5000` | 0 – 120 000 | Grace after wake before probing resumes |
| `NotifyOnChange` | `true` | | Desktop notification on down/recovered |
| `NotifyOnDegraded` | `false` | | Desktop notification on entering/leaving degraded |
| `LogEnabled` | `true` | | Write the transition CSV |
| `LogPath` | `pingboard-events.csv` | | Transition CSV; relative paths resolve beside the `.ini` |
| `OutageLogEnabled` | `true` | | Persist outages to `<board>.outages.csv` |
| `DegradedLatencyMs` | `0` (off) | ≤ 0 → off; max 600 000 | Average RTT above which a still-replying target shows as degraded |
| `DegradedLossPercent` | `0` (off) | ≤ 0 or non-numeric → off; otherwise 0.1 – 100 | Loss % above which a target shows as degraded |
| `DegradedSamples` | `20` | 3 – 1000 | Recent probes the degraded assessment averages over |
| `CertCheckHours` | `6` | 1 – 720 | How often to re-read each HTTPS target's certificate |
| `CertWarnDays` | `14` | 1 – 365 | Flag a certificate with fewer days than this left |
| `TraceOnFailure` | `true` | | Trace the path when a target is declared down |
| `TraceMaxHops` | `30` | 1 – 64 | |
| `TraceHopTimeoutMs` | `1000` | 100 – 10 000 | Per-hop wait |

The two `Degraded*` thresholds are **off by default on purpose**: "too slow" is a statement about
one link (80 ms is a broken LAN and an excellent path to the other side of the world), so it is
meant to be set per target. See the comment on `DegradedLatencyMs` in `Settings.cs`.

---

## `[Target:<name>]`

The section name is the target's identity — it keys the persisted counters, so it must be unique
and renaming it resets that target's history.

| Key | Default | Meaning |
|---|---|---|
| `Address` | *required* | An IP literal or a hostname |
| `Probe` | `icmp` | `icmp`, `tcp`, `http` or `https` |
| `Port` | `80` for `http`, otherwise `443`; clamped 1 – 65 535 | Written only for `tcp`/`http`/`https` |
| `Path` | `/` | Request path for `http`/`https`. Written only when it isn't `/` |
| `ExpectStatus` | *any 2xx/3xx* | Status code an HTTP probe must see; clamped 100 – 599 |
| `Enabled` | `true` | `false` pauses the target. Written only when false |
| `Tab` | *(General)* | The one tab this target belongs to. Empty means the default group |
| `Site` | *(none)* | The one physical site it belongs to. Empty means none |
| `Tags` | *(none)* | Free-form labels, any number, comma-separated |
| `Maintenance` | *(none)* | Quiet hours, e.g. `Sat 22:00-02:00, Sun 03:00-05:00`. Probing continues; only alerts are held back. Syntax in the [README](../README.md#maintenance-windows) |

**Per-target overrides** — each inherits the `[Settings]` value when absent:

`IntervalMs` · `TimeoutMs` · `PayloadBytes` · `Ttl` · `FailuresBeforeDown` · `DegradedLatencyMs` ·
`DegradedLossPercent`

`FailuresBeforeDown` is worth setting per target because it only means something relative to that
target's interval: three failures at 5 s is fifteen seconds of silence, at 1 s it is three.

```ini
[Target:api]
Address=api.example.com
Probe=https
Path=/healthz
ExpectStatus=200
IntervalMs=5000
FailuresBeforeDown=2
DegradedLatencyMs=300
Tab=Services
Site=Primary
Tags=prod, customer-facing
Maintenance=Sun 03:00-05:00
```

---

## `[Tab:<name>]`

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` stops probing every target in the tab, exactly as if each were paused |
| `Muted` | `false` | Silences desktop, webhook and email alerts for the tab while probing continues |
| `Order` | file order | Display order |
| `SelectedTags` | *(none)* | Non-empty turns the tab into a **saved view** over the *whole* board: it shows every target carrying at least one of these tags, wherever its own `Tab` points |
| `SelectedSites` | *(none)* | The same, against `Site`. When both filters are set a target must pass both |

Membership lives on the **target** (`Tab=`), not here, so a target belongs to exactly one tab for
probing, muting and enable/disable purposes. A filter only changes what the tab *displays*. The
default group is called `General`; it cannot be renamed or deleted.

## `[Site:<name>]`

| Key | Meaning |
|---|---|
| `Abbreviation` | Short form for the `Site Abbreviation` column. Stored once per site, not per target |

A site is a *place*; a tab is a *function*. They are deliberately orthogonal.

---

## `[Alerts]`

Where a transition is sent beyond the tray notification and the CSV. Both sinks are **off by
default**, and an enabled sink with nowhere to send (no URL / no host, from or to) is switched off
on load rather than left silently broken.

| Key | Default | Range | Meaning |
|---|---|---|---|
| `WebhookEnabled` | `false` | | |
| `WebhookUrl` | *(empty)* | must be `http`/`https` | |
| `WebhookAuthorization` | *(empty)* | | Value of the `Authorization` header. **DPAPI-encrypted** |
| `EmailEnabled` | `false` | | |
| `SmtpHost` | *(empty)* | | |
| `SmtpPort` | `587` | 1 – 65 535 | |
| `SmtpUseStartTls` | `true` | | |
| `SmtpUser` | *(empty)* | | |
| `SmtpPassword` | *(empty)* | | **DPAPI-encrypted** |
| `EmailFrom`, `EmailTo` | *(empty)* | | Both required for the sink to stay enabled |
| `MinIntervalSeconds` | `60` | 0 – 86 400 | Suppresses repeat alerts **for the same target** within this window, so a flapping host does not fill an inbox. `0` disables the suppression |
| `NotifyOnRecovery` | `true` | | |
| `NotifyOnDegraded` | `false` | | |
| `TimeoutMs` | `10000` | 1000 – 120 000 | Per-delivery timeout |

**Secrets.** `WebhookAuthorization` and `SmtpPassword` are stored as `dpapi:<base64>`, encrypted to
the Windows **user profile** that wrote them, so a config that ends up in a backup, a sync folder
or a repo carries no usable credential. The corollary is deliberate: copied to another machine or
account they normally cannot be decrypted and load as **empty** — re-enter them there (⚙ →
*Settings…* → *Alerting* → *Send a test alert*). A plaintext value typed straight into the file is
accepted and encrypted on the next save; never commit one.

---

## `ui-state.ini`

Per-machine UI preferences, in `%AppData%\PingBoard\ui-state.ini`. It is generated and safe to
delete (you lose the preferences, not the board). It is not part of a board and does not travel
with one.

| Key | Default | Meaning |
|---|---|---|
| `LastConfigPath` | | The board to reopen on a plain launch. Saved the moment the board changes |
| `WindowX`, `WindowY`, `WindowWidth`, `WindowHeight` | | Placement for the monitor layout it was saved on |
| `Theme` | `System` | `System`, `Light`, `Dark` or `Matrix` |
| `ZoomPercent` | `100` | 70 – 250 |
| `AutoFitColumns` | `true` | |
| `ColumnOrder` | | Column order, a single setting across the board |
| `HiddenColumns` | | Hidden columns; per-tab overrides are kept alongside once a tab diverges |
| `MuteUntil` | | End of a global desktop-notification mute |
| `CheckUpdatesOnStartup` | `true` | |
| `LastUpdateCheck` | | |