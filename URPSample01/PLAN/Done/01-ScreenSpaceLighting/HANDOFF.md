# ScreenSpaceLighting 交接

> 更新：2026-11-15（T14 退出；同日补 SSR 反射修复与 GTAO 条带修复）；2026-11-17 记入 06-ValidationAllRDC 进行中。架构理由、边界和完整任务依赖见 [实施计划](HDRP%20ScreenSpaceLighting%20迁移到%20URP%2014.0.12%20实施计划.md)；本文件只维护下一步。

## 当前位置

- **T0 已冻结**。任何边界变更都必须先重新打开 T0，并同步 PLAN 与本文件。
- **T1 已按 R3 协议退出（2026-11-14）**，并在 T2 Core 改动后由 **Run15 / Run16** 复验：三路径 PNG 逐字节一致、graphics-driver 显存一致、CPU p95 波动 **0.28% / 0.01% / 0.04%**，构建、Frame Debugger 与设置恢复全部通过。`T1Report.json` 当前指向 Run17/Run18（后续复验）；Run13–Run16 作为历史有效运行对保留。
- **T2 已退出（2026-11-14）**。Core 已提供 DepthNormals smoothness 编码、最早 Motion/Depth 调度和调度单元测试；无插件时三路径 Frame Debugger 事件数据与 T1 基线逐事件一致，探针在 203 时 Motion/Depth 已有效，TAA/Motion Blur 早/晚 Motion 输出逐字节一致。
- **T3 已退出（2026-11-14）**。两个空 Renderer Feature（SSL / GTAO）、Volume 组件、能力检测、neutral 输出和私有 history 骨架已落地；真实 UI 路径配置、Volume 控队、空效果逐像素一致、无插件回归和 EditMode 测试全部通过，唯一事实入口为 `validation/T3/T3Report.json`（`t3ExitCriteriaMet=true`）。
- **T4 已退出（2026-11-14）**。SSL 私有 packed Hi-Z（mip/offset/UV scale）、当前帧 Gaussian 颜色金字塔、双缓冲 previous color/depth、reset generation 和 7 个调试视图已落地；三路径下卷开启时 Pass 均执行且最终图像与 T1 基线逐字节一致，首帧/相机 cut/尺寸/render scale 重置、同帧读写槽分离、双相机隔离和 GTAO 缺失时 SSL 独立工作全部通过，Frame Debugger 证明 203 → 400 → 501 顺序。唯一事实入口为 `validation/T4/T4Report.json`（`t4ExitCriteriaMet=true`）。当前仍没有任何 GTAO、SSGI、SSR 或 Refraction 渲染效果实现完成。
- **T5 已退出（2026-11-14）**。独立 GTAO 全 kernel（Main / Spatial / Temporal / CopyHistory / Blur+Upsample）、私有 packed Hi-Z、双缓冲 AO 历史、调试视图和 SSAO 式 Inspector 已落地；Forward/Forward+/Deferred 三路径 AO 可见，SSL 特征顺序任意组合图像逐字节一致，intensity=0 与 Feature 关闭恢复 T1 基线逐字节一致。**同日按用户要求移除 URP 内置 SSAO（像素着色器实现）**：GTAO 成为唯一 AO 生产者，材质侧 `_SCREEN_SPACE_OCCLUSION` 契约保留。唯一事实入口为 `validation/T5/T5Report.json`（`t5ExitCriteriaMet=true`）。SSGI、SSR、Refraction 仍未实现。
- **T6 已退出（2026-11-14）**。SSGI Trace/Reproject、TemporalFilter validation/accumulation、DiffuseDenoiser 必要支路、半分辨率 Bilateral Upsample、HF/LF 历史与相机 reset 已落地；材质端 `_SCREEN_SPACE_GI` 混合在 Forward/Forward+/Deferred 统一消费。三路径 `T6Verification.json` 全部 `passed=true`：miss 像素与 baked GI 逐像素一致（最大通道差 1.19e-7）、首帧与 intensity=0 逐字节恢复基线、半分辨率 upsample 真实派发、调试图与最终消费纹理一致（921,600 像素中 19 像素差异、最大 0.0012）、发光立方体自身亮度比 0.991（未重复叠加 emission）、相机 reset 计数 2。Frame Debugger 顺序为 Depth Pyramid → Screen Space Global Illumination → opaque，三路径一致。EditMode 111 passed / 0 failed。唯一事实入口为 `validation/T6/T6Report.json`（`t6ExitCriteriaMet=true`）。SSR、Transparent SSR 与 Refraction 仍未实现。
- **T7 已退出（2026-11-14）**。Opaque SSR Approximation（Trace → Reproject，RGBA16F radiance + weight）、roughness/edge fade、屏外/非有限/负值拒绝和 probe/sky 回退已落地；三路径 `T7Verification.json` 全部 `passed=true`：miss 像素与 probe 基线逐像素一致（670,152 像素，最大通道差 0）、intensity=0 与首帧逐字节恢复基线、金属粗糙度权重梯度单调（0.444/0.350/0.198/0，低于 minSmoothness 为 0）、非金属光滑球权重 0.410、干净首帧的双倍 intensity 响应比 2.000（误差 0）、调试图与消费纹理 921,600 像素 0 差异、无 NaN/Inf/负值。Frame Debugger 顺序为 Depth Pyramid → Screen Space Reflections → opaque，三路径一致。EditMode 118 passed / 0 failed。唯一事实入口为 `validation/T7/T7Report.json`（`t7ExitCriteriaMet=true`）。上述权重均值是 `2b38091` 射线原点修复前的快照；当前验收以 `SsrRayOriginFix` 的 maxWeight vs fade 为准。SSR PBR Accumulation、Transparent SSR 与 Refraction 仍未实现。
- **T8 已退出（2026-11-14）**。SSR PBR Accumulation（GGX VNDF trace、3×3 cross reproject、双 RGBA16F 累积历史）落地；`usedAlgorithm` 在 Approximation/PBR 间切换并 reset，9 个公开速度拒绝组合各自命中独立 kernel（NoWorld/Hard/Smooth × Both/Source/Target），surface/target 在动态场景产生 226,681 像素差异、speed 参数 0↔1 产生 232,951 像素差异，硬拒绝调试图 100% 红、接受调试图 100% 绿，算法切换 reset 2 次、相机 cut reset 2 次。PBR 仍遵守 T7 权重契约：miss 的 670,222 像素与 probe 基线逐像素一致、首帧与 intensity=0 逐字节恢复基线、无 NaN/Inf/负值。三路径 `T8Verification.json` 全部 `passed=true`，EditMode 126 passed / 0 failed。唯一事实入口为 `validation/T8/T8Report.json`（`t8ExitCriteriaMet=true`）。**T8 已被后续轮次替代**：18 个累积 kernel 与速度拒绝在 02-SSRImprove 中被 HDRP `ReflectionDenoiser` 替换（`4d7697f`/`ed4ed8b`），本证据仅作历史归档。Transparent SSR 与 Refraction 仍未实现。
- **T9 已退出（2026-11-14）**。Lit、ComplexLit、ShaderGraph Lit（Forward + GBuffer）全部接入 SSGI/SSR；ComplexLit 的 clear coat 在 DepthNormals 写入 coat SSR smoothness（mask>0 时 alpha=1.0），材质端 base/coat 使用独立 mask/roughness 与 HDRP 式 hierarchy weight，base smoothness 0.3 的 coat 球 trace weight 仍为 1.0。三路径 `T9Verification.json` 全部 `passed=true`：intensity=0 逐字节恢复 probe 基线、572,746 个零权重像素与 probe 图像逐像素一致（最大差 0）、coat smoothness 1↔0.2 改变 270,919 像素（Deferred 270,957）、coat mask 1↔0 改变 271,300 像素（Deferred 271,339）；SimpleLit/Particles/Terrain Lit 的 depth normals alpha 与 SSR weight 均为 0；alpha test 实体/裁剪像素 weight 1.0/0；ForceLOD 0/1 均覆盖；SG Lit 的 SSR/SSGI 均生效。小场景 lightmap（Mixed + Subtractive）回归通过；APV 因 URP 14.0.12 无 ProbeVolume 集成标为不支持。EditMode 132 passed / 0 failed。唯一事实入口为 `validation/T9/T9Report.json`（`t9ExitCriteriaMet=true`）。Transparent SSR 与 Refraction 仍未实现。
- **T10 已退出（2026-11-15）**。Core 新增 `UniversalTransparentDepthNormals` LightMode 共享实现（`Shaders/Utils/TransparentDepthNormalsPass.hlsl`）并接入 Lit/ComplexLit；透明材质通过 `_TransparentSsrReceive` 属性/`_TRANSPARENT_SSR_RECEIVE` 关键字显式 opt-in，未 opt-in 的片段在 prepass 中 `clip`。SSL 在 447 用 CopyDepth 把 opaque 深度复制到私有 depth 并从该深度起绘制透明 depth/normal，449 完成 Transparent SSR Trace/Reproject（独立 hit point/output、`_SCREEN_SPACE_TRANSPARENT_SSR`、Approximation-only）并在 450 前绑定；主透明 pass 的深度语义未改。Volume 新增 `enabledTransparent`。三路径 `T10Verification.json` 全部 `passed=true`：intensity=0 与关闭后逐字节恢复、opaque weight 与 camera depth 差异 0、receiver 改变 785 像素/weight 1.0、opt-out 与 SimpleLit 改变 0 像素/weight 0、ComplexLit clear coat 透明 1,266 像素/weight 1.0、transparent depth 0.006142 > opaque 0.003819（9,560 像素不同）、双层最近面胜出（near-only=both=0.006007 > far 0.004822）、PBR 下 transparent 4/4 dispatch 且 accumulation 0、移动透明体 ray origin 跟随且无 NaN。EditMode 142 passed / 0 failed。唯一事实入口为 `validation/T10/T10Report.json`（`t10ExitCriteriaMet=true`）。Refraction 仍未实现。
- **T11 已退出（2026-11-15）**。Core 新增折射材质契约：`_RefractionModel`（None 默认，Box/Sphere/Thin）、`_Ior`、`_Thickness`、`_TransmittanceColor`、`_TransmittanceDistance`、`_REFRACTION_PLANE/SPHERE/THIN` 与 `_SCREEN_SPACE_REFRACTION`，Lit/ComplexLit GUI 与 ShaderGraph Lit 共用同一组属性/关键字；`ShaderLibrary/ScreenSpaceLighting/ScreenSpaceRefraction.hlsl` 移植 HDRP proxy raycast、深度拒绝、absorption、edge fade 与 (1-FGD) 透射。SSL 在 448 绑定 400 生成的当前帧 color pyramid、opaque depth 与最近的 box-projected probe AABB；Volume 新增 `screenFadeDistance`/`intensity`（默认关闭）。ShaderGraph Lit 补上 T10 遗留的 `UniversalTransparentDepthNormals` Pass、`_TRANSPARENT_SSR_RECEIVE` 和 `_SCREEN_SPACE_TRANSPARENT_SSR` 消费。三路径 `T11Verification.json` 全部 `passed=true`：legacy 透明 0 像素变化、模型 None/intensity=0/关闭相位逐像素恢复（1e-6）、无 box probe 时逐像素回退、box/complex/sphere/SG 分别改变 5,915/2,996/1,233/3,092 像素、SG 透明 prepass 改变 543 像素、红色吸收红通道比 0.363、背景橙→蓝时玻璃均值 (1.502,0.867,0.319)→(0.274,0.438,1.577)、透明面板开关时玻璃圆内与 color pyramid 均 0 像素变化。EditMode 149 passed / 0 failed。唯一事实入口为 `validation/T11/T11Report.json`（`t11ExitCriteriaMet=true`）。
- **T12 已退出（2026-11-15）**。三渲染器各 34 个组合用例（Deferred 35，含实际 Forward 回退）全部通过：Forward/Forward+/Deferred、Deferred + ForwardOnly ComplexLit（5,214 px）、Deferred 实际 Forward 回退（`GL.wireframe` 触发 `renderingModeActual`）、SSL/GTAO 单开与组合、GTAO 前置顺序（0 px 差异）、Volume 缺失组件/overrideState=off/双 Volume 混合/相机 Volume Mask、render scale 与目标尺寸重置（reset 计数 2/2）、camera cut（2）、SSR 算法切换（3）、Depth Priming、Opaque Texture、TAA、Motion Blur、DBuffer/ScreenSpace Decal（70,054/64,017 px）、HDR/MSAA（Forward/F+ MSAA 生效，Deferred 兼容路径忽略 MSAA）、Base+Overlay 相机栈（效果可见 541,499 px，overlay 由能力检测拒绝）、Preview/Reflection 相机、SimpleLit 回退（0 px）。Frame Debugger 三路径 94/94/105 事件，顺序 `MotionVectors → SSL Depth Pyramid → GTAO Depth Pyramid → SSGI/SSR → DrawOpaque/GBuffer`，Pre-GBuffer 未进入 AfterGbuffer 回退；全部 SSL 全局 keyword 在相机清理后关闭。私有资源 1280×720（base level）：SSL 98,611,832 B、GTAO 9,216,120 B、SSL packed Hi-Z 5,529,600 B（12 mip，1 次 copy + 11 次 downsample），颜色金字塔 9 mip，每帧 1 次透明 prepass。EditMode 153 passed / 0 failed。唯一事实入口为 `validation/T12/T12Report.json`（`t12ExitCriteriaMet=true`）。
- **T13 已退出（2026-11-15）**。**XR 逐眼矩阵与 slice**：SSL compute 与 GTAO 的 world→view 行改为按 XR view 索引读取，单视图（non-XR / multipass）走原有 `unity_MatrixVP`/CB 路径；SSGI/SSR/透明 SSR 的相机位置、上一帧逆 VP 均按眼记录在 history entry 中，`CameraHistoryStore` 继续按 (camera, multipassId) 分键。修复了一个 **T4 遗留的 shader 宏错误**：`ScreenSpaceLightingCommon.hlsl` 的 XR 分支守卫写成 `SSGI_XR_ARRAY`，而实际 keyword 是 `SSL_XR_ARRAY`，导致 single-pass 的 compute 实际绑定 2D 纹理（dispatch 被跳过、hit point 全 0）。Mock HMD 播放器三 API（DX11/DX12/Vulkan）双模式 `T13XrPlayerVerification.json` 全部 `passed=true`：两眼镜像差 924,469–1,060,399 px、AllOff 恢复参考、SSGI 两眼 214k–232k px、SSR 两眼 6.7k–6.8k px、折射两眼 6.9k–7.0k px、GTAO 两眼 45k–82k px；single-pass 历史 1 entry × 2 slices（Tex2DArray）、深度金字塔 2 slices、上一帧颜色金字塔两 slice 哈希不同；multipass 历史 2 entries × 1 slice、两眼下级 prev 矩阵均已记录。**Foveation**：DX12/Vulkan `foveatedRenderingCaps=FoveationImage`，全屏 raster/prepass 通过 `SetFoveatedRenderingMode(Disabled)` 守卫（DX11 caps=None，不适用）；图像级 foveation 质量未比较。**动态分辨率**：URP 14.0.12 不驱动 `DynamicResolutionHandler`，`ScalableBufferManager` 只改变呈现结果、不缩放 RTHandle 相机目标（DX11 `IntermediateTargetsNotRescaled`；DX12/Vulkan readback 无效），按引擎限制记录为不支持，不以删测试换取通过；静态 render scale 由 T12 覆盖。T12 全组合回归（34/34/35）与 EditMode 153 passed / 0 failed 均未回退。唯一事实入口为 `validation/T13/T13Report.json`（`t13ExitCriteriaMet=true`），未实测平台（Metal/mobile/OpenGLES/真实 HMD）在支持矩阵中明确标记。T14 未开始。
- **T14 已退出（2026-11-15）**。发布级回归与归档完成：EditMode 153/153、PlayMode 6/6、T12 组合矩阵回放 34/34/35 并复现全部 103 张 T12 图像哈希、Frame Debugger 94/94/105 事件顺序不变、Release Player（无 Development/TestAssemblies）0 errors 且 22 个必需 ComputeShader/Shader 全部在包内、三 API 双 XR 模式播放器全部通过。支持矩阵、已知差异、性能/显存与归档清单见 `validation/T14/`；T1 与 T14 临时 Player、`Assets/Settings` 中 SSAO 残留已清理。唯一事实入口为 `validation/T14/T14Report.json`（`t14ExitCriteriaMet=true`）。
- **GPU p95 为参考数据**。本机 DVFS 与 GeForce/WDDM 不执行 `nvidia-smi -lgc` 导致 720p GPU p95 无法重复；证据与理由见 [Environment.json](validation/T1/Environment.json) 和 `validation/T1/Diagnostics/Dvfs-2026-11-14/`。10% 容差、场景、预算数值均未变，Run1–Run16 与无效运行全部保留。
- **T1 与 T14 临时 Player 已清理**；原始运行目录、日志、CSV、PNG 和报告均保留。四项 ProjectSettings 已恢复原始 SHA-256。

