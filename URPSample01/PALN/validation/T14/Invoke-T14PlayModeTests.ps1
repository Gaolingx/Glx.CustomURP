param(
    [string]$LogPath = (Join-Path $PSScriptRoot "UnityPlayModeTests.log"),
    [string]$ResultsPath = (Join-Path $PSScriptRoot "UnityPlayModeTests.xml")
)

# PlayMode tests must be driven by Unity's -runTests CLI: with -executeMethod + -quit the editor
# terminates before the deferred play-mode start, so the TestRunnerApi path only works for EditMode.
$ErrorActionPreference = "Stop"
$wrapper = Join-Path $PSScriptRoot "..\T1\Invoke-T1Unity.ps1"
$restorationPath = Join-Path $PSScriptRoot ([IO.Path]::GetFileNameWithoutExtension($LogPath) + "SettingsRestoration.json")

& $wrapper `
    -LogPath $LogPath `
    -ReportPath $restorationPath `
    -AdditionalArguments @(
        "-runTests",
        "-testPlatform", "PlayMode",
        "-testResults", $ResultsPath,
        "-testFilter", "Unity.ScreenSpaceLighting.Tests.PlayMode"
    )
exit $LASTEXITCODE