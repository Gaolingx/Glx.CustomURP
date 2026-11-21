# 11-LightCulling 善后：死代码清理计划（暂不执行）

> 位置说明：11-LightCulling 的实现与验收已归档到 [`PLAN/Done/11-LightCulling`](../../Done/11-LightCulling/HANDOFF.md)。本文件是**后续善后计划**，按用户指定放在 TODO，便于在下一轮（建议 `12-*`）启动时并入该轮任务表；它不是已归档交付物的一部分。
>
> 状态：规划稿，2026-11-22。所有“可删”项都带有前置条件；**当前一行都还不能删**。

---

## 0. 先纠正四个前提

用户提到“CPU jobs 光剔除、stencil deferred、`CullingResults`/cullingMask/layer 都会变成死代码”。大方向对，但需要拆开看：

### 0.1 CPU 光列表现在不是死代码

当前 clustered kernel 的输入仍然是 CPU 构建的 tile/zbin 列表：

- `ForwardLights.PreSetup` → `LightMinMaxZJob` / `ReflectionProbeMinMaxZJob` / `ZBinningJob` / `TilingJob` / `TileRangeExpansionJob`
- 产物 `URP_ZBinBuffer`、`urp_TileBuffer`、`_FPParams0/1/2`
- kernel 侧 `URP_FP_TILE_SCALE` / `URP_FP_TILE_COUNT_X` / `URP_FP_WORDS_PER_TILE`

只有当 **GPU 光表（lightlistbuild + FPTL 精剪 + 异步两段式）成为默认路径**后，这一整套才变成死代码。

### 0.2 Stencil deferred 现在仍承担四件事

1. 本轮全部 A/B 的参照路径（`validation/T4-shadows`、`T5-runtime` 的 stencil 半边）。
2. 不支持 compute / `useStructuredBuffer=true` / GLES 时的回退。
3. Preview / Overlay / XR 相机的路径（clustered 只接受 Game + Base + 非 XR）。
4. deferred 的 legacy fog（`DeferredLights.RenderFog`）与旧 AO 入口（`RenderAmbientOcclusionBeforeShading`，clustered 已用自己的实现替代）。

删除前每一项都要有替代或明确放弃的决定，否则它是回退路径，不是死代码。

### 0.3 `CullingResults`、`cullingMask`、layer 语义不能整体删

- `CullingResults` 是引擎类型（`UnityEngine.Rendering`），不是 URP 代码，删不掉；URP 能删的只是**灯列表构建**对 `visibleLights` 的那部分消费。它同时承载 `visibleRenderers`（DrawRenderers）、`visibleReflectionProbes`、shadow casters、lightmap 等。
- `Camera.cullingMask` 决定**物体是否渲染**，与灯无关；`Light.cullingMask` 与 `renderingLayerMask` 决定**哪些灯照哪些物体**，GPU 光表仍必须消费（clustered kernel 里的 `IsMatchingLightLayer` 就是它）。
- 能删的是 URP 侧“为了是否把某盏灯塞进某物体索引”而做的 CPU 逐物体过滤（`GetPerObjectLightFlags`、Forward+ 的 per-object 索引生成），前提是所有受光物体都改为读 GPU 光表。

### 0.4 “保留 CPU 可见光剔除”是输入供给问题，不是 fallback

GPU 光表仍需要一份候选灯数据。最便宜的来源就是 CPU 的 `CullingResults.visibleLights`（引擎已按 cullingMask/距离/阴影剔除）。如果连它也要删，等于把“场景灯注册表 + GPU 上传 + 灯层/阴影/烘焙状态维护”一起搬到 GPU——那是独立的一轮工作量，不是清理。
建议：**保留 CPU 可见光剔除作为候选供给，只删 tile/zbin/job 构建**；“完全 GPU 灯注册表”列为可选的 C8。

---

## 1. 目标终态（先冻结决策）

删除动作取决于终态。下面 8 个决策必须在 C0 冻结，否则清理会把回退路径一起删掉。

