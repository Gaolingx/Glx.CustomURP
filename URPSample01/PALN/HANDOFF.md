# ScreenSpaceLighting 交接

> 更新：2026-11-15（T10 退出）。架构理由、边界和完整任务依赖见 [实施计划](HDRP%20ScreenSpaceLighting%20迁移到%20URP%2014.0.12%20实施计划.md)；本文件只维护下一步。

## 当前位置

- **T0 已冻结**。任何边界变更都必须先重新打开 T0，并同步 PLAN 与本文件。
- **T1 已按 R3 协议退出（2026-11-14）**，并在 T2 Core 改动后由 **Run15 / Run16** 复验：三路径 PNG 逐字节一致、graphics-driver 显存一致、CPU p95 波动 **0.28% / 0.01% / 0.04%**，构建、Frame Debugger 与设置恢复全部通过。`T1Report.json` 当前指向 Run15/Run16；Run13/Run14 作为 T2 前的有效运行对保留。
- **T2 已退出（2026-11-14）**。Core 已提供 DepthNormals smoothness 编码、最早 Motion/Depth 调度和调度单元测试；无插件时三路径 Frame Debugger 事件数据与 T1 基线逐事件一致，探针在 203 时 Motion/Depth 已有效，TAA/Motion Blur 早/晚 Motion 输出逐字节一致。
- **T3 已退出（2026-11-14）**。两个空 Renderer Feature（SSL / GTAO）、Volume 组件、能力检测、neutral 输出和私有 history 骨架已落地；真实 UI 路径配置、Volume 控队、空效果逐像素一致、无插件回归和 EditMode 测试全部通过，唯一事实入口为 `validation/T3/T3Report.json`（`t3ExitCriteriaMet=true`）。
- **T4 已退出（2026-11-14）**。SSL 私有 packed Hi-Z（mip/offset/UV scale）、当前帧 Gaussian 颜色金字塔、双缓冲 previous color/depth、reset generation 和 7 个调试视图已落地；三路径下卷开启时 Pass 均执行且最终图像与 T1 基线逐字节一致，首帧/相机 cut/尺寸/render scale 重置、同帧读写槽分离、双相机隔离和 GTAO 缺失时 SSL 独立工作全部通过，Frame Debugger 证明 203 → 400 → 501 顺序。唯一事实入口为 `validation/T4/T4Report.json`（`t4ExitCriteriaMet=true`）。当前仍没有任何 GTAO、SSGI、SSR 或 Refraction 渲染效果实现完成。
- **T5 已退出（2026-11-14）**。独立 GTAO 全 kernel（Main / Spatial / Temporal / CopyHistory / Blur+Upsample）、私有 packed Hi-Z、双缓冲 AO 历史、调试视图和 SSAO 式 Inspector 已落地；Forward/Forward+/Deferred 三路径 AO 可见，SSL 特征顺序任意组合图像逐字节一致，intensity=0 与 Feature 关闭恢复 T1 基线逐字节一致。**同日按用户要求移除 URP 内置 SSAO（像素着色器实现）**：GTAO 成为唯一 AO 生产者，材质侧 `_SCREEN_SPACE_OCCLUSION` 契约保留。唯一事实入口为 `validation/T5/T5Report.json`（`t5ExitCriteriaMet=true`）。SSGI、SSR、Refraction 仍未实现。
- **T6 已退出（2026-11-14）**。SSGI Trace/Reproject、TemporalFilter validation/accumulation、DiffuseDenoiser 必要支路、半分辨率 Bilateral Upsample、HF/LF 历史与相机 reset 已落地；材质端 `_SCREEN_SPACE_GI` 混合在 Forward/Forward+/Deferred 统一消费。三路径 `T6Verification.json` 全部 `passed=true`：miss 像素与 baked GI 逐像素一致（最大通道差 1.19e-7）、首帧与 intensity=0 逐字节恢复基线、半分辨率 upsample 真实派发、调试图与最终消费纹理一致（921,600 像素中 19 像素差异、最大 0.0012）、发光立方体自身亮度比 0.991（未重复叠加 emission）、相机 reset 计数 2。Frame Debugger 顺序为 Depth Pyramid → Screen Space Global Illumination → opaque，三路径一致。EditMode 111 passed / 0 failed。唯一事实入口为 `validation/T6/T6Report.json`（`t6ExitCriteriaMet=true`）。SSR、Transparent SSR 与 Refraction 仍未实现。
- **T7 已退出（2026-11-14）**。Opaque SSR Approximation（Trace → Reproject，RGBA16F radiance + weight）、roughness/edge fade、屏外/非有限/负值拒绝和 probe/sky 回退已落地；三路径 `T7Verification.json` 全部 `passed=true`：miss 像素与 probe 基线逐像素一致（670,152 像素，最大通道差 0）、intensity=0 与首帧逐字节恢复基线、金属粗糙度权重梯度单调（0.444/0.350/0.198/0，低于 minSmoothness 为 0）、非金属光滑球权重 0.410、干净首帧的双倍 intensity 响应比 2.000（误差 0）、调试图与消费纹理 921,600 像素 0 差异、无 NaN/Inf/负值。Frame Debugger 顺序为 Depth Pyramid → Screen Space Reflections → opaque，三路径一致。EditMode 118 passed / 0 failed。唯一事实入口为 `validation/T7/T7Report.json`（`t7ExitCriteriaMet=true`）。SSR PBR Accumulation、Transparent SSR 与 Refraction 仍未实现。
- **T8 已退出（2026-11-14）**。SSR PBR Accumulation（GGX VNDF trace、3×3 cross reproject、双 RGBA16F 累积历史）落地；`usedAlgorithm` 在 Approximation/PBR 间切换并 reset，9 个公开速度拒绝组合各自命中独立 kernel（NoWorld/Hard/Smooth × Both/Source/Target），surface/target 在动态场景产生 226,681 像素差异、speed 参数 0↔1 产生 232,951 像素差异，硬拒绝调试图 100% 红、接受调试图 100% 绿，算法切换 reset 2 次、相机 cut reset 2 次。PBR 仍遵守 T7 权重契约：miss 的 670,222 像素与 probe 基线逐像素一致、首帧与 intensity=0 逐字节恢复基线、无 NaN/Inf/负值。三路径 `T8Verification.json` 全部 `passed=true`，EditMode 126 passed / 0 failed。唯一事实入口为 `validation/T8/T8Report.json`（`t8ExitCriteriaMet=true`）。Transparent SSR 与 Refraction 仍未实现。
- **T9 已退出（2026-11-14）**。Lit、ComplexLit、ShaderGraph Lit（Forward + GBuffer）全部接入 SSGI/SSR；ComplexLit 的 clear coat 在 DepthNormals 写入 coat SSR smoothness（mask>0 时 alpha=1.0），材质端 base/coat 使用独立 mask/roughness 与 HDRP 式 hierarchy weight，base smoothness 0.3 的 coat 球 trace weight 仍为 1.0。三路径 `T9Verification.json` 全部 `passed=true`：intensity=0 逐字节恢复 probe 基线、572,746 个零权重像素与 probe 图像逐像素一致（最大差 0）、coat smoothness 1↔0.2 改变 270,919 像素（Deferred 270,957）、coat mask 1↔0 改变 271,300 像素（Deferred 271,339）；SimpleLit/Particles/Terrain Lit 的 depth normals alpha 与 SSR weight 均为 0；alpha test 实体/裁剪像素 weight 1.0/0；ForceLOD 0/1 均覆盖；SG Lit 的 SSR/SSGI 均生效。小场景 lightmap（Mixed + Subtractive）回归通过；APV 因 URP 14.0.12 无 ProbeVolume 集成标为不支持。EditMode 132 passed / 0 failed。唯一事实入口为 `validation/T9/T9Report.json`（`t9ExitCriteriaMet=true`）。Transparent SSR 与 Refraction 仍未实现。
- **T10 已退出（2026-11-15）**。Core 新增 `UniversalTransparentDepthNormals` LightMode 共享实现（`Shaders/Utils/TransparentDepthNormalsPass.hlsl`）并接入 Lit/ComplexLit；透明材质通过 `_TransparentSsrReceive` 属性/`_TRANSPARENT_SSR_RECEIVE` 关键字显式 opt-in，未 opt-in 的片段在 prepass 中 `clip`。SSL 在 447 用 CopyDepth 把 opaque 深度复制到私有 depth 并从该深度起绘制透明 depth/normal，449 完成 Transparent SSR Trace/Reproject（独立 hit point/output、`_SCREEN_SPACE_TRANSPARENT_SSR`、Approximation-only）并在 450 前绑定；主透明 pass 的深度语义未改。Volume 新增 `enabledTransparent`。三路径 `T10Verification.json` 全部 `passed=true`：intensity=0 与关闭后逐字节恢复、opaque weight 与 camera depth 差异 0、receiver 改变 785 像素/weight 1.0、opt-out 与 SimpleLit 改变 0 像素/weight 0、ComplexLit clear coat 透明 1,266 像素/weight 1.0、transparent depth 0.006142 > opaque 0.003819（9,560 像素不同）、双层最近面胜出（near-only=both=0.006007 > far 0.004822）、PBR 下 transparent 4/4 dispatch 且 accumulation 0、移动透明体 ray origin 跟随且无 NaN。EditMode 142 passed / 0 failed。唯一事实入口为 `validation/T10/T10Report.json`（`t10ExitCriteriaMet=true`）。Refraction 仍未实现。
- **T11–T14 未开始**。
- **GPU p95 为参考数据**。本机 DVFS 与 GeForce/WDDM 不执行 `nvidia-smi -lgc` 导致 720p GPU p95 无法重复；证据与理由见 [Environment.json](validation/T1/Environment.json) 和 `Diagnostics/Dvfs-2026-11-14/`。10% 容差、场景、预算数值均未变，Run1–Run16 与无效运行全部保留。
- 临时 Player 已清理；原始运行目录、日志、CSV、PNG 和报告均保留。四项 ProjectSettings 已恢复原始 SHA-256。

