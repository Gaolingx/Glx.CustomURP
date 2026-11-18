param(
    [string]$LogPath = (Join-Path $PSScriptRoot "UnityEditModeTests.log"),
    [string]$ResultsPath = (Join-Path $PSScriptRoot "UnityEditModeTests.xml")
)

# The T14 regression drives EditMode through -runTests instead of TestRunnerApi + -executeMethod +
# -quit: the TestRunnerApi run is deferred by one editor tick and the CLI quit can win the race,
# which silently produced an empty run. -runTests also writes the NUnit XML the report consumes.
$ErrorActionPreference = "Stop"
$wrapper = Join-Path $PSScriptRoot "..\T1\Invoke-T1Unity.ps1"
$restorationPath = Join-Path $PSScriptRoot ([IO.Path]::GetFileNameWithoutExtension($LogPath) + "SettingsRestoration.json")

$filters = @(
    "UnityEditor.Rendering.Universal.Tests.UniversalRendererPassInputTests",
    "UnityEditor.Rendering.Universal.Tests.CameraHistoryStoreTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceCapabilityTests",
    "UnityEditor.Rendering.Universal.Tests.PluginIsolationTests",
    "UnityEditor.Rendering.Universal.Tests.PackedMipChainInfoTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingVolumeTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingRendererFeatureTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingHistoryTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingSsgiTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingSsrTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingSsrPbrTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingTransparentSsrTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingRefractionTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingT12SchedulingTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingMaterialTests",
    "UnityEditor.Rendering.Universal.Tests.GroundTruthAmbientOcclusionRendererFeatureTests",
    "UnityEditor.Rendering.Universal.Tests.GroundTruthAmbientOcclusionHistoryTests",
    "UnityEditor.Rendering.Universal.Tests.GroundTruthAmbientOcclusionCBufferTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceDepthPyramidSharingTests",
    "UnityEditor.Rendering.Universal.Tests.ScreenSpaceEditorCameraMotionPassTests",
    "UnityEditor.Rendering.Universal.Tests.AccurateGbufferNormalsTests"
)

& $wrapper `
    -LogPath $LogPath `
    -ReportPath $restorationPath `
    -AdditionalArguments @(
        "-runTests",
        "-testPlatform", "EditMode",
        "-testResults", $ResultsPath,
        "-testFilter", ($filters -join ";")
    )
exit $LASTEXITCODE