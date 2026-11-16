# 04-SSGIFallbackImprove 交接

> 更新：2026-11-16
> 状态：**调研与方案冻结完成，尚未改代码。** 方案 A（复用探针 atlas + 暴力遍历 + renderer feature 开关）待实施。
> 基线：Unity `2022.3.62f1`，URP `14.0.12`，上一阶段 `02-SSRImprove` T0–T6 已完成（提交 `ed4ed8b`，EditMode 418 passed）。
> URP 目标包：`E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12`（下文 `URP/`）。
> HDRP 源包：`E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12`（下文 `HDRP/`）。
> 关联文档：`PLAN/TODO/02-SSRImprove/HANDOFF.md`、`PLAN/Done/01-ScreenSpaceLighting/validation/T14/T14KnownDifferences.md`。

---

## 1. 本轮做了什么（调研，无代码改动）

### 1.1 触发问题

`02-SSRImprove` 收尾后审计 SSGI/SSR 与 HDRP 的差异时确认：URP SSGI 的未命中回退在 **Forward / Deferred** 下只采样 `ReflectionProbe.defaultTexture`（默认反射/天空 cubemap），不会读取场景里的局部反射探针；Forward+ 才使用逐像素探针列表。HDRP 在所有光栅路径都用 FPTL 逐像素 env-light 列表。

### 1.2 HDRP 事实（本次核实）

- SSGI miss 回退在 `TraceReflectionProbes`：`GetCountAndStart(posInput, LIGHTCATEGORY_ENV)` 取逐像素 env-light 列表，只处理 `IsEnvIndexCubemap`，忽略 planar。
- 权重与投影：`EvaluateLight_EnvIntersection` 用 **influence shape**（Box/Sphere）算权重、用 **proxy volume** 求交并改写采样方向；权重再 `Smoothstep01` 并乘 `light.weight`；`UpdateLightingHierarchyWeights` 保证累计 ≤1。
- atlas 采样 **LOD 0**，乘 `rangeCompressionFactorCompensation` 并 `ClampToFloat16Max`；权重饱和后余量给 `EvaluateAmbientProbe(normalWS)`（ambient SH）。
- APV 是独立 `else if` 分支，与探针权重不混合；URP 14 无 APV，可忽略。
- 容量：`k_MaxCubeReflectionsOnScreen = 128`，默认 `maxCubeReflectionOnScreen = 128/4 = 32`；数据走自建 `EnvLightData` StructuredBuffer + FPTL 索引，不复用引擎 env light。
- 参考：`HDRP/Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute`（`TraceReflectionProbes` 与 `REPROJECT_GLOBAL_ILLUMINATION`）、`HDRP/Runtime/Lighting/LightEvaluation.hlsl`（`EvaluateLight_EnvIntersection`）、`HDRP/Runtime/Lighting/Reflection/VolumeProjection.hlsl`、`HDRP/Runtime/Material/Material.hlsl`（层级权重）。

### 1.3 URP 现状（关键门控）

| 位置 | 现状 |
|---|---|
| `URP/Runtime/ForwardLights.cs` | `ReflectionProbeManager` **只在** `m_UseForwardPlus` 时创建（约 L134）、`UpdateGpuData` 只在 Forward+ 调用（约 L375）、`Cleanup` 只在 Forward+ 释放（约 L440） |
| `URP/Runtime/UniversalRenderer.cs` | `ForwardLights.Setup` 在三种模式都会执行（`SetupLights`，约 L1391）；Deferred 额外调用 `DeferredLights.SetupLights` |
| `URP/Runtime/ReflectionProbeManager.cs` | 数据源是 `renderingData.cullResults.visibleReflectionProbes`，与 cluster 无关；打包 boxMin/boxMax（w=blendDistance/importance）、capture position（w=boxProjection 标志）、mip scale/offset |
| `URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs` | `SSL_PROBE_ATLAS` 仅在 `renderingModeActual == ForwardPlus` 打开（约 L230-233） |
| `URP/Shaders/Utils/ScreenSpaceLighting/ReflectionProbeFallback.hlsl` | 已有完整 Forward+ 探针实现：cluster 迭代 + 盒权重 + box projection + atlas LOD 0 + 权重饱和 |
| `URP/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute` | 无 cluster 时 `SsgiSampleReflectionProbe` 只采样默认 cubemap 且 `weight = 1`，天空 SH 不会再补余量 |
| 探针上限 | `UniversalRenderPipeline.maxVisibleReflectionProbes = min(maxVisibleAdditionalLights, 64)`，mobile 32；HDRP 128/32 |
| 材质一致性 | Forward+ 的材质探针与 SSGI 都用同一 atlas/cluster；Forward/Deferred 的材质走逐对象 `unity_SpecCube0/1`，与 atlas 列表可能不同 |

