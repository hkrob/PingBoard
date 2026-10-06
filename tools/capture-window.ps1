<#
.SYNOPSIS
  Saves a PNG of one window without taking focus from whoever is using the machine.

.DESCRIPTION
  Uses PrintWindow with PW_RENDERFULLCONTENT, so a window that is on another monitor or partly
  covered still renders, and nothing is brought to the front.

  Two things this guards against, both learned the hard way:

  * DPI. A process that is not DPI-aware is given virtualised window rectangles, so the capture
    comes out cropped on a scaled display. The script makes itself per-monitor aware first.
  * Black frames. WinUI 3 stops rendering a window that is minimised, hidden to the tray, or
    completely covered by other windows - and Windows stops compositing apps altogether while the
    workstation is LOCKED - and PrintWindow then returns solid black. The script
    refuses to save an all-black image rather than producing a screenshot that looks fine until
    someone opens it. If the window is merely covered, use -Raise.

.PARAMETER ProcessId
  Process whose main window to capture.

.PARAMETER Out
  PNG path to write.

.PARAMETER Raise
  Lift the window to the top for the instant of the capture, then put it back. It is raised
  WITHOUT being activated, so it never takes the keyboard focus, but it is briefly visible over
  whatever else is on screen. Needed when other windows cover it.

.EXAMPLE
  .\capture-window.ps1 -ProcessId 1234 -Out board.png
#>
param(
    [Parameter(Mandatory)] [int]$ProcessId,
    [Parameter(Mandatory)] [string]$Out,
    [switch]$Raise
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class CaptureNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

# DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 is (HANDLE)-4.
[void][CaptureNative]::SetProcessDpiAwarenessContext([IntPtr](-4))

$proc = Get-Process -Id $ProcessId
for ($i = 0; $i -lt 60 -and $proc.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $proc.Refresh() }
if ($proc.MainWindowHandle -eq 0) {
    throw "Process $ProcessId has no visible main window. A window hidden to the tray cannot be captured."
}
$hwnd = $proc.MainWindowHandle
if ([CaptureNative]::IsIconic($hwnd)) { throw "The window is minimised; restore it first (a minimised WinUI window renders black)." }

$rect = New-Object CaptureNative+RECT
[void][CaptureNative]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { throw "Window has no size ($w x $h)." }

# SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE. HWND_TOPMOST = -1, HWND_NOTOPMOST = -2.
$noMoveSizeActivate = 0x0001 -bor 0x0002 -bor 0x0010
if ($Raise) {
    [void][CaptureNative]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, $noMoveSizeActivate)
    Start-Sleep -Milliseconds 900     # let the compositor draw a frame now that it is uncovered
}

$bmp = New-Object System.Drawing.Bitmap($w, $h)
try {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    try { $ok = [CaptureNative]::PrintWindow($hwnd, $dc, 2) }   # 2 = PW_RENDERFULLCONTENT
    finally { $g.ReleaseHdc($dc); $g.Dispose() }
    if (-not $ok) { throw "PrintWindow failed." }

    # A real UI always has some non-black pixel, even the black Matrix theme (it has green text).
    $lit = 0
    for ($y = 0; $y -lt $h; $y += 7) {
        for ($x = 0; $x -lt $w; $x += 7) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.R -gt 40 -or $c.G -gt 40 -or $c.B -gt 40) { $lit++ }
        }
    }
    if ($lit -lt 20) { throw "Captured an all-black frame ($lit lit samples). Windows only composites apps on an unlocked, awake desktop, so the usual causes are: the workstation is LOCKED, the display is off, the window is hidden to the tray or minimised, or it has not drawn yet." }

    $dir = Split-Path -Parent $Out
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $bmp.Dispose()
    if ($Raise) { [void][CaptureNative]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, $noMoveSizeActivate) }
}

"captured ${w}x${h} -> $Out"