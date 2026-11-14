param(
    [Parameter(Mandatory = $true)][string]$Action,
    [string]$Path = "",
    [int]$MaxWindows = 6
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Cap {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
"@
[Cap]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

function Get-UnityProcess {
    $process = Get-Process -Name Unity -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 -or $true } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if (-not $process) { throw "No Unity process found." }
    return $process
}

function Get-ProcessWindows([int]$wantedPid) {
    $list = New-Object System.Collections.ArrayList
    $callback = [Cap+EnumProc]{
        param($h, $p)
        $wpid = 0
        [Cap]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
        if ($wpid -eq $wantedPid -and [Cap]::IsWindowVisible($h)) {
            $rect = New-Object Cap+RECT
            [Cap]::GetWindowRect($h, [ref]$rect) | Out-Null
            if ($rect.R - $rect.L -gt 40 -and $rect.B - $rect.T -gt 40 -and $rect.L -gt -10000) {
                $title = New-Object System.Text.StringBuilder 256
                [Cap]::GetWindowText($h, $title, 256) | Out-Null
                $className = New-Object System.Text.StringBuilder 256
                [Cap]::GetClassName($h, $className, 256) | Out-Null
                [void]$list.Add([pscustomobject]@{
                    handle = $h
                    left = $rect.L; top = $rect.T; right = $rect.R; bottom = $rect.B
                    width = $rect.R - $rect.L; height = $rect.B - $rect.T
                    title = $title.ToString(); className = $className.ToString()
                })
            }
        }
        return $true
    }
    [Cap]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    return $list
}

function Capture-Window($window) {
    $bitmap = New-Object System.Drawing.Bitmap($window.width, $window.height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    $ok = [Cap]::PrintWindow($window.handle, $hdc, 2)
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
    if (-not $ok) {
        $bitmap.Dispose()
        return $null
    }
    return $bitmap
}

$process = Get-UnityProcess
$windows = Get-ProcessWindows $process.Id
if ($windows.Count -eq 0) { throw "No visible Unity windows for pid $($process.Id)." }

$main = $windows | Sort-Object { $_.width * $_.height } -Descending | Select-Object -First 1

switch ($Action) {
    "list" {
        $windows | ForEach-Object { "$($_.className) '$($_.title)' $($_.left),$($_.top) $($_.width)x$($_.height) hwnd=$($_.handle)" }
    }
    "window" {
        $bitmap = Capture-Window $main
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        "saved $Path $($main.width)x$($main.height)"
    }
    "composite" {
        $canvas = New-Object System.Drawing.Bitmap($main.width, $main.height)
        $graphics = [System.Drawing.Graphics]::FromImage($canvas)
        $graphics.Clear([System.Drawing.Color]::FromArgb(30, 30, 30))

        # EnumWindows returns topmost first; draw the main window, then popups bottom-up.
        $overlays = @($windows | Where-Object { $_.handle -ne $main.handle } | Select-Object -First $MaxWindows)
        [array]::Reverse($overlays)

        $mainBitmap = Capture-Window $main
        if ($null -eq $mainBitmap) { throw "PrintWindow failed for the main editor window." }
        $graphics.DrawImage($mainBitmap, 0, 0, $main.width, $main.height)
        $mainBitmap.Dispose()

        foreach ($window in $overlays) {
            $bitmap = Capture-Window $window
            if ($null -ne $bitmap) {
                $graphics.DrawImage($bitmap, $window.left - $main.left, $window.top - $main.top, $window.width, $window.height)
                $bitmap.Dispose()
            }
        }
        $graphics.Dispose()
        $canvas.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $canvas.Dispose()
        "saved $Path $($main.width)x$($main.height) overlays=$($overlays.Count)"
    }
    default { throw "Unknown action: $Action" }
}
