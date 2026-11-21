# 11-LightCulling 报告：Clustered Deferred (URP BRDF)

> 结论：**验收通过（mixed lighting-shadowmask 为 PARTIAL）**。clustered deferred 路径在 D3D11/D3D12 与 stencil deferred 逐像素 A/B 通过（mean ≤ 2/255、max ≤ 24/255），阴影/cookie/light layers/GTAO 直接光+间接光 AO 单项通过；EditMode 484/484、PlayMode 36/36；tile 宽度 8/16/32/64 实测生效；性能交叉点在 8–32 盏附加灯之间。mixed lighting-shadowmask 的实现与编译已覆盖，但本工程没有 baked shadowmask 数据，运行时 A/B 不可得。

## 1. 目标

- Deferred 模式下新增一条 compute 路径：GBuffer 之后按 tile 列表循环光源，复用 URP 的 GBuffer 解码、BRDF、阴影、cookie、light layers 与 GTAO 语义。
- 与现有 stencil deferred 可逐像素 A/B、可运行时回退；关闭开关时行为不变。
- 不新增 GBuffer 布局，不引入 HDRP 面光源/材质体系。

计划与决策（D1–D16）见 [HANDOFF.md](HANDOFF.md) §0、§4。

## 2. 实现

| 层 | 文件 | 内容 |
|---|---|---|
| 光列表 | `Runtime/ForwardLights.cs` | `InitParams.clusteredDeferred` → `m_UseClusters` 仅控制 cluster 构建/上传；`_FORWARD_PLUS` 与逐物体灯索引仍只由 `m_UseForwardPlus` 控制。新增 `CompleteClusterJobs()`。 |
| 接入 | `Runtime/UniversalRenderer.cs` | 渲染器开关只对 `RenderingMode.Deferred` 生效；设备不支持时回退并告警；`maximumVisibleLights` 在 culling 前收紧；native RP 在构造期关闭并告警；`EnqueueDeferred` 二选一；保留 forward-only pass。 |
| Mask | `Shaders/Utils/ClusteredDeferredMask.shader` | 两个 stencil 测试全屏 draw，写 `R8_UINT`：0 跳过 / 1 Lit / 2 SimpleLit。 |
| Kernel | `Shaders/Utils/ClusteredDeferred.compute`、`ShaderLibrary/ClusteredDeferred.hlsl` | 像素并行 16×16 dispatch；主光、附加方向光、cluster 附加光；阴影、cookie、light layers、mixed lighting、GTAO 直接光系数 + 间接光取消系数。 |
| Resolve | `Shaders/Utils/ClusteredDeferredResolve.shader` | `Blend One SrcAlpha, Zero One` 把 UAV 直接光加进 GBuffer3。 |
| C# pass | `Runtime/ClusteredDeferred/ClusteredDeferredPass.cs` | 可选 shadowmask/rendering-layers 绑定、mix-mode uniform、`_DEFERRED_MIXED_LIGHTING` 关键字、RT 生命周期。 |

开关：`UniversalRendererData.clusteredDeferredLighting`（默认关，`SetDirty()` 触发渲染器重建，可运行时切换）。

## 3. 验证

### 3.1 Player A/B 与性能

D3D11/D3D12 Development player（`ClusteredDeferredArena`，1280×720，48 盏附加灯）：

| 项 | 结果 |
|---|---|
| stencil vs clustered（48 灯） | mean 0.785/255、max 9/255、>8 仅 5/921600 px → `passed=true` |
| 性能交叉点（D3D11，720p，GPU mean） | 0 灯 0.186 / 8 灯 0.197 / 16 灯 0.192 / 32 灯 0.212 / 48 灯 0.241 / 64 灯 0.254 ms；stencil 对应 0.191 / 0.212 / 0.230 / 0.344 / 0.484 / 0.709 ms |
| 32 灯 GPU 比值 | clustered/stencil = 0.616；64 灯 = 0.358 |
| Forward+ | 同场景 0.255–0.420 ms，始终高于 clustered |

完整数据：`validation/T5-runtime/player-d3d11/CdPerf.json`、`validation/T7/T7Perf.json`。

### 3.2 特性矩阵

7 个单项在 D3D11/D3D12 均 `passed=true`：主光 cascade、`_RECEIVE_SHADOWS_OFF`、screen-space shadow、附加光 atlas、spot cookie、light layers、GTAO 直接光 AO。要点：