唯一事实入口：[T1Report.json](validation/T1/T1Report.json)（`t1ExitCriteriaMet=true`）、[T2Verification.json](validation/T2/T2Verification.json)（`passed=true`）、[T5Report.json](validation/T5/T5Report.json)（`t5ExitCriteriaMet=true`）、[T6Report.json](validation/T6/T6Report.json)（`t6ExitCriteriaMet=true`）、[T9Report.json](validation/T9/T9Report.json)（`t9ExitCriteriaMet=true`）与 [T10Report.json](validation/T10/T10Report.json)（`t10ExitCriteriaMet=true`）。

## 下一步：T11 Refraction

按计划 7.5、8.4 节与第 9 节推进；T10 已交付的 Transparent SSR 不得回退：

1. Core 新增折射材质契约：开关/模型、IOR、厚度、transmittance、roughness mip、`receive Transparent SSR` 与旧材质默认值；Lit/ShaderGraph Lit GUI、keyword 与默认行为必须保持原画面。
2. 当前帧 Color Pyramid（400）供折射使用；首版不含 pre-refraction transparent，proxy 与 pre-refraction 顺序差异必须记录。
3. 移植 HDRP proxy raycast，映射到 URP `unity_SpecCube0_BoxMin/BoxMax/ProbePosition/HDR` 等 probe box 数据；完成深度拒绝、roughness mip、transmittance 与 edge fade。
4. 无有效 proxy、深度拒绝或屏幕边缘时回退原 URP probe/透明路径；不读取本帧尚未写入的颜色。
5. 项目内新增折射材质测试资产，三路径验证正确 fallback、旧材质画面不变和 proxy 顺序差异。