| ID | 决策 | 建议默认 | 影响 |
|---|---|---|---|
| D-C1 | 设备门槛 | D3D11/D3D12/Vulkan/Metal（compute + 常量灯数组）；明确放弃 GLES/WebGL | 放弃 GLES 才能删 `IsSupportedDevice`/回退与 stencil 路径 |
| D-C2 | Deferred 模式 | 只保留 clustered；`UniversalRendererData.clusteredDeferredLighting` 开关与 stencil 分支一起删除 | 决定 `DeferredPass`/`StencilDeferred.*` 是否可整删 |
| D-C3 | 灯数据来源 | 短期保留 CPU `visibleLights` 候选；长期（C8）再评估 GPU 灯注册表 | 决定 `CullingResults.visibleLights` 的消费能否归零 |
| D-C4 | 透明/forward-only | 未接管前，`_AdditionalLightsIndices`、`maxPerObjectAdditionalLightsCount`、Forward+ 索引**不能删** | 决定 C2 的规模 |
| D-C5 | deferred fog | 迁到 clustered resolve（或 GL 内置 fog），否则接受 deferred 无雾 | 决定 `RenderFog` 的删除顺序 |
| D-C6 | 阴影 | 保留 `AdditionalLightsShadowCasterPass`（CPU 逐灯 atlas 设置）；只删 stencil 光照 | 阴影仍需 CPU 可见灯 |
| D-C7 | Preview/Overlay/XR | 允许退到 Forward 并保留 `UniversalForward` 灯路径，或禁止这些相机 | 决定是否保留 forward-only 灯代码 |
| D-C8 | 兼容性 | 本仓库是 embedded 包，允许 break；不承诺外部工程 API 兼容 | 允许直接删除公开属性 |

---

## 2. 删除清单（按子系统）

### 2.1 Stencil deferred 光照

| 对象 | 规模/位置 | 前置 | 替代 / 处理 |
|---|---|---|---|
| `Runtime/Passes/DeferredPass.cs` | 84 行 | C2/C5 完成 | 整文件删除；`EnqueueDeferred` 只留 clustered 分支 |
| `Shaders/Utils/StencilDeferred.shader` | 476 行 | clustered fog/AO 迁移 | 整文件 + `.meta` 删除 |
| `Shaders/Utils/StencilDeferred.hlsl` | 352 行 | 同上 | 整文件删除 |
| `DeferredLights` 的 stencil 部分 | `DeferredLights.cs` 共 1213 行 | GBuffer/附件/深度/mixed 检测保留 | 删 `PrecomputeLights`、`RenderStencilLights*`、`SetupShaderLightConstants`（与 ForwardLights 重复）、`RenderAmbientOcclusionBeforeShading`、`RenderFog`、`InitStencilDeferredMaterial`、`CreateSphereMesh`/`CreateHemisphereMesh`/`CreateFullscreenMesh`、`ExecuteDeferredPass`、`ClearStencilPartial`；`OverwriteStencil` 只删灯体积用法，**GBuffer 材质 stencil 位保留**（mask pass 依赖） |
| 材质/资源引用 | `shaders.stencilDeferredPS`、`m_StencilDeferredMaterial`、`InitStencilDeferredMaterial` | 同上 | 删除字段与 `ResourceReloader` 项；`UniversalRendererData` 资源重新序列化 |
| Profiler/名称 | `k_DeferredStencilPass`、`m_ProfilingSamplerDeferredStencilPass` | 同上 | 删除；RenderDoc 事件名随之消失 |
| 关键字 | `_DEFERRED_STENCIL`、`_DEFERRED_FIRST_LIGHT`、stencil 专用 `_LIT`/`_SIMPLELIT` 变体 | 先确认 `_LIT`/`_SIMPLELIT` 是否被其他 shader 复用 | 删除 shader 内 `multi_compile` 与 `ShaderKeywordStrings`；保留 `_DEFERRED_MIXED_LIGHTING`（clustered 也用） |

**不能删**：GBuffer pass 写入的 `MaterialLit`/`MaterialSimpleLit` stencil 位与 `StencilUsage`——clustered 的 mask pass 仍靠它分流 0/1/2。

### 2.2 CPU 光列表构建（GPU 光表上线后）

| 对象 | 规模/位置 | 处理 |
|---|---|---|
| `Runtime/Tiling/LightMinMaxZJob.cs` | 56 行 | 删除 |
| `Runtime/Tiling/ZBinningJob.cs` | 102 行 | 删除 |
| `Runtime/Tiling/TilingJob.cs` | 955 行 | 删除 |
| `Runtime/Tiling/TileRangeExpansionJob.cs` | 63 行 | 删除 |
| `Runtime/Tiling/ReflectionProbeMinMaxZJob.cs` | 41 行 | 删除（探针列表若由 GPU 重建，一并处理） |
| `Runtime/Tiling/InclusiveRange.cs`、`TileSize.cs` | 68 行 | 仅被上述 jobs 使用则删 |
| `ForwardLights` 的 `PreSetup`/`m_CullingHandle`/`m_ZBins`/`m_TileMasks`/`*Buffer`/`m_ActualTileWidth`/`m_WordsPerTile`/`m_TileResolution` | `ForwardLights.cs` | 删除 job 与缓冲；保留 `SetupShaderLightConstants`、cookie atlas、probe manager、灯数据上传 |
| `_FPParams0/1/2`、`URP_ZBinBuffer`、`urp_TileBuffer` | C# + HLSL | GPU 光表改用新布局后删除；HANDOFF D4 的“保持缓冲布局不变”在这一步失效，需同步更新契约 |
| `ClusteredDeferred.hlsl` 的 `URP_FP_*` 宏与 cluster 迭代器 | include | 换成 GPU 光表读取；`Clustering.hlsl` 的 CPU 列表语义一并删除 |

