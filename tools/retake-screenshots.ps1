<#
.SYNOPSIS
  Regenerates docs/screenshots from the public demo board, without touching your real board.

.DESCRIPTION
  Launches the app against a scratch copy of docs/demo.ini, lets it build up history, drives it
  with UI Automation, and captures the three README screenshots:

    board-dark.png    the board
    board-detail.png  one row expanded to show its latency graph
    board-matrix.png  the green-phosphor theme

  What it protects, because launching the app rewrites state you care about:

  * %AppData%\PingBoard\ui-state.ini decides which board your installed copy reopens, and
    launching with --config overwrites it. It is backed up first and restored in a `finally`
    block, with a hash check, however the run ends.
  * Only the demo process this script started is ever stopped - never an installed copy.
  * The demo board has notifications, logging and trace-on-failure off, so it cannot raise a toast
    or fire a traceroute.
  * New PNGs are written to a scratch folder and copied into docs/screenshots only if all three
    succeeded, so a half-failed run cannot replace good screenshots with bad ones.

  It cannot work on a locked workstation: Windows stops compositing apps, and every capture is a
  black frame. The script checks for that first and stops.

  The demo window is briefly raised over your other windows (without taking focus) for each
  capture, and is visible for the few minutes of warm-up.

.PARAMETER Exe
  The built app. Defaults to the Release build; build it first with
  `dotnet build PingBoard.slnx -c Release`.

.PARAMETER WarmupSeconds
  How long to let the demo board probe before capturing. History is what makes the sparklines and
  graph worth looking at; ~150 s is enough for the latency graph to fill.

.PARAMETER SkipLockCheck
  For testing the launch/UI-automation/cleanup path on a locked machine. The captures will fail.
#>
param(
    [string]$Exe,
    [int]$WarmupSeconds = 150,
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

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('pingboard-shots-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$shots   = Join-Path $scratch 'out'
$demoDir = Join-Path $scratch 'demo'
New-Item -ItemType Directory -Force -Path $shots, $demoDir | Out-Null
$demo = Join-Path $demoDir 'demo.ini'
Copy-Item (Join-Path $repo 'docs\demo.ini') $demo

# ---- protect the real UI state ----------------------------------------------------------------
$live = Join-Path $env:APPDATA 'PingBoard'
New-Item -ItemType Directory -Force -Path $live | Out-Null
$backup = @{}
foreach ($f in 'ui-state.ini', 'ui-state.ini.bak') {
    $src = Join-Path $live $f
    if (Test-Path $src) { $backup[$f] = @{ Bytes = [IO.File]::ReadAllBytes($src); Hash = (Get-FileHash $src).Hash } }
}

function Write-Seed([string]$theme) {
    # Deterministic UI: window geometry, which columns show, theme, and no update check.
    $lines = @(
        '[Ui]', 'WindowX=60', 'WindowY=60', 'WindowWidth=1486', 'WindowHeight=463',
        'HiddenColumns=AvgMinMax,Fails,Uptime,Probe,SiteName,SiteAbbreviation,Tags',
        "Theme=$theme", 'AutoFitColumns=true', 'CheckUpdatesOnStartup=false', "LastConfigPath=$demo"
    )
    [IO.File]::WriteAllText((Join-Path $live 'ui-state.ini'), ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
}

$script:demoProc = $null
function Start-Demo([string]$theme) {
    Write-Seed $theme
    $script:demoProc = Start-Process $Exe -ArgumentList '--config', "`"$demo`"" -PassThru
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
function Expand-Row([string]$name) {
    $win = $AE::FromHandle([IntPtr]$script:demoProc.MainWindowHandle)
    $rows = $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)))
    foreach ($row in $rows) {
        $hit = $row.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)))
        if ($hit) {
            $btn = $row.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Show latency graph and path trace')))
            $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            return
        }
    }
    throw "No row named '$name' on the demo board."
}

try {
    Write-Host "Starting the demo board (dark)..."
    Start-Demo 'Dark'
    Write-Host "Warming up for $WarmupSeconds s so there is history to show..."
    Start-Sleep -Seconds $WarmupSeconds

    & $capture -ProcessId $script:demoProc.Id -Out (Join-Path $shots 'board-dark.png') -Raise

    Expand-Row 'github.com'
    Start-Sleep -Seconds 2
    & $capture -ProcessId $script:demoProc.Id -Out (Join-Path $shots 'board-detail.png') -Raise

    # Theme is read at launch, so relaunch rather than drive the menu. History survives: it is
    # persisted to the demo's own sidecar every minute and restored on load.
    Write-Host "Restarting in the Matrix theme..."
    Stop-Demo
    Start-Demo 'Matrix'
    Start-Sleep -Seconds 25
    & $capture -ProcessId $script:demoProc.Id -Out (Join-Path $shots 'board-matrix.png') -Raise

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
        elseif (Test-Path $dst -and $f -eq 'ui-state.ini') {
            # There was none before, so leaving the demo's seed behind would point a later launch at a deleted board.
            Remove-Item $dst -Force
            Write-Host "Removed the demo's ui-state.ini (none existed before)."
        }
    }
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}