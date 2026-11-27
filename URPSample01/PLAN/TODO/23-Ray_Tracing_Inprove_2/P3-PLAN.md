# 23 P3 实施计划：SSR 后置 + SS/RT 共用命中着色（根治）

**状态：** B0–B4 完成（2026-11-27）。`SsrP3ContractTests` 12 过 / 0 红 / 1 跳过（仅 D3D12 payload 编译）。B2–B4 为 build/静态验证，像素/性能在 B6。B5（编排清理/回滚开关收尾）与 B6（统一复测）未开工。前置：P0 完成、P1 PARTIAL。
**上位计划：** [plan.md](plan.md) §P3；契约：[contracts.md](contracts.md)。
**验收方式：** 实现与文档按 B0–B5 逐步提交，**测量统一放到最后的 B6 一批**（含决策 #4/#5 的量化与六项阻断门禁）。

## 0. 现状事实（决定本计划形状的约束）

| 事件 | 值 | 当前用途 |
|---|---|---|
| `AfterRenderingPrePasses` | 200 | 深度/法线/运动 prepass 之后 |
| SSGI pass | 205 | 间接漫反射（不随本计划移动） |
| **SSR pass** | **206** | R1 分类 / SS trace / RT trace / 命中核 / temporal / 重建 |
| **材质绑定 pass** | **207** | 把 SSGI/SSR 纹理 + keyword 绑成全局 |
| `BeforeRenderingGbuffer` | 210 | Lit GBuffer pass（4 个 RT） |
| `AfterRenderingGbuffer` | 220 | 目标注入点；Decal 也在此事件 |
| `BeforeRenderingDeferredLights` | 230 | ClusteredDeferred 计算 |
| `BeforeRenderingOpaques` | 250 | Forward opaque（含 forward-only 物体） |
| `AfterRenderingSkybox` | 400 | 本帧颜色金字塔 |
| `BeforeRenderingTransparents` | 450 | 透明；透明 SSR 449 / 折射 448 / 透明深度法线 447 |
| `AfterRenderingTransparents + 1` | 501 | 颜色历史捕获 |

**关键约束（已核对代码）：**

1. 延迟接收者的间接镜面**写在 GBuffer pass 里**：`LitGBufferPass.hlsl` 的片元调用 `GlobalIllumination()`，其中 `ScreenSpaceLightingApplyIndirectSpecular`（SSR）与 `GlossyEnvironmentReflection` 的结果和 bakedGI/emission 一起写进 GBuffer4（`BRDFDataToGbuffer(..., surfaceData.emission + color, ...)`）。
2. `ClusteredDeferred.compute` 只 `LOAD_TEXTURE2D(_CdBakedColor)` 加直射光；`Lit.shader` 的 GBuffer pass 明确注释 **“the deferred kernel never samples SSL textures”**。
3. 因此“SSR 挪到 220”**不能只改事件号**：延迟接收者的间接镜面必须从 GBuffer pass 移到 GBuffer 之后的阶段；否则 GBuffer 会采到未写入/上一帧的 SSR。
4. 透明 SSR（449）与颜色金字塔（400）继续走前帧颜色采样，**不随本计划改变**。

## 1. 终态数据流

```
Prepass(200) → SSGI(205) → GBuffer(210，只写 albedo/specular/normal/baked+env+emission，不再写 SSR)
→ SSR(220)：
    R1 分类（prepass 深度/法线）
    SS trace（半分辨率）→ 命中 UV + 有效性（不再采前帧颜色）
    RT trace（FDT 预算队列，不变）
    命中着色核（trace 全屏 dispatch）：SS 命中读当前 GBuffer 构面；RT 命中读 SsrHitSurface；
                                    两者调同一个 SsrShadeSurface；写 radiance+权重
    temporal / PBR merge / denoiser / 重建（输入语义不变）
→ 间接镜面合成(225)：读 GBuffer1/2+depth 重建反射向量，env(probe/sky) 与 SSR 按权重混合，
                     写一张 specular RT（或直接由 230 采样）
→ Deferred lights(230)：直射 + 间接镜面 RT
→ Setup/绑定(221→ 实际需 >220 且 <230)：SSR/SSGI 纹理与 keyword
→ Forward opaque(250) / 透明(449+)：保持现有材质端混合
```