唯一事实入口：[T1Report.json](validation/T1/T1Report.json)（`t1ExitCriteriaMet=true`）、[T2Verification.json](validation/T2/T2Verification.json)（`passed=true`）、[T5Report.json](validation/T5/T5Report.json)（`t5ExitCriteriaMet=true`）、[T6Report.json](validation/T6/T6Report.json)（`t6ExitCriteriaMet=true`）、[T9Report.json](validation/T9/T9Report.json)（`t9ExitCriteriaMet=true`）、[T10Report.json](validation/T10/T10Report.json)（`t10ExitCriteriaMet=true`）、[T11Report.json](validation/T11/T11Report.json)（`t11ExitCriteriaMet=true`）、[T12Report.json](validation/T12/T12Report.json)（`t12ExitCriteriaMet=true`）、[T13Report.json](validation/T13/T13Report.json)（`t13ExitCriteriaMet=true`）与 [T14Report.json](validation/T14/T14Report.json)（`t14ExitCriteriaMet=true`）。

## 当前位置：T14 已退出，发布回归完成

- EditMode 153 passed / 0 failed、PlayMode 6/6、T12 组合矩阵回放 34/34/35 全部通过，103 张图像与 T12 记录哈希逐字节一致。
- Frame Debugger 重新采集：Forward/Forward+ 94 事件、Deferred 105 事件，顺序为 `MotionVectors(7/7/9) → SSL Depth Pyramid(9/9/10) → GTAO Depth Pyramid(20/20/21) → SSGI(36/36/37) → SSR(43/43/44) → DrawOpaque/GBuffer(45/45/46)`，与 T12 一致。
- Release Player（`BuildOptions.None`，不含 Development/TestAssemblies）构建成功、0 errors，22 个必需 ComputeShader/Shader 资产全部在包内；DX11/DX12/Vulkan 三 API 双 XR 模式全部 `passed=true`。
- 支持矩阵、已知差异、性能/显存与归档清单已生成；T1 与 T14 临时 Player 目录已删除。
- 四个模板/测试 pipeline 资产上被构建期 shader prefiltering 改写的字段已还原；`URP-HighFidelity-Renderer` 与 `URP-Balanced-Renderer` 中 T5 删除 SSAO 后残留的 SSAO Feature 已移除（这正是此前每次构建日志里 `missing RendererFeatures` 报错的来源）。
- **06-ValidationAllRDC 已完成（2026-11-17）**：P0–P4 已完成并留证，P5 差异文档已完成，P6 完成（EditMode 455/455、PlayMode 6/6、T12 回放 34/34/35、1280×720 CPU p95 与显存复测；10× RT 计数与 XR 按 Accepted）。四分类差异（A 引擎能力 / B URP 架构 / C 有意适配 / D 缺陷）与 F-01..F-12 状态见 [KnownDifferences.md](../../Done/06-ValidationAllRDC/KnownDifferences.md)；任务表、证据索引与退出标准见 [06 HANDOFF](../../Done/06-ValidationAllRDC/HANDOFF.md)，回归证据见 [P6/Report.md](../../Done/06-ValidationAllRDC/validation/P6/Report.md)。T14 的“重复 Hi-Z 显存”条目已作废（GTAO 与 SSL 共享 packed Hi-Z），支持矩阵已补 06 覆盖。

