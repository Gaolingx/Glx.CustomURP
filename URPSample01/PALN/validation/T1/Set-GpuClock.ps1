param(
    [int]$MHz = 1800,
    [switch]$Restore
)

$ErrorActionPreference = "Stop"
$nvidiaSmi = Join-Path $env:SystemRoot "System32\nvidia-smi.exe"
if (-not (Test-Path -LiteralPath $nvidiaSmi -PathType Leaf)) {
    throw "nvidia-smi not found: $nvidiaSmi"
}

$arguments = if ($Restore) { @("-rgc") } else { @("-lgc", $MHz.ToString()) }
$elevated = Start-Process -FilePath $nvidiaSmi -ArgumentList $arguments -Verb RunAs -PassThru -Wait
if ($elevated.ExitCode -ne 0) {
    throw "nvidia-smi $($arguments -join ' ') failed with exit code $($elevated.ExitCode)"
}

& $nvidiaSmi --query-gpu=clocks.sm,clocks.max.sm,pstate,power.draw --format=csv,noheader
Write-Host "When the GPU is idle the reported clock can stay low even while the lock policy is active; verify clocks.sm during the measured run."
