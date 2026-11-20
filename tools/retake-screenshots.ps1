<#
.SYNOPSIS
  Regenerates docs/screenshots from the public demo board, without touching your real board.

.DESCRIPTION
  Launches the app against scratch copies of docs/demo.ini, lets it build up history, drives it
  with UI Automation, and captures the four README screenshots:

    board-dark.png    the board                                   (real probes)
    board-menu.png    the right-click menu on a row               (real probes)
    board-matrix.png  the green-phosphor theme                    (real probes)
    board-detail.png  one row expanded on its 7-day latency graph (SEEDED history)

  The 7-day graph cannot come from real probes - a demo board cannot build a week of history in a
  few minutes - so that board is given a generated 30-day history sidecar (fixed random seed, so
  reruns draw the same shape) and the README captions it as illustrative. Everything else is real.

  What it protects, because launching the app rewrites state you care about:

  * %AppData%\PingBoard\ui-state.ini decides which board your installed copy reopens, and
    launching with --config overwrites it. It is backed up first and restored in a `finally`
    block, with a hash check, however the run ends.
  * Only the demo processes this script started are ever stopped - never an installed copy.
  * The demo board has notifications, logging and trace-on-failure off, so it cannot raise a toast
    or fire a traceroute.
  * New PNGs are written to a scratch folder and copied into docs/screenshots only if all four
    succeeded, so a half-failed run cannot replace good screenshots with bad ones.

  It cannot work on a locked workstation: Windows stops compositing apps, and every capture is a
  black frame. The script checks for that first and stops.

  It uses the mouse, briefly, so do not touch it while the script runs. Before each capture it
  clicks the empty desktop just below the demo window (WinUI hides keyboard focus rectangles only
  after real pointer input) and puts the pointer back. The MENU shot also brings the demo window to
  the front and right-clicks a row, because a context menu only opens for real input; pass
  -SkipMenu to skip that and keep the existing board-menu.png. Leave the area below the window
  clear: whatever is there receives the click.

  Images are saved at twice the window's logical size (1250 x 515 logical, so about 2500 x 1030 px),
  whatever the display scaling, so a rerun on another machine produces comparable files.

  Works in Windows PowerShell 5.1 and in PowerShell 7.

.PARAMETER Exe
  The built app. Defaults to the Release build; build it first with
  `dotnet build PingBoard.slnx -c Release`.

.PARAMETER WarmupSeconds
  How long to let the demo board probe before capturing. History is what makes the sparklines worth
  looking at; ~150 s is enough.

.PARAMETER SkipMenu
  Leave board-menu.png as it is, and do not right-click or bring the demo window to the front.

.PARAMETER SkipLockCheck
  For testing the launch/UI-automation/cleanup path on a locked machine. The captures will fail.
#>
param(
    [string]$Exe,
    [int]$WarmupSeconds = 150,
    [switch]$SkipMenu,
    [switch]$SkipLockCheck
)

$ErrorActionPreference = 'Stop'

$repo    = Resolve-Path (Join-Path $PSScriptRoot '..')
$capture = Join-Path $PSScriptRoot 'capture-window.ps1'
if (-not $Exe) { $Exe = Join-Path $repo 'src\PingBoard.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\PingBoard.App.exe' }
if (-not (Test-Path $Exe)) { throw "App not built: $Exe`nRun: dotnet build PingBoard.slnx -c Release" }

