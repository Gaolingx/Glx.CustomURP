# Screen Space Lighting 全量 GPU 验证与收尾交接（06-ValidationAllRDC）

> 更新：2026-11-17
> 状态：**静态审计完成；P0 基础设施完成并通过 9/9 冒烟；P1-1（F-01）与 P2-1（SSGI 生效链）完成**；P1-2、P1-3、P2-2 起未开始。P0 交付通用三渲染器 player、manifest、binding probe 与 `Report.json`/`BindingProbe_*.json` 证据格式。P1-1 修复 GTAO 全分辨率深度守卫；P2-1 完成 SSGI dispatch/history/LUT/atlas 的 CPU+GPU 验收（10/10）。
> 上游语义基准：HDRP 14.0.12（`E:\workspace\GitHub\HDRPSample01\Library\PackageCache\com.unity.render-pipelines.high-definition@14.0.12`，下文 `HDRP/`）
> URP 目标包：`URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12`（下文 `URP/`）
> 关联文档：[T14 已知差异](../../Done/01-ScreenSpaceLighting/validation/T14/T14KnownDifferences.md)、[T14 支持矩阵](../../Done/01-ScreenSpaceLighting/validation/T14/T14SupportMatrix.md)、[01 ScreenSpaceLighting](../../Done/01-ScreenSpaceLighting/HANDOFF.md)、[02 SSRImprove](../../Done/02-SSRImprove/HANDOFF.md)、[03 SSRImprove2](../../Done/03-SSRImprove2/HANDOFF.md)、[04 SSGIFallbackImprove](../../Done/04-SSGIFallbackImprove/HANDOFF.md)、[05 ScreenSpaceRefractionImprove](../../Done/05-ScreenSpaceRefractionImprove/HANDOFF.md)、[RenderDoc 操作手册](../../../../MCP/RenderDocOperations.md)

---

## 0. 本轮做了什么

本轮先做**静态审计**，目标是回答三个问题：效果是否真的"生效"、绑定是否正确、与 HDRP 还剩哪些差异。审计方法：四路并行只读审计（GTAO / SSGI / SSR+Transparent SSR / 共享 keyword-uniform-lifecycle），再由主线直接复核高危结论。

**已直接复核的结论**

| 结论 | 证据 |
|---|---|
| GTAO 全分辨率路径的中心/采样深度分支被编译掉 | `GTAOCommon.hlsl:88,97` 使用 `GTAO_FULL_RES`，但整个包从未定义；`GTAO.compute:4` 只定义 `HALF_RES/FULL_RES` |
| SSR 反射降噪器的像素展开角从未真正绑定 | `ReflectionDenoiser.compute:75` 把 `_PixelSpreadAngleTangent` 映射到 `_SsgiDenoiserParams.x`；`SsrPass.cs:363` 写的是独立 float；`_SsgiDenoiserParams` 只有 `SsgiPass.cs:729` 上传 |
| 折射全局 keyword 没有相机起点清理 | `ScriptableRenderer.cs:1419-1445` 清理 GI/SSR/TransparentSSR，没有 `_SCREEN_SPACE_REFRACTION` |
| SSR 调试视图会覆盖正式输出 | `SsrPass.cs:278-285` 的 `DebugSsrParams` kernel 写入 `m_History.ssrOutput`，而该纹理随后交给材质绑定 |
| 透明深度种子忽略 Y 翻转 | `TransparentDepthNormalRenderPass.cs:109` 固定 `new Vector4(1,1,0,0)`；URP 自己的 `CopyDepthPass` 会按 `IsHandleYFlipped` 取反 |
| SSGI 探针 atlas 开关不在历史 stamp 内 | `SsgiPass.cs:702-718` 的 `ComputeSettingsStamp` 未包含 `publishProbeAtlas` |
| 光线步进最大 mip 6 是有意适配 | `RayMarching.hlsl:145-147` 与 `ScreenSpaceReflections.compute:15,38` 有明确注释，不是缺陷 |

**结论摘要**

- GTAO、SSGI、SSR（含 Transparent SSR）的"效果开关 → 全局 keyword → 材质变体"主链是完整的，前几轮 T5–T12 的结构性证据仍然成立。
- 发现 **2 个高危绑定缺陷**：GTAO 全分辨率深度读取错误；SSR 降噪器像素展开角未绑定（导致移动相机下时域累积近似失效）。
- 发现 **6 个中危问题**：GTAO 正交深度/视线方向、SSR 调试输出覆盖、折射 keyword 泄漏、透明深度 Y 翻转、SSGI 历史 stamp 不完整、共享 ComputeShader keyword 依赖调用顺序。
- 发现一批低危死代码/重复声明/过时文档，适合本轮直接删除（不做兼容）。
- 既有证据的覆盖盲区：GTAO 全分辨率与正交、SSGI 的多探针混合与覆盖比例、SSR 降噪器常量与 history 接受率、三效果的 cbuffer 数值绑定，都没有 GPU 侧证据。

**本轮未做**：任何运行时修改、shader 修改、测试运行、player 构建、`.rdc` 采集。本文件只冻结范围、缺陷与验收标准。

**工具链冒烟（2026-11-17）**：RenderDoc 1.47、renderdoc-mcp v0.3.0、Python 3.13.9、Unity 2022.3.62f1 与三渲染器资产均已确认；在归档 P1-3 抓帧上跑通 `renderdoc-cli info/draws`、`rdmcp.py open/debug-pixel`，像素输出与归档证据一致。构建/抓帧段路径与参数就绪，实际 player 构建留给 P0-1。详见 [validation/P0/ToolchainSmoke.md](validation/P0/ToolchainSmoke.md)。原生 MCP 未在当前会话暴露，使用 CLI 或 `MCP/tools/rdmcp.py`。

**P0 基础设施与冒烟（2026-11-17）**：`ScreenSpaceLightingValidationPlayerSetup` 生成三套 validation renderer/pipeline、Profile、材质与场景，并构建 D3D11 Development player；`ScreenSpaceLightingValidationPlayerVerification` 按 manifest 切换渲染器/Volume/Feature、设置位姿、预热、抓帧、截图并写 `Report.json`；`ScreenSpaceLightingBindingProbeFeature` + `ScreenSpaceLightingBindingProbe.compute` 回读 35 个 cbuffer/全局字段。9 个用例（三渲染器 × GTAO/SSGI/SSR/折射）全部通过：抓帧成功、0 非有限像素、probe 0 不匹配；dispatch census 与图像差异见 [validation/P0/SmokeReport.md](validation/P0/SmokeReport.md)。`BuildPlayer` 以 Deferred 资产构建（否则 URP 会剥离 deferred 变体），Forward/Forward+ 仍在运行时切换正常。

**P1-1 与 P2-1（2026-11-17）**：F-01 修复 `GTAOCommon.hlsl` 的 `#ifdef FULL_RES` 守卫并删除 `_GtaoParams0.x` 死写入；新增 EditMode 回归测试。GPU 前后对比中 full↔half 相关系数 0.99090 → 0.99983、平均绝对差 1.005 → 0.144，半分辨率路径不变（证据 [validation/P1-1/Report.md](validation/P1-1/Report.md)）。P2-1 为 `ScreenSpaceLightingSsgiPass` 增加 DEV-only 调度快照（计数、组维度、keyword、history 槽位），10 个 SSGI 用例覆盖 full/half、temporal/denoise 变体、atlas 与三渲染器；CPU 逐 kernel 匹配，GPU 抓帧确认 dispatch 顺序、history 乒乓、blue-noise LUT 与探针 atlas 绑定（证据 [validation/P2-SSGI/Report.md](validation/P2-SSGI/Report.md)）。

---

## 1. 目标、边界与不变量

### 1.1 目标

1. **生效（active）**：对 GTAO、SSGI、SSR、Transparent SSR，在 Forward / Forward+ / Deferred 三种渲染器下证明
   - C# 效果开关正确映射到全局/材质 keyword；
   - 每个 compute/shader pass 确实被调度，且 dispatch 维度、纹理尺寸、slice 数正确；
   - uniform / cbuffer / 纹理 / 历史缓冲按预期绑定，数值来自本帧本相机；
   - 材质端确实消费结果（weight > 0 的像素存在），且只在预期材质上生效；
   - 关闭效果 / intensity=0 / Volume 关闭时输出逐字节回到基线。
2. **正确（correct）**：用数值、像素与时间序列证明
   - 深度/法线/运动矢量/金字塔/探针 atlas 的语义正确；
   - 时域累积与历史重置在静止、移动、相机 cut、分辨率变化下行为正确；
   - 半分辨率/全分辨率、正交/透视、MSAA、Deferred 兼容路径不产生 NaN、越界、双计或黑块；
   - 发现并修复审计列出的缺陷。