### 1.4 结论

- **能力可行**：`visibleReflectionProbes` 在 Forward/Deferred 都存在，`ReflectionProbeManager` 的打包逻辑与 cluster 无关；缺少的只是“创建/更新/关闭”三处门控和一个非 cluster 的着色器分支。
- 采用 **方案 A**：Forward/Deferred 也发布 atlas，SSGI 用暴力遍历可见探针（低成本盒权重早退），Forward+ 保持现有 cluster 路径。
- 非目标：不做 HDRP 完整 `EnvLightData` 对齐（sphere influence、法线/逐面权重、per-probe multiplier、独立 proxy volume）、不做逐像素探针 tile 列表、不修改材质探针采样路径、不引入 APV。

---

## 2. 目标与范围

### 2.1 目标

1. Forward / Deferred（以及任意非 Forward+ 的 `UniversalRenderer`）的 SSGI miss 回退能采样“附近反射探针”，不再固定落默认 cubemap。
2. 复用现有 atlas、box 数组与 `ReflectionProbeFallback.hlsl` 的权重/投影/采样代码，不新增资产、不新增 kernel。
3. 在 **Screen Space Lighting Renderer Feature** 上增加开关，控制非 Forward+ 是否发布 atlas：
   - 关（默认）：保持今天的行为与零 atlas 成本。
   - 开：Forward/Deferred 发布 atlas，SSGI 走探针列表。
   - Forward+ 不受开关影响（它本来就需要 atlas）。
4. 不回归 Forward+、SSR、SSGI 现有验证（T5/T6 证据、EditMode 418）。

### 2.2 非目标

- 不追求与 HDRP 逐像素一致的探针混合（URP 探针数据模型更简单）。
- 不改变 Forward/Deferred 材质的 `unity_SpecCube0/1` 探针选择（避免影响现有光照）。
- 不为探针单独建立 tile/zbin 列表（方案 B，实测超预算再考虑）。
- 不处理 APV/probe volume。

---

## 3. 任务拆分

> 依赖顺序：P0 → P1 → P2 → P3 → P4 → P5。每项独立提交，不合并大提交。

### P0：冻结开关语义与行为（0.5 天）

- 决定开关名称、默认值与 Tooltip：建议 `ScreenSpaceLightingSettings.publishProbeAtlas`（`[SerializeField] internal bool`，默认 **false**，opt-in；Tooltip 说明“非 Forward+ 模式发布反射探针 atlas，供 SSGI 未命中回退使用；开启会增加 atlas 显存/更新开销”）。
- 决定开启后的回退语义：
  - `rayMiss = ReflectionProbesAndSky`：探针权重不足 1 时补 ambient SH（与 HDRP 一致）。**行为变化**：当前默认 cubemap 是方向性天空，开启后天空部分变为 SH，需要记录并测试。
  - `rayMiss = ReflectionProbes`：无探针覆盖时 weight 0，材质保留 baked GI（URP 既有 weight 契约）；当前默认 cubemap 会给 weight 1，也是行为变化。
  - `rayMiss = Sky/None`：保持现状。
- 决定开关是否支持运行时切换：建议支持（P1 在 `ForwardLights.Setup` 每帧按开关创建/停止更新 manager，关闭时保留已分配 atlas 以避免抖动）。
- 输出：本节更新为结论表；后续代码按此实现。

### P1：解耦 `ReflectionProbeManager` 生命周期（1–1.5 天）

- `ForwardLights.InitParams` 增加 `publishProbeAtlas`（默认 false）。
- 构造：`m_UseForwardPlus || publishProbeAtlas` 时创建 manager；cluster buffer 仍只在 Forward+ 创建。
- `Setup`：
  - Forward+ 分支保持现有顺序（先 `UpdateGpuData`，再上传 cluster buffer）。
  - 非 Forward+ 且开关开启时调用 `UpdateGpuData`。
  - 每帧从 renderer/feature 读取开关，支持运行时切换；关闭时跳过更新（不强制释放）。