### 2.3 逐物体灯索引 / Forward+ 过期路径（C2，视 D-C4/D-C7）

| 对象 | 处理条件 |
|---|---|
| `_AdditionalLightsIndices` 上传与 shader 读取 | 仅当透明/forward-only 已改为读 cluster/GPU 光表 |
| `LightData.maxPerObjectAdditionalLightsCount`、`GetPerObjectLightFlags` | 同上 |
| `ForwardLights.Setup` 中的 `AdditionalLightsVertex/Pixel` 关键字、`ForwardPlus` 关键字 | 若仍保留 Forward/Forward+ 渲染模式则**保留** |
| `LIGHT_LOOP_BEGIN`/`INITIALIZE_LIGHT_LOOP` 的 per-object 分支 | 保留给 forward-only 物体，直到 D-C7 决定禁用它 |

> 这一块最容易误删：mask=0 的 forward-only 物体、透明物体、粒子目前仍走 forward 灯路径。**先让它们消费新光表，再删索引。**

### 2.4 变体、剥离与测试

| 对象 | 处理 |
|---|---|
| `UniversalRenderPipelineAssetPrefiltering.cs` | 删除 stencil/deferred 关键字收集；记录新的变体基线 |
| `Tests/Editor/ShaderScriptableStripperTests.cs`、`ShaderBuildPreprocessorTests.cs`、`ShaderPrefilteringTests.cs` | 更新期望的 keyword/变体数；删除只为 stencil 存在的断言 |
| `Tests/Editor/ClusteredDeferred` | 改为只测新路径；删除“渲染器开关只对 Deferred 生效”等过渡断言 |
| `Assets/Tests/ClusteredDeferred/Renderers/CdStencil*/CdShadowStencil*/CdScreenShadowStencil*/CdFeatureStencil*` | 删除；`Cd*Clustered*` 保留并成为基线 |
| `Assets/Tests/ScreenSpaceLighting/Renderers/Ssl*DeferredRenderer.asset`（13 个） | 把 `clusteredDeferredLighting` 打开并重建 golden images，或改为 Forward 路径（取决于 D-C2） |
| `ClusteredDeferredArenaRunner` | 删除 stencil/Forward+ 模式、`--cd-t4` 的 stencil 半边、A/B 的 stencil 图；只保留 frozen baseline 对比 |
| `ClusteredDeferredArenaBuild` | 删除 stencil 管线构建入口；保留 D3D11/D3D12 release 构建 |
| `T4ShaderBuild.json`、`T5VariantCount.json`、`T7PlayerSummary.json` | 归档为 SUPERSEDED；新预算另立 |

### 2.5 灯数据与 `CullingResults`（只收窄，不整体删）

| 保留 | 理由 |
|---|---|
| `context.Cull` / `CullingResults` / `visibleRenderers` | 物体、阴影、探针、decal 的可见性 |
| `visibleReflectionProbes` | 探针与 SSGI/SSR fallback |
| `visibleLights` 作为候选数组 | GPU 光表的数据源（除非走 C8） |
| shadow caster 的逐灯设置 | 阴影 atlas 仍在 CPU |
| 烘焙状态（`bakingOutput`） | mixed lighting 检测 |

| 可删 | 前置 |
|---|---|
| Deferred 分支的 `maximumVisibleLights = maxVisibleAdditionalLights` 特殊 cap | GPU 光表拥有灯上限后 |
| `DeferredLights.PrecomputeLights` 对 `visibleLights` 的预计算 | stencil 删除时 |
| `LightData.maxPerObjectAdditionalLightsCount` | per-object 索引删除后 |
| `ForwardLights` 中的探针排序/插入排序 | GPU 光表接管探针后 |

### 2.6 layer / cullingMask（只删应用层）

