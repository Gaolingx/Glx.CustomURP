# P2-3 SSGI 降噪器屏幕投影 Y 翻转（F-09）（2026-11-17）

> 任务：P2-3。目标：用一个屏幕上下不对称的场景确认 denoiser 的 tap 投影是否需要 `UNITY_UV_STARTS_AT_TOP` 翻转。
> 结论：**确认是缺陷并已修复**。修复前 16 个 tap 只有 1 个通过深度/法线测试，降噪器输出与输入 99.99% 字节相同（近似直通，alpha 高频比 1.01）；修复后 16/16 tap 通过，同一帧输入/输出有 18.4% 像素不同，alpha 高频能量降到 **0.505×**。全量 EditMode **454/454**。

## 1. 根因

Unity 在 UV 原点朝上的平台把渲染目标翻转烘焙进投影矩阵，因此 core 程序库在 **CS→NDC** 反算时手动取反：

```hlsl
// com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl
float3 ComputeNormalizedDeviceCoordinatesWithZ(float3 position, float4x4 clipSpaceTransform)
{
    float4 positionCS = ComputeClipSpacePosition(position, clipSpaceTransform);
#if UNITY_UV_STARTS_AT_TOP
    positionCS.y = -positionCS.y;   // 投影矩阵里已烘焙了一次翻转
#endif
    ...
}
```

- 光线步进（`RayMarching.hlsl`）与位置重建（`ComputeWorldSpacePosition`）都走 core helper，所以方向正确。
- `DiffuseDenoiser.compute` 手写 `mul(SSL_VIEW_PROJ, wsPos)` 后直接 `hClip.xy * 0.5 + 0.5`，**漏掉了这一步**。tap 因此落在镜像行，深度/法线测试全部失败，过滤器回退为“当前像素”，近似直通。HDRP 的 `DiffuseDenoiser.compute` 有对应处理，URP 移植时遗漏。

## 2. 修复

[DiffuseDenoiser.compute](../../../../../Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/Denoising/DiffuseDenoiser.compute) 在裁剪空间除法后、NDC 映射前补上与 core helper 一致的翻转：

```hlsl
hClip.xyz /= hClip.w;
#if UNITY_UV_STARTS_AT_TOP
hClip.y = -hClip.y;
#endif
```

只在 UV 原点朝上的平台生效；其他平台编译结果不变。

## 3. GPU 证据

### 3.1 tap 位置与接受数（RenderDoc `debug_thread`）

对抓帧中的 `BilateralFilterColor` dispatch（EID 392）逐指令追踪 `Paint` 像素 (967,216)：

| 项 | 修复前 | 修复后 |
|---|---|---|
| 过滤器循环是否执行 | 是（781 步） | 是（1560 步） |
| 16 个 tap 的深度/法线读取 | 16 次 | 16 次 |
| 通过测试并读取 `_DenoiseInputTexture` 的 tap | **1 / 16** | **16 / 16** |
| 首个 tap 的投影屏幕 y | 513 | 206 |

修复前 tap y=513，而中心像素在纹理空间是 y=216（`H-1-216=503`），即整体镜像；修复后落在中心附近（206），符合 ~60 px 的降噪半径。

### 3.2 同一帧输入/输出纹理

抓帧配置：[CaptureManifest.json](CaptureManifest.json)（Forward、全分辨率、temporal off、denoise on、`rayMiss=None`）。从同一抓帧导出 `SslSsgiProjection`（过滤器输入）与 `SslSsgiDenoisedHF`（过滤器输出；导出文件见 [Player/renderdoc-mcp-export/](Player/renderdoc-mcp-export/) 与 [Player/fix/renderdoc-mcp-export/](Player/fix/renderdoc-mcp-export/)）。alpha 是 SSGI 有效性（命中 1、miss 0），因此“输入二值、输出平滑”直接反映过滤器是否真的在平均邻居。