3. **差异收尾（difference review）**：把与 HDRP 14 的差异分为四类并固化：
   - A. 必须依赖 HWRT 或本机不具备的引擎能力（排除，不实现）；
   - B. URP 引擎架构限制（可记录、不承诺）；
   - C. 有意的算法/实现适配（记录理由）；
   - D. 本轮发现的缺陷（修复或明确接受）。
4. **清理归档**：删除死代码、重复声明、兼容 shim 与过时注释；更新已知差异/支持矩阵；把本轮证据归档到 `PLAN/Done/06-ValidationAllRDC/`。

### 1.2 不在本轮范围

- 任何硬件光追效果：HDRP 的 RTR、RTAO、RTGI、RTSSS、RT refraction，以及依赖加速结构的降噪路径。
- HDRP 的 FPTL / 逐像素 env-light hierarchy / `EnvLightData` StructuredBuffer / 多光源 influence 权重；URP 继续使用反射探针 atlas 与盒权重。
- HDRP 的 stencil 粗粒度拒绝、曝光控制、APV、透明物体 motion vector。
- 折射的 `P2-2` pre-refraction 队列、`P2-3` 厚度/透射贴图、`P2-4` 预积分 FGD LUT；折射只做共享绑定清理与回归，不重新验收其算法。
- Metal / mobile / OpenGLES / 真实 HMD；硬件动态分辨率（URP 14.0.12 不支持）。
- 跨管线逐像素一致。HDRP 与 URP 的曝光、探针、GI、雾不同，差异复盘以源码对照 + URP 自身数值断言为准。

### 1.3 必须保持的不变量

1. **关闭基线逐字节不变**：效果关闭、Volume 关闭、`intensity=0`、材质未 opt-in 时，最终图像与当前基线逐字节一致（折射沿用 05 的约定）。
2. **非目标材质不受影响**：SimpleLit、Unlit、Particles、Terrain、Decal、未 opt-in 的透明材质输出不变，SSGI/SSR 权重为 0。
3. **无每帧托管分配**：新增验证代码不得进入播放器每帧路径；probe 扫描仍只在 30 帧节流点发生。
4. **无资源泄漏**：相机切换、分辨率变化、效果开关循环后，RTHandle/RT 计数回到基线；全局纹理不得指向已释放句柄。
5. **证据可复现**：每个缺陷/验收项必须有 `validation/06-ValidationAllRDC/` 下的图像、JSON 或 GPU 抓帧；不接受"看起来正常"。
6. **允许破坏性变更**：本轮可以删除 Volume 字段、死 uniform、兼容分支与旧调试视图；但必须同步更新测试、文档和已知差异，不保留旧路径。

---

## 2. 现状：执行链、生效链、绑定链

### 2.1 每帧执行顺序

GTAO 位于 `URP/Runtime/RendererFeatures/GroundTruthAmbientOcclusion/` 与 `URP/Shaders/Utils/GroundTruthAmbientOcclusion/`；SSGI/SSR/折射位于 `ScreenSpaceLighting/`。

| 事件 | Pass | 说明 |
|---|---|---|
| 202 | Motion Vectors | 核心 URP；SSL 的时域依赖 |
| 203 | `ScreenSpaceDepthPyramidPass` | GTAO 与 SSL 共享 packed Hi-Z；1 次 copy + N-1 次 downsample |
| 204 | `GroundTruthAmbientOcclusionPass` | compute-only；末尾激活 `_SCREEN_SPACE_OCCLUSION` 并写 `_AmbientOcclusionParam` |
| 205 | `ScreenSpaceLightingSsgiPass` | trace → reproject → validate → temporal → copy → denoiser → upsample |
| 206 | `ScreenSpaceLightingSsrPass` | Approximation 2 dispatch；PBR 全分辨率 6；PBR 半分辨率 7 |
| 207 | `ScreenSpaceLightingSetupPass` | 绑定 SSGI/SSR 纹理并启用 `_SCREEN_SPACE_GI` / `_SCREEN_SPACE_SSR` |
| 210 | Deferred GBuffer | Deferred 材质在这里消费 SSGI/SSR |
| 250 | 不透明物体 | Forward / Forward+ 在这里消费 |
| 300 | Cleanup | 关闭不透明阶段的 GI/SSR keyword（透明不再消费） |
| 400 | `CurrentColorPyramidPass` | 生成当前帧颜色金字塔 |
| 447 | `TransparentDepthNormalRenderPass` | 复制不透明深度 + 绘制 opt-in 透明深度/法线 |
| 448 | `RefractionPass` | 绑定折射颜色金字塔/深度/probe 表，启用 `_SCREEN_SPACE_REFRACTION` |
| 449 | `TransparentSsrPass` | 透明 SSR trace + reproject |
| 450 | 透明物体 | 消费折射/透明 SSR |
| 501 | `ColorHistoryCapturePass` | 颜色金字塔 + 上一帧深度/法线，交换 history |
| 551 / 552 | SSL Debug / GTAO Debug | 仅在调试模式开启时入队 |

### 2.2 生效链

| 效果 | 用户入口 | C# 条件 | 全局/材质 keyword | Shader 分支 |
|---|---|---|---|---|
| GTAO | Renderer Feature 上的 `GroundTruthAmbientOcclusionSettings`（**没有 Volume 组件**） | `intensity > 0` 或 debug | `_SCREEN_SPACE_OCCLUSION` | `AmbientOcclusion.hlsl`、`StencilDeferred.hlsl` |
| SSGI | Volume `ScreenSpaceGlobalIllumination.enabled` | pass 产出有效输出 | `_SCREEN_SPACE_GI` | `ScreenSpaceLightingBlend.hlsl`、`Lit.shader`、`ComplexLit.shader` |
| SSR | Volume `ScreenSpaceReflection.enabled` | pass 产出有效输出 | `_SCREEN_SPACE_SSR` | 同上 |
| Transparent SSR | `enabledTransparent` + 材质本地 `_TRANSPARENT_SSR_RECEIVE` | pass 产出有效输出 | `_SCREEN_SPACE_TRANSPARENT_SSR` | 同上 |
| 折射 | Volume `ScreenSpaceRefraction.enabled` | Volume + intensity>0 + pyramid/depth 可用 | `_SCREEN_SPACE_REFRACTION` | `ScreenSpaceRefraction.hlsl`、`LitForwardPass.hlsl` |

计算侧 keyword 全部设置在 `ComputeShader` 资产上（不是 per-dispatch）：`SSL_XR_ARRAY`、`SSGI_HALF_RES`、`SSGI_NORMALS_OCT`、`SSR_APPROX`、`SSR_HALF_RES`、`SSR_NORMALS_OCT`、`SSL_PROBE_ATLAS`、`SSL_PROBE_LIST`、`SSGI_BLUE_NOISE`。同一帧内多相机/多渲染器共享同一资产状态，正确性依赖调用顺序（见 F-08）。

### 2.3 绑定链

| 类别 | 代表项 | 写入 | 读取 |
|---|---|---|---|
| 尺寸/常量 | `_SsgiScreenSize`、`_SsgiTraceSize`、`_SsrScreenSize`、`_SsrTraceSize` | `SsgiPass.cs:668-679`、`SsrPass.cs:502-542` | `ScreenSpaceLightingCommon.hlsl`、各 compute |
| 时域 | `_SsgiRayMarchingParams2.z`（history validity）、`_SsgiPrevInvViewProj`、`_SsrPbrFlags` | `SsgiPass.cs:675`、`SsrPass.cs:338,527` | `TemporalFilter.compute`、`ReflectionDenoiser.compute` |
| 像素展开角 | `_SsgiDenoiserParams.x` | **只有** `SsgiPass.cs:729` | `ReflectionDenoiser.compute:75,175` |
| 深度金字塔 | `_SsgiDepthPyramid` + `_SsgiDepthPyramidMipLevelOffsets` | 共享 pyramid pass | `RayMarching.hlsl`、GTAO compute |
| 颜色金字塔 | 当前/上一帧 RGBA16F + mip | `ColorPyramidPass.cs` | SSGI reproject、SSR reproject、折射 |
| 探针 atlas | `urp_ReflProbes_*`、`_SsgiFallbackParams`、`_SsgiAmbientProbeSH` | `ReflectionProbeManager`、`SsgiPass.cs:304-314,689-700` | `ReflectionProbeFallback.hlsl` |
| 折射表 | `_SslRefractionProbeMin/Max/Params[8]`、`_SslRefractionProbeCount` | `RefractionPass.cs:132-148` | `ScreenSpaceRefraction.hlsl:283-303` |
| 历史 | SSGI/SSR 双缓冲 + R16 sample count | `ScreenSpaceLightingHistory.cs` | 各 temporal/denoiser kernel |