> 225 的位置是“>220 且 <230”的具体取值；若选择让 230 直接采样，则不新增 pass（见 D1）。

## 2. 设计决策

### D1 延迟接收者的间接镜面怎么接回（本计划最大改动）

三个候选：

| 方案 | 做法 | 风险 |
|---|---|---|
| **A（推荐）** | 新增 `SsrIndirectSpecularPass`（事件 225，计算）：读 GBuffer1/2 + depth，重建反射向量，采样 env（probe/sky）与 SSR，按 `lerp(env, ssr, w)` 写 specular RT；`ClusteredDeferred.compute` 读该 RT 相加 | 需要在 compute 里实现 `GlossyEnvironmentReflection` 的可行子集 |
| B | 让人 230 的核直接采样 SSL 纹理并调用 blend | 污染聚类核；env 问题同 A，回归面更大 |
| C | GBuffer 仍写 env（不写 SSR），225 只做“替换” | 仍要 env 才能算 `(ssr-env)*w`，不可行 |

**env 的三种来源（A 内部的子决策，B0 里定）：**

- A1：默认反射探针（`unity_SpecCube0` + `_GlossyEnvironmentCubeMap_HDR` 语义），无探针混合/盒投影——最省，视觉上是近似；
- A2：复用 feature 的探针图集（`publishProbeAtlas` 的产物），按像素盒投影 + 混合——质量好，工作量最大；
- A3：保留 P1 的 `_GlxHitAmbientIrradiance` 级近似（均一环境），仅作 fallback。

**推荐：A + A1 起步**，把 A2 记为 P3 已知质量差（与 A 决策“阴影/探针质量差计入容差”一致）。`rayTracingProbeFallback` 开启时优先 A2。

#### D1-B0 选型结论（2026-11-27，代码核验）

结论：**A + A1，且 A1 只采 `_GlossyEnvironmentCubeMap`，不碰 `unity_SpecCube0`。** 依据（`universal@14.0.12`）：

- `_GlossyEnvironmentCubeMap` / `_GlossyEnvironmentCubeMap_HDR` 在 `ShaderLibrary/Input.hlsl:96-98` **无条件声明**，并由 `UniversalRenderPipeline.SetupPerCameraShaderConstants`（`Runtime/UniversalRenderPipeline.cs:1686-1687`）在每个相机的 command buffer 上 `SetGlobalTexture/Vector`。compute dispatch 记录在同一 command buffer 里，因此这两个全局**必然已绑定**——不会触发 B5-fix2 那种“声明了资源但未绑定导致 Unity 丢弃 dispatch”。
- `unity_SpecCube0` 虽在 `ShaderLibrary/UnityInput.hlsl:228-229` 无条件声明，但它是引擎按对象的反射探针全局，在 compute 里只对应“全局反射”，绑定取决于 `RenderSettings.defaultReflectionMode`；为规避未绑定丢弃风险，**A1 不引用它**，也就不做探针混合/盒投影（与 A1 定义一致）。
- 采样所需 helper 在 compute 兼容头里：`PerceptualRoughnessToMipmapLevel`（core `ImageBasedLighting.hlsl`）、`DecodeHDREnvironment`（core `EntityLighting.hlsl`）；SSR compute 已 include `ImageBasedLighting.hlsl`，225/hit 核可再 include `EntityLighting.hlsl`。
- A2（`GlxProbeQuery`）发布的是**方向化辐照度**（`GlxSampleProbeIrradiance` × distance moments），没有 roughness mip，只能做漫反射级近似；仅在 `rayTracingProbeFallback` 开启且 A1 不可用时启用。A3 = `_GlxHitAmbientIrradiance` 兜底。