if (-not $SkipLockCheck -and (Get-Process -Name LogonUI -ErrorAction SilentlyContinue)) {
    throw "This workstation is locked. Windows stops compositing apps while locked, so every capture would be a black frame. Unlock it and run this again."
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ShotNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
# Per-monitor aware, so UI Automation rectangles and the mouse agree in physical pixels.
[void][ShotNative]::SetProcessDpiAwarenessContext([IntPtr](-4))
$scale = [ShotNative]::GetDpiForSystem() / 96.0

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
function ByName($n) { New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $n) }
function ByType($t) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }
function Both($a, $b) { New-Object System.Windows.Automation.AndCondition($a, $b) }

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('pingboard-shots-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$shots   = Join-Path $scratch 'out'
New-Item -ItemType Directory -Force -Path $shots | Out-Null

function New-DemoBoard([string]$name) {
    $dir = Join-Path $scratch $name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $ini = Join-Path $dir 'demo.ini'
    Copy-Item (Join-Path $repo 'docs\demo.ini') $ini
    $ini
}

# ---- seeded history for the graph shot ----------------------------------------------------------
# Same encoding as AvailabilityLog: hours since the Unix epoch in LOCAL time; Availability is
# hour:ok:total, Latency is hour:min:max:sum:bin.count/... over the engine's 32 histogram bins.
$BinEdges = @(0, 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100, 125, 150, 200, 250, 300,
              400, 500, 650, 800, 1000, 1500, 2500, [int]::MaxValue)
function BinOf([int]$rtt) { for ($i = 0; $i -lt $BinEdges.Count; $i++) { if ($rtt -le $BinEdges[$i]) { return $i } }; return $BinEdges.Count - 1 }

function Write-SeededHistory([string]$statePath) {
    $rng = New-Object System.Random 11
    $now = [long][math]::Floor(((Get-Date) - [datetime]'1970-01-01').TotalHours)
    # Typical round-trip times for the demo board's public hosts. Backup site (192.0.2.10) is left
    # out: it is a documentation address, and its real all-failure history is the point of it.
    $targets = [ordered]@{ 'github.com' = 28; 'Google DNS' = 4; 'Cloudflare DNS' = 3; 'Quad9' = 30;
                           'OpenDNS' = 3; 'example.com' = 12; 'wikipedia.org' = 36 }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('; Generated by tools/retake-screenshots.ps1: ILLUSTRATIVE history for the README graph shot.')
    foreach ($name in $targets.Keys) {
        $base = $targets[$name]
        $avail = New-Object System.Collections.Generic.List[string]
        $lat = New-Object System.Collections.Generic.List[string]
        for ($h = $now - 719; $h -lt $now; $h++) {
            $age = $now - $h
            if ($age -ge 100 -and $age -lt 110) { continue }                     # machine off: a gap
            $n = 30
            if ($name -eq 'github.com' -and $age -eq 60) { $avail.Add("${h}:0:$n"); continue }  # no replies
            $fails = 0; if ($age % 41 -eq 0) { $fails = $rng.Next(1, 4) }       # a few lossy hours
            $evening = 1.0; if (($h % 24) -ge 19 -and ($h % 24) -le 23) { $evening = 1.7 }
            $bins = @{}; $min = [int]::MaxValue; $max = 0; $sum = 0
            for ($i = 0; $i -lt $n - $fails; $i++) {
                # Log-normal around the base, with an occasional slow reply.
                $u1 = 1.0 - $rng.NextDouble(); $u2 = $rng.NextDouble()
                $z = [math]::Sqrt(-2.0 * [math]::Log($u1)) * [math]::Cos(2.0 * [math]::PI * $u2)
                $r = $base * $evening * [math]::Exp(0.2 * $z)
                if ($rng.NextDouble() -lt 0.04) { $r *= 2 + 3 * $rng.NextDouble() }
                $rtt = [int][math]::Round($r)
                $b = BinOf $rtt; if ($bins.ContainsKey($b)) { $bins[$b]++ } else { $bins[$b] = 1 }
                if ($rtt -lt $min) { $min = $rtt }; if ($rtt -gt $max) { $max = $rtt }; $sum += $rtt
            }
            $ok = $n - $fails
            $avail.Add("${h}:${ok}:$n")
            $binText = ($bins.Keys | Sort-Object | ForEach-Object { "$_.$($bins[$_])" }) -join '/'
            $lat.Add("${h}:${min}:${max}:${sum}:$binText")
        }
        $lines.Add("[Target:$name]")
        $lines.Add('Availability=' + ($avail -join ','))
        $lines.Add('Latency=' + ($lat -join ','))
    }
    [IO.File]::WriteAllLines($statePath, $lines, (New-Object Text.UTF8Encoding($false)))
}

# ---- protect the real UI state ----------------------------------------------------------------
$live = Join-Path $env:APPDATA 'PingBoard'
New-Item -ItemType Directory -Force -Path $live | Out-Null
$backup = @{}
foreach ($f in 'ui-state.ini', 'ui-state.ini.bak') {
    $src = Join-Path $live $f
    if (Test-Path $src) { $backup[$f] = @{ Bytes = [IO.File]::ReadAllBytes($src); Hash = (Get-FileHash $src).Hash } }
}

function Write-Seed([string]$board, [string]$theme, [int]$logicalHeight) {
    # Deterministic UI: window geometry (in physical pixels, so scaled), which columns show, theme,
    # and no update check.
    $lines = @(
        '[Ui]', 'WindowX=30', 'WindowY=30',
        ('WindowWidth=' + [int](1260 * $scale)), ('WindowHeight=' + [int]($logicalHeight * $scale)),
        'HiddenColumns=AvgMinMax,Fails,Uptime,Probe,SiteName,SiteAbbreviation,Tags,Avail7d,CertExpiring,CertDays,LastNok',
        "Theme=$theme", 'AutoFitColumns=true', 'CheckUpdatesOnStartup=false', "LastConfigPath=$board"
    )
    [IO.File]::WriteAllText((Join-Path $live 'ui-state.ini'), ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
}

$script:demoProc = $null
function Start-Demo([string]$board, [string]$theme, [int]$logicalHeight) {
    Write-Seed $board $theme $logicalHeight
    $script:demoProc = Start-Process $Exe -ArgumentList '--config', "`"$board`"" -PassThru
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500; $script:demoProc.Refresh()
        if ($script:demoProc.MainWindowHandle -ne 0) { return }
    }
    throw 'The demo window never appeared.'
}
function Stop-Demo {
    # Only the process we started; the installed copy that is monitoring a real board is never touched.
    if ($script:demoProc -and -not $script:demoProc.HasExited) { Stop-Process -Id $script:demoProc.Id -Force; Start-Sleep -Seconds 2 }
}
function Window { $AE::FromHandle([IntPtr]$script:demoProc.MainWindowHandle) }
function Find-Row([string]$name) {
    foreach ($row in (Window).FindAll($TS::Descendants, (ByType $CT::ListItem))) {
        if ($row.FindFirst($TS::Descendants, (Both (ByType $CT::Text) (ByName $name)))) { return $row }
    }
    throw "No row named '$name' on the demo board."
}

# Resamples a capture to twice its logical size, so output does not depend on display scaling.
# $crop (optional) is the part of the source to keep, in source pixels.
function Save-Normalised([string]$src, [string]$dst, $crop = $null) {
    $img = [System.Drawing.Image]::FromFile($src)
    try {
        if (-not $crop) { $crop = New-Object System.Drawing.Rectangle 0, 0, $img.Width, $img.Height }
        $w = [int]($crop.Width * 2 / $scale); $h = [int]($crop.Height * 2 / $scale)
        $out = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($out)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($img, (New-Object System.Drawing.Rectangle 0, 0, $w, $h), $crop, [System.Drawing.GraphicsUnit]::Pixel); $g.Dispose()
        $out.Save($dst, [System.Drawing.Imaging.ImageFormat]::Png); $out.Dispose()
    }
    finally { $img.Dispose() }
}
# WinUI draws keyboard focus rectangles after keyboard or UI Automation input, and on some
# controls at launch. A real mouse click switches it back to pointer mode, which hides them, so
# click the empty desktop just below the demo window. The pointer goes back where it was.
function Clear-FocusVisuals {
    $was = [System.Windows.Forms.Cursor]::Position
    $fr = New-Object ShotNative+RECT
    [void][ShotNative]::DwmGetWindowAttribute($script:demoProc.MainWindowHandle, 9, [ref]$fr, 16)
    $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $y = [math]::Min($fr.Bottom + [int](12 * $scale), $area.Bottom - 2)
    Move-Pointer ($fr.Left + 200 * $scale) $y
    [ShotNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
    [ShotNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 800
    [void][ShotNative]::SetCursorPos($was.X, $was.Y)
}

function Capture([string]$name) {
    Clear-FocusVisuals
    $raw = Join-Path $scratch "raw-$name"
    & $capture -ProcessId $script:demoProc.Id -Out $raw -Raise | Out-Null
    # PrintWindow captures the whole window rectangle, which includes an invisible resize border
    # that renders black. Keep only the visible frame (DWMWA_EXTENDED_FRAME_BOUNDS).
    $h = $script:demoProc.MainWindowHandle
    $wr = New-Object ShotNative+RECT; [void][ShotNative]::GetWindowRect($h, [ref]$wr)
    $fr = New-Object ShotNative+RECT; [void][ShotNative]::DwmGetWindowAttribute($h, 9, [ref]$fr, 16)
    $crop = New-Object System.Drawing.Rectangle ($fr.Left - $wr.Left), ($fr.Top - $wr.Top), ($fr.Right - $fr.Left), ($fr.Bottom - $fr.Top)
    Save-Normalised $raw (Join-Path $shots $name) $crop
}

# Moves the pointer the way a hand does - in small relative steps - so WinUI sees it arrive.
# A bare SetCursorPos jump can leave the next press unarmed.
function Move-Pointer([double]$x, [double]$y) {
    [void][ShotNative]::SetCursorPos([int]$x - 12, [int]$y); Start-Sleep -Milliseconds 80
    foreach ($i in 1..4) { [ShotNative]::mouse_event(0x0001, 3, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40 }
    Start-Sleep -Milliseconds 200
}

function Capture-Menu([string]$rowName, [string]$name) {
    [void][ShotNative]::SetForegroundWindow($script:demoProc.MainWindowHandle)
    Start-Sleep -Milliseconds 500
    $cell = (Find-Row $rowName).FindFirst($TS::Descendants, (Both (ByType $CT::Text) (ByName $rowName)))
    $r = $cell.Current.BoundingRectangle
    Move-Pointer ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2)
    [ShotNative]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
    [ShotNative]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 900

    # The menu is its own popup; find its first item, and rest the pointer on it so the row's
    # tooltip closes and the item shows the hover highlight a real right-click would.
    $mine = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $script:demoProc.Id)
    $first = $AE::RootElement.FindAll($TS::Descendants, (Both (ByType $CT::MenuItem) $mine)) |
             Where-Object { $_.Current.Name -like 'Edit*' } | Select-Object -First 1
    if (-not $first) { throw 'The right-click menu did not open.' }
    $fr = $first.Current.BoundingRectangle
    Move-Pointer ($fr.X + $fr.Width * 0.4) ($fr.Y + $fr.Height / 2)
    Start-Sleep -Seconds 2

    # PrintWindow does not include popups, so this one is a screen copy of the window's visible
    # frame (DWMWA_EXTENDED_FRAME_BOUNDS, which excludes the invisible resize border).
    $b = New-Object ShotNative+RECT
    [void][ShotNative]::DwmGetWindowAttribute($script:demoProc.MainWindowHandle, 9, [ref]$b, 16)
    $bmp = New-Object System.Drawing.Bitmap ($b.Right - $b.Left), ($b.Bottom - $b.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size); $g.Dispose()
    $raw = Join-Path $scratch "raw-$name"
    $bmp.Save($raw, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Save-Normalised $raw (Join-Path $shots $name)

    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
}

try {
    $real = New-DemoBoard 'demo'

    Write-Host "Starting the demo board (dark)..."
    Start-Demo $real 'Dark' 520
    Write-Host "Warming up for $WarmupSeconds s so there is history to show..."
    Start-Sleep -Seconds $WarmupSeconds
    Capture 'board-dark.png'

    if (-not $SkipMenu) {
        Write-Host "Opening the right-click menu (this takes the mouse for a moment)..."
        Capture-Menu 'Cloudflare DNS' 'board-menu.png'
    }

    # Theme is read at launch, so relaunch rather than drive the menu. History survives: it is
    # persisted to the demo's own sidecar every minute and restored on load.
    Write-Host "Restarting in the Matrix theme..."
    Stop-Demo
    Start-Demo $real 'Matrix' 520
    Start-Sleep -Seconds 25
    Capture 'board-matrix.png'
    Stop-Demo

    Write-Host "Starting a board with seeded 30-day history for the graph shot..."
    $seeded = New-DemoBoard 'seeded'
    Write-SeededHistory ([IO.Path]::ChangeExtension($seeded, '.state.ini'))
    Start-Demo $seeded 'Dark' 650
    Start-Sleep -Seconds 90
    $row = Find-Row 'github.com'
    $row.FindFirst($TS::Descendants, (ByName 'Show latency graph and path trace')).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    $combo = (Find-Row 'github.com').FindFirst($TS::Descendants, (ByName 'Latency graph time span'))
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 800
    $week = $AE::RootElement.FindFirst($TS::Descendants, (Both (ByType $CT::ListItem) (ByName '7 days')))
    if (-not $week) { throw 'The graph range selector did not offer "7 days".' }
    $week.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 3
    Capture 'board-detail.png'
    Stop-Demo

    $dest = Join-Path $repo 'docs\screenshots'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item (Join-Path $shots '*.png') $dest -Force
    Write-Host "Updated $dest :"
    Get-ChildItem $dest -Filter *.png | ForEach-Object { '  {0}  {1:N0} bytes' -f $_.Name, $_.Length }
}
finally {
    Stop-Demo
    foreach ($f in 'ui-state.ini', 'ui-state.ini.bak') {
        $dst = Join-Path $live $f
        if ($backup.ContainsKey($f)) {
            [IO.File]::WriteAllBytes($dst, $backup[$f].Bytes)
            $ok = (Get-FileHash $dst).Hash -eq $backup[$f].Hash
            Write-Host ("Restored {0}: {1}" -f $f, $(if ($ok) { 'verified identical' } else { 'HASH MISMATCH - check it by hand' }))
        }
        elseif ((Test-Path $dst) -and $f -eq 'ui-state.ini') {
            # There was none before, so leaving the demo's seed behind would point a later launch at a deleted board.
            Remove-Item $dst -Force
            Write-Host "Removed the demo's ui-state.ini (none existed before)."
        }
    }
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
