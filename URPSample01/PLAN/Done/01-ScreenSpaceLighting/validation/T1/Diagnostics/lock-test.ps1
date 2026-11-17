$log = "E:\GitHub\Glx.CustomURP\URPSample01\PLAN\Done\01-ScreenSpaceLighting\validation\T1\Diagnostics\lock-log.txt"
Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
"user: $($identity.Name)" | Out-File -LiteralPath $log
"elevated: $($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))" | Add-Content -LiteralPath $log
& "$env:SystemRoot\System32\nvidia-smi.exe" -lgc 1800 *>&1 | Add-Content -LiteralPath $log
"lock exit: $LASTEXITCODE" | Add-Content -LiteralPath $log
Start-Sleep -Milliseconds 500
& "$env:SystemRoot\System32\nvidia-smi.exe" --query-gpu=clocks.sm,pstate --format=csv,noheader *>&1 | Add-Content -LiteralPath $log