A1 的最小实现（compute 侧，225 与 hit 核共用）：
```
TEXTURECUBE(_GlossyEnvironmentCubeMap); SAMPLER(sampler_GlossyEnvironmentCubeMap);
half4 _GlossyEnvironmentCubeMap_HDR;
half3 GlxSampleEnvReflection(float3 dir, half perceptualRoughness)
{
    half mip = PerceptualRoughnessToMipmapLevel(perceptualRoughness);
    half4 enc = SAMPLE_TEXTURECUBE_LOD(_GlossyEnvironmentCubeMap, sampler_GlossyEnvironmentCubeMap, dir, mip);
    return DecodeHDREnvironment(enc, _GlossyEnvironmentCubeMap_HDR);
}
```

> 剩余待运行时确认项：A1 的首帧 `_GlossyEnvironmentCubeMap` 在 compute 下的实际取值（skybox/默认反射）随 fixture 而定，B6 用 `ssroff` 帧做对照量化；`unity_SpecCube0` 不作为 A1 依赖，故不需要单独 spike。

### D2 SS 命中的表面来源

SS 命中是 trace 分辨率上的屏幕空间点，当前帧 GBuffer 已经存在：**直接读命中 UV 处的 GBuffer0/1/2 + depth 构面**，不新增中间缓冲。

- 复用现有 `_SsrHitPointTexture`（trace 分辨率）拿命中 UV；
- 边缘/邻居校验沿用现 `SsrReadOriginalColor` 的 depth/normal 一致性判据（去掉颜色采样）；
- 记录按 C2.7 字段构面，`SsrHitSurface.source` 增加 `SsrHitSurfaceSourceGbuffer = 2`（版本位仍 1，字段不变）；
- 命中的 GBuffer 读取必须使用**当前帧**（这是必须后置到 220 的根本原因）。

### D3 公共着色函数

新建 `Shaders/RayTracing/SsrShadeSurface.hlsl`：

```
struct SsrShadedSample { float3 radiance; float weight; };
SsrShadedSample SsrShadeSurface(SsrHitSurface s, float3 positionWS, float3 rayDir, ...);
```

- raygen 的 closest-hit（回退路径）与 `SsrHitDeferred.compute` 都调它；
- 内部就是现 `SsrHitDeferred` 的 `CdShade` 逻辑 + 环境光项，保持 P1 的 SHADOW_LOW/64 灯光照口径；
- raygen 侧只写记录（24B 不变），着色仍可由 kernel 完成，避免 closest-hit UAV 限制。

### D4 权重与 denoiser

- `ssShare` 只表示 SS 覆盖率（含 roughness/edge fade）；`hwShare = 1 - ssShare` 只是覆盖率补集；
- 命中核按 slot 输出 `radiance`（预乘）与 `weight`，Temporal 与 `SsrPbrHwMerge` 不再用 `min(1-s, requestShare)` 做“路径折中”；
- denoiser 输入仍是 radiance+weight，历史/降噪语义不变。

### D5 决策 #4 / #5（在 B6 统一量化，不再拖）

- **#4 一帧跳变**：开关 `ssrAfterGbuffer` 时，同一相机静止帧在切换前后各取一帧，统计 ROI 的 meanAbs/p99/max；判据：整帧 meanAbs ≤ 1e-3 且无 >0.05 的区域性跳变，否则记录为已知代价。
- **#5 历史捕获时机**：不透明 SSR 不再依赖 400 的颜色金字塔（只有透明 SSR 依赖），因此 400/501 保持不变；B6 用一次 300 帧静止序列确认无新 ghost。

### D6 回滚开关

- feature 级 `ssrAfterGbuffer`（默认 P3 完成后开）；关闭时：SSR 回 206、Setup 回 207、GBuffer 恢复写 SSR（即 P1 路径）；
- 两种路径共用 R1/R2 调度与输出契约；切换只改注入点、接收者混合与命中核的 SS 分支。

