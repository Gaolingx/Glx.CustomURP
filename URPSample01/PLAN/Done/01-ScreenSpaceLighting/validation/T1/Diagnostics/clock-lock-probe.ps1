$log = "E:\GitHub\Glx.CustomURP\URPSample01\PLAN\Done\01-ScreenSpaceLighting\validation\T1\Diagnostics\clock-lock-probe.txt"
Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
$nvidiaSmi = "$env:SystemRoot\System32\nvidia-smi.exe"
& $nvidiaSmi -lgc 1800 *>&1 | Add-Content -LiteralPath $log
"lock exit: $LASTEXITCODE" | Add-Content -LiteralPath $log
for ($i = 0; $i -lt 100; $i++) {
    $stamp = Get-Date -Format "HH:mm:ss.fff"
    $sample = (& $nvidiaSmi --query-gpu=clocks.sm,pstate,utilization.gpu --format=csv,noheader) -join " | "
    "$stamp $sample" | Add-Content -LiteralPath $log
    Start-Sleep -Milliseconds 200
}
