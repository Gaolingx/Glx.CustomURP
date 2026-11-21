# C1 验证记录：clustered 模式跳过 stencil 灯预计算

> 轮次：12-GpuLightList 的 C1（来自清理计划的 C1 项）。
> 环境：Unity 2022.3.62f1（D3D11 / D3D12 standalone Development player）、RTX 5060 Ti、Windows 10.0.20348、1280×720、64 盏点/聚光、48 灯截图。

## 1. 改动

- `DeferredLights.ShouldPrecomputeStencilLights`：只有 clustered base game 相机跳过；stencil、Preview、Overlay、XR 相机保持构建。
- `DeferredLights.SetupLights(..., precomputeStencilLights)`：跳过时把 `m_stencilVisLights`/`m_stencilVisLightOffsets` 置 default（Temp 分配跨帧失效，不能让回退路径拿到旧句柄）；关键字/常量设置不变。
- `ClusteredDeferredCpuProbe`：默认关闭的 CPU 分解探针；arena runner `--cd-cpu` 打开，按切片累计 `Stopwatch` tick，逐帧均值写入 `CdCpuBreakdown.json`。
- 新增 EditMode 断言 `StencilLightPrecomputeIsSkippedOnlyForEligibleClusteredCameras`。

## 2. 命令

```bash
U="/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe"
P="E:/GitHub/Glx.CustomURP/URPSample01"
OUT="$P/PLAN/TODO/12-GpuLightList/validation/C1"

# 测试（不带 -quit；-runTests 会自行退出）
"$U" -batchmode -projectPath "$P" -runTests -testPlatform EditMode \
  -testResults "$OUT/tests/C1EditMode.xml" -logFile "$OUT/tests/C1EditMode.log"
"$U" -batchmode -projectPath "$P" -runTests -testPlatform PlayMode \
  -testResults "$OUT/tests/C1PlayMode.xml" -logFile "$OUT/tests/C1PlayMode.log"

# T5 构建 + A/B + CPU 分解
"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildD3D11AndExit \
  -logFile "$OUT/logs/build-t5-d3d11.log"
"$P/Builds/ClusteredDeferredArena/Direct3D11/CdArena.exe" --cd-output "$OUT/T5-runtime/player-d3d11"
"$P/Builds/ClusteredDeferredArena/Direct3D11/CdArena.exe" --cd-output "$OUT/cpu" --cd-cpu

# T4 构建 + 特性矩阵（D3D11 / D3D12 各一次）
"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildT4D3D11AndExit \
  -logFile "$OUT/logs/build-t4-d3d11.log"
"$P/Builds/ClusteredDeferredArenaT4/Direct3D11/CdArenaT4.exe" --cd-output "$OUT/T4-shadows/player-d3d11" --cd-t4
```

D3D12 对应 `BuildD3D12AndExit` / `BuildT4D3D12AndExit`，输出目录换成 `player-d3d12`、`cpu/d3d12`。

## 3. 结果摘要

| 项 | D3D11 | D3D12 |
|---|---|---|
| T5 A/B（48 灯） | mean 0.803/255、max 9、>8 差 13 px，pass | mean 0.804/255、max 9、>8 差 9 px，pass |
| 64 灯 GPU：stencil / clustered | 0.715 / 0.347 ms | 1.127 / 0.424 ms |
| 64 灯 CPU 帧 | 1.730 / 1.237 ms（stencil/clustered） | 1.849 / 1.161 ms |
| `PrecomputeLights` | 0.00349 / 0.00009 ms | 0.0030 / 0.00008 ms |
| `PreSetup` + jobs complete + 上传 | 0 / 0.051 + 0.110 + 0.012 ms | 0 / 0.050 + 0.112 + 0.011 ms |
| T4 特性矩阵 | 见 `T4-shadows/player-d3d11/T4FeatureMatrix.json` | 见 `T4-shadows/player-d3d12/T4FeatureMatrix.json` |
| EditMode / PlayMode | 485/485、36/36 | — |

## 4. 证据说明

- 提交：测试 XML、`CdPerf.json/csv`、`CdCpuBreakdown.json`、两份 `T4FeatureMatrix.json`、构建摘要、本文件。
- T4 的 56 张 720p PNG 已在本地重跑并通过（矩阵 `passed=true`）；为控制体积未重复入库，像素参考保留在 `PLAN/Done/11-LightCulling/validation/T4-shadows/`。C1 只改 CPU 侧，kernel/缓冲输入未变，A/B 数值与 11 轮一致（差异来自运行间浮点/驱动排序）。
- 构建期间 `ClusteredDeferredTestAssetSetup.CreateAll()` 会重写场景与管线资产（对象 ID/序列化版本漂移），验证结束后已把这些工程文件还原为基线；证据对应的语义资产与提交版本一致。
- 未新增 `.rdc`：C1 不改变 pass 结构或 GPU 输入；11 轮的 D3D12 抓帧仍是当前结构参考（`Done/11-LightCulling/validation/T5-runtime/`）。