**关于 cbuffer 数值证据。** 现有 EditMode 测试（`SsgiConstantBufferLayoutMatchesTheGeneratedInclude`、`SsrConstantBufferLayoutMatchesTheGeneratedInclude`、`GTAO` 同类测试）只校验 C# 结构与生成 HLSL include 的字段顺序/大小，不校验运行值。RenderDoc 抓帧里 Unity player 会剥离 DXBC 反射变量名，`renderdoc-mcp v0.3.0` 又没有 cbuffer 工具；因此本轮用 **binding probe**（6.3）把选定的 uniform 原样输出到 RT 并 CPU 回读，作为 cbuffer 数值绑定的主证据；RDC 的 dispatch/纹理/PSO/shader hash 作为第二证据。可选升级：从 `renderdoc-mcp` 上游 `main` 源码构建带 `list_cbuffers` / `get_cbuffer_contents` 的版本，作为第三证据。

### 2.4 材质消费

| 材质 | SSGI | SSR | Transparent SSR | 折射 | GTAO |
|---|---|---|---|---|---|
| Lit / ComplexLit | 是 | 是 | 仅 opt-in | 仅 `_REFRACTION_*` | 是 |
| ShaderGraph Lit | 是 | 是 | 仅 opt-in | 是 | 是 |
| SimpleLit / BakedLit | 否 | 否 | 否 | 否 | 是（若写 DepthNormals） |
| Particles / Terrain | 否 | 否 | 否 | 否 | 视 shader |
| Unlit | 否 | 否 | 否 | 否 | 否 |

Deferred 下 SSGI/SSR 在 GBuffer 阶段消费（事件 207 < 210）；Forward/Forward+ 在不透明绘制阶段消费（250）。透明阶段不再消费不透明 SSGI/SSR，这是有意设计（Cleanup 在 300）。

---

## 3. 审计结论：缺陷清单

严重度定义：**High** = 效果在常见配置下明显错误或失效；**Medium** = 特定场景/相机/分辨率下错误，或状态泄漏；**Low** = 死代码、清理、可维护性。

| ID | 级别 | 效果 | 现象 | 证据 | 对应任务 |
|---|---|---|---|---|---|
| F-01 | High | GTAO | 全分辨率模式的中心深度/首次采样读取错误区域，AO 在 full-res 下与实际几何不匹配 | `GTAOCommon.hlsl:88,97` 用未定义的 `GTAO_FULL_RES`；`GTAO.compute:4` 只定义 `FULL_RES` | P1-1 |
| F-02 | High | SSR | 反射降噪器的像素展开角为 0；移动相机时 history 世界半径退化为 0.1 下限，时域累积近似失效；SSR-only 相机读到未定义值 | `ReflectionDenoiser.compute:75`；`SsrPass.cs:363` 写独立 float；`_SsgiDenoiserParams` 仅 `SsgiPass.cs:729` 上传 | P3-1 |
| F-03 | Medium | GTAO | 正交相机用透视 `_ZBufferParams` 线性化深度；`V = normalize(-positionVS)` 在正交下方向随屏幕位置变化 | `GroundTruthAmbientOcclusionPass.cs:356`；`GTAO.compute:143` | P1-2 |
| F-04 | Medium | SSR | 开启 `SsrPbrParams` 调试图时，`DebugSsrParams` 覆盖正式 `ssrOutput`，最终图被替换为诊断参数 | `SsrPass.cs:278-285`；`ScreenSpaceLightingSetupPass.cs` 绑定同一纹理 | P3-2 |
| F-05 | Medium | 折射 | `_SCREEN_SPACE_REFRACTION` 无相机起点清理；被能力检测拒绝的相机可能继承上一相机的全局 keyword/纹理绑定 | `ScriptableRenderer.cs:1419-1445` 未清理折射；`RefractionPass.DisableMaterialBinding` 只清参数 | P4-1 |
| F-06 | Medium | Transparent SSR | 透明深度种子固定 `(1,1,0,0)`，在 Y 翻转目标（RT/XR）上与后续深度读取上下颠倒 | `TransparentDepthNormalRenderPass.cs:109` vs `CopyDepthPass.cs:181-182` | P3-3 |
| F-07 | Medium | SSGI | `publishProbeAtlas` 等开关切换后不重置历史；旧 history 与新的回退策略混用 | `SsgiPass.cs:702-718` 的 stamp 不包含该字段 | P2-2 |
| F-08 | Medium | SSR/SSGI | 多个共享 `ComputeShader` 的 keyword 依赖调用顺序：透明 pass 不显式设置 `SSR_HALF_RES` 与 `SSGI_BLUE_NOISE`（`SSR_NORMALS_OCT` 已显式关闭） | `TransparentSsrPass.cs:132-136` | P3-3 |
| F-09 | Low | SSGI | denoiser 的屏幕投影未处理 `UNITY_UV_STARTS_AT_TOP`，与 HDRP 不一致；需确认 Y 翻转目标是否偏移 | `DiffuseDenoiser.compute` 的 tap 投影 vs `HDRP/.../DiffuseDenoiser.compute:150-153` | P2-3 |
| F-10 | Low | GTAO | `intensity>0` 且 history 槽为空时提前 return，但 `_SCREEN_SPACE_OCCLUSION` 仍处于开启状态，可能消费上一帧纹理 | `GroundTruthAmbientOcclusionPass.cs:109,202-203` | P1-3 |
| F-11 | Low | GTAO/SSGI/SSR | 死代码、重复 ID、过时注释与文档（清单见 6.6） | 见 6.6 | P4-4 |

### 3.1 F-01 GTAO 全分辨率深度

HDRP 的语义（`HDRP/Runtime/Lighting/ScreenSpaceLighting/GTAOCommon.hlsl:54-104`）：

- `FULL_RES` + `HALF_RES_DEPTH_WHEN_FULL_RES=1`：中心深度读 mip0 的 `positionSS`；采样深度读 mip1 的 `positionSS/2`。
- `HALF_RES`：中心读 mip1 的 `positionSS`；采样按 `lowerRes` 读 mip2 或 mip1。

URP 的代码结构已经按这个语义写好，只是守卫名写成了 `GTAO_FULL_RES`，而 `GTAO.compute:4` 与 `GTAOTemporalDenoise.compute:4` 只定义 `FULL_RES`。因此全分辨率模式始终走 `#else`：中心深度读 `mip1Offset + positionSS`，在 full-res 坐标下越过 mip1 的半尺寸区域，读到 mip2 或空白区域；深度采样也退化。

修复：`GTAOCommon.hlsl` 两处 `#ifdef GTAO_FULL_RES` 改为 `#ifdef FULL_RES`。同时删除 `_GtaoParams0.x`（`fullResolution ? 0 : 1`）这个已经无人读取的字段。

### 3.2 F-02 SSR 降噪器像素展开角

`ReflectionDenoiser.compute` 复用了 SSGI 的常量集合：

```hlsl
#define _PixelSpreadAngleTangent _SsgiDenoiserParams.x
```

`SsgiPass` 会上传 `_SsgiDenoiserParams = (pixelSpreadTangent, -1, 0, 0)`（`SsgiPass.cs:679,729`），所以 SSGI 开启时 SSR 降噪器"碰巧"拿到了值；SSGI 关闭或先于 SSR 未执行时，降噪器读到 0。`SsrPass` 自己计算的 `ComputePixelSpreadTangent(fov, effectWidth, effectHeight)` 写到 `_PixelSpreadAngleTangent` 这个独立 float，被宏遮蔽，是死写入。

后果：`SsgiComputeMaxReprojectionWorldRadius` 退化为 `max(0.1, …)` 下限（`DenoisingUtils.hlsl:26`），任何重投影误差大于 0.1 世界单位的像素都拒绝 history。静止相机看不出问题；移动相机时 PBR SSR 的历史几乎每帧重置，时域降噪等于关闭。这与 `SsrGhostAnalysis` 里"停止拖动后第一帧就收敛、移动时差异大"的记录一致。

修复（最小改动）：在 `PushDenoiserShaderVariables` 或 `DispatchReflectionDenoise` 中改为上传向量：

```csharp
float tangent = ScreenSpaceLightingRenderUtils.ComputePixelSpreadTangent(fieldOfView, effectWidth, effectHeight);
cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiDenoiserParams,
    new Vector4(tangent, -1f, 0f, 0f));
```

删除独立的 `SetComputeFloatParam(PixelSpreadAngleTangent, …)`。同时检查 `SsgiPass.cs:353,557` 对 `TemporalFilter` 的同类写入是否也是死写入，一并清理。

### 3.3 F-03 GTAO 正交

- `GroundTruthAmbientOcclusionPass.cs:356` 无论相机是否正交都用 `ComputeZBufferParams(near, far)` 生成透视参数，`GTAOCommon.hlsl:110` 的 `LinearEyeDepth` 在正交窗口深度上不成立。
- `GTAO.compute:143` 的 `V = normalize(-positionVS)` 在正交下随屏幕坐标变化；正交相机的视线方向应恒为视图空间 `(0,0,1)`（指向相机）。