## 后续缺陷修复（2026-11-15）：GTAO 竖向条带

用户报告开启 GTAO 后地面出现满屏竖向黑色条带（与 quality 设置无关，仅 intensity=0 时消失）。
逐阶段 dump 证明 GTAO 主 trace 输出正确，但空间双边降噪与上采样用 `GatherRed`+UV 取邻居时只保留
了 X 坐标，导致 AO 沿 Y 被抹成竖条；URP 兼容路径的 compute 没有绑定该 sampler。

修复：空间降噪与双边上采样改为整数像素 `Load`，去掉依赖 groupshared smart blur 的上采样前置模糊
（空间降噪已平滑），并让非 temporal 半分辨率路径也走空间降噪、上采样重新绑定私有深度金字塔。
`T12Verification` 的 `UnsupportedMaterial` 用例在两次捕获中都关闭 GTAO（SimpleLit 本来就应该接收
GTAO，该用例只验证 SSL 权重为 0）。

验证：EditMode 153/153、PlayMode 6/6、T12 组合矩阵 34/34/35 通过；GTAO 关闭的帧与归档图像逐像素
一致，只有 GTAO 开启的用例按设计改变。证据见 `validation/GtaoBandingFix/Report.md`。
未复跑：XR single-pass / Vulkan / DX12 立体玩家构建。

## 后续缺陷修复（2026-11-15）：SSR 反射不可见

用户报告开启 SSR 后看不到正确反射：地面没有反射，Frame Debugger 里 hit point 有命中但 SSR 输出接近全黑。
batchmode 复现（T3 场景 + 临时镜面地面）证实：修复前镜面地面的 `_SsrOutputTextureRW` alpha=1 而
rgb≈全黑，SSR 开/关的最终图均值只差 4e-4；上一帧颜色金字塔内容正常（天空均值 0.61/0.83/0.97），
排除深度金字塔与颜色金字塔。

