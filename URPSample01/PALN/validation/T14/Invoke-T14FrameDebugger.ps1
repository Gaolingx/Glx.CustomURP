param(
    [string]$LogPath = (Join-Path $PSScriptRoot "FrameDebugger\UnityT14FrameDebugger.log"),
    [string]$OutputRoot = (Join-Path $PSScriptRoot "FrameDebugger")
)

# Frame Debugger capture for T14. It must run in an interactive editor with the Unity window in the
# foreground: in batch mode the debugger never receives frames and the capture times out.
$ErrorActionPreference = "Stop"
$wrapper = Join-Path $PSScriptRoot "..\T1\Invoke-T1Unity.ps1"
$restorationPath = Join-Path $PSScriptRoot ([IO.Path]::GetFileNameWithoutExtension($LogPath) + "SettingsRestoration.json")
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

& $wrapper `
    -ExecuteMethod ScreenSpaceLightingFrameDebuggerCapture.Capture `
    -LogPath $LogPath `
    -ReportPath $restorationPath `
    -InteractiveEditor `
    -AdditionalArguments @(
        "--ssl-fd-output", $OutputRoot,
        "--ssl-fd-names", "Forward,ForwardPlus,Deferred",
        "--ssl-fd-pipelines", "Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardPlusPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT12DeferredPipeline.asset",
        "--ssl-fd-scene", "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity"
    )
exit $LASTEXITCODE