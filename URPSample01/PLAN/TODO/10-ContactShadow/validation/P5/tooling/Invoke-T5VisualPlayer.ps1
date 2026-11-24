param(
    [string]$UnityPath = 'D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../Player')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$project = (Resolve-Path (Join-Path $PSScriptRoot '../../../../../..')).Path
$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
foreach ($protected in 'Assets','Packages','ProjectSettings') {
    $path = (Join-Path $project $protected).TrimEnd('\') + '\'
    if (($out.TrimEnd('\') + '\').StartsWith($path, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe evidence path: $out" }
}
if (!(Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw "Unity not found: $UnityPath" }
if ((Test-Path -LiteralPath $out) -and @(Get-ChildItem -LiteralPath $out -Force).Count) { throw "Use a fresh evidence directory: $out" }
[void][IO.Directory]::CreateDirectory($out)
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('T5Visual-' + [guid]::NewGuid().ToString('N'))
function Q($path) { '"{0}"' -f $path }
# As in T3: wait for the editor, then each player. Leave Unity/Bee/shader worker counts at their defaults.
function Invoke-ProbeProcess($exe, $arguments, $seconds, $log) {
    $p = Start-Process -FilePath $exe -ArgumentList ($arguments + @('-logFile', (Q $log))) -PassThru
    try {
        if (!$p.WaitForExit($seconds * 1000)) { $p.Kill(); $p.WaitForExit(); throw "Timed out: $log" }
        if ($p.ExitCode -ne 0) { throw "Exit $($p.ExitCode): $log" }
    } finally { $p.Dispose() }
}
function Read-Json($path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -ErrorAction Stop }
$r = [ordered]@{ passed = $false; build = $null; playerExeSHA256 = $null; players = @(); errors = @() }
try {
    $assets = [IO.Directory]::CreateDirectory((Join-Path $scratch Assets)).FullName
    $settings = [IO.Directory]::CreateDirectory((Join-Path $scratch ProjectSettings)).FullName
    $fixture = 'Assets/Tests/ContactShadow'
    $dest = [IO.Directory]::CreateDirectory((Join-Path $scratch $fixture)).FullName
    [void][IO.Directory]::CreateDirectory((Join-Path $scratch Player))
    Copy-Item -LiteralPath (Join-Path $project Packages) -Destination $scratch -Recurse
    foreach ($file in 'ProjectVersion.txt','ProjectSettings.asset','GraphicsSettings.asset','QualitySettings.asset','EditorSettings.asset','TagManager.asset') {
        Copy-Item -LiteralPath (Join-Path $project "ProjectSettings/$file") -Destination $settings
    }
    foreach ($file in 'UniversalRenderPipelineGlobalSettings.asset','UniversalRenderPipelineGlobalSettings.asset.meta') {
        Copy-Item -LiteralPath (Join-Path $project "Assets/$file") -Destination $assets
    }
    foreach ($dir in 'Materials','Pipelines','Profiles','Scenes') {
        Copy-Item -LiteralPath (Join-Path $project "$fixture/$dir") -Destination $dest -Recurse
        Copy-Item -LiteralPath (Join-Path $project "$fixture/$dir.meta") -Destination $dest
    }
    foreach ($file in 'T5VisualProbe.cs','link.xml') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $assets }
    $exe = Join-Path $scratch 'Player/T5Visual.exe'
    $json = Join-Path $scratch 'Build.json'
    Write-Host "T5 build: $scratch; summary: $out"
    Invoke-ProbeProcess $UnityPath @('-batchmode','-quit','-buildTarget','Win64','-force-d3d11','-projectPath',(Q $scratch),
        '-executeMethod','T5VisualProbe.Build','-probeExe',(Q $exe),'-probeJson',(Q $json)) 3600 (Join-Path $scratch 'Build.log')
    $r.build = Read-Json $json
    if ($r.build.target -ne 'StandaloneWindows64' -or $r.build.errors -ne 0 -or $r.build.options -match 'Development' -or
        ($r.build.qualityPipelines -join ',') -cne 'Forward,ForwardPlus,Deferred') { throw 'Invalid release build evidence' }
    $r.playerExeSHA256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    foreach ($v in 11,12) {
        $api = "Direct3D$v"; $json = Join-Path $scratch "$api.json"
        try {
            Invoke-ProbeProcess $exe @("-force-d3d$v",'-batchmode','-screen-fullscreen','0','-screen-width','320','-screen-height','240',
                '-probeApi',$api,'-probeJson',(Q $json)) 180 (Join-Path $scratch "$api.log")
            $p = Read-Json $json
            if (!$p.passed -or $p.development -or $p.platform -ne 'WindowsPlayer' -or $p.api -ne $api -or
                ([guid]$p.buildGuid) -ne ([guid]$r.build.guid) -or ($p.paths.actual -join ',') -cne 'Forward,ForwardPlus,Deferred' -or
                @($p.paths | Where-Object { !$_.passed -or $_.requested -cne $_.actual }).Count) { throw "Missing release camera proof: $api" }
            $r.players += $p
        } catch {
            $r.errors += "${api}: $_"
            if (Test-Path -LiteralPath $json) { $r.players += Read-Json $json }
        }
    }
    $r.passed = $r.errors.Count -eq 0
} catch { $r.errors += $_.ToString() }
finally {
    [IO.File]::WriteAllText((Join-Path $out 'Summary.json'), ($r | ConvertTo-Json -Depth 8))
    if ($r.passed) {
        Copy-Item -LiteralPath (Join-Path $scratch 'Build.log') -Destination (Join-Path $out 'Build.log')
        # Windows PowerShell's recursive provider can fail on Unity's deep PackageCache paths.
        & $env:ComSpec /d /c ('rmdir /s /q "{0}"' -f $scratch)
        if (Test-Path -LiteralPath $scratch) { throw "Player checks passed; could not remove scratch: $scratch" }
    }
}
if (!$r.passed) { throw "T5 failed. Diagnostics retained in $scratch. $($r.errors -join '; ')" }
Write-Host "Passed: $out\Summary.json"
