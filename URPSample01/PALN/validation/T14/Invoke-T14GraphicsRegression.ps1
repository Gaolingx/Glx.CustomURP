param(
    [string]$LogPath = (Join-Path $PSScriptRoot "Graphics\UnityT14GraphicsRegression.log"),
    [string]$OutputRoot = (Join-Path $PSScriptRoot "Graphics")
)

# Replays the T12 combination matrix as the T14 graphics regression. -ssl-t12-output redirects the
# captures and T12Verification.json into the T14 folder so the recorded T12 evidence stays intact.
$ErrorActionPreference = "Stop"
$wrapper = Join-Path $PSScriptRoot "..\T1\Invoke-T1Unity.ps1"
$restorationPath = Join-Path $PSScriptRoot ([IO.Path]::GetFileNameWithoutExtension($LogPath) + "SettingsRestoration.json")
$absoluteOutput = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $absoluteOutput -Force | Out-Null

& $wrapper `
    -ExecuteMethod ScreenSpaceLightingT12Verification.RunT12Verification `
    -LogPath $LogPath `
    -ReportPath $restorationPath `
    -AdditionalArguments @("-ssl-t12-output", $absoluteOutput) `
    -Quit
exit $LASTEXITCODE