修复：为 `GTAO_ORTHO` 增加线性深度参数（例如 `_GtaoOrthoDepthParams = (1/(far-near), near, 0, 0)`，`linearDepth = rawDepth * scale + bias`），并在正交分支把 `V` 固定为 `(0,0,1)`。正交支持是 URP 新增能力，HDRP 无对照，验收以"世界半径不变"为准。

### 3.4 F-04 SSR 调试覆盖

`SsrPass` 在 `debugParams` 分支把 `DebugSsrParams` 写入 `m_History.ssrOutput` 后直接 `return`；该纹理正是 `ScreenSpaceLightingSetupPass` 交给材质的 `_ScreenSpaceSsrTexture`。因此只要开启 `SsrPbrParams` 调试视图且 SSR 生效，最终图就会被诊断值替换。

修复：为调试参数分配独立 RT（例如 `m_History.ssrDebugParams`），或在 `ScreenSpaceLightingDebugPass` 内执行该 kernel。验收：开启任意调试视图时最终图与关闭调试逐字节一致。

### 3.5 F-05 折射 keyword 泄漏

`ScriptableRenderer.ClearRenderingState` 每相机清理 GI/SSR/TransparentSSR，但没有 `_SCREEN_SPACE_REFRACTION`。被 `ScreenSpaceCapability` 拒绝的相机（Preview/Reflection/Overlay/零尺寸/XR 布局不符）不会入队任何 SSL pass，于是上一相机的折射 keyword 与纹理绑定会残留。`RefractionPass.DisableMaterialBinding` 只把参数清零，shader 会提前返回，但全局纹理仍可能指向已释放句柄。

修复：在 `ClearRenderingState` 关闭 `_SCREEN_SPACE_REFRACTION`，并把 `_SslRefractionParams` 与关键纹理绑定重置为安全值（黑纹理或空）。验收：A 相机开启折射 → B 相机为 Preview/Reflection → B 期间 keyword 全关且无 stale 绑定。

### 3.6 F-06 透明深度 Y 翻转

`TransparentDepthNormalRenderPass` 用 `Blitter.BlitTexture(cmd, depthSource, new Vector4(1,1,0,0), material, 0)` 复制不透明深度。URP 的 `CopyDepthPass` 会按源/目标句柄的 Y 方向取反（`cameraData.IsHandleYFlipped(source) != cameraData.IsHandleYFlipped(destination)`）。在 Y 翻转目标（部分 RT/XR）上，种子深度与后续 `_SsrDepthTexture` 的读取方向相反，透明 SSR 的命中位置会上下颠倒。

修复：复用 `CopyDepthPass` 的判定：

```csharp
bool yflip = cameraData.IsHandleYFlipped(depthSource) != cameraData.IsHandleYFlipped(m_History.transparentDepth);
Blitter.BlitTexture(cmd, depthSource,
    new Vector4(1f, yflip ? -1f : 1f, 0f, yflip ? 1f : 0f), material, 0);
```

验收：在 Y 翻转的 RT 相机与普通相机上，透明 opt-in 面板的深度/法线可视化方向一致。

### 3.7 F-07 SSGI 历史 stamp

`ComputeSettingsStamp` 覆盖 `fullResolution`、`rayMiss`、`temporalAccumulation`、`denoise`、`secondDenoiserPass`、`halfResolutionDenoiser`、trace 尺寸与资源代数，但不包含 `publishProbeAtlas`、`maxRaySteps`、`depthBufferThickness`、`denoiserRadius`、`bilateralUpsample`、`intensity`。切换这些值不会重置 history，旧 radiance 会与新配置混合。

修复：把至少 `publishProbeAtlas` 与 `rayMiss`（已有）纳入；其余按"影响 history 语义"判断，逐项加入或显式注释为何不需要。验收：切换 `publishProbeAtlas` 后下一帧 `historyValidity=0`，无跨配置残影。

### 3.8 F-08 共享 ComputeShader keyword

`TransparentSsrPass` 只设置 `SSL_XR_ARRAY`、`SSR_APPROX`、`SSR_NORMALS_OCT`（`TransparentSsrPass.cs:132-136`），没有显式设置 `SSR_HALF_RES`、`SSGI_BLUE_NOISE`。当前透明 pass 的 trace 尺寸等于屏幕尺寸，`SSR_HALF_RES` 残留恰好无害；蓝色噪声也没有被调用。这属于"靠调用顺序正确"，不是可验证的契约。

修复：在透明 pass 入口显式设置它需要的全部 keyword（`SSR_HALF_RES=false`、`SSGI_BLUE_NOISE` 按资源存在与否、`SSR_NORMALS_OCT=false`），不依赖不透明 pass 的副作用。

### 3.9 F-09 SSGI 降噪器投影（待确认）

HDRP 的 `DiffuseDenoiser.compute:150-153` 在 tap 投影时处理 `UNITY_UV_STARTS_AT_TOP`；URP 的移植没有。D3D11 的 UV 原点与 HDRP 目标平台约定可能不同，需要用一个屏幕上下不对称的梯度场景确认。若确认偏移，补上翻转；若确认等价，在已知差异中写明。

### 3.10 F-10 GTAO 提前返回

`GroundTruthAmbientOcclusionPass.cs:202-203` 在 history 槽为空时 `return`，但 `_SCREEN_SPACE_OCCLUSION` 已在 `:109` 启用。若此时材质仍采样 `_ScreenSpaceOcclusionTexture`，会读到上一帧或未初始化的内容。修复：在提前返回前关闭 keyword，或保证首帧一定写入中性 AO。

---

## 4. 与 HDRP 的差异复盘（排除 HWRT）

### 4.1 排除项（不实现）

| HDRP 能力 | 排除原因 |
|---|---|
| RTR / RTAO / RTGI / RTSSS | 依赖硬件光追与加速结构；URP 14 无此路径 |
| 光线追踪反射降噪的 RT 专用分支 | 需要 RT 信号语义（世界位置/法线 G-buffer），屏幕空间路径不复用 |
| 逐像素 env-light hierarchy / FPTL / `EnvLightData` | URP 无 FPTL 数据与光照列表；用反射探针 atlas + 盒权重替代 |
| 粗粒度 stencil 拒绝、透明物 motion、曝光控制 | URP 14 无对应 stencil bit / motion / 预曝光阶段的等价数据 |
| APV / Probe Volume | URP 14.0.12 未集成 |
| 动态分辨率 | URP 14.0.12 不驱动 `DynamicResolutionHandler` |

### 4.2 GTAO

**已对齐**

- 主 kernel 的半径/FOV 修正、6 方向 × 步数、temporal 只取 1 方向、旋转/偏移查表索引、min radius 与 tolerance、temporal 累积公式、空间双边、半分辨率 smart blur+upsample 的顺序与条件，均与 `HDRP/Runtime/Lighting/ScreenSpaceLighting/HDRenderPipeline.AmbientOcclusion.cs:173-178` 一致。
- AO 符号：URP 输出 visibility `saturate(pow(vis,intensity))`，材质用 `_AmbientOcclusionParam=(1,0,0,direct)`；HDRP 输出 `1 - pow(AO,intensity)` 并用 `1 - tex`，两者等价。
- 降噪邻居读取从 `GatherRed`+UV 改为整数 `Load`，这是条带修复的必要适配（`GTAOSpatialDenoise.compute`、`GTAOBlurAndUpsample.compute`）。

**有意差异**

- GTAO **没有 Volume 组件**，参数序列化在 Renderer Feature 上；因此没有 Volume 混合、逐相机覆盖与运行时插值。这是单 AO 生产者的设计选择，本轮记录、不改架构。
- 正交支持是 URP 新增；HDRP 无正交 GTAO。
- URP 使用对象 + 相机运动矢量；HDRP 只用相机运动。
- 深度金字塔与 SSL 共享；HDRP 在 prepass 内构建。T14 的"重复 Hi-Z 显存"条目已过时，必须修正。
- 材质侧只有间接/直接环境 AO，没有 HDRP 的 horizon-based specular occlusion 与多弹射变体；这是 URP 材质系统限制。

**本轮缺陷**：F-01（full-res）、F-03（ortho）、F-10（提前返回）。

### 4.3 SSGI

**已对齐**

- Trace：1 spp 余弦采样、BND 8SPP LUT、法线偏置、命中 UV snap，与 HDRP `ScreenSpaceGlobalIllumination.compute:78-139` 同构。
- Reproject：命中像素运动、UV limit、`DEPTH_DIFFERENCE_THRESHOLD 0.1`、颜色金字塔 `SSGI_MIP_OFFSET 1`、HSV clamp 7.0。
- Temporal：`sampleCount>=8 ? 0.93 : n/(n+1)`、`MAX_NORMAL_DIFFERENCE 0.65`、位置半径；sample count 存在独立 R16 纹理而不是 `history.w`。
- Diffuse denoiser：16/4 taps、点分布、4 帧抖动周期与 HDRP 一致。
- 探针 fallback 的 atlas 采样 LOD 0、权重饱和、天空 SH 余量，与 04 轮的契约一致。