T10 遗留的 ShaderGraph 透明 prepass/receive 未实现，按计划与折射的 ShaderGraph 目标改造一起在 T11 处理；在完成前 SG 透明材质继续回退 probe，不得宣称支持。

## T9 证据与复现

- 结论：[T9Report.json](validation/T9/T9Report.json)（`t9ExitCriteriaMet=true`）
- 渲染/生命周期：[T9Verification.json](validation/T9/T9Verification.json)（三路径 `passed=true`）
- EditMode：`validation/T9/UnityTests.log`（132 passed / 0 failed）
- 图：`validation/T9/T9_<Mode>_{Reference,CoatIntensity0,CoatMiss,CoatHits,CoatMaskOff,CoatRoughness,AlphaTestWeight,Lod1,ShaderGraphSsr,SsrWeight,NormalsAlpha}.png` 与 `T9_Lightmap_<Mode>.png`
- 资产：`Assets/Tests/ScreenSpaceLighting/Shaders/SslT9{Lit,ComplexLit}.shadergraph`、`Renderers/SslT9*`、`Scenes/ScreenSpaceLightingT9Lightmap.unity`（含烘焙 lightmap）

复现命令（先关闭其他 Unity 编辑器）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT9Verification.CreateT9Assets -LogPath "PALN/validation/T9/UnityT9Compile.log" -ReportPath "PALN/validation/T9/UnityT9CompileSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT9Verification.RunT9Verification -LogPath "PALN/validation/T9/UnityT9Verification.log" -ReportPath "PALN/validation/T9/UnityT9VerificationSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PALN/validation/T9/UnityTests.log" -ReportPath "PALN/validation/T9/UnityTestsSettingsRestoration.json"

python "PALN/validation/T9/Analyze-T9Evidence.py"
```

T9 事实与限制：

- 权重读取：使用 `SsrWeight` 调试视图（栅格化到相机目标）而不是直接 blit compute 的 RTHandle；后者存在纵向坐标约定差异，曾导致 0 权重误判。
- coat 球中心：球心镜面反射方向指向相机，因此覆盖判定取投影包围盒内的最大权重；不支持材质取中心点。
- `minSmoothness=1` 的 “全 miss” 状态仍会让 coat roughness 0 的像素通过 `>` 判定（会改变 3,411 像素），因此 miss 回归使用实际零权重像素与 probe 图像逐点比较。
- Terrain Lit 用 shader + 平面验证材质契约；运行时创建的 Terrain 对象不被手动 `Camera.Render` 循环绘制，不能作为权重证据。
- APV 在 URP 14.0.12 没有 ProbeVolume 集成，支持矩阵标为不支持；lightmap/SH/mixed lighting 回归通过。
- 仅 Compatibility Execute；XR 逐眼矩阵仍留到 T13。

## T10 证据与复现

- 结论：[T10Report.json](validation/T10/T10Report.json)（`t10ExitCriteriaMet=true`）
- 渲染/生命周期：[T10Verification.json](validation/T10/T10Verification.json)（三路径 `passed=true`）
- EditMode：`validation/T10/UnityTests.log`（142 passed / 0 failed）
- 图：`validation/T10/T10_<Mode>_{Reference,OpaqueOnly,TransparentOn,TransparentIntensity0,TransparentOffAgain,SsrWeight,SsrTransparentWeight,SsrTransparentRaw,SsrTransparentWeightPbr,TransparentDepth,CameraDepth,CameraDepthWithTransparentPrepass}.png`
- 资产：`Assets/Tests/ScreenSpaceLighting/Materials/T10Transparent{Receiver,NoReceiver,SimpleLit,ComplexLit}.mat`、`Renderers/SslT10{Forward,ForwardPlus,Deferred}{Renderer,Pipeline}.asset`

复现命令（先关闭其他 Unity 编辑器；当前证据为 Run3，下次复验请递增 Run 名）：

```bash
powershell.exe -NoProfile -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT10Verification.CreateT10Assets -LogPath "PALN/validation/T10/UnityT10Compile.Run3.log" -ReportPath "PALN/validation/T10/UnityT10Compile.Run3SettingsRestoration.json" -Quit

powershell.exe -NoProfile -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT10Verification.RunT10Verification -LogPath "PALN/validation/T10/UnityT10Verification.Run3.log" -ReportPath "PALN/validation/T10/UnityT10Verification.Run3SettingsRestoration.json" -Quit

powershell.exe -NoProfile -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PALN/validation/T10/UnityTests.log" -ReportPath "PALN/validation/T10/UnityTestsSettingsRestoration.json"

