$log = "E:\GitHub\Glx.CustomURP\URPSample01\PLAN\Done\01-ScreenSpaceLighting\validation\T1\Diagnostics\clock-lock-verify.txt"
Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
$nvidiaSmi = "$env:SystemRoot\System32\nvidia-smi.exe"

function Log-Clock([string]$label) {
    $clock = (& $nvidiaSmi --query-gpu=clocks.sm,pstate --format=csv,noheader) -join " | "
    "$label $clock" | Add-Content -LiteralPath $log
}

Log-Clock "baseline"
& $nvidiaSmi -lgc 600,600 *>&1 | Add-Content -LiteralPath $log
Log-Clock "after-lock-600-immediate"
Start-Sleep -Seconds 2
Log-Clock "after-lock-600-2s"
& $nvidiaSmi -lgc 300,300 *>&1 | Add-Content -LiteralPath $log
Log-Clock "after-lock-300-immediate"
Start-Sleep -Seconds 2
Log-Clock "after-lock-300-2s"
& $nvidiaSmi -rgc *>&1 | Add-Content -LiteralPath $log
Log-Clock "after-restore"