**有意差异**

- 材质混合是 `lerp(bakedGI, radiance*intensity, weight)`；HDRP 直接用 SSGI 替换 `bakeDiffuseLighting`。URP 保留权重语义，便于 miss 时回退 baked GI。
- 探针权重是线性盒权重 + box projection；HDRP 是 influence shape + proxy volume，且上限 128/32 vs URP 64/32，无 range compensation、无 half16 clamp、无 per-probe multiplier。
- 曝光控制关闭（SSL 在预曝光阶段）；HDRP SSGI 在曝光控制下运行。
- Bilateral upsample 是 2×2 深度加权；HDRP 是 6×6 LDS gather。
- 光线步进最大 mip 6；HDRP 允许完整金字塔（URP 的单一最近深度层限制）。
- Forward/Deferred 的探针 atlas 默认关闭；开启后天空余量从默认 cubemap 变为 ambient SH（04 轮记录）。
- `reflectSky` 在 SSGI 调用点恒为 1，`reflectsSky=false` 分支不可达。

**本轮缺陷**：F-07（stamp）、F-09（Y 翻转待确认）。

### 4.4 SSR / Transparent SSR

**已对齐**

- PBR 路径使用 HDRP 的 `ReflectionDenoiser`（TemporalAccumulationFullRes/HalfRes、CopyHistory、BilateralFilter H/V），而不是旧的 18 个累积 kernel。
- 重投影校验（远深度、屏外、世界半径）+ 屏幕空间法线拒绝 `dot(N, historyN) < 0.75`。
- Kernel mapping 纹理按 `(NoV, roughness)` 采样；半分辨率降噪半径减半。
- 透明 SSR 从透明深度/法线 prepass 出发，对不透明深度金字塔步进；`reflectSky=0`。
- Approximation 的 3×3 空洞填充是 URP 新增，HDRP miss 直接 weight 0。

**有意差异**

- URP 用 PBR 降噪器替代 HDRP 的 screen-space SSR 累积族与速度拒绝；旧 Volume 字段已删除。
- 无 stencil 位、无对象运动拒绝、无曝光控制。
- 半分辨率只作用于 PBR；HDRP 的屏幕空间 SSR 是全分辨率。
- 辐射写为非预乘 `(radiance, weight)`；HDRP 写预乘 `opacity*outputs/wAll`。
- 材质用 `lerp(probe, radiance, weight)` 后统一乘一次 `EnvironmentBRDF`；HDRP 先乘 `specularFGD/coatIblF` 并保留 hierarchy weight。
- `intensity` 乘在 weight 上（0–1 有效），>1 不增亮；HDRP 无 SSR intensity。
- ComplexLit 的 coat 只写基础 lobe 的 smoothness；coat 反射用基础粗糙度追踪，材质阶段再按 coat 权重混合。

**本轮缺陷**：F-02（像素展开角）、F-04（调试覆盖）、F-06（Y 翻转）、F-08（keyword 泄漏）。

### 4.5 差异分类汇总

| 分类 | GTAO | SSGI | SSR |
|---|---|---|---|
| A. HWRT/引擎能力（排除） | RT AO | RTGI、FPTL、APV | RTR、stencil、曝光 |
| B. URP 架构限制 | 无 specular AO、无 Volume 组件 | 探针盒权重、64/32、预曝光 | 无对象 motion/stencil、半分辨率策略 |
| C. 有意适配 | 正交、对象 motion、共享 Hi-Z、整数 Load | 权重混合、2×2 upsample、mip 6、atlas 默认关 | PBR 降噪器、空洞填充、非预乘、透明 opt-in |
| D. 本轮缺陷 | F-01/F-03/F-10 | F-07/F-09 | F-02/F-04/F-06/F-08 |

---

## 5. 任务拆分

依赖顺序：`P0 → P1/P2/P3（可并行）→ P4 → P5 → P6`。每个任务独立提交；修复与证据分开提交，便于 public review。

| ID | 级别 | 任务 | 依赖 | 主要文件 | 状态 |
|---|---|---|---|---|---|
| P0-1 | P0 | 通用 validation player（三渲染器 × manifest × 位姿） | 无 | `Assets/Tests/ScreenSpaceLighting/Editor|Runtime/`、`Renderers/` | **完成（冒烟）** |
| P0-2 | P0 | RDC 分析管线（census / binding / pixel / RT 导出） | P0-1 | `MCP/tools/`、`Assets/Tests/.../Editor/` | 冒烟通过（CLI，未脚本化） |
| P0-3 | P0 | Binding probe（uniform 原样输出 + CPU 回读） | P0-1 | `Assets/Tests/ScreenSpaceLighting/Shaders/` | **完成（冒烟）** |
| P0-4 | P0 | 证据 schema、`.rdc` 体积策略、基线哈希 | 无 | `validation/06-ValidationAllRDC/` | **完成（schema 落地）** |
| P1-1 | P1 | 修复 GTAO 全分辨率深度（F-01） | P0 | `GTAOCommon.hlsl` | **完成（Fixed，证据 [P1-1/Report.md](validation/P1-1/Report.md)）** |
| P1-2 | P1 | 修复 GTAO 正交深度/视线（F-03） | P0 | `GroundTruthAmbientOcclusionPass.cs`、`GTAO.compute`、`GTAOCommon.hlsl` | 待开始 |
| P1-3 | P1 | GTAO 生效与正确性 GPU 验收（含 F-10） | P0/P1-1/P1-2 | `validation/.../P1-GTAO/` | 待开始 |
| P1-4 | P1 | GTAO 材质覆盖与 Deferred 双路径 | P1-3 | 测试场景/材质 | 待开始 |
| P2-1 | P2 | SSGI 生效链 GPU 验收（dispatch/history/LUT/atlas） | P0 | `validation/.../P2-SSGI/` | **完成（10/10，证据 [P2-SSGI/Report.md](validation/P2-SSGI/Report.md)）** |
| P2-2 | P2 | 修复 SSGI 历史 stamp（F-07） | P2-1 | `SsgiPass.cs`、`ScreenSpaceLightingSettings.cs` | 待开始 |
| P2-3 | P2 | 确认/修复 denoiser Y 翻转（F-09） | P2-1 | `DiffuseDenoiser.compute` | 待开始 |
| P2-4 | P2 | SSGI 覆盖/多探针/时域正确性场景 | P2-1/P2-2 | 测试场景/材质 | 待开始 |
| P2-5 | P2 | 清理 SSGI 死代码 | P4-4 统一 | `SsgiPass.cs`、`RayMarching.hlsl` | 待开始 |
| P3-1 | P3 | 修复 SSR 像素展开角绑定（F-02） | P0 | `SsrPass.cs`、`ReflectionDenoiser.compute` | 待开始 |
| P3-2 | P3 | 修复 SSR 调试覆盖（F-04） | P0 | `SsrPass.cs`、`ScreenSpaceLightingDebugPass.cs` | 待开始 |
| P3-3 | P3 | 修复透明 Y 翻转与共享 keyword（F-06/F-08） | P0 | `TransparentDepthNormalRenderPass.cs`、`TransparentSsrPass.cs` | 待开始 |
| P3-4 | P3 | SSR 生效链 GPU 验收（两种算法/全半分辨率/透明） | P0/P3-1..3 | `validation/.../P3-SSR/` | 待开始 |
| P3-5 | P3 | SSR 正确性场景（反射/梯度/空洞/透明/probe 回退） | P3-3 | 测试场景/材质 | 待开始 |
| P4-1 | P4 | 修复折射 keyword/绑定泄漏（F-05） | P0 | `ScriptableRenderer.cs`、`RefractionPass.cs` | 待开始 |
| P4-2 | P4 | keyword 泄漏与多相机矩阵 | P4-1 | 测试 | 待开始 |
| P4-3 | P4 | 分辨率/MSAA/渲染器矩阵 | P0 | 测试 | 待开始 |
| P4-4 | P4 | 删除死代码/重复声明/过时注释（F-11 + 6.6） | P1–P3 | 全量 | 待开始 |
| P5-1 | P5 | 新建 `06/KnownDifferences.md`（四分类） | P1–P4 | 文档 | 待开始 |
| P5-2 | P5 | 更新支持矩阵与 T14 过时条目 | P5-1 | 文档 | 待开始 |
| P5-3 | P5 | 更新主 HANDOFF 的当前位置 | P5-2 | 文档 | 待开始 |
| P6-1 | P6 | 汇总 `Report.json` / `Report.md`（exitCriteriaMet） | 全部 | `validation/` | 待开始 |
| P6-2 | P6 | 清理 player/设置/临时抓帧，恢复 ProjectSettings | 全部 | 工作区 | 待开始 |
| P6-3 | P6 | 归档到 `PLAN/Done/06-ValidationAllRDC/` | P6-1/P6-2 | 目录 | 待开始 |