- `Cleanup`：manager 非空即释放；cluster buffer 仍按 Forward+ 释放。
- `UniversalRenderer`：构造 `ForwardLights` 前检查 `data.rendererFeatures` 中是否存在 `ScreenSpaceLightingRendererFeature` 且开关开启；`SetupLights` 每帧同步开关到 `ForwardLights`。
- **不要**为非 Forward+ 修改 `cullingParameters.reflectionProbeSortingCriteria`（Forward+ 设 `None` 是为了 cluster 索引与 atlas 对齐；非 Forward+ 改变排序会影响引擎逐对象探针选择）。暴力遍历不依赖顺序。
- 验收：开关关闭时 `reflectionProbeManager == null`、atlas 未创建；开启且场景有探针时 `urp_ReflProbes_Count > 0`、atlas 有效；Forward+ 行为与提交前逐字节一致。

### P2：着色器非 cluster 探针列表（1 天）

- `ReflectionProbeFallback.hlsl` 新增 `SslSampleReflectionProbesList`：
  - 直接 `for (i < (uint)urp_ReflProbes_Count && totalWeight < 0.99)`。
  - 复用 `SslCalculateProbeWeight`、`SslBoxProjectedCubemapDirection`、`urp_ReflProbes_MipScaleOffset[i * 7]`、`SAMPLE_TEXTURE2D_LOD(..., 0.0)`。
  - 语义与 cluster 版本一致：权重 `min(weight, 1-totalWeight)`，权重 ≤0 跳过，LOD 0。
- `ScreenSpaceGlobalIllumination.compute`：
  - 新增 `#pragma multi_compile _ SSL_PROBE_LIST`。
  - 把 `ReflectionProbeFallback.hlsl` 的 include 条件从 `#if defined(SSL_PROBE_ATLAS)` 扩大为 `#if defined(SSL_PROBE_ATLAS) || defined(SSL_PROBE_LIST)`（header 内的 cluster 函数不会在 list 路径被调用，未绑定的 `URP_ZBins`/`urp_Tiles` CBUFFER 不影响运行）。
  - 回退分支优先级：`SSL_PROBE_ATLAS`（Forward+ cluster） → `SSL_PROBE_LIST`（非 FP 暴力） → 默认 cubemap。
  - 天空余量逻辑保持不变（`weight < 1` 时补 ambient SH）。
- `ScreenSpaceLightingShaderIDs`：新增 `SslProbeListKeyword`（`"SSL_PROBE_LIST"`），与现有 keyword 常量一起维护。
- `SsgiPass`：
  - 计算 `forwardPlus`、`probeList = !forwardPlus && m_Settings.publishProbeAtlas`。
  - 设置 `SSL_PROBE_ATLAS` / `SSL_PROBE_LIST` 关键字；不新增纹理绑定（数组/atlas 是全局属性）。
  - 防御：`urp_ReflProbes_Count == 0` 时循环不执行，不需要判定 atlas 是否为空。
- 验收：三个 compute 强制重导入 0 消息；两个 keyword 变体都存在；无 cluster 时不再依赖 `URP_ZBins`/`urp_Tiles`。

### P3：Feature 开关与 Inspector（0.5 天）

- `ScreenSpaceLightingSettings` 增加 `publishProbeAtlas` 字段（P0 决定默认值）。
- `ScreenSpaceLightingEditor` 的 `base.OnInspectorGUI()` 会自动绘制新字段；补充 HelpBox 文案说明开关只影响非 Forward+。
- `UniversalRenderer` 读取开关的方式：
  - 建议在 `UniversalRenderer` 内提供 `internal bool publishSslProbeAtlas`，构造时遍历 `data.rendererFeatures`；`SetupLights` 每帧传给 `ForwardLights`。
  - 避免让 `ForwardLights` 依赖 `RendererFeature` 类型。
- 验收：Inspector 可见、序列化正确、开关切换下一帧生效（或明确记录需重建 renderer）。

### P4：测试与验证（1–1.5 天）

**EditMode**

- `ScreenSpaceLightingSettings` 默认值/Tooltip 测试（反射或直接访问 internals）。
- `ForwardLights`/manager 生命周期单测：开关关闭不创建 manager；开启创建；`Cleanup` 可重复调用不抛异常。
- 强制重导入 `ScreenSpaceGlobalIllumination.compute`：`SSL_PROBE_ATLAS`、`SSL_PROBE_LIST` 两个变体编译 0 消息。

