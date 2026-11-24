param(
    [string]$UnityPath = 'D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe',
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '../../../../../..')).Path
$run = [guid]::NewGuid().ToString('N')
if (!$OutputRoot) { $OutputRoot = Join-Path $project "Builds/T3Kernel/$run" }
$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
if (!(Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw "Unity not found: $UnityPath" }
if ((Test-Path -LiteralPath $out) -and @(Get-ChildItem -LiteralPath $out -Force).Count) { throw "Use a fresh evidence directory: $out" }
[void][IO.Directory]::CreateDirectory($out)
$scratch = Join-Path ([IO.Path]::GetTempPath()) "T3Kernel-$run"

function Invoke-ProbeProcess($exe, $arguments, $timeoutSeconds, $log) {
    $process = Start-Process -FilePath $exe -ArgumentList ($arguments + @('-logFile', ('"{0}"' -f $log))) -PassThru
    if (!$process.WaitForExit($timeoutSeconds * 1000)) {
        $process.Kill(); $process.WaitForExit(); throw "Timeout: $log"
    }
    if ($process.ExitCode -ne 0) { throw "Exit $($process.ExitCode): $log" }
}

try {
    # Isolate URP build preprocessors and PlayerSettings from the real project. Use its exact fork and UPM lock.
    foreach ($dir in 'Assets','ProjectSettings','Player') { [void][IO.Directory]::CreateDirectory((Join-Path $scratch $dir)) }
    Copy-Item -LiteralPath (Join-Path $project 'Packages') -Destination $scratch -Recurse
    Copy-Item -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') -Destination (Join-Path $scratch 'ProjectSettings')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'T3KernelProbe.cs') -Destination (Join-Path $scratch 'Assets')
    $exe = Join-Path $scratch 'Player/T3Kernel.exe'
    Write-Host "T3 player evidence: $out"
    Invoke-ProbeProcess $UnityPath @('-batchmode', '-quit', '-buildTarget', 'Win64', '-force-d3d11', '-projectPath', ('"{0}"' -f $scratch),
        '-executeMethod', 'T3KernelProbe.Build', '-probeExe', ('"{0}"' -f $exe), '-probeOut', ('"{0}"' -f $out)) 1800 (Join-Path $out 'Build.log')
    $summary = Get-Content -LiteralPath (Join-Path $out 'BuildSummary.json') -Raw | ConvertFrom-Json
    if ($summary.result -ne 'Succeeded' -or $summary.errors -ne 0 -or !(Test-Path -LiteralPath $exe)) { throw 'Standalone build failed' }
    $failures = @()
    foreach ($v in 11,12) {
        $api = "Direct3D$v"
        $dir = [IO.Directory]::CreateDirectory((Join-Path $out $api)).FullName
        try {
            Invoke-ProbeProcess $exe @("-force-d3d$v", '-batchmode', '-screen-fullscreen', '0', '-screen-width', '320', '-screen-height', '200',
                '-probeApi', $api, '-probeOut', ('"{0}"' -f $dir)) 120 (Join-Path $dir 'Player.log')
            $r = Get-Content -LiteralPath (Join-Path $dir 'Probe.json') -Raw | ConvertFrom-Json
            if ($r.passed -ne $true -or $r.platform -ne 'WindowsPlayer' -or $r.api -ne $api -or !(Test-Path (Join-Path $dir 'MaskFade.png'))) { throw 'Missing player proof' }
            Write-Host "$api hit=$($r.hitWord) lit=$($r.litWord) mirror=$($r.mirrorWord) noLightNonzero=$($r.nonzeroWithoutLights)"
        } catch { $failures += "${api}: $_" }
    }
    if ($failures.Count) { throw ($failures -join '; ') }
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