python "PALN/validation/T10/Analyze-T10Evidence.py"
```

T10 事实与限制：

- 材质契约：透明 Lit/ComplexLit 通过 Inspector 的 `Receive Transparent SSR` 开关（`_TransparentSsrReceive` → `_TRANSPARENT_SSR_RECEIVE`）opt-in；未 opt-in 的片段在 447 prepass 中 `clip`，因此不会成为其他透明物体的 ray origin。SimpleLit/Particles/Terrain 无该 LightMode，继续写正常 alpha，weight 恒 0。
- 深度语义：447 只写私有 depth/normal；私有 depth 先用 URP CopyDepth 从 `_CameraDepthTexture` 写入 `_OUTPUT_DEPTH`，再以 `ZWrite On`/`CommonTransparent` 绘制 opted-in 表面。`CameraDepth` 调试视图在三路径下运行前后逐像素一致（diff=0），主透明 pass 未被改写。
- 顺序证明：透明 SSR 只在 `prepass.producedFrameSerial == history.frameSerial` 时 dispatch；该串行号每次相机渲染递增，若 447 未在本帧先执行则 449 不产生任何输出。验证中 PBR 模式 4 帧得到 trace/reproject 4/4，说明三路径的 447→449→450 顺序成立。本轮未另采 Frame Debugger（T4–T7 的事件证据和 EditMode 事件断言保留）；T12 组合回归可补。
- 输出隔离：opaque SSR 使用 `ssrHitPoint/ssrOutput`，透明使用 `ssrTransparentHitPoint/ssrTransparentOutput`；透明开启与关闭时 opaque `SsrWeight` 逐像素一致（diff=0）。
- 算法：Transparent SSR 固定 Approximation，`usedAlgorithm=PBRAccumulation` 时仍为 4/4 trace+reproject、accumulation dispatch=0、opaque accumulation kernel=4。透明不使用任何跨帧累积历史，只有“上一帧 color pyramid”这一输入。
- 权重/回退：intensity=0 与关闭后逐字节恢复；opt-out 材质和 SimpleLit 区域在透明开启前后 0 像素变化、weight 0；ComplexLit clear coat 透明球改变 1,266 像素、weight 1.0。
- 透明 motion：URP 14.0.12 不把透明 object motion 写入 `_MotionVectorTexture`。移动测试证明 ray origin 每帧跟随当前几何（旧像素回落到 opaque 0.003819、新位置 0.006146、无 NaN），但 reprojection 只使用 hit pixel / camera motion；移动透明体反射可能滞后，属已知限制。
- 天空：透明反射关闭 sky 命中（`_SsrReflectsSky=0`，与 HDRP 一致），miss 时材质保留 reflection probe。
- ShaderGraph Lit 透明 prepass/receive 尚未实现，继续回退 probe；与 T11 折射的 ShaderGraph 改造一起处理。
- 透明 prepass 使用 Renderer Data 的 `transparentLayerMask`（与内置透明 pass 一致），而不是相机 cullingMask；私有 depth/normal 目标恒为单采样，MSAA 相机组合在 T12 验证。
- 仅 Compatibility `ScriptableRenderPass.Execute`；XR 单视图矩阵，逐眼矩阵留到 T13。私有透明资源（depth/normal/hit point/output）按相机分配，1280×720 约 20 MB。

## T8 证据与复现

- 结论：[T8Report.json](validation/T8/T8Report.json)（`t8ExitCriteriaMet=true`）
- 渲染/生命周期：[T8Verification.json](validation/T8/T8Verification.json)（三路径 `passed=true`）
- EditMode：`validation/T8/UnityTests.log`（126 passed / 0 failed）
- 图：`validation/T8/T8_<Mode>_{Reference,Approximation,PbrDefault,MovingTargetOnly,MovingRejectAll,DebugSpeedRejectAll,DebugSpeedNoReject}.png`
- 说明：18 个 Accumulate kernel 在 Frame Debugger 中同属 “Screen Space Reflections” sampler，无法按 kernel 名区分；kernel 选择由 `s_EditorAccumulateKernelCounts` 逐组合证据代替，不再单独采集 T8 Frame Debugger。

复现命令（先关闭其他 Unity 编辑器）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT8Verification.CreateT8Assets -LogPath "PALN/validation/T8/UnityT8Compile.log" -ReportPath "PALN/validation/T8/UnityT8CompileSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT8Verification.RunT8Verification -LogPath "PALN/validation/T8/UnityT8Verification.Run6.log" -ReportPath "PALN/validation/T8/UnityT8Verification.Run6SettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PALN/validation/T8/UnityTests.log" -ReportPath "PALN/validation/T8/UnityTestsSettingsRestoration.json"

python "PALN/validation/T8/Analyze-T8Evidence.py"
```

T8 事实与限制：

- 结果契约：PBR 累积的是非预乘 radiance + weight（`result = lerp(prev, original, coefficient)`），材质端仍是 T7 的 `lerp(probe, radiance, weight)`；HDRP 累积预乘 radiance，这是有意差异。
- Kernel 选择：`enableWorldSpeedRejection` 与 `speedSmoothReject` 决定 NoWorld / Hard / Smooth 三组，`speedSurfaceOnly`/`speedTargetOnly` 决定 Both/SourceOnly/TargetOnly；两者都关闭时回落 Both（HDRP 为 `#error`）。
- World speed 移植自 HDRP 的 `mul(UNITY_MATRIX_I_VP, float4(motion, depth, 0))` 重建，静态场景下基线会饱和归一化速度，因此行为测试用屏幕空间 kernel；world kernel 由组合计数与硬拒绝调试图覆盖。
- 调试视图：`SsrAccumulationSpeed` 输出拒绝决策着色（Hard 红/绿，Smooth 色相）；`SsrPbrParams` 为 CB 诊断视图，不参与材质消费。
- 算法切换按相机历史记录最后算法，任方向切换都清空 PBR 累积并递增 `ssrPbrResetCount`；相机 cut 同样清空。
- 仅 Compatibility `ScriptableRenderPass.Execute`；XR 单视图矩阵，逐眼矩阵留到 T13。

## T7 证据与复现

- 结论：[T7Report.json](validation/T7/T7Report.json)（`t7ExitCriteriaMet=true`）
- 渲染/生命周期：[T7Verification.json](validation/T7/T7Verification.json)（三路径 `passed=true`）
- Frame Debugger：[FrameDebuggerReport.json](validation/T7/FrameDebugger/FrameDebuggerReport.json)（Forward/Forward+ 76 事件，Deferred 87；`SSL Depth Pyramid → Screen Space Reflections → opaque → SSL Color Pyramids`）
- EditMode：`validation/T7/UnityTests.log`（118 passed / 0 failed）
- 调试图：`validation/T7/T7_<Mode>_{Reference,SsrFullRes,SsrRaw,SsrWeight}.png`

复现命令（先关闭其他 Unity 编辑器）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT7Verification.CreateT7Assets -LogPath "PALN/validation/T7/UnityT7Compile.log" -ReportPath "PALN/validation/T7/UnityT7CompileSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT7Verification.RunT7Verification -LogPath "PALN/validation/T7/UnityT7Verification.Run6.log" -ReportPath "PALN/validation/T7/UnityT7Verification.Run6SettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PALN/validation/T7/UnityTests.log" -ReportPath "PALN/validation/T7/UnityTestsSettingsRestoration.json"

