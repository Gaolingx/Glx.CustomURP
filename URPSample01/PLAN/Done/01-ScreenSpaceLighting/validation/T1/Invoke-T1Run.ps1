param(
    [Parameter(Mandatory = $true)]
    [string]$RunName,
    [int]$ClockSampleIntervalMs = 50
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$projectRoot = (Resolve-Path (Join-Path $root "..\..\..\..\..")).Path
$player = Join-Path $root "Player\SslBaselinePlayer.exe"
$runDirectory = Join-Path $root $RunName
$clockCsv = Join-Path $runDirectory "GpuClocks.csv"

if (-not (Test-Path -LiteralPath $player -PathType Leaf)) {
    throw "T1 Player not found: $player"
}
if (Test-Path -LiteralPath $runDirectory) {
    throw "Run directory already exists: $runDirectory"
}
New-Item -ItemType Directory -Path $runDirectory | Out-Null

& nvidia-smi -q -d CLOCK,PERFORMANCE > (Join-Path $runDirectory "nvidia-smi-q-before.txt")
$before = & nvidia-smi --query-gpu=clocks.sm,pstate --format=csv,noheader

$sampler = Start-Process -FilePath (Join-Path $env:SystemRoot "System32\nvidia-smi.exe") -ArgumentList @(
    "--query-gpu=timestamp,clocks.sm,clocks.mem,pstate,power.draw,utilization.gpu,temperature.gpu",
    "--format=csv,noheader",
    "-lms", $ClockSampleIntervalMs.ToString()
) -RedirectStandardOutput $clockCsv -NoNewWindow -PassThru

try {
    $arguments = @(
        "-force-d3d11", "-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720",
        "-logFile", (Join-Path $runDirectory "PlayerRun.log"),
        "--ssl-baseline-output", $runDirectory
    )
    $playerProcess = Start-Process -FilePath $player -ArgumentList $arguments -WorkingDirectory (Split-Path $player) -PassThru -Wait
    $playerExitCode = $playerProcess.ExitCode
}
finally {
    if (-not $sampler.HasExited) {
        Stop-Process -Id $sampler.Id -Force
        $sampler.WaitForExit()
    }
}

& nvidia-smi -q -d CLOCK,PERFORMANCE > (Join-Path $runDirectory "nvidia-smi-q-after.txt")
$after = & nvidia-smi --query-gpu=clocks.sm,pstate --format=csv,noheader

$validation = [ordered]@{
    run = $RunName
    protocolRevision = "R3"
    gpuTimingPolicy = "reference-only"
    playerExitCode = $playerExitCode
    before = $before
    after = $after
    clockSampleIntervalMs = $ClockSampleIntervalMs
    clockSamples = (Get-Content -LiteralPath $clockCsv | Measure-Object -Line).Lines
    playerLog = Join-Path $runDirectory "PlayerRun.log"
}
$validation | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory "GpuClockValidation.json")

if ($playerExitCode -ne 0) {
    throw "T1 player exited with code $playerExitCode. See $runDirectory"
}
Write-Host "Run $RunName completed. Clock samples: $($validation.clockSamples)"