---

## 6. 实现细节

### 6.1 通用 validation player

复用 `ScreenSpaceLightingP13PlayerSetup` / `ScreenSpaceLightingP13PlayerVerification` 的成熟模式，抽成通用入口：

- `ScreenSpaceLightingValidationPlayerSetup`
  - `CreateScenes`：生成/复用三类场景资产（基础几何 + 镜面地面 + 发光盒 + 可区分探针 + 透明 opt-in 面板 + 全屏梯度）。
  - `BuildPlayer`：D3D11、Development、1280×720、windowed；所有材质/Shader/ComputeShader 必须被场景引用（player 不能 `Shader.Find` 补齐）。
  - 三种渲染器：优先用一个 player + 三个 Quality Level（各自指向 `Ssl{Forward,ForwardPlus,Deferred}Pipeline.asset`）运行时切换；若切换后首帧状态不可靠，退化为三次构建。
- `ScreenSpaceLightingValidationPlayerVerification`
  - 解析 `-ssl-validation-manifest <json>` 与 `-ssl-validation-out <dir>`。
  - 每个 case：设置渲染器/Volume/Feature 参数 → 设置相机位姿 → 预热 N 帧（默认 10）→ `TriggerCapture` → 等待 `GetNumCaptures` 增加 → 同配置截 PNG → 记录指标。
  - 每个 case 写一条 `Report.json`：`id`、`renderer`、`effect`、`params`、`camera`、`rdc`、`png`、`metrics`、`passed`。
  - 结束时 `Application.Quit(passed ? 0 : 1)`。

manifest 示例：

```json
{
  "renderer": "ForwardPlus",
  "scene": "ValidationCore",
  "warmupFrames": 10,
  "cases": [
    {
      "id": "gtao_half_temporal_radius1",
      "effect": "GTAO",
      "settings": { "fullResolution": false, "temporal": true, "radius": 1.0, "intensity": 1.0 },
      "camera": { "position": [0, 2, -6], "rotation": [10, 0, 0] }
    },
    {
      "id": "ssr_pbr_full_pan",
      "effect": "SSR",
      "settings": { "usedAlgorithm": "PBRAccumulation", "fullResolution": true },
      "camera": { "position": [0, 2, -6], "rotation": [10, 0, 0], "panPixelsPerFrame": 0.5 },
      "captureAfterFrames": 120
    }
  ]
}
```

### 6.2 RDC 分析管线

- 用 `renderdoccmd capture -w --opt-hook-children` 启动 player；player 内 in-app API 精确抓帧。
- 用 `renderdoc-cli` / `MCP/tools/rdmcp.py`：
  - `info` / `events`：事件总数、API；
  - `list_draws` / `list_passes`：定位 opaque / transparent / fullscreen 消费者；
  - `get_bindings` / `get_pipeline_state`：核对 SRV/UAV、RT、viewport；
  - `debug_pixel`：对目标材质像素取 PS 输出，证明走了哪个分支；
  - `export_rt` / `get_texture_stats`：导出 SSGI final、SSR output、GTAO occlusion 并统计 NaN/Inf、alpha 分布；
  - `shader ps`：导出 PS 反汇编，比较效果开/关的 shader 哈希与指令数。
- 输出 `Census.json`：每个 dispatch 的序号、维度、绑定的主要纹理/缓冲；`Pixels.json`：目标像素的 PS 输出。

**已知限制**：Unity player 剥离 DXBC 反射名；`renderdoc-mcp v0.3.0` 没有 cbuffer 工具。因此 cbuffer 数值以 binding probe 为准；RDC 负责"是否调度、是否绑定、像素是否变化"。可选 P0-5：从上游源码构建带 cbuffer 工具的 MCP，补齐直接数值 dump。

### 6.3 Binding probe

新增一个仅测试用的 fullscreen shader（`Assets/Tests/ScreenSpaceLighting/Shaders/ScreenSpaceLightingBindingProbe.shader`）：

- 声明与效果完全相同的 uniform/cbuffer 名称与类型（`_SsgiScreenSize`、`_SsgiTraceSize`、`_SsgiRayMarchingParams*`、`_SsgiDenoiserParams`、`_Ssr*`、`_Gtao*`、`_SslRefractionProbe*` 等）。
- 通过 `_SslBindingProbeIndex` 选择要输出的字段，写入一张 4×1 的 `RGBAFloat` RT。
- C# 侧 `ReadPixels` 回读，与同一帧的 C# 期望值逐字段比较（浮点按 1e-6 相对误差；int/flag 精确）。
- 覆盖每类绑定：尺寸、frame index、history validity、pixel spread、mip offsets、probe count、probe table 首项、ZBuffer 参数、XR view 数。
- 该 shader 只在测试资产中，不进入包/发布构建；player 场景显式引用它。

这一步直接回答"script 是否正确绑定到 shader/cbuffer"：如果 C# 值、HLSL 声明名、类型、cbuffer 布局任一处错误，回读值就对不上。

### 6.4 修复草图

**F-01**

```hlsl
// GTAOCommon.hlsl
#ifdef FULL_RES          // was GTAO_FULL_RES
    return GTAOLoadDepth((int2)positionSS);
#else
    return GTAOLoadDepth((int2)(_GtaoFirstDepthMipOffset + positionSS));
#endif
```

同一文件第二处 `GTAOGetDepthSample` 同样改为 `FULL_RES`。删除 `_GtaoParams0.x` 的写入与结构字段。

**F-02**

```csharp
// SsrPass.DispatchReflectionDenoise
float tangent = ScreenSpaceLightingRenderUtils.ComputePixelSpreadTangent(fieldOfView, effectWidth, effectHeight);
cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiDenoiserParams,
    new Vector4(tangent, -1f, 0f, 0f));
// delete the standalone SetComputeFloatParam(PixelSpreadAngleTangent, ...)
```

**F-03**

```hlsl
// GTAOCommon.hlsl
#ifdef GTAO_ORTHO
    float linearDepth = rawDepth * _GtaoOrthoDepthParams.x + _GtaoOrthoDepthParams.y;
#else
    float linearDepth = LinearEyeDepth(rawDepth, _GtaoZBufferParams);
#endif
```

`GTAO.compute` 的 `V` 在正交分支固定为 `float3(0, 0, 1)`；C# 上传 `_GtaoOrthoDepthParams = (1/(far-near), near, 0, 0)`。

**F-04**：把 `DebugSsrParams` 的目标改为独立 RT（`m_History.ssrDebugParams`），`ScreenSpaceLightingDebugPass` 读取它；`SetupPass` 永远拿正式 `ssrOutput`。

**F-05**：`ScriptableRenderer.ClearRenderingState` 增加 `cmd.DisableShaderKeyword(ShaderKeywordStrings.ScreenSpaceRefraction);`；在 `RefractionPass.DisableMaterialBinding` 中把纹理绑定重置为黑纹理并清零 `_SslRefractionParams`。

**F-06**

```csharp
bool yflip = IsHandleYFlipped(cmd, depthSource);
Blitter.BlitTexture(cmd, depthSource,
    new Vector4(1f, yflip ? -1f : 1f, 0f, yflip ? 1f : 0f), material, 0);
```

**F-07**：`ComputeSettingsStamp` 增加 `publishProbeAtlas`；其余字段逐项评估并加注释。

**F-08**：`TransparentSsrPass` 入口显式设置 `SSR_HALF_RES=false`、`SSR_NORMALS_OCT=false`、`SSGI_BLUE_NOISE=资源存在`，不继承不透明 pass。

### 6.5 验证场景设计

一个核心场景覆盖三种效果，减少场景维护：

- 地面：镜面/半粗糙两套材质，用于 SSR 与 SSGI 反弹。
- 立方体/球体阵列：smoothness 0.95/0.80/0.65/0.40，用于 SSR 梯度与 GTAO 接触阴影。
- 发光盒：SSGI 光源，放在地面中心，便于测 G 通道增益。
- 4 个颜色可区分的反射探针：SSGI 多探针混合（补 04 的空白）。
- 透明 opt-in 面板：Transparent SSR。
- 玻璃盒：折射回归（共享清理）。
- 全屏渐变/棋盘：检测 Y 翻转与 denoiser 偏移。
- 天空盒：SSR/SSGI miss 回退。
- 第二相机（Preview/Reflection）：keyword 泄漏测试。

### 6.6 死代码 / 重复代码清理清单

按"看到直接删"的原则，逐条确认无引用后删除：