根因：SSR trace/reproject 复用了共享的 `SsgiReconstructPositionWS()`，该 helper 读 `_SsgiScreenSize`
（SSGI 常量缓冲字段），而 `ScreenSpaceReflections.compute` 只收到 `_Ssr*` 参数，该字段恒为 0 →
`uv = (positionSS + 0.5) * 0`，每个像素都用屏幕左下角的世界坐标求视线与反射方向，命中错误几何且辐射近黑。
Frame Debugger 的 SSR Vectors 列表中没有 `_SsgiScreenSize` 可直接印证。HDRP 在 SSR shader 内用自身的
`_ScreenSize` 重建世界坐标，因此不存在该问题。

修复：`ScreenSpaceReflections.compute` 新增 `SsrReconstructPositionWS()`（按 `_SsrScreenSize` 缩放、
`SSL_INV_VIEW_PROJ`），trace 与 PBR reproject 的 3 处调用切换；同时修正 PBR 累积 kernel 把 hit NDC
先除以颜色金字塔 limit（无缩放时=1）再转像素的问题，速度拒绝的 motion/depth 取样点此前落在错误像素。

验收测试同步：T7 的 roughness fade 证据改用“像素权重上限 vs 解析 fade”（修正 trace 后球体的屏内命中
比例随位置变化，mean 不再单调）；SSL/GTAO history 测试去掉提交 `770777a` 已移走的 depth pyramid 归属
断言，并在批处理下初始化 `RTHandles`。

验证：T7 三路径 3/3（上限 1.0000/0.8301/0.3289/0 vs fade 1.0000/0.8438/0.3164/0）、T8 三路径 3/3、
EditMode 154 passed / 0 failed。未复跑 T10/T12/T13。证据见 `validation/SsrRayOriginFix/Report.md`。

## 后续改进（2026-11-16）：SSR 空洞填充与拖动残影分析

**空洞填充**：用户报告光滑地面上的反射出现单像素空洞。Approximation 复投影此前是单次采样，未命中即
weight 0；现在未命中的像素会用 HDRP PBR 复投影的同一 3×3 cross 邻域填充，但只接受“同一表面”（世界
位置差 < 1% 视距、法线 dot ≥ 0.9）、自身可参与 SSR（roughness ≤ fade end）且有有效命中的邻居；填充
权重 = 接受邻居的 opacity / 4，因此四邻全命中才完全覆盖、全 miss 区域仍回落到 probe。有效像素的锐利
反射不受影响，T7 的权重最大值与解析 fade 仍逐项一致（1.0000/0.8301/0.3289/0），rough metal 仍为 0。
T7 rig 上新增覆盖 2,920 像素、无丢失像素；三个金属球 mean weight 上升约 16%。T7/T8 三路径 3/3 通过。
证据见 `validation/SsrHoleFilling/Report.md`。

**拖动残影**：逐帧（跨 editor tick 推进 `Time.frameCount`）的隔离测试证明 motion vector 有效（3°/帧
偏航 → 平均前向位移 0.037 UV），SSR 没有自己的历史，停止拖动后**第一帧**就与收敛图逐像素一致（0 像素
差异）；剩下的只有“上一帧颜色金字塔”的 1 帧滞后，HDRP 的 `ColorBufferMipChain` 也是同一结构，因此
属结构性而非移植缺陷。放大因素来自颜色金字塔反馈（捕获的图像已包含上一帧 SSR 结果，weight≈1 的镜面
会形成反射链），相机移动会打断这条链。唯一会残留衰减的是 SSGI（0.93 累积；纯旋转时历史位置校验仍
判为有效，数帧内回收），HDRP 的校验规则相同，端口按计划省略了历史法线比较与 object-motion stencil
拒绝。可选缓解：切到 PBR 累积（HDRP 的 artifact 管理路径，速度拒绝会在移动时丢弃历史），或为
Approximation 增加按 motion 的 mip bias / 权重衰减（非 HDRP 行为，需要产品决策）。证据见
`validation/SsrGhostAnalysis/Report.md`。

## T14 证据与复现

- 结论：[T14Report.json](validation/T14/T14Report.json)（`t14ExitCriteriaMet=true`，九项 criteria 全为 true）
- 支持矩阵：[T14SupportMatrix.md](validation/T14/T14SupportMatrix.md)
- 已知差异：[T14KnownDifferences.md](validation/T14/T14KnownDifferences.md)
- 性能/显存：[T14Performance.json](validation/T14/T14Performance.json)（T1 基线 CPU p95 与 graphics-driver 显存、1280×720 私有资源字节）
- 归档清单：[T14ArchiveManifest.json](validation/T14/T14ArchiveManifest.json)（排除 Player 本体）
- 图形回归：[T14/Graphics/T12Verification.json](validation/T14/Graphics/T12Verification.json) 与 103 张 PNG
- Frame Debugger：[T14/FrameDebugger/FrameDebuggerReport.json](validation/T14/FrameDebugger/FrameDebuggerReport.json)
- PlayMode：`validation/T14/UnityPlayModeTests.xml`（6 passed / 0 failed）
- EditMode：`validation/T14/UnityEditModeTests.xml`（153 passed / 0 failed）
- 构建：[T14PlayerBuildSummary.json](validation/T14/T14PlayerBuildSummary.json)（Succeeded，0 errors，163,703,618 B，2093 packed assets）；`UnityT14PlayerBuild.Run3.log` 约 27 MB，按 T13 `Diagnostics/*.log` 的先例只保留在本地，由构建命令重新生成
- Player 运行：`validation/T14/Runs/{DX11,DX12,Vulkan}/T13XrPlayerVerification.json` 与 14 张/轮眼睛 PNG
- 复跑历史：`UnityPlayModeTests.Run1..5`、`UnityEditModeTests.Run1IsolationFailure/Run2`、`UnityT14FrameDebugger.Run1Aborted/Run2` 全部保留；Player 构建的三次运行中只保留最终 `UnityT14PlayerBuild.Run3.log`（Run1/Run2 日志各约 27 MB，内容为同一命令的迭代构建，摘要已被 Run3 覆盖，设置恢复报告仍保留）

复现命令（先关闭其他 Unity 编辑器）：

```bash
powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14EditModeTests.ps1" -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T14/UnityEditModeTests.Run2.log" -ResultsPath "PLAN/Done/01-ScreenSpaceLighting/validation/T14/UnityEditModeTests.xml"
powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14PlayModeTests.ps1" -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T14/UnityPlayModeTests.Run5.log" -ResultsPath "PLAN/Done/01-ScreenSpaceLighting/validation/T14/UnityPlayModeTests.xml"
powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14GraphicsRegression.ps1" -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Graphics/UnityT14GraphicsRegression.log" -OutputRoot "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Graphics"
powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14FrameDebugger.ps1" -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T14/FrameDebugger/UnityT14FrameDebugger.Run2.log" -OutputRoot "PLAN/Done/01-ScreenSpaceLighting/validation/T14/FrameDebugger"
powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT14PlayerSetup.BuildReleasePlayer -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T14/UnityT14PlayerBuild.Run3.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T14/UnityT14PlayerBuild.Run3SettingsRestoration.json" -Quit

"PLAN/Done/01-ScreenSpaceLighting/validation/T14/Player/SslT14Player.exe" -ssl-t13-out "<abs>/PLAN/Done/01-ScreenSpaceLighting/validation/T14/Runs/DX11"
"PLAN/Done/01-ScreenSpaceLighting/validation/T14/Player/SslT14Player.exe" -force-d3d12 -ssl-t13-out "<abs>/PLAN/Done/01-ScreenSpaceLighting/validation/T14/Runs/DX12"
"PLAN/Done/01-ScreenSpaceLighting/validation/T14/Player/SslT14Player.exe" -force-vulkan -ssl-t13-out "<abs>/PLAN/Done/01-ScreenSpaceLighting/validation/T14/Runs/Vulkan"

python "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Analyze-T14Evidence.py"
```