## 3. Pass 编排（改前 / 改后）

| 事件 | 改前 | 改后 |
|---|---|---|
| 205 | SSGI | SSGI（不变） |
| 206 | **SSR** | （回滚路径专用） |
| 207 | 材质绑定 | （回滚路径专用） |
| 210 | GBuffer（含 SSR） | GBuffer（**不含 SSR**，保留 env） |
| **220** | Decal | **SSR**（+ Decal 同事件，顺序按 queue 固定） |
| **221** | — | 材质绑定（SSGI + SSR） |
| **225** | — | 间接镜面合成（D1-A） |
| 230 | ClusteredDeferred | ClusteredDeferred（+ specular RT） |
| 250 | Forward opaque | 不变（仍走材质端 blend） |
| 400/449/501 | 颜色金字塔/透明 SSR/历史 | 不变 |

排序约束（写进 tripwire）：

1. SSR(220) 必须在 GBuffer(210) 之后、Deferred lights(230) 之前；
2. 绑定 pass 必须 >220 且 <230；
3. 透明 SSR(449) 与折射(448) 的事件不变；
4. 回滚路径的 206/207 必须仍可独立工作。

## 4. 契约与测试变更

| 契约 | 变更 |
|---|---|
| C1（注入点） | 206 → 220；保留“回滚路径 206”记录；`SsrP3ContractTests` 断言改为“220 或 206+开关” |
| C2（24B 发布） | 布局不变；`flags` 增加 `SS` 位（SS 命中由核发布），24B 不变 |
| C2.7（表面记录） | `source` 增加 `gbuffer = 2`；字段与 80B 布局不变；版本位仍 1 |
| C8（写者） | raygen 只写 RT 记录；命中核接受 `source ∈ {rt, gbuffer}`；SS 不写记录时不触碰 24B 之外的字段 |
| 新增 tripwire | `SsrShadeSurface` 被 raygen 与 kernel 同时 include；GBuffer pass 不再 include SSR blend；pass 事件断言；`ssrAfterGbuffer` 默认值与开关路径 |

## 5. 任务分解

> 每个任务一个本地提交；构建与静态检查随任务，**像素/性能复测统一在 B6**。

### B0 选型与量测钩子（前置）
> **状态：完成（2026-11-27）。** D1 结论见 §2「D1-B0 选型结论」：A1 只采 `_GlossyEnvironmentCubeMap`，不碰 `unity_SpecCube0`。feature 新增 `ssrAfterGbuffer`（默认 false）/`ssrIndirectSource`（默认 A1）；捕获器新增 `-p3-ssr-after-gbuffer`/`-p3-ssr-rollback`/`-p3-ssr-indirect {a1|a2|a3}` 并写入 `Player.json`（`p3*Requested` 与 `feature*` 回读分开记录）；fixture builder 显式写出默认值并记入 `Fixture.json`。开关在 B2 前不改变行为。
- 读代码确认 D1 的 A1/A2 工作量：compute 下可用的反射探针全局（`unity_SpecCube0`、`_GlossyEnvironmentCubeMap_HDR`）是否在 URP 14 的 compute 中可用；不可用则退回 A2/A3。
- 给 harness 增加开关：`-p3-ssr-after-gbuffer`、`-p3-ssr-indirect {a1|a2|a3}`、`-p3-ssr-rollback`（写入 `Player.json`）。
- 产出：D1 选型记录（追加到本文件 §2）。

### B1 契约先行
> **状态：完成（先红，2026-11-27）。** contracts.md 已按 C1/C2/C2.7/C8 更新；`SsrP3ContractTests` 新增 4 条 P3 断言 + C2.7 gbuffer 源断言（预期红，B2–B4 转绿）；legacy 206 事件断言保持绿。未改渲染行为。
- 更新 contracts.md（C1/C2/C2.7/C8 上述变更）+ `SsrP3ContractTests` 新断言（先红）。
- 不改渲染行为。

