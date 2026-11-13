# Run13.InvalidClockGate

保留目录，不是基线运行。R2 采集脚本在运行前要求 SM 时钟已锁定到 1800 MHz；当时探测到的空闲时钟为 224 MHz，脚本在启动 Player 前中止，因此目录内只有 `nvidia-smi-q-before.txt`，没有 Benchmark 报告、截图或计时 CSV。该目录用于证明 R2 门槛在本机不可满足，随后被 R3（GPU p95 参考数据）取代；失败的运行不必删除，故保留。