- `ScreenSpaceLightingHistory`：`ssrOutputProduced` / `MarkSsrOutputProduced`、`ssrHasValidHistory`、`validFrameCount`、`resourceGeneration`（以及仅测试读取的 `historyColorPyramidCount` / `previousDepthCount`，同步改测试）。
- `ScreenSpaceLightingTransparentSsrPass.usingPbrAccumulation`。
- `ScreenSpaceLightingShaderIDs.SslProbeListKeyword`（未引用）。
- `ScreenSpaceLightingShaderIDs` 中无 SSR 读取的 SSGI 遗留 ID（`AccumulationOutputTextureRW`、`ValidationBuffer(RW)`、`PointDistribution(RW)`、`HalfResolutionFilter`、`JitterFramePeriod`、`Receiver/OccluderMotionRejection`、`EnableExposureControl`、`*ExposureMultiplier`、`DenoiseSampleCountRW`）。
- `_SsrPyramidParams.y`（depth mip count 无人读）、`_SsgiTemporalParams.zw`、`_SsgiDenoiserParams.yzw`、`_SsgiRayMarchingParams2.w`、`_SslRefractionParams.w`、`_SsrPbrParams.x/z/w`、`_SsrPbrFlags.zw`。`_SslRefractionProbeParams.y` 在 05 轮是留给 light layer 的保留槽；既然 URP 14 不暴露 probe rendering layer，本轮一并删除并在已知差异中保留说明。
- `_PixelSpreadAngleTangent` 独立 float 写入（修复 F-02 后）。
- `GroundTruthAmbientOcclusionShaderIDs.DebugMode/DebugMip`、`GroundTruthAmbientOcclusionHistory.depthPyramidMipCount`。
- `_GtaoScreenSize.zw`、`_GtaoParams0.x`、`_GtaoOrthoHalfSize.zw`。
- `ScreenSpaceLightingDebugMode` 值 13 的空洞。
- 重复声明：`_SslDepthMipChain` / `_SslSrcOffsetAndLimit` / `_SslDstOffset`、`_CameraDepthTexture` 的多处 PropertyToID、`SSL_XR_ARRAY` 字符串的 5 处重复。
- `ScreenSpaceLightingRenderUtils.ComputeZBufferParams` 与全局 `_ZBufferParams` 重复（在正交修复后确认是否仍需私有副本）。
- `ScreenSpaceDepthPyramid.SwapBuffers()` 空 stub。
- `UniversalRenderer.m_PublishSslProbeAtlas` 与 `ForwardLights.m_PublishProbeAtlas` 的重复状态（保留一条）。
- GTAO 的"私有深度金字塔，从不与 SSL 别名"过时注释与 Inspector 文案；T14 已知差异中的"重复 Hi-Z 显存"。
- SSGI `reflectsSky=false` 不可达分支、`SSGI_TEXTURE_TYPE_FLOAT4/_UINT` 未用宏。

每项删除后记录：变体数、shader 体积、EditMode/PlayMode、T12 回放结果。

---

## 7. 验收标准

### 7.1 三层证据

每个效果必须同时具备：

1. **CPU 层**：pass 计数器、`Shader.IsKeywordEnabled`、Volume/Feature 参数、history 代数、GC 分配。
2. **GPU 层**：RDC 的 dispatch 数量/维度、绑定的 SRV/UAV/RT、shader 哈希、binding probe 回读值。
3. **图像层**：目标像素的 `debug_pixel` 输出、RT 统计（NaN/Inf、alpha 分布、覆盖率）、开/关图像差异。

只有 CPU 计数器或只有"看起来对"的截图都不算通过。

### 7.2 单项验收

**GTAO**

| 项 | 标准 |
|---|---|
| G1 调度 | 每帧 main=1；temporal 时 spatial=1、temporal=1、copy=首帧/reset；half-res 非 temporal 时 blur+upsample=1；维度 `ceil(ao/8)`，z=viewCount |
| G2 keyword | `_SCREEN_SPACE_OCCLUSION` 在不透明绘制期间开启、Cleanup 后关闭；`intensity=0` 全程关闭 |
| G3 输出 | 接触角 visibility ≤0.7、开阔面 ≥0.98；intensity 0/1/4 单调；radius 0.25/1/3 影响范围单调；无 NaN/负值 |
| G4 full-res（F-01） | 中心深度 == 摄像机同像素深度，相对误差 ≤1e-5；full-res 与 half-res 的结构相关系数 ≥0.95；无越界读数 |
| G5 正交（F-03） | 2 m 与 20 m 深度下同一世界半径的 AO 屏占比差 ≤5%；AO 不随屏幕位置改变方向；`V=(0,0,1)` |
| G6 覆盖 | ComplexLit/SimpleLit/Unlit/Terrain/透明覆盖符合 2.4；Deferred 有/无方向光两条 AO 路径都只暗化一次；关闭基线 0 像素差 |
| G7 提前返回（F-10） | 首帧/空 history 时不消费未初始化 AO；keyword 与纹理状态一致 |

**SSGI**

| 项 | 标准 |
|---|---|
| S1 调度 | trace → reproject → validate → temporal → copy → denoiser → gather/upsample 的数量/尺寸与配置一致；history 双缓冲交替；blue-noise 纹理已绑定 |
| S2 生效 | `_SCREEN_SPACE_GI` 仅在不透明阶段开启；`coveredFraction ≥0.15`（T6 仅 0.025）；发光盒在地面的 G 增益 ≥1.5×；未覆盖像素与 baked GI 差 ≤1e-6 |
| S3 时域 | 静止 30 帧收敛；相机 cut/reset 后首帧 `historyValidity=0`；30 帧后残影 ≤1/255 |
| S4 探针 | toggle on 时三渲染器命中局部探针（G/R ≥1.5）；off 时 Forward/Deferred 用默认 cubemap、Forward+ 仍用 atlas；4 探针四色权重随距离单调；0 探针无 NaN |
| S5 半分辨率 | half/full 边缘无 1 px 硬缝；upsample 与 full 相关 ≥0.98；无 NaN |
| S6 材质 | SimpleLit/Particles/Terrain/Unlit 权重 0；Lit/ComplexLit/SG Lit 生效 |
| S7 stamp（F-07） | 切换 `publishProbeAtlas` 后下一帧 validity=0，无跨配置残影 |

**SSR / Transparent SSR**

| 项 | 标准 |
|---|---|
| R1 调度 | Approximation 2 dispatch；PBR 全分辨率 6；PBR 半分辨率 7；透明 +2；尺寸/组数正确 |
| R2 像素展开角（F-02） | binding probe 读出 `_PixelSpreadAngleTangent == 2·tan(fov/2)/min(traceW,traceH)`（60°/720p→0.0016046，half→0.0032092，误差 ≤1e-6）；SSGI 关闭的 SSR-only 相机也非 0；慢速平移（0.5 px/帧）history 接受率 ≥50%（修复前 <5%） |
| R3 反射 | 镜面地面反射物体中心误差 ≤2 px；smoothness 0.95/0.80/0.65/0.40 的 weight 单调；空洞填充覆盖 ≥95% 单像素洞且不跨轮廓 |
| R4 透明 | 仅 opt-in 生效；Y 翻转目标与普通目标深度/法线方向一致（F-06）；`reflectSky=0`；透明 keyword 只在 449–450 生效 |
| R5 关闭基线 | `intensity=0` / 关闭 / 非 opt-in 0 像素差；任意调试视图不影响最终图（F-04） |
| R6 history | slot 交替；sample count 在粗糙像素饱和到 8；相机 cut/算法切换/半分辨率切换重置 |
| R7 keyword（F-08） | 透明 pass 不依赖不透明 pass 的副作用；单独运行透明路径时 keyword 正确 |

**交叉**

| 项 | 标准 |
|---|---|
| X1 泄漏（F-05） | A 相机开启全部效果 → B 相机为 Preview/Reflection/Overlay/缺资源 → B 期间 5 个贡献 keyword 全关，无 stale RTHandle 绑定 |
| X2 分辨率 | render scale 0.5/0.75/1/2 + MSAA on/off + 三渲染器：dispatch 维度正确、无 NaN、无越界；硬件动态分辨率仍标记不支持 |
| X3 清理 | 删除死代码后 0 error / 0 新 warning；EditMode/PlayMode 全绿；T12 回放 34/34/35；效果关闭基线逐字节一致 |
| X4 构建 | Release Player 成功；17 个必需 Shader/ComputeShader 在包内；无 missing feature |

### 7.3 本轮退出标准

