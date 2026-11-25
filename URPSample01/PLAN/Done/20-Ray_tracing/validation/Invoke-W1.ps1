[CmdletBinding()]
param(
    [ValidateSet('T1','G1')][string[]]$Milestone = @('T1','G1'),
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Run = ('run-' + [datetime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
)
$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot/../../../..").Path
$wrapper = "$project/PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1"
$ns = 'UnityEditor.Rendering.Universal.Tests'
$pkg = "$project/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime"
foreach ($id in $Milestone) {
    $fixture = if ($id -eq 'T1') { "$ns.RayTracingSceneGateTests" } else { "$ns.RayTracingProbeMathTests" }
    $apis = if ($id -eq 'T1') { 11,12 } else { 11 }
    foreach ($api in $apis) {
        $out = "$PSScriptRoot/$id/Win64-D3D$api/$Run"
        if (Test-Path -LiteralPath $out) { throw "Select a fresh -Run; output exists: $out" }
        [void][IO.Directory]::CreateDirectory($out)
        $argsForUnity = @("-force-d3d$api",'-runTests','-testPlatform','EditMode','-testFilter',$fixture,'-testResults',('"{0}/Tests.xml"' -f $out))
        $files = @(Get-ChildItem "$project/Assets/Tests/RayTracing" -Recurse -File) + @(Get-ChildItem "$pkg/RendererFeatures/RayTracing" -Recurse -File) + @(Get-Item "$pkg/AssemblyInfo.cs")
        $changed = @(git -C $project diff HEAD --name-only --relative -- 'Packages/*.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Cannot identify modified runtime inputs' }
        $files += @($changed | ForEach-Object { Get-Item -LiteralPath "$project/$_" })
        $inputs = [ordered]@{}
        foreach ($f in $files | Sort-Object FullName) { $inputs[$f.FullName.Substring($project.Length+1).Replace('\','/')] = (Get-FileHash -LiteralPath $f.FullName).Hash }
        $report = [ordered]@{ sourceBase=(git -C $project rev-parse HEAD); freezeSha256=(Get-FileHash "$PSScriptRoot/B0/Freeze.json").Hash; inputs=$inputs; fixture=$fixture; api="Direct3D $api"; status='blocked'; scope='EditMode fixture, not standalone Player or GPU dispatch proof' }
        try {
            & $wrapper -LogPath "$out/Unity.log.txt" -ReportPath "$out/Restoration.json" -AdditionalArguments $argsForUnity
            $report.exitCode = $LASTEXITCODE
            $restore = Get-Content -LiteralPath "$out/Restoration.json" -Raw | ConvertFrom-Json
            $report.settingsRestored = $restore.allSettingsRestored
            $log = [IO.File]::ReadAllText("$out/Unity.log.txt")
            $report.actualDevice = [regex]::Match($log,'(?m)^\s*Version:\s+(Direct3D [^\r\n]+)').Groups[1].Value
            $report.gpu = [regex]::Match($log,'(?m)^\s*Renderer:\s+([^\r\n]+)').Groups[1].Value
            if (!(Test-Path -LiteralPath "$out/Tests.xml")) {
                $report.compilerErrors = @([regex]::Matches($log,'(?m)^[^\r\n]*\berror CS\d+:[^\r\n]*') | ForEach-Object { $_.Value } | Select-Object -Unique)
                throw "No NUnit result for $id D3D$api (Unity exit $($report.exitCode)); see $out/Unity.log.txt"
            }
            $xml = ([xml][IO.File]::ReadAllText("$out/Tests.xml")).'test-run'
            $cases = @($xml.SelectNodes('.//test-case'))
            $report.total = [int]$xml.total; $report.passed = [int]$xml.passed
            $report.failedTests = @($cases | Where-Object { $_.result -ne 'Passed' } | ForEach-Object { $_.fullname })
            $report.status = 'failed'
            if ($report.exitCode -ne 0 -or !$report.settingsRestored -or $report.actualDevice -notmatch "^Direct3D $api\b" -or $xml.result -ne 'Passed' -or $report.total -lt 1 -or $report.passed -ne $report.total -or $cases.Count -ne $report.total -or @($cases | Where-Object { $_.classname -ne $fixture }).Count) { throw "Failed $id D3D$api; see $out" }
            $report.status = 'passed'
        } finally { [IO.File]::WriteAllText("$out/Summary.json", ($report | ConvertTo-Json -Depth 5)) }
        Write-Host "$id D3D$api : $($report.passed)/$($report.total), settings restored"
    }
}