T14 事实与限制：

- **PlayMode 必须走 `-runTests`**：`TestRunnerApi` + `-executeMethod` + `-quit` 的 EditMode 路径在本环境曾出现“运行在下一个编辑器 tick 才开始、quit 先到”的空跑；T14 两个套件都由 CLI `-runTests` 驱动并输出 NUnit XML。`Invoke-T1Unity.ps1` 的 `-ExecuteMethod` 因此改为可选，既有命令不受影响。
- **PlayMode 测试用反射读取插件内部**：`PluginIsolationTests` 禁止 Core 源码点名插件类型，所以没有给 Runtime 加 `InternalsVisibleTo`；测试覆盖三 Renderer 的 dispatch/history、运行中关闭 Feature、移除 Volume 组件、第二相机 entry 与销毁回收。
- **Frame Debugger 需要交互式编辑器**：第一次采集时 Unity 自行重启了编辑器（新进程无 `-executeMethod`），报告未生成；关闭重启后的编辑器再跑 Run2 才成功。Run1 日志保留为 `UnityT14FrameDebugger.Run1Aborted.log`。
- **Vulkan release player 不会自行退出**：报告、日志与 14 张 PNG 写完后进程仍存活，由工具终止，因此 Vulkan 的退出码不参与判定（DX11/DX12 退出码 0）；该现象在 T13 的 Vulkan 轮次同样存在。
- **release 构建的 stripping 审计**：`ScreenSpaceLightingT14PlayerSetup` 额外生成 `ScreenSpaceLightingT14Stripping.unity`（引用 ComplexLit/SimpleLit/ShaderGraph 材质，并为其创建 `SslT9LitT14.mat`/`SslT9ComplexLitT14.mat`），否则这些变体不进包。插件自身没有 build-time strip 优化，四条 `_SCREEN_SPACE_*` 关键字变体在每次构建中保留，已在已知差异中记录；未在 T14 引入该优化，因为它改变发布行为且需要独立的正确性周期。
- **检查基准未动**：T13Report 记录的 5 个必需文件哈希仍一致；`T1..T13` 证据目录在分析时无未提交改动（唯一例外是被 T14 扩展、但未改证据的 `Invoke-T1Unity.ps1`）。
- **清理范围**：删除 `PLAN/Done/01-ScreenSpaceLighting/validation/T1/Player/` 与 `PLAN/Done/01-ScreenSpaceLighting/validation/T14/Player/`；`Assets/Settings` 下的 SSAO Feature 残留（T5 删除 SSAO 后遗留的 Missing Script）已移除；`Assets/XR` 与 Mock HMD 设置保留，供 XR 播放器复现。

## T13 证据与复现

- 结论：[T13Report.json](validation/T13/T13Report.json)（`t13ExitCriteriaMet=true`）
- XR 播放器（DX11/DX12/Vulkan，multipass + single-pass）：`validation/T13/Runs/{DX11,DX12,Vulkan}/T13XrPlayerVerification.json` 与 14 张/轮眼睛 PNG
- 编辑器能力与动态分辨率：`validation/T13/T13EditorVerification.{DX11,DX12,Vulkan}.json`
- EditMode：`validation/T13/UnityTests.log`（153 passed / 0 failed）
- T12 回归：`validation/T13/T12RegressionVerification.json`（34/34/35 用例全部通过）与 `validation/T13/UnityT12Regression.Run1.log`
- 构建：`validation/T13/T13PlayerBuildSummary.json`（StandaloneWindows64，DX11/DX12/Vulkan 三 API）；播放器本体（约 190 MB）在证据归档后已删除，`SslT13XrPlayer.exe` 由构建命令重新生成

复现命令（先关闭其他 Unity 编辑器）：

```bash
powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT13PlayerSetup.BuildPlayer -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T13/Diagnostics/UnityT13PlayerBuild.Run26.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T13/Diagnostics/UnityT13PlayerBuild.Run26SettingsRestoration.json" -Quit

"PLAN/Done/01-ScreenSpaceLighting/validation/T13/Player/SslT13XrPlayer.exe" -ssl-t13-out "<abs>/PLAN/Done/01-ScreenSpaceLighting/validation/T13/Runs/DX11"
"PLAN/Done/01-ScreenSpaceLighting/validation/T13/Player/SslT13XrPlayer.exe" -force-d3d12 -ssl-t13-out "<abs>/PLAN/Done/01-ScreenSpaceLighting/validation/T13/Runs/DX12"
"PLAN/Done/01-ScreenSpaceLighting/validation/T13/Player/SslT13XrPlayer.exe" -force-vulkan -ssl-t13-out "<abs>/PLAN/Done/01-ScreenSpaceLighting/validation/T13/Runs/Vulkan"

powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT13Verification.RunEditorVerification -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T13/UnityT13EditorVerification.DX11.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T13/UnityT13EditorVerification.DX11SettingsRestoration.json" -AdditionalArguments "-force-d3d11" -Quit
# DX12 / Vulkan 同样命令，替换 -force-* 与日志名；随后 cp T13EditorVerification.json 到对应 API 文件名

python "PLAN/Done/01-ScreenSpaceLighting/validation/T13/Analyze-T13Evidence.py"
```

T13 事实与限制：

- **XR 测试环境**：`com.unity.xr.management@4.5.3` + `com.unity.xr.mock-hmd@1.5.0-exp.3`（以及自动解析的 `com.unity.xr.core-utils`、`com.unity.xr.legacyinputhelpers` 与 `com.unity.inputsystem@1.7.0`）。`Assets/XR` 中 Mock HMD loader 的 “Initialize XR on Startup” 关闭，只有 T13 播放器显式启动显示，其他验证仍走单视图。
- **Mock HMD 限制**：只有独立播放器会创建 XR render pass；编辑器批处理（edit mode 与 play mode）`GetRenderPassCount()` 恒为 0，因此 XR 证据必须由 `SslT13XrPlayer.exe` 采集。ear 纹理分辨率沿用 Mock HMD 默认 1512×1680。
- **逐眼矩阵**：single-pass 的 `_SslXrViewProj/_SslXrInvViewProj/_SslXrPrevInvViewProj/_SslXrCameraPositionWS` 由 C# 通过 `SetGlobal*Array` 推入；shader 以 dispatch slice 选择。上一帧矩阵按眼存入 `ScreenSpaceLightingHistory`（non-XR 仍沿用 motion vector 历史）。修复了宏名不匹配后 single-pass compute 才实际生效。
- **历史隔离**：multipass 每眼一个 entry；single-pass 一个 entry、2 slices。XR 布局（multipass ↔ single-pass）切换时清空 store，避免残留的“另一只眼”entry。
- **几何/模式切换**：single-pass 与 multipass 的顺序切换在同一播放器内验证；切回后参考图恢复。
- **Foveation**：DX11 caps=None；DX12/Vulkan caps=FoveationImage。颜色金字塔、颜色历史捕获、透明 depth/normal prepass、调试视图在全屏 raster 期间关闭 foveated rendering（compute 不受影响）。未做图像级 foveation 质量对比。
- **动态分辨率**：`ScalableBufferManager` 在本环境中不能缩放 URP 的 RTHandle 相机目标；DX11 记录 `IntermediateTargetsNotRescaled`，DX12/Vulkan readback 无效。SSL 历史保持全分辨率并通过 `SrcScaleBias` 重采样缩放源；该子项按引擎限制记录为不支持。静态 render scale 由 T12 覆盖。
- **未实测平台**：Metal、Android/OpenGLES、macOS/iOS 与真实 HMD 均未测试，支持矩阵标 NotTested，不计入通过。
- **T12 回归**：T12 验证器固定写回 `validation/T12/T12Verification.json`，回归运行会覆盖该文件；本轮结果另存 `T12RegressionVerification.json`，提交时不把被覆盖的 T12 证据纳入变更。

