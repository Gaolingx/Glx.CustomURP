param(
    [Parameter(Mandatory = $true)][string]$Action,
    [int]$ProcessId = 0,
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
using System.Runtime.InteropServices;
public static class UiWin32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
}
"@

function Get-UnityWindow {
    if ($ProcessId -ne 0) {
        return (Get-Process -Id $ProcessId -ErrorAction Stop)
    }
    $process = Get-Process -Name Unity -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if (-not $process) { throw "No Unity editor window found." }
    return $process
}

function Focus-Window($process) {
    $handle = $process.MainWindowHandle
    $foreground = [UiWin32]::GetForegroundWindow()
    $currentThread = [UiWin32]::GetCurrentThreadId()
    $foregroundThread = [UiWin32]::GetWindowThreadProcessId($foreground, [IntPtr]::Zero)
    $attached = $false
    if ($foregroundThread -ne 0 -and $foregroundThread -ne $currentThread) {
        $attached = [UiWin32]::AttachThreadInput($currentThread, $foregroundThread, $true)
    }
    [UiWin32]::ShowWindow($handle, 3) | Out-Null
    [UiWin32]::SetWindowPos($handle, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002) | Out-Null
    [UiWin32]::BringWindowToTop($handle) | Out-Null
    [UiWin32]::SetForegroundWindow($handle) | Out-Null
    [UiWin32]::SetFocus($handle) | Out-Null
    if ($attached) { [UiWin32]::AttachThreadInput($currentThread, $foregroundThread, $false) | Out-Null }
    Start-Sleep -Milliseconds 400
    if ([UiWin32]::GetForegroundWindow() -ne $handle) {
        # ALT tap releases the Windows foreground lock for the calling process.
        [UiWin32]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [UiWin32]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        [UiWin32]::SetForegroundWindow($handle) | Out-Null
        Start-Sleep -Milliseconds 400
    }
    if ([UiWin32]::GetForegroundWindow() -ne $handle) {
        throw "Could not bring the Unity window to the foreground."
    }
}

function Get-WindowRect($process) {
    $rect = New-Object UiWin32+RECT
    [UiWin32]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
    return $rect
}

switch ($Action) {
    "info" {
        $process = Get-UnityWindow
        $rect = Get-WindowRect $process
        $point = New-Object UiWin32+POINT
        [UiWin32]::GetCursorPos([ref]$point) | Out-Null
        [pscustomobject]@{
            id = $process.Id
            title = $process.MainWindowTitle
            left = $rect.Left
            top = $rect.Top
            width = $rect.Right - $rect.Left
            height = $rect.Bottom - $rect.Top
            foreground = ([UiWin32]::GetForegroundWindow() -eq $process.MainWindowHandle)
            cursorX = $point.X
            cursorY = $point.Y
        } | ConvertTo-Json -Compress
    }
    "focus" {
        Focus-Window (Get-UnityWindow)
        "focused"
    }
    "snap" {
        $process = Get-UnityWindow
        $rect = Get-WindowRect $process
        $width = $rect.Right - $rect.Left
        $height = $rect.Bottom - $rect.Top
        $bitmap = New-Object System.Drawing.Bitmap($width, $height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $graphics.Dispose()
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        "saved $Path ${width}x${height}"
    }
    "fullsnap" {
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
        $graphics.Dispose()
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        "saved $Path $($bounds.Width)x$($bounds.Height)"
    }
    "cursor" {
        $point = New-Object UiWin32+POINT
        [UiWin32]::GetCursorPos([ref]$point) | Out-Null
        "cursor=$($point.X),$($point.Y)"
    }
    "clickabs" {
        $process = Get-UnityWindow
        Focus-Window $process
        [UiWin32]::SetCursorPos($X, $Y) | Out-Null
        Start-Sleep -Milliseconds 200
        $point = New-Object UiWin32+POINT
        [UiWin32]::GetCursorPos([ref]$point) | Out-Null
        [UiWin32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [UiWin32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 700
        "clicked screen $X,$Y (cursor now $($point.X),$($point.Y))"
    }
    "click" {
        $process = Get-UnityWindow
        Focus-Window $process
        $rect = Get-WindowRect $process
        [UiWin32]::SetCursorPos($rect.Left + $X, $rect.Top + $Y) | Out-Null
        Start-Sleep -Milliseconds 150
        [UiWin32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [UiWin32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 700
        "clicked $X,$Y"
    }
    "hover" {
        $process = Get-UnityWindow
        Focus-Window $process
        $rect = Get-WindowRect $process
        [UiWin32]::SetCursorPos($rect.Left + $X, $rect.Top + $Y) | Out-Null
        Start-Sleep -Milliseconds 900
        "hovered $X,$Y"
    }
    "scroll" {
        $process = Get-UnityWindow
        Focus-Window $process
        $rect = Get-WindowRect $process
        [UiWin32]::SetCursorPos($rect.Left + $X, $rect.Top + $Y) | Out-Null
        Start-Sleep -Milliseconds 150
        [UiWin32]::mouse_event(0x0800, 0, 0, [uint32]$Y, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 400
        "scrolled $Y at $X"
    }
    "type" {
        Focus-Window (Get-UnityWindow)
        [System.Windows.Forms.SendKeys]::SendWait($Text)
        Start-Sleep -Milliseconds 400
        "typed $Text"
    }
    default { throw "Unknown action: $Action" }
}