**渲染验证**（新增 `Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingSsgiProbeFallbackVerification.cs`）

- 场景：复用 T3 验证场景；创建两个颜色可区分的反射源：
  - 默认反射（`RenderSettings` custom sky 或默认 cubemap）设为红色。
  - 一个覆盖地面区域的局部 `ReflectionProbe`（Custom cubemap 或捕获绿色自发光）设为绿色。
- 用例（每个渲染器）：
  1. SSGI off：Reference。
  2. SSGI on + 开关 off：Forward/Deferred 命中默认红色；Forward+ 命中绿色（atlas 恒开）。
  3. SSGI on + 开关 on：三个渲染器都命中绿色（探针权重区域）。
  4. `rayMiss=ProbesAndSky` 且探针不覆盖：探针 weight 0 → ambient SH；记录与默认 cubemap 的差异。
  5. 0/1/4/32 个探针的压力用例，确认循环与权重饱和。
- 断言：
  - 开关 on 时探针覆盖区域的平均颜色偏向探针颜色（例如 G/R > 1.5），开关 off 时偏向默认颜色（G/R < 1）。
  - Forward+ 在开关 off/on 两图逐像素一致。
  - 无 NaN/Inf/负值；不出现 magenta。
  - 默认 cubemap 路径与列表路径的差异只出现在 miss 像素（weight 0 区域）。
- 输出：`T5` 风格 JSON + 三渲染器 PNG + EditMode XML，保存到本目录 `validation/`。

**Frame Debugger（可选但推荐）**

- 采集 Forward/Deferred 开启开关后 SSGI reproject 的纹理绑定，确认 `urp_ReflProbes_Atlas` 被绑定、`urp_ReflProbes_Count > 0`。
- 记录 SSGI 事件数不变（探针回退在 reproject kernel 内，不新增 dispatch）。

**性能**

- 1280×720、32 个可见探针：记录 SSGI reproject 的 GPU 时间增量，建议预算 ≤0.2 ms（RTX 5060 Ti / DX11）；若超预算，优先限制探针数或改半分辨率 trace，再考虑方案 B。

### P5：收尾（0.5 天）

- 更新 `T14KnownDifferences.md`：新增 Forward/Deferred SSGI 探针回退条目（开关、URP 盒权重 vs HDRP influence shape、64 vs 128、天空余量行为变化、材质探针来源可能不同）。
- 更新本文状态与结论；如需要，更新 `02-SSRImprove` 的关联说明。
- 清理临时脚本/临时资源；确认 `validation/` 证据完整。

---

## 4. 实现细节（供实施时参考）

### 4.1 C# 改动草图

```csharp
// ForwardLights.InitParams
public bool publishProbeAtlas;

// ForwardLights ctor
if (m_UseForwardPlus)
    CreateForwardPlusBuffers();
if (m_UseForwardPlus || initParams.publishProbeAtlas)
    m_ReflectionProbeManager = ReflectionProbeManager.Create();

// ForwardLights.Setup
if (m_UseForwardPlus)
{
    m_ReflectionProbeManager.UpdateGpuData(cmd, ref renderingData);
    // ... existing cluster upload ...
}
else if (m_PublishProbeAtlas && m_ReflectionProbeManager != null)
{
    m_ReflectionProbeManager.UpdateGpuData(cmd, ref renderingData);
}

// ForwardLights.Cleanup
if (m_UseForwardPlus) { /* cluster buffers */ }
m_ReflectionProbeManager?.Dispose();
m_ReflectionProbeManager = null;
```

运行时开关：`ForwardLights` 增加 `internal void SetProbeAtlasEnabled(bool)`，`UniversalRenderer.SetupLights` 每帧从 feature settings 同步；关闭时跳过 `UpdateGpuData`，保留 manager 以免重复分配。

### 4.2 Shader 草图

