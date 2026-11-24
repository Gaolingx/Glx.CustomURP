# From URPSample01: powershell -NoProfile -File PLAN/TODO/20-Ray_tracing/validation/B0/regression/Invoke-B0LegacyRegression.ps1
[CmdletBinding()]
param(
    [ValidateSet('D3D11','D3D12')][string[]]$Api = @('D3D11','D3D12'),
    [string]$UnityPath = 'D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]*$')][string]$RunId = ('run-' + [datetime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
)
$ErrorActionPreference = 'Stop'
$b0 = Split-Path $PSScriptRoot
$project = (Resolve-Path "$b0/../../../../..").Path
$repoHead = git -C $project rev-parse HEAD
if ($LASTEXITCODE -or !(Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw 'Cannot resolve HEAD or Unity executable' }
$editTemplate = "$project/PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14EditModeTests.ps1"
$edit = @([regex]::Matches([IO.File]::ReadAllText($editTemplate), '"(UnityEditor\.Rendering\.Universal\.Tests\.\w+)"') | ForEach-Object { $_.Groups[1].Value })
if ($edit.Count -ne 21) { throw 'Review the T14 EditMode filter list before proceeding.' }
$play = @('Unity.ScreenSpaceLighting.Tests.PlayMode')
$scene = 'Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity'
$expected = [ordered]@{Forward=34; ForwardPlus=34; Deferred=35}
function Json($path,$value) { [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 12)) }
function Sha($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function Check-FreeProject {
    if (Test-Path -LiteralPath "$project/Temp/UnityLockfile") { throw "Project has UnityLockfile; not opening $project" }
    foreach ($p in Get-CimInstance Win32_Process -Filter "Name='Unity.exe'") {
        # T1's global Unity process guard would also reject unrelated scratch projects.
        if ($p.CommandLine -notmatch '(?i)(?:^|\s)-projectPath\s+(?:"([^"]+)"|(\S+))') { throw "Cannot establish project of Unity PID $($p.ProcessId)" }
        $other = ($Matches[1] + $Matches[2]).TrimEnd('\','/')
        if (![IO.Path]::IsPathRooted($other) -or [IO.Path]::GetFullPath($other) -eq $project) { throw "Main or ambiguous editor PID $($p.ProcessId); not launching" }
    }
}
function Launch($folder,[string[]]$cli) {
    Check-FreeProject
    $log = Join-Path $folder 'Unity.log.txt'
    $argv = @('-batchmode','-projectPath',$project,'-logFile',$log,$force) + $cli
    Json "$folder/Invocation.json" ([ordered]@{ executable=$UnityPath; workingDirectory=$project; arguments=$argv; head=$repoHead })
    $backup = Join-Path ([IO.Path]::GetTempPath()) ('B0Legacy-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($backup) | Out-Null
    $protected = @('ProjectSettings/GraphicsSettings.asset','ProjectSettings/QualitySettings.asset','ProjectSettings/ProjectSettings.asset','ProjectSettings/EditorSettings.asset') + @(Get-ChildItem "$project/Assets/Tests/ScreenSpaceLighting/Renderers/SslT12*.asset" | ForEach-Object { 'Assets/Tests/ScreenSpaceLighting/Renderers/' + $_.Name })
    $before = @{}
    foreach ($f in $protected) { $before[$f] = Sha "$project/$f"; [IO.File]::Copy("$project/$f", (Join-Path $backup ([IO.Path]::GetFileName($f)))) }
    $exitCode = $null
    $start = [datetime]::UtcNow.ToString('o')
    try {
        $quoted = @($argv | ForEach-Object { '"' + $_ + '"' }) -join ' '
        $p = Start-Process -FilePath $UnityPath -WorkingDirectory $project -ArgumentList $quoted -PassThru
        $p.WaitForExit(); $exitCode = $p.ExitCode
    } finally {
        $rows = @(foreach ($f in $protected) {
            $path = "$project/$f"; $after = if (Test-Path -LiteralPath $path) { Sha $path }; $err = $null
            try { if ($before[$f] -ne $after) { [IO.File]::Copy((Join-Path $backup ([IO.Path]::GetFileName($f))),$path,$true) }; $end = Sha $path } catch { $err = $_.Exception.Message; $end = $null }
            [ordered]@{file=$f; before=$before[$f]; afterUnity=$after; restored=$end; ok=($end -eq $before[$f]); error=$err}
        })
        $ok = @($rows | Where-Object { !$_.ok }).Count -eq 0
        Json "$folder/Restoration.json" ([ordered]@{startedUtc=$start; finishedUtc=[datetime]::UtcNow.ToString('o'); exitCode=$exitCode; allRestored=$ok; files=$rows})
        if (!$ok) { throw "Restoration failed: inspect $backup; stop before opening another editor" }
        Remove-Item -LiteralPath $backup -Recurse -Force
    }
    $text = [IO.File]::ReadAllText($log)
    $versions = @([regex]::Matches($text,'(?m)^\s*Version:\s+Direct3D (\d+)[^\r\n]*') | ForEach-Object { 'D3D' + $_.Groups[1].Value } | Sort-Object -Unique)
    return [ordered]@{exitCode=$exitCode; observedApi=($versions -join ','); apiVerified=($versions.Count -eq 1 -and $versions[0] -eq $backend); renderer=([regex]::Match($text,'(?m)^\s*Renderer:\s+([^\r\n]+)').Groups[1].Value); driver=([regex]::Match($text,'(?m)^\s*Driver:\s+([^\r\n]+)').Groups[1].Value)}
}
$anyFailure = $false
foreach ($backend in $Api) {
    $out = "$b0/Win64-$backend/regression/$RunId"
    if (Test-Path -LiteralPath $out) { throw "Refusing existing output: $out" }
    New-Item -ItemType Directory -Path $out | Out-Null
    $force = '-force-' + $backend.ToLowerInvariant()
    $summary = [ordered]@{head=$repoHead; api=$backend; force=$force; scope='T14 filtered Editor tests and T12 Editor verification only; no Player gate or budgets'; scene=$scene; sceneSha256=(Sha "$project/$scene"); defaultSize='1280x720'; defaultWarmupRenders=10; filters=[ordered]@{EditMode=$edit; PlayMode=$play}; stages=[ordered]@{}; status='running'}
    $hashRuns = @(); $bad = $false
    foreach ($stage in 'EditMode','PlayMode','T12-1','T12-2') {
        $dir = "$out/$stage"; New-Item -ItemType Directory -Path $dir | Out-Null
        $entry = [ordered]@{status='blocked'}; $summary.stages[$stage]=$entry
        try {
            if ($stage -in 'EditMode','PlayMode') {
                $entry.device = Launch $dir @('-runTests','-testPlatform',$stage,'-testResults',"$dir/Tests.xml",'-testFilter',($summary.filters[$stage] -join ';'))
                $x = ([xml][IO.File]::ReadAllText("$dir/Tests.xml")).'test-run'
                $cases = @($x.SelectNodes('.//test-case')); $names = @($cases | ForEach-Object { $_.fullname })
                $missing = @($summary.filters[$stage] | Where-Object { $pattern = "$_.*"; !($names -like $pattern) }); $entry.missingFilters = $missing
                $entry.counts = [ordered]@{total=[int]$x.total; passed=[int]$x.passed; failed=[int]$x.failed; skipped=[int]$x.skipped; inconclusive=[int]$x.inconclusive}
                $ok = $x.result -eq 'Passed' -and $x.total -eq $x.passed -and $cases.Count -gt 0 -and $cases.Count -eq [int]$x.total -and !$missing.Count
            } else {
                $entry.device = Launch $dir @('-executeMethod','ScreenSpaceLightingT12Verification.RunT12Verification','-quit','-ssl-t12-output',$dir)
                $r = [IO.File]::ReadAllText("$dir/T12Verification.json") | ConvertFrom-Json
                $entry.unityVersion=$r.unityVersion; $entry.reportApi=$r.graphicsDeviceType; $entry.modes=[ordered]@{}; $hashes=@{}
                $ok = $r.completed -and $r.passed -and $r.graphicsDeviceType -eq ($backend -replace 'D3D','Direct3D') -and $r.modes.Count -eq 3
                foreach ($m in $r.modes) {
                    $passed = @($m.cases | Where-Object passed).Count
                    $entry.modes[$m.name]=[ordered]@{passed=$passed;total=$m.cases.Count;issues=$m.issues}
                    $ok = $ok -and $m.passed -and $passed -eq $expected[$m.name] -and $m.cases.Count -eq $passed
                    foreach ($c in $m.cases) { $hashes.Add(($m.name+'/'+$c.name), [string]$c.sha256) }
                }
                $hashRuns += $hashes
                $entry.pngs = @(); foreach ($png in Get-ChildItem -LiteralPath $dir -Filter '*.png') {
                    if ($stage -eq 'T12-1' -and $png.Name -match '^T12_(Forward|ForwardPlus|Deferred)_(Reference|AllOn|AllOffAgain)\.png$') {
                        $entry.pngs += [ordered]@{file=$png.Name; sha256=(Sha $png.FullName)}
                    } elseif ($ok) { Remove-Item -LiteralPath $png.FullName }
                }
                if ($stage -eq 'T12-1' -and $entry.pngs.Count -ne 9) { $ok = $false }
            }
            $entry.status = if ($ok -and $entry.device.apiVerified -and $entry.device.exitCode -eq 0) {'passed'} else {'failed'}
        } catch {
            $entry.error=$_.Exception.Message
            # Never launch another editor if restoration or the process/lock guard failed.
            if (!(Test-Path -LiteralPath "$dir/Restoration.json") -or !((Get-Content -Raw -LiteralPath "$dir/Restoration.json" | ConvertFrom-Json).allRestored)) { $summary.status='blocked'; Json "$out/Summary.json" $summary; throw }
        }
        if ($entry.status -ne 'passed') { $bad=$true }; Json "$out/Summary.json" $summary
        Write-Host "$backend $stage : $($entry.status)"
    }
    if ($hashRuns.Count -eq 2) {
        $a,$b=$hashRuns; $keys=@(@($a.Keys)+@($b.Keys) | Sort-Object -Unique)
        $diff=@($keys | Where-Object { !$a.ContainsKey($_) -or !$b.ContainsKey($_) -or $a[$_] -cne $b[$_] }); $hashed=@($keys | Where-Object { $a[$_] -and $b[$_] })
        $summary.editorReplay=[ordered]@{cases=$keys.Count; pairedSampleHashes=$hashed.Count; identicalSampleHashes=@($hashed | Where-Object { $a[$_] -ceq $b[$_] }).Count; mismatchedOrMissing=$diff; note='SHA256 from T12 HashSamples; cases without image samples have no hash. Not a development Player raw HDR comparison.'}
    }
    $summary.status=if ($bad) {'failed'} else {'passed'}; Json "$out/Summary.json" $summary
    $anyFailure = $anyFailure -or $bad
}
if ($anyFailure) { exit 1 }