### B2 接收者重路由（D1）
> **状态：完成（2026-11-27，build/静态；像素在 B6）。** 分片提交：B2a 事件开关、B2b GBuffer 去镜面、B2c 225 复合 pass、B2d 230 相加、B2e 221 绑定。
> 实现取舍：GBuffer3 去掉**全部间接镜面**（含环境项），由 225 重算 `lerp(env,ssr,w)·EnvironmentBRDFSpecular·occlusion` 后由 230 相加——这是 D1-A 的自洽形态（避免 env 双计）；上文“保留 env”指保留漫反射环境/bakedGI，不含镜面 env。
> 解耦：core 的 `ClusteredDeferred.compute` 用 `_GLX_SSR_SPECULAR` 关键字门控 `_GlxSsrSpecular` 的**声明**，插件在 225 绑定纹理后开启、Cleanup 关闭，从而“关时 kernel 不声明该纹理”，规避未绑定声明丢 dispatch（B5-fix2 的坑）。
> 225 门控放宽：只要 `ssrAfterGbuffer` 且是 Deferred 就运行，SSR 无输出时按黑纹理（权重 0，仅环境项），保证 SSR Volume 关闭或冷帧时环境镜面不消失。
> `ssrAfterGbuffer` 关闭时：不 enqueue 221/225，`_GlxSsrAfterGbuffer=0`，`_GLX_SSR_SPECULAR` 变体不启用——GBuffer/230 与 P1 逐位一致（B6 校验）。
- `LitGBufferPass.hlsl`：延迟路径不再写 SSR（保留 env + bakedGI + emission）；forward 路径不变；
- 新增 `SsrIndirectSpecularPass`（225）与 specular RT 生命周期；
- `ClusteredDeferred.compute` 读 specular RT 相加；
- `ssrAfterGbuffer` 关闭时行为与 P1 逐位一致。

### B3 SS 命中读当前 GBuffer
> **状态：完成（2026-11-27，build/静态；像素在 B6）。** 实际实现：
> - 新增 `SsrHitSs.compute`：按 trace 像素全屏 dispatch，读 `_SsrHitPointTexture`→当期 `_GBuffer0/1/2`+depth 构面（`source = gbuffer`），调 B4 的 `SsrShadeSurface`，写 `_R2Hits[slot]`（`flags = GlxHit | SS`）。由 `SsrPass.TryTrace` 在 DispatchRays 后、Temporal 前 dispatch。
> - `R2Temporal` 跳过 SS 位（SS 不进硬件历史，避免二次合并）；RT 路径不变。
> - `SSR_REPROJECT` 加 `_GLX_SSR_SHADE_SS` 关键字分支：P3 时读 `_R2Hits[slot]` 的 SS radiance 与 `_R1Original[slot].ssShare` 覆盖率，替代前帧色采样；keyword 关闭（P1/D3D11）时整段不引用 R1/R2 缓冲，避免未绑定丢 dispatch。
> - `SsrReadOriginalColor` 仍在 R1 分类里跑——但其**覆盖率**（edge×fade=ssShare）在 P3 复用，只有**颜色**改为当期 GBuffer 着色，符合“覆盖率只表噪声/覆盖、不表着色折中”。
> - D3D11（无 hybrid 队列）：SS 核不派发、keyword 关闭，P3 回落到前帧色 SS + 225 仅环境项；`ssrAfterGbuffer` 默认关时整条 P3 不激活。
- `ScreenSpaceReflections.compute` 的 trace 输出命中 UV/有效性，不再调 `SsrReadOriginalColor`（opaque 路径）；
- `SsrHitDeferred.compute` 改为 trace 全屏 dispatch：SS 分支读当前 GBuffer 构面，RT 分支读记录；
- Temporal/PBR merge 改按覆盖率加权；denoiser 不变。