```hlsl
// ReflectionProbeFallback.hlsl
float3 SslSampleReflectionProbesList(float3 directionWS, float3 positionWS, out float totalWeight)
{
    totalWeight = 0.0;
    float3 color = 0.0;
    uint count = (uint)urp_ReflProbes_Count;
    [loop] for (uint i = 0; i < count && totalWeight < 0.99; ++i)
    {
        float weight = SslCalculateProbeWeight(positionWS, urp_ReflProbes_BoxMin[i], urp_ReflProbes_BoxMax[i]);
        weight = min(weight, 1.0 - totalWeight);
        if (weight <= 0.0) continue;

        float3 sampleVector = SslBoxProjectedCubemapDirection(directionWS, positionWS,
            urp_ReflProbes_ProbePosition[i], urp_ReflProbes_BoxMin[i], urp_ReflProbes_BoxMax[i]);
        float2 uv = saturate(PackNormalOctQuadEncode(sampleVector) * 0.5 + 0.5);
        float4 scaleOffset = urp_ReflProbes_MipScaleOffset[i * 7];
        color += weight * SAMPLE_TEXTURE2D_LOD(urp_ReflProbes_Atlas, samplerurp_ReflProbes_Atlas,
            uv * scaleOffset.xy + scaleOffset.zw, 0.0).rgb;
        totalWeight += weight;
    }
    return color;
}
```

`ScreenSpaceGlobalIllumination.compute` 回退处：

```hlsl
#if defined(SSL_PROBE_ATLAS)
    color += SslSampleReflectionProbesAtlas(sampleDir, positionWS, normalizedUV, weight);
#elif defined(SSL_PROBE_LIST)
    color += SslSampleReflectionProbesList(sampleDir, positionWS, weight);
#endif
```

### 4.3 边界情况

- `urp_ReflProbes_Count` 为 0 或 atlas 未发布：循环不执行；`ProbesAndSky` 由 SH 补齐，`Probes` 保持 weight 0。
- XR：探针列表是相机级全局数据，两眼相同；cluster 路径不变。
- Mobile/GLES：数组 32 项，循环上限同步；避免在移动端默认开启。
- 多相机：每相机 `SetupLights` 更新 atlas；`SetGlobal*` 走当前 command buffer。
- 场景超过 64 个可见探针：`ReflectionProbeManager` 只打包前 64 个（与 Forward+ 现状一致），其余忽略；记录为已知限制。

---

## 5. 验收标准

### 5.1 静态/编译

- [ ] `ScreenSpaceGlobalIllumination.compute`、`ReflectionProbeFallback.hlsl` 强制重导入 0 条 `ShaderUtil` 消息；`SSL_PROBE_ATLAS` 与 `SSL_PROBE_LIST` 两个变体均存在。
- [ ] URP Runtime/Editor/Test 编译 0 error；新增字段序列化与 Tooltip 正常。
- [ ] 无临时脚本、无 `ProjectSettings` 残留改动；`git status` 仅含预期文件。

### 5.2 功能

- [ ] 开关关闭：Forward/Deferred 行为与改动前逐字节一致（默认 cubemap），manager 不创建、atlas 不占显存。
- [ ] 开关开启：Forward/Deferred 的 SSGI miss 命中局部探针；`ProbesAndSky` 余量补 ambient SH；`Probes` 无覆盖时 weight 0。
- [ ] Forward+：开关 off/on 输出逐字节一致，cluster 路径无回归。
- [ ] 运行时切换开关下一帧生效（若 P0 决定不支持，则明确要求重建 renderer 并测试）。
- [ ] SSGI/SSR 的相机 cut、分辨率变化、历史 reset 不受影响。

### 5.3 视觉/数值

- [ ] 局部探针场景：开关 on 的探针区域颜色偏向探针（G/R > 1.5），开关 off 偏向默认（G/R < 1），三渲染器一致（Forward+ 恒为探针色）。
- [ ] 无 NaN/Inf/负值、无 magenta、无探针边界硬切（blendDistance 生效）。
- [ ] 0/1/4/32 探针压力用例通过；32 探针时权重饱和与早退生效。
- [ ] `rayMiss` 四种取值的回退链符合 P0 冻结的语义。

### 5.4 性能

- [ ] 1280×720、32 探针：SSGI reproject GPU 时间增量 ≤0.2 ms（RTX 5060 Ti / DX11）；记录测量方法。
- [ ] 开关关闭时 atlas 显存为 0、每帧无额外 CPU 上传。

### 5.5 回归

- [ ] EditMode 全量通过（基线 418，新增测试后记录新总数）。
- [ ] `02-SSRImprove` 的 T5 三渲染器验证可重跑（SSR 不受影响）。
- [ ] Forward+ 的 Frame Debugger 事件数/顺序不变；Forward/Deferred 的 SSGI 事件数不变（探针回退在现有 kernel 内）。

### 5.6 证据清单（完成后必须存在）