## T12 证据与复现

- 结论：[T12Report.json](validation/T12/T12Report.json)（`t12ExitCriteriaMet=true`）
- 组合矩阵：[T12Verification.json](validation/T12/T12Verification.json)（三路径全部用例通过）
- Frame Debugger：[FrameDebuggerReport.json](validation/T12/FrameDebugger/FrameDebuggerReport.json)（Forward/Forward+ 94 事件，Deferred 105；Motion → SSL Depth Pyramid → GTAO Depth Pyramid → SSGI/SSR → opaque/GBuffer）
- EditMode：`validation/T12/UnityTests.log`（153 passed / 0 failed）
- 资产：`Assets/Tests/ScreenSpaceLighting/Renderers/SslT12{Forward,ForwardPlus,Deferred}{Renderer,Pipeline}.asset`
- 图：`validation/T12/T12_<Mode>_<Case>.png`（Reference/AllOn/AllOffAgain/GtaoFirst/DepthPrimingForced/OpaqueTexture/TemporalAA/MotionBlur/DecalDBuffer/DecalScreenSpace/MSAA4x/DeferredFallbackForward 等）

复现命令（先关闭其他 Unity 编辑器；Frame Debugger 必须使用交互式编辑器并保持窗口前台）：

```bash
powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT12Verification.CreateT12Assets -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T12/UnityT12Compile.Run3.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T12/UnityT12Compile.Run3SettingsRestoration.json" -Quit

powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT12Verification.RunT12Verification -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T12/UnityT12Verification.Run8.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T12/UnityT12Verification.Run8SettingsRestoration.json" -Quit

powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T12/UnityTests.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T12/UnityTestsSettingsRestoration.json"

python "PLAN/Done/01-ScreenSpaceLighting/validation/T12/Analyze-T12Evidence.py"
```

Frame Debugger 采集：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1' -ExecuteMethod ScreenSpaceLightingFrameDebuggerCapture.Capture -LogPath 'PLAN/Done/01-ScreenSpaceLighting/validation/T12/FrameDebugger/UnityT12FrameDebugger.Run2.log' -ReportPath 'PLAN/Done/01-ScreenSpaceLighting/validation/T12/FrameDebugger/UnityT12FrameDebugger.Run2SettingsRestoration.json' -InteractiveEditor -AdditionalArguments @('--ssl-fd-output','PLAN/Done/01-ScreenSpaceLighting/validation/T12/FrameDebugger','--ssl-fd-names','Forward,ForwardPlus,Deferred','--ssl-fd-pipelines','Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardPlusPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT12DeferredPipeline.asset','--ssl-fd-scene','Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity')"
```

T12 事实与限制：

- **Deferred MSAA**：URP 14 兼容路径的 Deferred 渲染器忽略 MSAA（Deferred 的 `MSAA4x` 与 `AllOn` 图像逐字节一致；Forward/Forward+ 则不同）；这是 URP 行为，不是 SSL 缺陷，支持矩阵中按“Deferred 不承诺 MSAA”记录。
- **相机栈**：ScreenSpaceCapability 拒绝 Overlay 相机；Base 相机在栈中仍输出效果。URP 栈路径会改变中间色彩精度，因此“加 overlay 前后”不做逐字节比较，只验证效果可见、无 NaN/Inf。
- **Post Processing / Opaque Texture**：PP 与 Opaque Texture 会改变 URP 的中间色彩管线/格式，因此仅验证效果在对应路径下可见且有限，不宣称与关闭路径逐字节一致。
- **Deferred 实际回退**：用 `GL.wireframe` 触发 `renderingModeActual=Forward` 的编辑期确定性用例；回退路径下效果有限、无错误。
- **Decal**：DBuffer 与 ScreenSpace 两种技术均渲染成功；Deferred 的 SSGI/SSR 仍使用 DepthNormals prepass（decal 前材质数据），属于 T0/计划记录的已知差异。
- **成本**：`sslPrivateBytes`/`gtaoPrivateBytes` 为 base-level RTHandle + ComputeBuffer 字节和；颜色金字塔 mip 链未乘 4/3，Hi-Z 为 packed atlas 实际字节（5,529,600 B）。透明 prepass 每帧 1 次；GTAO 与 SSL 共享 packed Hi-Z（`770777a`），T5 的“重复 Hi-Z 显存”记录已作废（06 已同步）。
- Scene View 相机无法在 batchmode 中可靠采样，标为未测试；Preview/Reflection 相机由能力检测拒绝并验证无异常。XR/platform 逐眼矩阵仍留 T13。

## T11 证据与复现

- 结论：[T11Report.json](validation/T11/T11Report.json)（`t11ExitCriteriaMet=true`）
- 渲染/生命周期：[T11Verification.json](validation/T11/T11Verification.json)（三路径 `passed=true`）
- EditMode：`validation/T11/UnityTests.log`（149 passed / 0 failed）
- 图：`validation/T11/T11_<Mode>_{Reference,RefractionOn,ModelsNone,Intensity0,OffAgain,NoProxyFallback,Tinted,BackdropMoved,TransparentPanelOff,TransparentPanelOn,CurrentColorPyramidPanelOff,CurrentColorPyramidPanelOn,ShaderGraphTransparentSsr}.png`
- 资产：`Assets/Tests/ScreenSpaceLighting/Materials/T11Refraction{Box,Sphere,Thin,None,Tint,ComplexLit,ShaderGraph}.mat`、`T11LegacyTransparent.mat`、`Shaders/SslT11Lit.shadergraph`、`Renderers/SslT11{Forward,ForwardPlus,Deferred}{Renderer,Pipeline}.asset`

复现命令（先关闭其他 Unity 编辑器；下次复验请递增 Run 名）：

```bash
powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT11Verification.CreateT11Assets -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T11/UnityT11Compile.Run5.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T11/UnityT11Compile.Run5SettingsRestoration.json" -Quit

powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT11Verification.RunT11Verification -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T11/UnityT11Verification.Run8.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T11/UnityT11Verification.Run8SettingsRestoration.json" -Quit

powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T11/UnityTests.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T11/UnityTestsSettingsRestoration.json"

