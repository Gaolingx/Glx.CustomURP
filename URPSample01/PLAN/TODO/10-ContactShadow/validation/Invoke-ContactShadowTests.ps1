param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string]$Filter = 'ContactShadowVolumeTests;ContactShadowFeatureImportTests',
    [switch]$RecordImportEvidence,
    [switch]$RecordDepthEvidence,
    [switch]$RecordRaymarchEvidence,
    [switch]$RecordSlotEvidence,
    [ValidateSet('d3d11', 'd3d12')][string]$GraphicsApi = 'd3d11'
)
$ErrorActionPreference = 'Stop'
$project = Resolve-Path (Join-Path $PSScriptRoot '../../../..')
$output = [IO.Directory]::CreateDirectory($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)).FullName
$results = Join-Path $output 'EditMode.xml'
$start = [DateTime]::UtcNow
$arguments = @('-runTests', '-testPlatform', 'EditMode', '-testFilter', $Filter, '-testResults', ('"{0}"' -f $results))
$arguments += "-force-$GraphicsApi"
if ($RecordSlotEvidence) { $arguments += @('-contactShadowT4Evidence', ('"{0}"' -f $output)) }
if ($RecordDepthEvidence) { $arguments += @('-contactShadowT2Evidence', ('"{0}"' -f $output)) }
if ($RecordRaymarchEvidence) { $arguments += @('-contactShadowT3Evidence', ('"{0}"' -f $output)) }
if ($RecordImportEvidence) { $arguments += @('-contactShadowT1Evidence', ('"{0}"' -f $output)) }
& (Join-Path $project 'PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1') -LogPath (Join-Path $output 'EditMode.log') -ReportPath (Join-Path $output 'EditModeSettings.json') -AdditionalArguments $arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (!(Test-Path $results) -or (Get-Item $results).LastWriteTimeUtc -lt $start) { throw 'Unity did not produce a new NUnit result.' }
$run = ([xml](Get-Content -Raw $results)).'test-run'
if ($run.result -ne 'Passed' -or [int]$run.passed -eq 0 -or (($RecordRaymarchEvidence -or $RecordSlotEvidence) -and [int]$run.passed -ne [int]$run.total)) { throw "EditMode: $($run.result), passed $($run.passed)/$($run.total)" }
foreach ($class in $Filter.Split(';')) {
    if (!($run.SelectNodes('.//test-case') | Where-Object { $_.fullname.Contains($class) })) { throw "No tests discovered for $class" }
}
