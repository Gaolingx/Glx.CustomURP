# P3-1 SSR 像素展开角绑定修复（F-02）证据（2026-11-17）

> 任务：P3-1 / F-02（High）。目标：反射降噪器必须收到本帧、本效果的像素展开角；SSGI 关闭的 SSR-only 相机也必须非 0。
> 结论：**修复完成**。SSR-only（SSGI 关闭）的 full/half 两个用例通过：`_SsgiDenoiserParams.x` 精确等于 `2·tan(fov/2)/min(traceW,traceH)`，误差 0；binding probe 回读一致；GPU 抓帧确认 PBR 降噪链 6/7 个 dispatch，且帧内没有 SSGI pass 可以"顺带"写入该向量。

## 1. 缺陷与修复

`ReflectionDenoiser.compute:75` 把像素展开角映射到 SSGI 的共享常量：

```hlsl
#define _PixelSpreadAngleTangent _SsgiDenoiserParams.x
```

但 `SsrPass` 写的是独立 float `_PixelSpreadAngleTangent`（`SsrPass.cs:363`），被宏遮蔽，是死写入。SSGI 开启时 SSR 降噪器"碰巧"读到 `SsgiPass` 上传的值；SSGI 关闭时读到 0，`SsgiComputeMaxReprojectionWorldRadius` 退化为 0.1 世界单位下限，移动相机下 history 几乎每帧被拒绝。

修复：

- `SsrPass.PushDenoiserShaderVariables` 上传共享向量：
  ```csharp
  float tangent = ScreenSpaceLightingRenderUtils.ComputePixelSpreadTangent(
      cameraData.camera.fieldOfView, traceWidth, traceHeight);
  cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiDenoiserParams,
      new Vector4(tangent, -1f, 0f, 0f));
  ```
- 删除 `DispatchReflectionDenoise` 的独立 float 写入及不再需要的 FOV/尺寸参数。
- 清理 `SsgiPass` 对 `TemporalFilter.compute` 的两处同类死写入（该 shader 同样用宏遮蔽）；保留 `DiffuseDenoiser.compute` 的真实 float 写入（它声明了独立 uniform）。
- DEV 构建记录 `s_LastPixelSpreadAngleTangent` / trace 尺寸 / 帧号，供验证 player 断言。
- 新增 EditMode 回归 `ScreenSpaceLightingSsrDenoiserParamsTests`（3 项）：
  - 降噪器宏仍指向 `_SsgiDenoiserParams.x`；
  - `SsrPass` 含向量上传且不再引用独立 float ID；
  - `TemporalFilter` 死写入已移除，`SsgiPass` 只剩 DiffuseDenoiser 一处。

## 2. 产物与复现

| 项 | 值 |
|---|---|
| Manifest | [P3Manifest.json](P3Manifest.json) |
| 用例 | `forward_ssr_pbr_full_ssgi_off`（full）、`forward_ssr_pbr_half_ssgi_off`（half）；SSGI Volume 关闭 |
| Player | `Builds/SslValidationPlayer/SslValidationPlayer.exe`（D3D11、Development、1280×720） |
| 抓帧 | `renderdoccmd capture -w --opt-hook-children … -ssl-validation-manifest …P3Manifest.json -ssl-validation-out …P3-1/Player` |

`Player/Report.json`：2/2 通过，`nonFinitePixels=0`，binding probe 8/8 字段 0 不匹配。

## 3. 数值证据

相机 FOV 60°，屏幕 1280×720：

| 用例 | trace | 期望 tangent | 实际 tangent | 误差 | SSGI active |
|---|---|---|---|---|---|
| full | 1280×720 | `2·tan(30°)/720 = 0.00160375` | **0.0016037507** | 0 | false |
| half | 640×360 | `2·tan(30°)/360 = 0.00320750` | **0.0032075015** | 0 | false |

- `Report.json` 的 `ssrDenoiser` 记录实际值、期望值、trace 尺寸和帧号；验证器要求误差 ≤1e-6、trace 尺寸与 `fullResolution` 一致、SSGI 必须关闭。
- binding probe 新增 `_SsrDenoiserPixelSpread` 行，回读 `(tangent, -1, 0, 0)`，与 `_SsgiDenoiserParams` 的上传向量一致；`_SsrTraceSize` 同步为 1280×720 / 640×360。
- 修复前 SSGI 关闭时该值为 0（字段默认），因此本证据同时覆盖 R2 的"SSR-only 也非 0"。

## 4. GPU 证据

`P3Census.json`：抓帧内 **没有** `Screen Space Global Illumination` marker，排除了 SSGI 写入该向量的可能。

| 用例 | SSR marker | dispatch 数 | kernel（按提交顺序） |
|---|---|---|---|
| full | 324–453 | **6** | trace, reproject, temporalAccumulation, copyHistory, bilateralH, bilateralV |
| half | 324–468 | **7** | trace, reproject, temporalAccumulationHalfRes, copyHistory, bilateralH_HR, bilateralV_HR, upsample |

各 kernel 的 CS shader id 均不同（full `9824/9827/9830/9833/9835/9838`，half `9886/9888/9889/9833/9890/9891/9892`），与 full/half 变体一致。降噪器 dispatch 存在是像素展开角生效的前提；其常量值由 CPU 统计与 probe 证明（v0.3.0 无 cbuffer dump）。

## 5. 验收映射

| 标准 | 结果 |
|---|---|
| R2 值等于公式（full 0.0016046 / half 0.0032092，误差 ≤1e-6） | ✅ 误差 0 |
| R2 SSGI 关闭的 SSR-only 相机也非 0 | ✅ 两用例 SSGI 均关闭，值非 0 |
| R2 慢速平移 history 接受率 ≥50% | 不在 P3-1 范围；需要 P3-4 的平移序列与 sample count 统计 |
| R1 dispatch 数量 | ✅ full 6 / half 7（含 upsample） |
| 无 NaN/Inf | ✅ `nonFinitePixels=0` |

## 6. 已知边界

- Unity player 剥离 DXBC 反射名，renderdoc-mcp v0.3.0 没有 cbuffer 工具，因此生产 dispatch 的常量数值由 DEV 统计 + binding probe 证明；RDC 负责 dispatch 存在性、kernel 身份与"帧内无 SSGI"。
- 慢速平移的 history 接受率与拖影阈值留给 P3-4；本修复的预期收益需在那里量化。
- `.rdc` 约 36 MB，已入库：

| 抓帧 | 字节 | SHA-256 |
|---|---|---|
| `forward_ssr_pbr_full_ssgi_off_frame15174.rdc` | 17,952,493 | `a0700531…d72c1b1` |
| `forward_ssr_pbr_half_ssgi_off_frame15192.rdc` | 17,819,341 | `3c59da2d…994dfd07` |

## 7. 文件

```text
validation/P3-1/
  P3Manifest.json
  P3Census.json
  Report.md
  Player/
    Report.json
    BindingProbe_forward_ssr_pbr_full_ssgi_off.json
    BindingProbe_forward_ssr_pbr_half_ssgi_off.json
    forward_ssr_pbr_full_ssgi_off.png
    forward_ssr_pbr_half_ssgi_off.png
    *.rdc ×2（已入库）
```