python "PLAN/Done/01-ScreenSpaceLighting/validation/T11/Analyze-T11Evidence.py"
```

T11 事实与限制：

- 材质契约：`_RefractionModel` 默认 None，关键字只在透明材质上设置；`_Ior` 与 `_Thickness`/`_TransmittanceDistance` 位于 LitInput 的 `UnityPerMaterial` CBUFFER（SRP Batcher 布局稳定）。ShaderGraph Lit 由 target 生成同名属性，材质 Inspector 的 Refraction 区与内置 Lit 共用 `LitGUI` 文案。
- Proxy：URP 没有 HDRP 的 per-material proxy volume。首选**最近的 box-projected 反射探针 AABB**；没有 box-projected 探针时退化为**任意 active 探针的 bounds**（HDRP 默认探针用 influence volume 作为 proxy，与此映射一致）；仍没有探针时使用 **far-projection 代理（URP 扩展，65504）**。HDRP 无 env light 时的默认折射代理是相机中心、由 renderer bounds 定半径的球，投影通常夹到 0；65504 只是其 sky cubemap 的 `minProjectionDistance`。运行时探针在本验证中未出现在 `CullingResults.visibleReflectionProbes`（计数 0），pass 回退到 30 帧缓存一次的 `FindObjectsOfType<ReflectionProbe>`。代理选择仍是相机级近似，不是 HDRP 的 per-object/per-pixel env light；旋转探针只取 AABB，Forward+ 的逐对象 probe atlas 不参与 proxy 选择。
- 当前帧 pyramid：折射读取 400 生成的 opaque+sky 颜色金字塔，不读取本帧 transparents；透明面板开关时玻璃圆内 0 像素变化、`CurrentColorPyramid` 调试视图 0 像素变化。因此 HDRP 的 pre-refraction transparent 顺序不迁移，作为已知差异记录。
- 深度拒绝：移植 HDRP 的 `hitLinearDepth > surfaceLinearDepth` 规则，前景遮挡时退回表面自身 UV；opaque depth 由 448 显式绑定，不依赖 `_CameraDepthTexture` 全局。
- Alpha 语义（05 轮 P0-1/P0-2 修正）：与 HDRP 一致，折射材质的 opacity 是 transmittance mask。`(1 - alpha)` 只在 diffuse/emission 桶与透射之间 lerp，specular 桶不参与 mask（2026-11-16 复核修复了此前把 diffuse+specular 一起 lerp 的偏差），折射成立时输出 alpha 固定为 1。屏幕 miss 时不再直接为 0，而是继续用反射探针/sky cubemap 沿折射方向补齐屏幕路径未消费的权重；无探针场景使用 URP 的 far-projection 代理（URP 扩展）。IOR 在效果开启时进入 F0（`IorToFresnel0`），同时影响反射与 `(1-F)` 透射；Thin 模型的吸收距离与厚度都固定为 0.005。
- SG：`UniversalTransparentDepthNormals` 复用 `DepthNormalsOnlyPass.hlsl`（`URP_TRANSPARENT_DEPTH_NORMALS` 定义切换为 opt-in clip），Forward 变体新增 `_SCREEN_SPACE_TRANSPARENT_SSR`，T10 遗留的 SG 透明 prepass/receive 已补齐；SG 折射属性由 target `CollectShaderProperties` 生成，材质端与内置 Lit 共用 `ScreenSpaceRefraction.hlsl`。
- 仅 forward 透明路径；Deferred 的 GBuffer 不参与折射（透明仍走 forward）。SimpleLit/Particles/Terrain 没有该契约，继续走 probe。
- 仅 Compatibility `ScriptableRenderPass.Execute`；XR 单视图矩阵、逐眼矩阵留到 T13；MSAA、Depth Priming、Opaque Texture、TAA/Motion Blur 组合留到 T12。

## T9 证据与复现

- 结论：[T9Report.json](validation/T9/T9Report.json)（`t9ExitCriteriaMet=true`）
- 渲染/生命周期：[T9Verification.json](validation/T9/T9Verification.json)（三路径 `passed=true`）
- EditMode：`validation/T9/UnityTests.log`（132 passed / 0 failed）
- 图：`validation/T9/T9_<Mode>_{Reference,CoatIntensity0,CoatMiss,CoatHits,CoatMaskOff,CoatRoughness,AlphaTestWeight,Lod1,ShaderGraphSsr,SsrWeight,NormalsAlpha}.png` 与 `T9_Lightmap_<Mode>.png`
- 资产：`Assets/Tests/ScreenSpaceLighting/Shaders/SslT9{Lit,ComplexLit}.shadergraph`、`Renderers/SslT9*`、`Scenes/ScreenSpaceLightingT9Lightmap.unity`（含烘焙 lightmap）

复现命令（先关闭其他 Unity 编辑器）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT9Verification.CreateT9Assets -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T9/UnityT9Compile.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T9/UnityT9CompileSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT9Verification.RunT9Verification -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T9/UnityT9Verification.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T9/UnityT9VerificationSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T9/UnityTests.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T9/UnityTestsSettingsRestoration.json"

python "PLAN/Done/01-ScreenSpaceLighting/validation/T9/Analyze-T9Evidence.py"
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
powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT10Verification.CreateT10Assets -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T10/UnityT10Compile.Run3.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T10/UnityT10Compile.Run3SettingsRestoration.json" -Quit

powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT10Verification.RunT10Verification -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T10/UnityT10Verification.Run3.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T10/UnityT10Verification.Run3SettingsRestoration.json" -Quit

powershell.exe -NoProfile -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T10/UnityTests.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T10/UnityTestsSettingsRestoration.json"

python "PLAN/Done/01-ScreenSpaceLighting/validation/T10/Analyze-T10Evidence.py"
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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT8Verification.CreateT8Assets -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T8/UnityT8Compile.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T8/UnityT8CompileSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT8Verification.RunT8Verification -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T8/UnityT8Verification.Run6.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T8/UnityT8Verification.Run6SettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T8/UnityTests.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T8/UnityTestsSettingsRestoration.json"

python "PLAN/Done/01-ScreenSpaceLighting/validation/T8/Analyze-T8Evidence.py"
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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT7Verification.CreateT7Assets -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T7/UnityT7Compile.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T7/UnityT7CompileSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT7Verification.RunT7Verification -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T7/UnityT7Verification.Run6.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T7/UnityT7Verification.Run6SettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T7/UnityTests.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T7/UnityTestsSettingsRestoration.json"

python "PLAN/Done/01-ScreenSpaceLighting/validation/T7/Analyze-T7Evidence.py"
```

Frame Debugger 采集（交互式编辑器，参数必须逐项传入）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1' -ExecuteMethod ScreenSpaceLightingFrameDebuggerCapture.Capture -LogPath 'PLAN/Done/01-ScreenSpaceLighting/validation/T7/FrameDebugger/UnityT7FrameDebugger.log' -ReportPath 'PLAN/Done/01-ScreenSpaceLighting/validation/T7/FrameDebugger/UnityT7FrameDebuggerSettingsRestoration.json' -InteractiveEditor -AdditionalArguments @('--ssl-fd-output','PLAN/Done/01-ScreenSpaceLighting/validation/T7/FrameDebugger','--ssl-fd-names','Forward,ForwardPlus,Deferred','--ssl-fd-pipelines','Assets/Tests/ScreenSpaceLighting/Renderers/SslT7ForwardPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT7ForwardPlusPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT7DeferredPipeline.asset','--ssl-fd-scene','Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity')"
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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT6Verification.RunT6Verification -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T6/UnityT6Verification.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T6/UnityT6VerificationSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T6/UnityTests.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T6/UnityTestsSettingsRestoration.json"

python "PLAN/Done/01-ScreenSpaceLighting/validation/T6/Analyze-T6Evidence.py"
```

Frame Debugger 采集（交互式编辑器，参数必须逐项传入）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1' -ExecuteMethod ScreenSpaceLightingFrameDebuggerCapture.Capture -LogPath 'PLAN/Done/01-ScreenSpaceLighting/validation/T6/FrameDebugger/UnityT6FrameDebugger.log' -ReportPath 'PLAN/Done/01-ScreenSpaceLighting/validation/T6/FrameDebugger/UnityT6FrameDebuggerSettingsRestoration.json' -InteractiveEditor -AdditionalArguments @('--ssl-fd-output','PLAN/Done/01-ScreenSpaceLighting/validation/T6/FrameDebugger','--ssl-fd-names','Forward,ForwardPlus,Deferred','--ssl-fd-pipelines','Assets/Tests/ScreenSpaceLighting/Renderers/SslT6ForwardPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT6ForwardPlusPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT6DeferredPipeline.asset','--ssl-fd-scene','Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity')"
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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT4Verification.RunT4Verification -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T4/UnityT4Verification.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T4/UnityT4VerificationSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T4/UnityTests.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T4/UnityTestsSettingsRestoration.json"

python "PLAN/Done/01-ScreenSpaceLighting/validation/T4/Analyze-T4Evidence.py"
```