python "PALN/validation/T7/Analyze-T7Evidence.py"
```

Frame Debugger 采集（交互式编辑器，参数必须逐项传入）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'PALN/validation/T1/Invoke-T1Unity.ps1' -ExecuteMethod ScreenSpaceLightingFrameDebuggerCapture.Capture -LogPath 'PALN/validation/T7/FrameDebugger/UnityT7FrameDebugger.log' -ReportPath 'PALN/validation/T7/FrameDebugger/UnityT7FrameDebuggerSettingsRestoration.json' -InteractiveEditor -AdditionalArguments @('--ssl-fd-output','PALN/validation/T7/FrameDebugger','--ssl-fd-names','Forward,ForwardPlus,Deferred','--ssl-fd-pipelines','Assets/Tests/ScreenSpaceLighting/Renderers/SslT7ForwardPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT7ForwardPlusPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT7DeferredPipeline.asset','--ssl-fd-scene','Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity')"
```

T7 事实与限制：

- 权重契约：Reproject 写 `(radiance, weight)` 且 radiance 不预乘；Core `ScreenSpaceLightingApplyIndirectSpecular` 在 `EnvironmentBRDF` 之前 `lerp(env, radiance, weight)`，因此 FGD/BRDF 只乘一次，miss 时 probe/sky 完整保留。干净首帧上 intensity 0.5→1.0 的整图响应比为 2.000、误差 0，证明权重只应用一次。
- Deferred smoothness：SSR 事件 206 早于 GBuffer 210，`cameraNormalsTextureHandle` 返回的 GBuffer2 仍是 DepthNormals prepass 写入的 `0.5 + 0.5 * smoothness`；因此三条路径统一按编码值解码，未使用 GBuffer 覆盖后的 raw smoothness。Deferred 与 Forward 的权重统计逐项一致。
- 颜色历史反馈：上一帧颜色金字塔包含上一帧 SSR 贡献，长时间平均会形成有界反馈；除首帧线性测试外，其余验证使用 32 帧预热后的稳定图像。
- 仅 Approximation；`usedAlgorithm`、双历史、速度拒绝和 PBR accumulation 属 T8。仅 Lit/ComplexLit base layer；ComplexLit coat 仍走 probe（T9）。
- 仅 Compatibility `ScriptableRenderPass.Execute` 路径；RenderGraph 路径未注册。XR 使用单视图矩阵，逐眼矩阵留到 T13。
- SSR 输出按相机分配 1280×720 RGBA16F 约 7.4 MB，hit point 约 7.4 MB；SSGI 资源策略不变。

## T6 证据与复现

- 结论：[T6Report.json](validation/T6/T6Report.json)（`t6ExitCriteriaMet=true`）
- 渲染/生命周期：[T6Verification.json](validation/T6/T6Verification.json)（三路径 `passed=true`）
- Frame Debugger：[FrameDebuggerReport.json](validation/T6/FrameDebugger/FrameDebuggerReport.json)（Forward/Forward+ 74 事件，Deferred 85；`SSL Depth Pyramid → Screen Space Global Illumination → opaque → SSL Color Pyramids`）
- EditMode：`validation/T6/UnityTests.log`（111 passed / 0 failed）
- 调试图：`validation/T6/T6_<Mode>_{SsgiRaw,SsgiDenoised,SsgiHistory,SsgiWeight}.png` 与 `Reference/SsgiFullRes/SsgiHalfRes`

复现命令（先关闭其他 Unity 编辑器）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT6Verification.RunT6Verification -LogPath "PALN/validation/T6/UnityT6Verification.log" -ReportPath "PALN/validation/T6/UnityT6VerificationSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PALN/validation/T6/UnityTests.log" -ReportPath "PALN/validation/T6/UnityTestsSettingsRestoration.json"

python "PALN/validation/T6/Analyze-T6Evidence.py"
```

Frame Debugger 采集（交互式编辑器，参数必须逐项传入）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'PALN/validation/T1/Invoke-T1Unity.ps1' -ExecuteMethod ScreenSpaceLightingFrameDebuggerCapture.Capture -LogPath 'PALN/validation/T6/FrameDebugger/UnityT6FrameDebugger.log' -ReportPath 'PALN/validation/T6/FrameDebugger/UnityT6FrameDebuggerSettingsRestoration.json' -InteractiveEditor -AdditionalArguments @('--ssl-fd-output','PALN/validation/T6/FrameDebugger','--ssl-fd-names','Forward,ForwardPlus,Deferred','--ssl-fd-pipelines','Assets/Tests/ScreenSpaceLighting/Renderers/SslT6ForwardPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT6ForwardPlusPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT6DeferredPipeline.asset','--ssl-fd-scene','Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity')"
```

T6 事实与限制：