```
PLAN/TODO/04-SSGIFallbackImprove/validation/
├─ P0/Contract.md                     # 开关默认值/回退语义冻结表（或写回本文）
├─ P1/UnityEditModeTests.xml          # manager 生命周期单测
├─ P2/SslShaderCheck.txt              # 两个 keyword 变体 + 0 消息
├─ P3/Inspector.png                   # Feature 开关截图（可选）
├─ P4/SsgiProbeFallbackVerification.json
├─ P4/{Forward,ForwardPlus,Deferred}_{Off,On,Reference}.png
├─ P4/FrameDebuggerReport.json        # 可选
├─ P4/UnityEditModeTests.xml
└─ P4/UnityEditMode.log
```

### 5.7 复现命令（关闭其他 Unity 编辑器后执行）

```bash
# EditMode
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" -runTests -testPlatform EditMode \
  -testResults "<abs>/validation/P4/UnityEditModeTests.xml" \
  -logFile "<abs>/validation/P4/UnityEditMode.log"

# 渲染验证（脚本名以 P4 实现为准）
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingSsgiProbeFallbackVerification.Run -quit \
  -logFile "<abs>/validation/P4/UnitySsgiProbeFallback.log"
```

> Unity 批处理会重写 `ProjectSettings/{Graphics,Quality,Project}Settings.asset`，跑完按惯例 `git checkout --` 还原。

---

## 6. 风险与已知限制

1. **材质一致性**：Forward/Deferred 材质探针走 `unity_SpecCube0/1`，SSGI 走 atlas 列表，二者选择的探针可能不同；只保证“附近探针”语义，不保证与材质逐像素一致。
2. **权重模型差异**：URP 只有线性盒 blend，没有 HDRP 的 sphere influence、法线/逐面权重、per-probe multiplier；64（桌面）/32（移动）vs HDRP 128/32。
3. **行为变化**：开关开启后，天空回退从默认 cubemap 的方向性采样变为 ambient SH；`rayMiss=ReflectionProbes` 无覆盖时从“默认 cubemap weight 1”变为 weight 0。需要 P0 明确并在文档/测试中固化。
4. **atlas 成本**：开启后非 Forward+ 也会创建/更新 atlas（显存与探针变化时的 blit）；默认关闭以保持零成本。
5. **运行时切换**：若 P1 采用每帧同步，切开关不重建 renderer；否则需要在 Tooltip 说明重建要求。
6. **排序**：不要为非 Forward+ 改 `reflectionProbeSortingCriteria`，否则可能改变材质探针选择；暴力遍历不需要顺序。
7. **探针上限**：超过 64 个可见探针时截断，可能缺少局部影响；记录为已知限制。
8. **GLES/移动端**：数组 32 项、循环成本更高，默认关闭；需要在目标平台实测。
9. **XR**：探针列表为相机级，两眼共享；与 HDRP 的逐眼一致（探针本身不分眼），无需特殊处理。

---

## 7. 关键文件索引

- 探针管理器：`URP/Runtime/ReflectionProbeManager.cs`
- Forward+ 光照/探针发布：`URP/Runtime/ForwardLights.cs`、`URP/Runtime/UniversalRenderer.cs`
- SSGI 调度：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs`
- SSGI 回退 shader：`URP/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute`
- 探针采样实现：`URP/Shaders/Utils/ScreenSpaceLighting/ReflectionProbeFallback.hlsl`
- Feature 设置/Inspector：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingSettings.cs`、`URP/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingEditor.cs`
- 已知差异文档：`PLAN/Done/01-ScreenSpaceLighting/validation/T14/T14KnownDifferences.md`
- HDRP 参照：`HDRP/Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute`、`HDRP/Runtime/Lighting/LightEvaluation.hlsl`、`HDRP/Runtime/Lighting/Reflection/VolumeProjection.hlsl`

---

## 8. 下一步

1. 先完成 P0 冻结（开关默认值、`rayMiss` 语义、运行时切换策略），写回本文。
2. 按 P1 → P2 → P3 顺序实现，每步单独提交并附编译/单测证据。
3. P4 用局部探针场景做三渲染器实拍与数值断言，性能测量后再决定是否需要方案 B（逐像素探针 tile 列表）。
4. P5 更新 T14KnownDifferences 与本文状态；全部完成后可随 `PLAN/TODO/04-SSGIFallbackImprove` 归档。