param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [ValidateSet('d3d11', 'd3d12')][string]$GraphicsApi = 'd3d11',
    [string]$Filter = 'ContactShadowTests.PlayMode'
)
$ErrorActionPreference = 'Stop'
$project = Resolve-Path (Join-Path $PSScriptRoot '../../../..')
$output = [IO.Directory]::CreateDirectory($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)).FullName
$xml = Join-Path $output 'Lighting.xml'
$start = [DateTime]::UtcNow
& (Join-Path $project 'PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1') -LogPath (Join-Path $output 'Lighting.log') -ReportPath (Join-Path $output 'LightingSettings.json') -AdditionalArguments @('-runTests','-testPlatform','PlayMode','-testFilter',$Filter,"-force-$GraphicsApi",'-testResults',('"{0}"' -f $xml),'-contactShadowEvidence',('"{0}"' -f $output))
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (!(Test-Path $xml) -or (Get-Item $xml).LastWriteTimeUtc -lt $start) { throw 'Missing fresh lighting results.' }
$r = ([xml](Get-Content -Raw $xml)).'test-run'
if ($r.result -ne 'Passed' -or [int]$r.passed -eq 0 -or $r.passed -ne $r.total) { throw "Lighting: $($r.passed)/$($r.total)" }
if ($GraphicsApi -eq 'd3d11' -and $Filter -eq 'ContactShadowTests.PlayMode') {
    $baseline = Get-Content -Raw (Join-Path $PSScriptRoot 'P5/PreIntegration.json') | ConvertFrom-Json
    foreach ($mode in 'Forward','ForwardPlus','Deferred') {
        $off = Join-Path $output "Direct3D11/$mode/Baseline-off.png"
        # Fresh output is per-mode; archived controls are shared only when byte-identical.
        $control = $baseline.controls.$mode
        $archived = Join-Path $PSScriptRoot $control.archivedOff
        if ((Get-FileHash -LiteralPath $archived).Hash -ne $control.offSha256 -or
            (Get-FileHash -LiteralPath $off).Hash -ne $control.offSha256) { throw "$mode inactive image differs from $($baseline.commit)" }
    }
}
Write-Host "$GraphicsApi lighting: $($r.passed)/$($r.total)"