- 权重契约：reprojection 输出 `(radiance, weight)`；miss/历史无效写 weight=0，材质 `lerp(bakedGI, radiance, weight)` 保留 baked GI/SH/APV。三路径 791,186 个零权重像素与 SSGI 关闭图像逐像素一致（最大差 1.19e-7）；`intensity=0` 与首帧逐字节恢复基线。
- 三路径 Pass 顺序：`SSL Depth Pyramid (203) → SSGI dispatch (205) → SSL Setup bind (207) → GBuffer (210) / Opaque (250) → Cleanup (300)`。Setup 只写全局纹理/keyword，无 GPU 事件，因此 Frame Debugger 中不出现；`s_EditorSsgiBoundCount` 证明绑定发生。
- 材质端：Core 新增 `ShaderLibrary/ScreenSpaceLighting/{ScreenSpaceLightingInput,ScreenSpaceLightingBlend}.hlsl`，`GlobalIllumination.hlsl` 完整重载和 `LitGBufferPass.hlsl` 消费；`Lit.shader`/`ComplexLit.shader` 增加 `_SCREEN_SPACE_GI` 变体；`ClearRenderingState` 每相机禁用 keyword。仅 Lit/ComplexLit；SimpleLit/Particles/Terrain/ShaderGraph 属 T9。
- 透明物体不接收 SSGI：Cleanup Pass 在 opaque 事件后立即禁用 keyword。折射/透明 SSR 留到 T10/T11。
- 历史验证使用当前深度/法线、上一帧深度、内置 Motion 和位置半径；T4 未保存上一帧法线，因此没有法线历史拒绝；也没有 object-motion stencil 拒绝（T13 前记录为限制）。
- 采样序列使用 IGN + R2 低差异序列替代 HDRP 的 blue-noise set；1 spp/帧、32 帧预热后仍有轻微 stipple，属已知画质限制（T14 可补 blue-noise 纹理）。
- 仅 Compatibility `ScriptableRenderPass.Execute` 路径；RenderGraph 路径未注册。XR 使用单视图矩阵，逐眼矩阵留到 T13。
- 曝光：URP 材质端消费发生在 post exposure 之前，SSGI 读取的颜色金字塔是线性 HDR 场景色（不含 exposure/tonemap），因此不需要 HDRP 的 pre-exposure 补偿；本实现不引入 exposure 控制。
- SSGI 资源按相机分配：1280×720 全分辨率约 79 MB（4 张当帧/降噪 RGBA16F + 4 张 HF/LF 历史 + 4 张 sample count + hitpoint/validation/final）。半分辨率时 trace 相关减半；第二 Pass 与 `ssgiFinal` 目前始终分配，T14 可按设置懒分配。

## T4 证据与复现

- 结论：[T4Report.json](validation/T4/T4Report.json)（`t4ExitCriteriaMet=true`）
- 渲染/生命周期：[T4Verification.json](validation/T4/T4Verification.json)（三路径 `passed=true`）
- Frame Debugger：[FrameDebuggerReport.json](validation/T4/FrameDebugger/FrameDebuggerReport.json)
- EditMode：`validation/T4/UnityTests.log`（85 passed / 0 failed）
- 调试图：`validation/T4/T4_<Mode>_*.png`（深度金字塔 mip0/mip2、当前/上一帧颜色 mip0/mip1、上一帧深度、相机深度、UV scale/limit、尺寸变化、debug-off 基线）

复现命令（先关闭其他 Unity 编辑器）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT4Verification.RunT4Verification -LogPath "PALN/validation/T4/UnityT4Verification.log" -ReportPath "PALN/validation/T4/UnityT4VerificationSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PALN/validation/T4/UnityTests.log" -ReportPath "PALN/validation/T4/UnityTestsSettingsRestoration.json"

python "PALN/validation/T4/Analyze-T4Evidence.py"
```

Frame Debugger 采集（交互式编辑器；参数必须逐项传入，逗号合并会被 `GetArgument` 当成一个参数）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'PALN/validation/T1/Invoke-T1Unity.ps1' -ExecuteMethod ScreenSpaceLightingFrameDebuggerCapture.Capture -LogPath 'PALN/validation/T4/FrameDebugger/UnityT4FrameDebugger.log' -ReportPath 'PALN/validation/T4/FrameDebugger/UnityT4FrameDebuggerSettingsRestoration.json' -InteractiveEditor -AdditionalArguments @('--ssl-fd-output','PALN/validation/T4/FrameDebugger','--ssl-fd-names','Forward,ForwardPlus,Deferred','--ssl-fd-pipelines','Assets/Tests/ScreenSpaceLighting/Renderers/SslT4ForwardPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT4ForwardPlusPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT4DeferredPipeline.asset','--ssl-fd-scene','Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity')"
```

T4 事实与限制：

- 深度金字塔副本 kernel 直接绑定 `UniversalRenderer.m_DepthTexture`。`CommandBuffer.SetGlobalTexture("_CameraDepthTexture")` 的全局在 CPU 侧不可读，`Shader.GetGlobalTexture` 返回 null；只声明全局名在 compute 中也拿不到有效数据。
- 深度金字塔为 `R32_SFloat` 原始设备深度，与 HDRP 单通道布局一致；T3 能力探测从 `R32G32_SFloat` 收紧为 `R32_SFloat`。
- 当前颜色金字塔在 400（opaque+sky 后）生成；上一帧颜色/深度在 501（transparent 后）双缓冲捕获，效果读取槽与当帧写入槽不同。
- URP 的 `resetHistoryFrames` 计数器有上升和下降两个边沿，因此一次手工 reset 可能连续失效两代；这是保守行为，不会复用未初始化历史。
- 仅实现 Compatibility `ScriptableRenderPass.Execute` 路径；RenderGraph 路径未注册这些 Pass。

## T5 证据与复现

- 结论：[T5Report.json](validation/T5/T5Report.json)（`t5ExitCriteriaMet=true`）
- 渲染/生命周期：[T5Verification.json](validation/T5/T5Verification.json)（三路径 `passed=true`）
- Frame Debugger：[FrameDebuggerReport.json](validation/T5/FrameDebugger/FrameDebuggerReport.json)（Forward/Forward+ 82 事件，Deferred 93；GTAO Depth Pyramid → GTAO dispatch → opaque/GBuffer）
- EditMode：`validation/T5/UnityTests.log`（103 passed / 0 failed）
- 调试图：`validation/T5/T5_<Mode>_*.png`（raw/final/global AO、深度金字塔 mip0/mip2、half/full、spatial-only、combined order、尺寸变化）

复现命令（先关闭其他 Unity 编辑器）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT5Verification.RunT5Verification -LogPath "PALN/validation/T5/UnityT5Verification.log" -ReportPath "PALN/validation/T5/UnityT5VerificationSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PALN/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PALN/validation/T5/UnityTests.log" -ReportPath "PALN/validation/T5/UnityTestsSettingsRestoration.json"

