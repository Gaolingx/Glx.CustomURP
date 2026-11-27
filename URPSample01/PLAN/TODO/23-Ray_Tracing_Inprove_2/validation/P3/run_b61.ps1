# 23 P3 B6.1 attribution matrix.
#
# One build, one scene, one camera. Every case runs twice in fresh processes so the
# determinism gate keeps the same shape as B6. The cases isolate the four suspects of the
# remaining reflection-edge artifacts:
#   base      fixture values (half res, thickness 0.01, K 32768, denoise on)
#   thick     thickness 0.05  (Hi-Z grazing acceptance at silhouettes)
#   k-low     reflectionRayBudget 8192 (hardware fill coverage under a smaller K; the queue
#             clamps at 32768, so K can only be lowered, not raised)
#   fullres   full resolution trace (half-res quantisation of the hit UV)
#   nodenoise raw reprojection/merge signal (separates coverage steps from filter smoothing)
#   p1        rollback path; the frozen hashes must not move
param(
    [string]$Unity = "D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe",
    [string]$Project = "E:\GitHub\Glx.CustomURP\URPSample01",
    [string]$Exe = ""
)
$ErrorActionPreference = "Stop"
$V = Join-Path $Project "PLAN\TODO\23-Ray_Tracing_Inprove_2\validation"
$out = Join-Path $V "P3\B61"
if ([string]::IsNullOrEmpty($Exe)) { $Exe = Join-Path $V "P0\Player\SsrP3Baseline.exe" }
if (!(Test-Path $Exe)) { throw "Player build not found: $Exe. Rebuild the P0 fixture first." }

$cases = @(
    @{ name = "base";     variant = "base";      extra = @() },
    @{ name = "thick";    variant = "base";      extra = @("-p3-thickness", "0.05") },
    @{ name = "k-low";    variant = "base";      extra = @("-p3-budget", "8192") },
    @{ name = "fullres";  variant = "base";      extra = @("-p3-fullres", "1") },
    @{ name = "nodenoise";variant = "nodenoise"; extra = @() },
    @{ name = "p1";       variant = "base";      extra = @("-p3-ssr-rollback") }
)

foreach ($api in @("d3d12", "d3d11")) {
    $flag = "-force-$api"
    foreach ($case in $cases) {
        foreach ($run in @("run-01", "run-02")) {
            $dir = Join-Path $out "$api\$($case.name)\$run"
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $cli = @($flag, "-p3-output", $dir, "-p3-variant", $case.variant, "-p3-run", $run)
            if ($case.name -ne "p1") { $cli += "-p3-ssr-after-gbuffer" }
            $cli += $case.extra
            & $Exe @cli
            if ($LASTEXITCODE -ne 0) { throw "Player failed: $api/$($case.name)/$run (exit $LASTEXITCODE)" }
        }
    }
}

Write-Host "B6.1 captures written to $out"
Write-Host "Next: python $V\P1\seam_metric.py --originals <_R1Original.bin> --frame base=<frame.raw> --out <seam.json>"