| 保留 | 理由 |
|---|---|
| `Camera.cullingMask` | 物体渲染 |
| renderer 的 `renderingLayerMask` / Rendering Layers | decal、SSR、阴影分层 |
| `Light.cullingMask` | 灯照哪些物体 |
| `Light.renderingLayers` + `IsMatchingLightLayer` | clustered 已实现，GPU 光表继续消费 |
| GBuffer 的 `_WRITE_RENDERING_LAYERS` / `GBufferRenderingLayers` | light layers 解码 |

| 可删 | 前置 |
|---|---|
| URP 侧逐物体灯过滤（`GetPerObjectLightFlags`、per-object 灯索引生成） | 所有受光物体读新光表 |
| `DeferredLights` 内为 stencil 预计算灯数组的 layer/subtractive 分支 | stencil 删除 |

---

## 3. 执行顺序

| 步骤 | 内容 | 退出条件 | 验证 | 回滚 |
|---|---|---|---|---|
| **C0** | 冻结基线：tag（如 `pre-gpu-lightlist`）、记录 HEAD；重跑 D3D11/D3D12 player 的 A/B、性能、变体、RenderDoc，保存到新轮次 `validation/baseline/` | 基线可复现 | player + `CdPerf.json` + `.rdc` + PNG + XML | 无需回滚 |
| **C1** | **立即可做的收益**：clustered 模式下 `DeferredLights.SetupLights` 跳过 `PrecomputeLights`（stencil 灯数组），保留关键字/常量设置 | CPU 帧时间下降；A/B 不变 | T4/T5 矩阵复跑；与其一帧的 RenderDoc 结构不变 | 单行开关 |
| **C2** | 透明/forward-only 接管新光表（如 D-C4 要求）；之后再删 `_AdditionalLightsIndices`/per-object 索引 | 透明/fwd-only 像素 A/B 通过 | PlayMode + player A/B（含透明用例） | 保留旧索引路径的 feature flag |
| **C3** | 删除 stencil deferred：先迁移 fog/AO 入口、Preview/Overlay/XR/GLES 的回退决定，再删 pass/shader/material/关键字 | 无 stencil 引用；build 0 error | 全量 EditMode/PlayMode；player A/B vs C0 基线；`git grep` 证明 | 按子模块拆 commit；保留 C0 tag |
| **C4** | GPU 光表上线后删除 CPU tile/zbin jobs 与 `_FPParams*`/旧缓冲 | 新光表成为唯一输入 | 性能、结构、A/B | 保留旧 kernel 一个 tag |
| **C5** | 清理测试资产/变体/剥离/golden images | 测试全绿、变体基线更新 | EditMode/PlayMode + build 日志 | 资产随 commit |
| **C6** | 文档：ACCEPTANCE 标 SUPERSEDED、KnownDifferences/report 更新、删 A/B 说明 | 五处一致 | 链接检查 | — |
| **C7** | 死代码证明与最终验收 | 见 §5 | 见 §4 | — |
| **C8**（可选） | GPU 灯注册表：删除 CPU 可见光剔除与 `CullingResults.visibleLights` 消费 | 灯数据完全 GPU 化 | 大规模灯场、cullingMask/layer/阴影矩阵 | 独立一轮 |

**顺序原则**：C1 可以现在做；C2（透明/fwd-only 供光）与 C3（删 stencil）相互独立，但都必须在 C4 之前完成，保证删除后仍有完整的受光路径；C4 必须在 GPU 光表成为默认之后。

---

## 4. 验证配方

### 删除前（证明“真的没人用”）

```bash
# 符号级：只允许预期消费者
git grep -nw "PrecomputeLights\|RenderStencilLights\|StencilDeferred\|m_StencilDeferredMaterial"
git grep -n "_DEFERRED_FIRST_LIGHT\|_DEFERRED_STENCIL"
git grep -n "URP_ZBinBuffer\|urp_TileBuffer\|_FPParams"
git grep -n "_AdditionalLightsIndices"
# 资产级：还有哪些 renderer 资产引用 stencil 材质/关键字
grep -rl "m_ClusteredDeferredLighting: 0" Assets/ | ...
```

### 删除后（证明“确实没坏”）

- D3D11/D3D12 player 构建：0 `Shader error`、0 missing reference；构建日志里不再出现 stencil 关键字。
- EditMode + PlayMode 全量绿；本轮的 ClusteredDeferred 套件改名为新路径套件。
- 与 C0 冻结 player 的 A/B：mean ≤ 2/255、max ≤ 24/255（或新基线阈值），>8 像素比例不上升。
- RenderDoc：pass 结构只剩 GBuffer → CopyDepth → Clustered（+ resolve）→ Forward Only/Transparent → FinalBlit；无 stencil 灯体积。
- 变体计数：记录删除前后差值；不得静默放宽。
- 资源生命周期：mask/lighting RT、GraphicsBuffer 在销毁时释放（复用本轮 PlayMode 用例）。