python "PALN/validation/T5/Analyze-T5Evidence.py"
```

Frame Debugger 采集（交互式编辑器，参数必须逐项传入）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'PALN/validation/T1/Invoke-T1Unity.ps1' -ExecuteMethod ScreenSpaceLightingFrameDebuggerCapture.Capture -LogPath 'PALN/validation/T5/FrameDebugger/UnityT5FrameDebugger.log' -ReportPath 'PALN/validation/T5/FrameDebugger/UnityT5FrameDebuggerSettingsRestoration.json' -InteractiveEditor -AdditionalArguments @('--ssl-fd-output','PALN/validation/T5/FrameDebugger','--ssl-fd-names','Forward,ForwardPlus,Deferred','--ssl-fd-pipelines','Assets/Tests/ScreenSpaceLighting/Renderers/SslT5ForwardPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT5ForwardPlusPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT5DeferredPipeline.asset','--ssl-fd-scene','Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity')"
```

T5 事实与限制：

- GTAO 拥有自己的 `GtaoDepthPyramid` + offset buffer + 双缓冲 AO 历史；与 SSL 同时开启时 Hi-Z 显存翻倍（1280×720 验证中 `duplicateHiZBytes=11,059,200`），按 T0 资源隔离接受。
- AO 输出为 URP 可见度（1=无遮挡、白色 neutral）；HDRP 的 inverse AO 在 `GTAOOutputFinalAO` 中转成正向。
- Deferred 路径中法线来自 GBuffer2：`UniversalRenderer.cameraNormalsTextureHandle` 统一返回 forward 的 `m_NormalsTexture` 或 deferred 的 GBuffer2 附件；tracing 使用 decal 前材质数据。
- 三路径 GTAO 主 pass 均在 204（GBuffer 210 / opaque 250 之前）；深度金字塔 203，Motion 202；Frame Debugger 已逐事件确认。
- URP 内置 SSAO 已按用户要求移除（2026-11-14），包括其 Feature、Editor、Shader、SSAO 专用 keyword 和 BlueNoise256 资源；GTAO 是唯一 AO 生产者，继续写入 `_SCREEN_SPACE_OCCLUSION`/`_ScreenSpaceOcclusionTexture`/`_AmbientOcclusionParam`。旧 Renderer Data 中若仍序列化 SSAO Feature，会出现 Missing Script，需手动删除后改用 GTAO。
- 构建期 AO keyword 预过滤固定为 `Select`（ON/OFF 都保留）；插件级 strip 优化留到 T14。`ScriptableRenderer.GetRendererFeature<T>()` 随冲突仲裁一并删除。
- 验证场景必须调用 `DynamicGI.UpdateEnvironment()`，否则 skybox 环境探针为黑，AO 只能通过 `directLightingStrength>0` 才可见；T5 验证使用默认 `directLightingStrength=0` 直接验证 indirect AO。
- 仅支持 Compatibility `ScriptableRenderPass.Execute`；RenderGraph 路径未注册。XR single-pass 复用单视图矩阵，逐眼矩阵留到 T13。

## T3 证据与复现

- 结论：`validation/T3/T3Report.json`（`t3ExitCriteriaMet=true`）
- 渲染/生命周期验证：`validation/T3/T3Verification.json`（三路径 `passed=true`；Volume 开启时 SSL/GTAO 均入队，Feature 关闭时不入队；空效果与 T1 基线逐像素一致；history 计数 1→2→1）
- 真实 UI 路径：`validation/T3/UI/T3UiEvidence.json` 与 13 张 `T3_UI_01..13`（Renderer List → Add Renderer Feature → SSL/GTAO → Volume Add Override → Camera Volume Mask）
- 无插件回归：`validation/T3/FrameDebuggerNoPlugin/`（三路径 `eventDataHash` 与 `validation/T1/FrameDebugger/FrameDebuggerReport.json` 相同）
- EditMode 测试：`validation/T3/UnityTests.log`（48 passed / 0 failed）

复现命令（先关闭其他 Unity 编辑器；UI 采集必须使用交互式编辑器并保持窗口可见）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PALN/validation/T1/Invoke-T1Unity.ps1" \
  -ExecuteMethod ScreenSpaceLightingT3UiEvidence.RunEvidence \
  -LogPath "PALN/validation/T3/UnityUiEvidence.Run.log" \
  -ReportPath "PALN/validation/T3/UnityUiEvidenceSettingsRestoration.json" \
  -InteractiveEditor

python "PALN/validation/T3/Analyze-T3Evidence.py"
```

`ScreenSpaceLightingT3UiEvidence` 走真实编辑器入口（`ScriptableRendererDataEditor.AddComponent`、`VolumeComponentListEditor.AddComponent` 与对应 FilterWindow 下拉），截图用 `PrintWindow` 合成；Popup 窗口按原生窗口类合成在最上层。

## T2 证据与复现

- 结论：`validation/T2/T2Verification.json`（三路径 `passed=true`）
- Frame Debugger（带探针）：`validation/T2/FrameDebugger/FrameDebuggerReport.json`；Forward/Forward+ 11 事件，Deferred 22 事件，顺序为 `DepthNormalPrepass → [Deferred: CopyDepth] → MotionVectors(202) → 探针(203) → GBuffer/Opaque → 天空盒/Blit`
- 无插件不变性：`validation/T2/FrameDebugger/WithoutProbe/`（6/6/16 事件，与 `validation/T1/FrameDebugger/FrameDebuggerReport.json` 的 `eventDataHash` 逐路径相同）
- EditMode 调度测试：`validation/T2/UnityTests.log`（13 passed / 0 failed）
- 采集迭代保留：`FrameDebugger/{DiagnosticRun,ProbeNotEnqueued,WithoutProbe}/`、`FrameDebuggerError.BatchmodeDefaultT1.txt` 及各次 Unity 日志

复现注意：

- Frame Debugger 采集必须使用**交互式编辑器**（`BatchMode: 0`）且 Unity 窗口位于前台、未被遮挡；窗口被遮挡时编辑器 tick 会停，采集超时。
- 探针 Feature 只有在材质与 target 均存在时才入队；`ScreenSpaceLightingFrameDebuggerCapture` 会自行加载 `SslProbe.mat`、创建 `SslT2Probe` 并启用探针。
- `Invoke-T1Unity.ps1` 现在只等待编辑器进程退出，不再被 `Unity.Licensing.Client`、Hub helper 等孤儿进程阻塞；退出码与设置恢复报告不变。

### 重建命令

在项目根目录执行；包装器会等待 Unity 退出并恢复四项项目设置。下次 T3 复验请使用递增 Run 名，不得覆盖已有运行：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PALN/validation/T1/Invoke-T1Unity.ps1" \
  -ExecuteMethod ScreenSpaceLightingBaselineSetup.BuildBaselinePlayer \
  -LogPath "PALN/validation/T1/UnityBuild.Run17-Run18.log" \
  -ReportPath "PALN/validation/T1/BuildSettingsRestoration.Run17-Run18.json" \
  -Quit
```