| 类别 | 标准 |
|---|---|
| 缺陷 | F-01..F-11 每项状态为 Fixed 或 Accepted+已记录；High 不允许 Accepted |
| 编译 | 0 error；新增 warning 为 0 或有明确理由 |
| 回归 | EditMode 全量、PlayMode 全量通过；T12 组合矩阵 34/34/35；关闭效果逐字节基线 |
| GPU 证据 | 三渲染器 × 三效果，每项至少有 1 个 `.rdc` + dispatch census + binding probe + 目标像素/RT 统计；Transparent SSR 至少 1 个透明抓帧 |
| 数值 | 7.2 的所有阈值达标；未达标项必须在报告中给出原因与后续任务 |
| 性能 | 记录 1280×720 GPU 时间增量（player 内成对测量）；相对本轮基线回归 ≤10%（DVFS 无效时标注参考） |
| 显存 | 修复后资源计数回到基线；分辨率/相机循环 10 次无泄漏 |
| 文档 | `06/KnownDifferences.md`、支持矩阵、主 HANDOFF、`Report.md` 四者一致；T14 过时条目已修正 |
| 归档 | 目录移动到 `PLAN/Done/06-ValidationAllRDC/`；`git status` 只含预期文件 |
| 提交 | 每个 commit 自包含、可 public review；不含 player 二进制、`_Data`、`Library/`、`Temp/`、`Logs/`、`.csproj`/`.sln` |

### 7.4 证据格式

```text
PLAN/TODO/06-ValidationAllRDC/validation/
  BaselineHashes.json
  Report.json
  Report.md
  P0/
    PlayerManifest.json
    Census-Forward.json
    Census-ForwardPlus.json
    Census-Deferred.json
    BindingProbe-*.json
  P1-GTAO/
    Player/*.rdc, *.png
    Census.json
    BindingProbe.json
    Metrics.csv
    Report.json
  P2-SSGI/...
  P3-SSR/...
  P4-Cross/{KeywordLeak.json,ResolutionMatrix.json}
  P5-Docs/
```

`Report.json` 每条用例至少包含：

```json
{
  "id": "gtao_fullres_center_depth",
  "effect": "GTAO",
  "renderer": "Forward",
  "passed": true,
  "rdc": "P1-GTAO/Player/gtao_fullres_frame162.rdc",
  "png": "P1-GTAO/gtao_fullres.png",
  "metrics": {
    "centerDepthError": 0.0,
    "fullHalfCorrelation": 0.972,
    "nanPixels": 0
  },
  "baselineHash": "sha256:..."
}
```

### 7.5 `.rdc` 体积与提交策略

- 每个效果每种渲染器至少保留 1 个代表抓帧；全部抓帧的 `Census.json`、`BindingProbe.json`、PNG、指标都提交。
- 提交的 `.rdc` 总量预算 **≤150 MB**，代表抓帧 **≤12 个**；超出部分只保留 SHA-256 与本地 `validation-local/` 路径，不入库。
- 不提交 player 可执行文件与 `_Data`/`UnityPlayer.dll`；`.rdc` 提交前确认仓库策略与 review 成本。

---

## 8. 收尾与清理

1. **先记录再删除**：保存 `git status --short`、`git diff --check` 到 `validation/P6/Cleanup/`。
2. **恢复 ProjectSettings**：批处理会重写 `Graphics/Quality/Project`，逐项恢复到开工前 SHA-256；记录校验结果。
3. **删除临时产物**：`Builds/Validation*`、`Temp/`、`Logs/`、`UserSettings/` 变更；保留 `validation/` 与测试资产。
4. **清理抓帧**：只保留预算内的 `.rdc`；`renderdoc-cli`/`qrenderdoc` 进程不占用文件后再移动。
5. **资源完整性**：所有新资产有 `.meta`，无孤儿 `.meta`；`Report.json` 中路径均存在。
6. **归档**：`PLAN/TODO/06-ValidationAllRDC` → `PLAN/Done/06-ValidationAllRDC`；检查旧路径无残留引用。
7. **最终检查**：`git status --short` 只含预期文件；无生成物；提交信息为英文、描述"为什么"而不是"改了什么"。

---

## 9. 风险与开放问题

1. **player 内多渲染器切换**：已在 P0 冒烟中验证 Forward → Forward+ → Deferred 运行时切换可用。新增约束：player 必须以 **Deferred** 资产构建，否则 URP 会剥离 deferred GBuffer/StencilDeferred 变体；Forward/Forward+ 是基础变体，切换不受影响。
2. **cbuffer 数值证据**：binding probe 能证明"值到达 shader"，但不能证明"实际效果 pass 的 cbuffer 未被另一个 SetCompute 覆盖"。RDC 的 shader 哈希 + 输出像素作为补充；若需要直接 dump，构建带 cbuffer 工具的 renderdoc-mcp。
3. **HDRP 对照不可运行**：HDRP 工程与本工程曝光/探针/GI 不同，跨管线像素对比不可作为通过/失败判据。差异复盘以源码对照为准。
4. **全分辨率 GTAO 修复后的视觉变化**：修复会改变 full-res 输出，属于设计变化；half-res 默认路径不变。需要在报告中保留 before/after。
5. **SSR 像素展开角修复后的视觉变化**：移动相机下 history 保留更多，可能出现轻微拖影；这是 HDRP 的预期行为，需与"残影 ≤1/255"阈值一起验收。
6. **正交 GTAO 是 URP 新增**：没有 HDRP 对照，验收依赖解析半径与稳定性，不承诺像素级一致。
7. **XR**：RDC 抓帧只保证 D3D11；Mock HMD + single-pass 的纹理数组绑定（`blackTexture` 2D 绑到 `TEXTURE2D_X`）需要单独 smoke，可能无法在 RDC 内完整验证。
8. **`.rdc` 体积**：大场景抓帧可能超过预算；优先保留小而可判读的场景，复杂场景只保留分析 JSON。
9. **破坏性清理**：删除 Volume 字段/旧调试视图会影响现有场景/Profile；本轮允许，但必须在迁移说明中列出并更新测试资产。

---

## 10. 执行顺序与提交计划

**阶段一：基础设施（P0）**

1. 通用 player + manifest（P0-1）。
2. RDC 分析 + binding probe（P0-2/P0-3）。
3. 基线哈希与证据 schema（P0-4）。

**阶段二：修复（可并行）**

4. GTAO F-01/F-03/F-10（P1-1/P1-2）。
5. SSR F-02/F-04/F-06/F-08（P3-1..P3-3）。
6. SSGI F-07/F-09（P2-2/P2-3）。
7. 折射 F-05（P4-1）。

**阶段三：GPU 验收（可并行）**

8. GTAO 场景与抓帧（P1-3/P1-4）。
9. SSGI 场景与抓帧（P2-1/P2-4）。
10. SSR 场景与抓帧（P3-4/P3-5）。

**阶段四：收尾**

11. keyword 泄漏/分辨率矩阵（P4-2/P4-3）。
12. 死代码清理（P4-4）。
13. 差异文档与支持矩阵（P5）。
14. 汇总、清理、归档（P6）。

**建议提交序列**（每个 commit 自包含，修复与证据分开）

| 提交 | 内容 |
|---|---|
| `06-01 Add the all-effects GPU validation plan` | 本文件 |
| `06-02 Fix GTAO full-resolution and orthographic depth` | F-01/F-03 + EditMode 断言 |
| `06-03 Bind the SSR denoiser pixel spread and isolate debug output` | F-02/F-04 |
| `06-04 Harden transparent SSR depth and keyword state` | F-06/F-08 |
| `06-05 Reset refraction state per camera` | F-05 |
| `06-06 Reset SSGI history on probe-atlas changes` | F-07 |
| `06-07 Add the validation player and binding probe` | P0 |
| `06-08 Add GTAO/SSGI/SSR RenderDoc evidence` | P1–P3 证据 |
| `06-09 Remove Screen Space Lighting dead code` | P4-4 |
| `06-10 Document the HDRP differences and archive the round` | P5/P6 |

> 提交前逐项确认：不含 player 二进制/`_Data`/`Library`/`Temp`/`Logs`/`.csproj`/`.sln`；`.rdc` 在预算内；`git diff --check` 无空白错误；文档中的文件/行号引用与当前代码一致。

---

## 11. 下一步

1. P0 已完成：三渲染器 player、manifest、抓帧、census 与 binding probe 均可复现；后续任务直接复用 `SslValidation*` 资产与 `validation/P0/SmokeReport.md` 的字段契约。
2. F-01（GTAO full-res）与 F-02（SSR 像素展开角）是两个最可能被用户直接看到的缺陷，优先修复并用小场景抓帧证明 before/after。修复后的 GTAO 用例复用 `forward_gtao_full`；F-02 需要用 P3-1 的 pass 级证据（probe 只能证明 C#→HLSL 契约）。
3. GTAO 正交与 SSR 透明 Y 翻转依赖特定相机/目标，放在基础场景稳定后单独验收。
4. 每完成一个修复就写 `validation/.../任务ID/` 证据，不攒到最后。
5. 最后一轮只做回归、文档、清理与归档。P6-2 需要处理本轮 Unity 2022.3.62f1 自动迁移的 `GraphicsSettings/QualitySettings/URP-Balanced/Performant`（与本轮改动无关，当前未提交）。