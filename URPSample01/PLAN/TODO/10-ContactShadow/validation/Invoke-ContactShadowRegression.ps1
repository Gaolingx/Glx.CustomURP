param([Parameter(Mandatory = $true)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$project = Resolve-Path (Join-Path $PSScriptRoot '../../../..')
$output = [IO.Directory]::CreateDirectory($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)).FullName
foreach ($name in 'Regression','PlayMode') {
    $reference = ([xml](Get-Content -Raw (Join-Path $PSScriptRoot "T4/Regression/$name.xml"))).'test-run'
    $filter = ($reference.SelectNodes('.//test-suite[@type="TestFixture"]') | ForEach-Object { $_.fullname } | Sort-Object -Unique) -join ';'
    $mode = if ($name -eq 'Regression') { 'EditMode' } else { 'PlayMode' }
    $xml = Join-Path $output "$name.xml"
    $start = [DateTime]::UtcNow
    & (Join-Path $project 'PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1') -LogPath (Join-Path $output "$name.log") -ReportPath (Join-Path $output "${name}Settings.json") -AdditionalArguments @('-force-d3d11','-runTests','-testPlatform',$mode,'-testFilter',$filter,'-testResults',('"{0}"' -f $xml))
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (!(Test-Path $xml) -or (Get-Item $xml).LastWriteTimeUtc -lt $start) { throw "Missing fresh $mode result." }
    $run = ([xml](Get-Content -Raw $xml)).'test-run'
    $difference = Compare-Object @($reference.SelectNodes('.//test-case') | ForEach-Object { $_.fullname }) @($run.SelectNodes('.//test-case') | ForEach-Object { $_.fullname })
    if ($difference -or $run.result -ne 'Passed' -or $run.passed -ne $reference.total -or $run.total -ne $reference.total) { throw "$mode regression: $($run.passed)/$($reference.total)" }
    Write-Host "$mode $($run.passed)/$($run.total)"
}