### 采集与分析命令

Player 重建后依次执行下列命令；两轮不得并发，Run 名必须递增：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PALN/validation/T1/Invoke-T1Run.ps1" -RunName Run17

powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PALN/validation/T1/Invoke-T1Run.ps1" -RunName Run18

python "PALN/validation/T1/Analyze-T1Evidence.py" --runs Run17 Run18
```

`Invoke-T1Run.ps1` 会记录 NVML 时钟采样到运行目录，仅作 GPU 参考数据，不参与判定。

当前固定协议：Unity `2022.3.62f1`、URP `14.0.12`、StandaloneWindows64 Development Player、DX11、1280×720、种子 `14012`、每路径预热 60 帧、采样 1800 帧、60 FPS、vSync 关闭。

### T1 验收门槛（R3，已满足）

- 构建 `Succeeded`、0 errors，Unity 退出码 0；构建和 Frame Debugger 的设置恢复报告均通过。
- 两轮报告均满足固定协议，三路径截图有效、无洋红，CPU/GPU 时间与 graphics-driver 显存计数均已采集。
- 三路径 PNG 一致、显存一致，所有 **CPU** p95 波动不超过 10%；GPU p95 仅记录和报告，不作为通过条件（理由见 `PerformanceBudget.json` 的 `gpuTimingPolicy`）。
- `GoldenImages` 三张 1280×720 PNG 及 `.meta` 齐全。
- `T1Report.json` 最终为 `t1ExitCriteriaMet=true`。
- 证据核对完成后删除 `PALN/validation/T1/Player/`，保留工具、日志、运行目录和报告。

## 证据入口

- 最新 T1 结论：[T1Report.json](validation/T1/T1Report.json)
- 两轮比较：[Reproducibility.json](validation/T1/Reproducibility.json)
- 原始计时：`validation/T1/Run15/RawFrameTimes.csv`、`validation/T1/Run16/RawFrameTimes.csv`
- GPU 参考数据：`validation/T1/Run15/GpuClocks.csv`、`validation/T1/Run16/GpuClocks.csv`
- T2 结论：[T2Verification.json](validation/T2/T2Verification.json)
- T2 Frame Debugger：[FrameDebuggerReport.json](validation/T2/FrameDebugger/FrameDebuggerReport.json)
- T3 结论：[T3Report.json](validation/T3/T3Report.json)
- T4 结论：[T4Report.json](validation/T4/T4Report.json)
- T4 渲染/生命周期：[T4Verification.json](validation/T4/T4Verification.json)
- T4 Frame Debugger：[FrameDebuggerReport.json](validation/T4/FrameDebugger/FrameDebuggerReport.json)
- T5 结论：[T5Report.json](validation/T5/T5Report.json)
- T5 渲染/生命周期：[T5Verification.json](validation/T5/T5Verification.json)
- T5 Frame Debugger：[FrameDebuggerReport.json](validation/T5/FrameDebugger/FrameDebuggerReport.json)
- T6 结论：[T6Report.json](validation/T6/T6Report.json)
- T6 渲染/生命周期：[T6Verification.json](validation/T6/T6Verification.json)
- T6 Frame Debugger：[FrameDebuggerReport.json](validation/T6/FrameDebugger/FrameDebuggerReport.json)
- T7 结论：[T7Report.json](validation/T7/T7Report.json)
- T7 渲染/生命周期：[T7Verification.json](validation/T7/T7Verification.json)
- T7 Frame Debugger：[FrameDebuggerReport.json](validation/T7/FrameDebugger/FrameDebuggerReport.json)
- T8 结论：[T8Report.json](validation/T8/T8Report.json)
- T8 渲染/生命周期：[T8Verification.json](validation/T8/T8Verification.json)
- T9 结论：[T9Report.json](validation/T9/T9Report.json)
- T9 渲染/生命周期：[T9Verification.json](validation/T9/T9Verification.json)
- T10 结论：[T10Report.json](validation/T10/T10Report.json)
- T10 渲染/生命周期：[T10Verification.json](validation/T10/T10Verification.json)
- T3 渲染/生命周期：[T3Verification.json](validation/T3/T3Verification.json)
- T3 真实 UI 路径：[T3UiEvidence.json](validation/T3/UI/T3UiEvidence.json)
- T3 无插件回归：`validation/T3/FrameDebuggerNoPlugin/`
- DVFS 诊断：[Diagnostics/Dvfs-2026-11-14/README.md](validation/T1/Diagnostics/Dvfs-2026-11-14/README.md)
- T1 Frame Debugger：[FrameDebuggerReport.json](validation/T1/FrameDebugger/FrameDebuggerReport.json)
- 构建设置恢复：[BuildSettingsRestoration.json](validation/T1/BuildSettingsRestoration.json)
- Frame Debugger 设置恢复：[FrameDebuggerSettingsRestoration.json](validation/T1/FrameDebuggerSettingsRestoration.json)
- 量化预算：[PerformanceBudget.json](validation/T1/PerformanceBudget.json)
