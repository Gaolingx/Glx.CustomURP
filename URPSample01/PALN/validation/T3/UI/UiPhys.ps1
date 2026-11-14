param(
    [Parameter(Mandatory = $true)][string]$Action,
    [int]$X = 0,
    [int]$Y = 0,
    [string]$Path = "",
    [string]$Text = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Phys {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
}
"@

[Phys]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

function Get-Handle {
    $process = Get-Process -Name Unity -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -match 'URPSample01' } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if (-not $process) { throw "No Unity editor window found." }
    return $process.MainWindowHandle
}

function Focus-Window([IntPtr]$handle) {
    $fg = [Phys]::GetForegroundWindow()
    $cur = [Phys]::GetCurrentThreadId()
    $fgThread = 0
    [Phys]::GetWindowThreadProcessId($fg, [ref]$fgThread) | Out-Null
    $attached = $false
    if ($fgThread -ne 0 -and $fgThread -ne $cur) {
        $attached = [Phys]::AttachThreadInput($cur, $fgThread, $true)
    }
    [Phys]::SetWindowPos($handle, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null
    [Phys]::BringWindowToTop($handle) | Out-Null
    [Phys]::SetForegroundWindow($handle) | Out-Null
    if ($attached) { [Phys]::AttachThreadInput($cur, $fgThread, $false) | Out-Null }
    Start-Sleep -Milliseconds 300
}

switch ($Action) {
    "info" {
        $handle = Get-Handle
        $rect = New-Object Phys+RECT
        [Phys]::GetWindowRect($handle, [ref]$rect) | Out-Null
        $point = New-Object Phys+POINT
        [Phys]::GetCursorPos([ref]$point) | Out-Null
        [pscustomobject]@{
            hwnd = $handle.ToInt64()
            dpi = [Phys]::GetDpiForWindow($handle)
            left = $rect.L; top = $rect.T; right = $rect.R; bottom = $rect.B
            foreground = ([Phys]::GetForegroundWindow() -eq $handle)
            cursorX = $point.X; cursorY = $point.Y
        } | ConvertTo-Json -Compress
    }
    "snap" {
        $handle = Get-Handle
        $rect = New-Object Phys+RECT
        [Phys]::GetWindowRect($handle, [ref]$rect) | Out-Null
        $width = $rect.R - $rect.L
        $height = $rect.B - $rect.T
        $bitmap = New-Object System.Drawing.Bitmap($width, $height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($rect.L, $rect.T, 0, 0, $bitmap.Size)
        $graphics.Dispose()
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        "saved $Path ${width}x${height} at $($rect.L),$($rect.T)"
    }
    "click" {
        $handle = Get-Handle
        Focus-Window $handle
        $rect = New-Object Phys+RECT
        [Phys]::GetWindowRect($handle, [ref]$rect) | Out-Null
        [Phys]::SetCursorPos($rect.L + $X, $rect.T + $Y) | Out-Null
        Start-Sleep -Milliseconds 250
        $point = New-Object Phys+POINT
        [Phys]::GetCursorPos([ref]$point) | Out-Null
        [Phys]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [Phys]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 700
        "clicked window $X,$Y -> cursor $($point.X),$($point.Y)"
    }
    "hover" {
        $handle = Get-Handle
        Focus-Window $handle
        $rect = New-Object Phys+RECT
        [Phys]::GetWindowRect($handle, [ref]$rect) | Out-Null
        [Phys]::SetCursorPos($rect.L + $X, $rect.T + $Y) | Out-Null
        Start-Sleep -Milliseconds 900
        "hovered $X,$Y"
    }
    "type" {
        Focus-Window (Get-Handle)
        [System.Windows.Forms.SendKeys]::SendWait($Text)
        Start-Sleep -Milliseconds 400
        "typed $Text"
    }
    default { throw "Unknown action: $Action" }
}