### B4 公共着色函数
> **状态：完成（2026-11-27）。** 抽出 `SsrShadeSurface.hlsl`（`SsrShadedSample { radiance, weight }` + `SsrShadeSurface(CdShade + 环境项)`），`SsrHitDeferred.compute` 改为调用它。
> 修订：真正的共用范围是**两个 compute 命中核**（RT 核 + B3 的 SS 核）；材质 closest-hit 运行在 per-draw 材质头环境，**无法**包含 compute 的 `ClusteredDeferred.hlsl`，故不接该头，仅作 `hitDeferredShading` 关闭时的 P1 回退（P3 路径下所有命中由核着色）。契约 C8 与 tripwire 同步。
- 抽 `SsrShadeSurface.hlsl`，命中核共用；
- probe/sky 层级：`rayTracingProbeFallback` 开时绑真实图集并把 `_GlxHitLowFrequencySource = 1`；否则保持 P1 近似。

### B5 编排与清理
- 回滚开关、Pass 断言、marker 命名（`SSR Hit Shading (SS)` / `(RT)`）；
- 删除 P1 的 206 专用分支（保留在开关后面），更新 RenderDoc 抓帧脚本说明。

### B6 统一复测（唯一测量批）
- 双 API、两进程、三变体（base/nodenoise/ssroff）× {P1 路径, P3 路径}；
- 六项阻断门禁 + D5 的 #4/#5 量化；
- 证据落 `validation/P3/`，更新 HANDOFF/README/ACCEPTANCE。

## 6. 验收（B6 统一执行）

沿用 plan.md §P3 六条，其中 1/2 的度量口径细化（P1 已证明原口径被内容/历史主导）：

1. **缝**：`nodenoise` 下，SS↔RT 相邻块（`reason==2` vs `reason==4/6`，P1 的 `seam_metric.py`）的 **median step ≤ P0 的 50%**，mean 不上升；洋红/关闭对照证明指标敏感；probe 边界不新增跳变。
2. **一致性**：同一条反射在 SS 块与 RT 块上的 radiance 均值/色温差 ≤ 阈值（不依赖 denoiser）。
3. **时间稳定**：静止 300 帧 ROI 方差 ≤ 基线；运动相机无 ghost。
4. **无 NaN/Inf**；D3D11 回退与 P0 逐位一致（或仅 half-ULP 噪声，需记录）。
5. **性能**：整帧 ≤ +0.5 ms、SSR 链 ≤ +0.4 ms；GPU 时间双 API 记录（Player FrameTimingManager 或带 counter 的抓帧）。
6. **证据**：双 API、两进程、原始帧 + 哈希 + 计数器 + 设置恢复。

## 7. 风险

| 风险 | 缓解 |
|---|---|
| compute 里没有可用的探针反射（A1 不可行） | B0 先验证；退回 A2（图集）或 A3（近似），并在 README 记已知质量差 |
| 225 新增全屏 pass 超性能预算 | 先测 225 单独耗时；必要时并入 230 核（方案 B） |
| GBuffer4 语义变化影响 forward-only 物体 | 两条路径分开：forward 仍走材质端 blend；只改 deferred |
| 回滚路径位图漂移 | B2 完成后立即做一次“开关关闭 = P1 帧 SHA”的静态校验 |
| 缝门禁仍不可达（内容主导） | 指标改为 median + nodenoise；必要时新增一份金属/多灯判别测例（新基线，记在 P3 证据里） |
| P2 未完成（世界 cluster/材质矩阵） | P3 的命中核先用 P1 的 64 灯全量遍历；P2 完成后替换，不阻塞 P3 |

## 8. 依赖与顺序

1. B0 → B1 → B2 → B3 → B4 → B5，逐任务提交；
2. B2 完成即可用开关做“P1 vs P3 接收者”的局部视觉检查（不跑正式门禁）；
3. B6 一次性跑全部测量与性能，通过后更新 HANDOFF/ACCEPTANCE 并归档；
4. 若 B6 未达门禁：保持 P3 开关默认关闭，按验收协议记录量测值（不做“已修复”表述）。