Frame Debugger 采集（交互式编辑器；参数必须逐项传入，逗号合并会被 `GetArgument` 当成一个参数）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1' -ExecuteMethod ScreenSpaceLightingFrameDebuggerCapture.Capture -LogPath 'PLAN/Done/01-ScreenSpaceLighting/validation/T4/FrameDebugger/UnityT4FrameDebugger.log' -ReportPath 'PLAN/Done/01-ScreenSpaceLighting/validation/T4/FrameDebugger/UnityT4FrameDebuggerSettingsRestoration.json' -InteractiveEditor -AdditionalArguments @('--ssl-fd-output','PLAN/Done/01-ScreenSpaceLighting/validation/T4/FrameDebugger','--ssl-fd-names','Forward,ForwardPlus,Deferred','--ssl-fd-pipelines','Assets/Tests/ScreenSpaceLighting/Renderers/SslT4ForwardPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT4ForwardPlusPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT4DeferredPipeline.asset','--ssl-fd-scene','Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity')"
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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingT5Verification.RunT5Verification -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T5/UnityT5Verification.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T5/UnityT5VerificationSettingsRestoration.json" -Quit

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" -ExecuteMethod ScreenSpaceLightingTestRunner.RunEditModeTests -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T5/UnityTests.log" -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T5/UnityTestsSettingsRestoration.json"

python "PLAN/Done/01-ScreenSpaceLighting/validation/T5/Analyze-T5Evidence.py"
```

Frame Debugger 采集（交互式编辑器，参数必须逐项传入）：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1' -ExecuteMethod ScreenSpaceLightingFrameDebuggerCapture.Capture -LogPath 'PLAN/Done/01-ScreenSpaceLighting/validation/T5/FrameDebugger/UnityT5FrameDebugger.log' -ReportPath 'PLAN/Done/01-ScreenSpaceLighting/validation/T5/FrameDebugger/UnityT5FrameDebuggerSettingsRestoration.json' -InteractiveEditor -AdditionalArguments @('--ssl-fd-output','PLAN/Done/01-ScreenSpaceLighting/validation/T5/FrameDebugger','--ssl-fd-names','Forward,ForwardPlus,Deferred','--ssl-fd-pipelines','Assets/Tests/ScreenSpaceLighting/Renderers/SslT5ForwardPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT5ForwardPlusPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT5DeferredPipeline.asset','--ssl-fd-scene','Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity')"
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
  -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" \
  -ExecuteMethod ScreenSpaceLightingT3UiEvidence.RunEvidence \
  -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T3/UnityUiEvidence.Run.log" \
  -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T3/UnityUiEvidenceSettingsRestoration.json" \
  -InteractiveEditor

python "PLAN/Done/01-ScreenSpaceLighting/validation/T3/Analyze-T3Evidence.py"
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
  -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" \
  -ExecuteMethod ScreenSpaceLightingBaselineSetup.BuildBaselinePlayer \
  -LogPath "PLAN/Done/01-ScreenSpaceLighting/validation/T1/UnityBuild.Run17-Run18.log" \
  -ReportPath "PLAN/Done/01-ScreenSpaceLighting/validation/T1/BuildSettingsRestoration.Run17-Run18.json" \
  -Quit
```

### 采集与分析命令

Player 重建后依次执行下列命令；两轮不得并发，Run 名必须递增：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Run.ps1" -RunName Run17

powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Run.ps1" -RunName Run18

python "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Analyze-T1Evidence.py" --runs Run17 Run18
```

`Invoke-T1Run.ps1` 会记录 NVML 时钟采样到运行目录，仅作 GPU 参考数据，不参与判定。

当前固定协议：Unity `2022.3.62f1`、URP `14.0.12`、StandaloneWindows64 Development Player、DX11、1280×720、种子 `14012`、每路径预热 60 帧、采样 1800 帧、60 FPS、vSync 关闭。

### T1 验收门槛（R3，已满足）

- 构建 `Succeeded`、0 errors，Unity 退出码 0；构建和 Frame Debugger 的设置恢复报告均通过。
- 两轮报告均满足固定协议，三路径截图有效、无洋红，CPU/GPU 时间与 graphics-driver 显存计数均已采集。
- 三路径 PNG 一致、显存一致，所有 **CPU** p95 波动不超过 10%；GPU p95 仅记录和报告，不作为通过条件（理由见 `PerformanceBudget.json` 的 `gpuTimingPolicy`）。
- `GoldenImages` 三张 1280×720 PNG 及 `.meta` 齐全。
- `T1Report.json` 最终为 `t1ExitCriteriaMet=true`。
- 证据核对完成后删除 `PLAN/Done/01-ScreenSpaceLighting/validation/T1/Player/`，保留工具、日志、运行目录和报告。

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
- GTAO 条带修复：[Report.md](validation/GtaoBandingFix/Report.md)
- SSR 反射修复：[Report.md](validation/SsrRayOriginFix/Report.md)、[T7VerificationAfterFix.json](validation/SsrRayOriginFix/T7VerificationAfterFix.json)、[T8VerificationAfterFix.json](validation/SsrRayOriginFix/T8VerificationAfterFix.json)
- DVFS 诊断：[Diagnostics/Dvfs-2026-11-14/README.md](validation/T1/Diagnostics/Dvfs-2026-11-14/README.md)
- T1 Frame Debugger：[FrameDebuggerReport.json](validation/T1/FrameDebugger/FrameDebuggerReport.json)
- 构建设置恢复：[BuildSettingsRestoration.json](validation/T1/BuildSettingsRestoration.json)
- Frame Debugger 设置恢复：[FrameDebuggerSettingsRestoration.json](validation/T1/FrameDebuggerSettingsRestoration.json)
- 量化预算：[PerformanceBudget.json](validation/T1/PerformanceBudget.json)

## 后续缺陷修复（2026-11-18）：Editor MV pass

**现象**：Edit Mode 下开启 GTAO temporal，拖动 Scene View 留下 AO 拖影；Player 正常。

**根因**：Scene View 相机没有 `UniversalAdditionalCameraData`，URP `MotionVectorRenderPass` 提前返回，motion 纹理保持清黑；GTAO 的时间降噪因此原地混合历史。共享深度金字塔又只按 `Time.frameCount` 去重，同一帧内的多次渲染（暂停态、脚本 `Camera.Render()` 循环）会复用旧姿态深度。

**修复**：

- `0bbce70`：新增 editor-only 相机 MV pass（事件 203），仅对无 ACD 相机写入，prev VP 存 per-camera history；shader 复刻 URP `CameraMotionVectors.shader`。
- `3761adc`：深度金字塔构建 token 增加非抖动 VP；同帧同姿态仍共享一次构建。

**验证**：E2E 三模式下无 ACD 与原生 MV 逐帧完全一致；抑制基线复现 8.8% 像素劣化；EditMode 173/173、PlayMode 6/6、T12 34/34/35（12 张差异均为姿态化金字塔纠正）。详见 `PLAN/TODO/07-EditorMVPass/Report.md`。