- AO 必须是“直接光系数 + 间接光取消系数”两段；只做直接光会在 GTAO 写 AO=0 的列上与 stencil 的 `_DEFERRED_FIRST_LIGHT` 取消语义不一致。
- cookie/light layers 在两种 API 上差异 ≤ 5/255；AO 因空间去噪的采样位置不同残留 ≤ 9/255、>8 至多 1 px。
- mixed lighting-shadowmask：kernel/绑定/变体已完成，但工程无 baked lightmap/probe，`Light.bakingOutput` 在运行时无法伪造，故标 PARTIAL。

### 3.3 编辑器测试与运行时门控

| 套件 | 结果 |
|---|---|
| EditMode（仅 ClusteredDeferred） | 10/10：compute/mask/resolve 编译、1280 变体矩阵、无 structured-buffer 分支、设备/相机门控、Deferred-only 生效、culling 上限/probe 排序、重复 Dispose |
| EditMode（全量） | 484/484 |
| PlayMode（仅 ClusteredDeferred） | 7/7 |
| PlayMode（全量） | 36/36 |
| ModeGate 单跑 | `T5ModeGateTests.xml` |

PlayMode 专题：

- **材质分流**：Lit/SimpleLit/Unlit/ComplexLit 混合场景，stencil vs clustered A/B 在阈值内，证明 mask 0/1/2 与 forward-only 回退正确。
- **运行时开关**：off 后与 stencil 管线等价，on 后复现 clustered 图像。
- **资源生命周期**：切换渲染器后 mask/lighting 临时 RT 释放为 0。
- **tile 宽度**：`_FPParams1` 实测 640×360/8 灯→8、1280×720/48 灯→16、1920×1080/65 灯→32、3840×2160/65 灯→64，无宽度 gate。

**测试暴露并修复的集成问题**：cluster jobs 在 `Setup` 早期调度、持有 culling 灯数组；同一 `Setup` 后半段的 `ResolveMixedLightingMode` 请求可变访问，编辑器 Job safety 抛异常并中断该帧，随后 `PreSetup` 的 MemClear 级联失败。修复为在 `ResolveMixedLightingMode` 之前完成 jobs（`UniversalRenderer.cs` + `ForwardLights.CompleteClusterJobs()`）。Player 关闭 safety 检查且该访问只读，GPU 输入不变；修复后 PlayMode 由 0/7 变为 7/7。

### 3.4 RenderDoc

D3D12 player 抓帧（1 个，14.2 MiB，预算 ≤20 MiB）：GBuffer（54 draws）→ CopyDepth → **Clustered Deferred**（清 mask、2 个 stencil mask draw、1 次 80×45 dispatch、1 次 resolve）→ Forward Only（0）→ FinalBlit；无 stencil 灯体积。绑定 5 SRV + 1 UAV + `URP_ZBinBuffer`(16 KB)/`urp_TileBuffer`(64 KB)，UAV 670 写、677 读。摘要：`validation/T5-runtime/renderdoc-clustered-deferred-d3d12.json`。

本轮目录另有 T6 基线的 stencil 对照抓帧 `validation/T6-preflight/clustered-deferred-d3d12-stencil-baseline.rdc`（7.4 MiB）。两个抓帧合计 22.2 MiB、均 ≤20 MiB，但数量超过 HANDOFF §7.5 的“默认 0、批准时最多 1 个”口径；两者分别用于 clustered 与 stencil 的 pass 结构对照，保留是为了 AC-12 的可复查性。

## 4. 已知差异与未覆盖

见 [KnownDifferences.md](KnownDifferences.md)。要点：mixed lighting PARTIAL；point/directional cookie 仅编译覆盖；Vulkan/Metal 未验证；性能仅 D3D11；player 内运行时开关未验证；player 证据早于 job 完成顺序修复（GPU 输出不变）。

## 5. 证据索引

- 计划与决策：`HANDOFF.md`（§7.5.1–§7.5.4）
- T4 阴影/特性：`validation/T4-shadows/`
- 运行时 A/B、性能、RenderDoc：`validation/T5-runtime/`
- 覆盖率扫描与 stencil 基线抓帧：`validation/T6-preflight/`
- T5/T7 测试与汇总：`validation/T7/`