| 指标（中央窗口 160..1120 × 100..650） | 修复前 | 修复后 |
|---|---|---|
| 输入 alpha 中间值占比 | 0.00% | 0.00% |
| 输出 alpha 中间值占比 | **0.00%** | **18.37%** |
| 输入/输出字节完全相同像素 | **99.99%** | 84.55% |
| 发生变化的像素 | 0.01% | **18.37%** |
| alpha 高频能量（Laplacian） | 0.1856 → 0.1856（比 1.01） | 0.1856 → **0.0938（比 0.505）** |

[analyze.py](analyze.py) → [Metrics.json](Metrics.json)：`sameFrame.passed=true`（高频比 ≤0.75、输出中间值 ≥1%、变化像素 ≥5%）。

### 3.3 截图记录

[P2Manifest.json](P2Manifest.json) 的 4 个用例（full/half × denoise off/on，debug view 10 显示 SSGI 有效性）用于视觉对照；跨用例的随机 hit mask 每帧不同，因此截图只作记录，数值门限以上面的同帧纹理为准。

## 4. 回归

| 项 | 结果 |
|---|---|
| P2-1 调度链 | 不受影响（只改 tap 坐标，dispatch 数量/组维度/绑定不变） |
| P2-2 stamp 用例（最终构建重跑） | **7/7**（[P2-2/Metrics.json](../P2-2/Metrics.json)） |
| 全量 EditMode | **454/454**（[EditModeFull.xml](EditModeFull.xml)） |
| **P2-4 关注** | 降噪器此前实际未生效；修复后 SSGI 的空间降噪才参与最终图像，S3/S5 的时域与半分辨率验收需要在 P2-4 重新度量 |

## 5. 产物与复现

| 项 | 值 |
|---|---|
| 降噪器截图用例 | [P2Manifest.json](P2Manifest.json) → `Player/*.png`、[Player/Report.json](Player/Report.json) |
| 同帧抓帧用例 | [CaptureManifest.json](CaptureManifest.json) |
| 分析 | [analyze.py](analyze.py) → [Metrics.json](Metrics.json) |
| 抓帧 | `Player/forward_ssgi_denoise_capture_frame13144.rdc`（修复前）、`Player/fix/forward_ssgi_denoise_capture_frame12655.rdc`（修复后），本地保留、不入库 |
| 导出纹理 | `Player/renderdoc-mcp-export/tex_ResourceId__9830_mip0.png, tex_ResourceId__9840_mip0.png`（修复前）、`Player/fix/renderdoc-mcp-export/…`（修复后） |

```bash
# 截图用例
"E:/GitHub/Glx.CustomURP/URPSample01/Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest ".../validation/P2-3/P2Manifest.json" \
  -ssl-validation-out ".../validation/P2-3/Player" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0
python analyze.py

# 同帧抓帧：renderdoccmd capture 后，用 renderdoc_mcp 打开 .rdc，
# list_resources(name=Ssgi) 取 9830/9840，export_texture 到 renderdoc-mcp-export/
"/c/Program Files/RenderDoc/renderdoccmd.exe" capture -w --opt-hook-children \
  --opt-disallow-fullscreen --opt-disallow-vsync \
  "E:/GitHub/Glx.CustomURP/URPSample01/Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest ".../validation/P2-3/CaptureManifest.json" \
  -ssl-validation-out ".../validation/P2-3/Player/fix" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0
```

## 6. 验收映射

| 标准 | 结果 |
|---|---|
| F-09：denoiser 屏幕投影是否需要处理 `UNITY_UV_STARTS_AT_TOP` | **需要**；已按 core helper 的规则补齐 |
| 修复可证实性 | 同帧 tap 接受数 1/16 → 16/16；输出高频能量 0.505×；变化像素 18.4% |
| 平台范围 | 仅 UV 原点朝上的平台编译翻转；其他平台不变 |
| XR | 单 pass 用 `_SslXrViewProj`（同样由 GPU 投影矩阵组成），同一个 `#if` 生效 |