每个 C 步骤一个可构建、可回滚的 commit；不把“删代码 + 改行为”混在同一次提交。

---

## 5. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 删 stencil 后 deferred legacy fog 消失 | C3 前把 fog 迁到 clustered resolve 或明确接受；A/B 加雾用例 |
| RendererFeature 注入在 GBuffer 与 deferred 之间（SSL/GTAO/SSR 依赖 `useRenderPassEnabled=false` 与 GBuffer3） | 保留 pass 顺序与 GBuffer3 语义；C3 后重跑 SSL 全家桶 |
| mixed lighting 仍是 PARTIAL | 清理不改变其状态；不要在 C3 顺带“修”它，另立任务 |
| 删 `_AdditionalLightsIndices` 导致透明/fwd-only 变黑 | C2 必须先让它们读新光表；加透明用例 A/B |
| 删 `CullingResults.visibleLights` 后灯数据来源缺失 | C8 单独做 GPU 灯注册表；C0–C7 保留候选供给 |
| GLES/mobile/XR 设备被永久排除 | D-C1 先决策；若产品需要，保留最小 Forward 回退而不是 stencil |
| 外部工程依赖 URP 公开 API（`DeferredLights` 等） | D-C8 明确允许 break；在 release note 记录 |
| golden images 大面积变化掩盖回归 | 先迁移渲染器、重建一次 golden，再开始删代码；A/B 只对 frozen player |
| 测试资产删除后无法复查历史 | 证据留在 `PLAN/Done/11-LightCulling/validation`；资产删除只影响可重跑性 |

---

## 6. 明确不删

- `context.Cull`、`CullingResults`、`visibleRenderers`、`visibleReflectionProbes`、shadow casters。
- `Camera.cullingMask`、renderer/light 的 layer 与 `renderingLayerMask`、`IsMatchingLightLayer`。
- GBuffer 的 `MaterialLit`/`MaterialSimpleLit` stencil 位与 `StencilUsage`（mask pass 依赖）。
- `_DEFERRED_MIXED_LIGHTING`（clustered 也用）、`LightmapShadowMixing`、`ShadowsShadowMask`、`MixedLightingSubtractive`。
- `DeferredLights` 的 GBuffer 附件、深度拷贝、`ResolveMixedLightingMode`、`UseShadowMask`/`UseRenderingLayers` 布局。
- `AdditionalLightsShadowCasterPass`、主光/附加光阴影常量、阴影 atlas。
- `ForwardLights.SetupShaderLightConstants`、cookie atlas、探针 manager。
- SSR/SSGI/GTAO 对 GBuffer/深度/法线的依赖。
- `Deferred.hlsl` 中 GBuffer 编码部分（与 stencil 无关）。

---

## 7. 退出标准

1. `git grep` 不再命中 stencil deferred 的实现符号与关键字；`Shader.Find("Hidden/Universal Render Pipeline/StencilDeferred")` 为空。
2. 两个 API 的 player 构建 0 shader error，且构建日志无 stencil 关键字；变体计数下降并记录。
3. EditMode/PlayMode 全绿；ClusteredDeferred 套件覆盖新的唯一路径。
4. 与 C0 冻结 player 的 A/B 在阈值内；RenderDoc 结构无 stencil 灯体积。
5. CPU 光列表代码删除后，性能不低于 C0（记录 GPU/CPU 差值）。
6. 五处文档一致；TODO/Done 不出现重复副本；`git status` 干净。

---

## 8. 工作量与衔接建议

| 步骤 | 粗估 |
|---|---|
| C0 基线 | 0.5 天 |
| C1（跳过 PrecomputeLights） | 0.5–1 天 |
| C2 透明/fwd-only 接管 + 删索引 | 3–5 天（取决于透明方案） |
| C3 删 stencil | 2–3 天 |
| C4 删 CPU jobs | 2–3 天 |
| C5 测试/资产/变体 | 2–3 天 |
| C6/C7 文档与最终验收 | 1–2 天 |
| C8 GPU 灯注册表（可选） | 5–10 天 |

**与下一轮的衔接**：下一轮按“GPU 光表 + async 两段式 → FPTL 精剪 → variant/间接派发”推进时，C1 可以顺手做；C3/C4 必须等新光表成为默认并拿到基线。不要把善后和功能开发放在同一个 commit 系列里——否则无法区分“功能回归”和“删代码回归”。
