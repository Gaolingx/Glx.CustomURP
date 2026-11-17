# 06 已知差异与收尾结论

> 更新：2026-11-17
> 范围：06-ValidationAllRDC 的 P1–P4 GPU 验证、缺陷修复与差异收敛。
> 语义基准：HDRP 14.0.12（`HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12`）；目标包：URP 14.0.12（`URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12`）。
> 状态源：[HANDOFF.md](HANDOFF.md) §3 与各任务 `validation/*/Report.md`。本文件只固化“与 HDRP 的差异”和“本轮缺陷的最终状态”，不重复验证过程。

## 0. 分类口径

| 分类 | 含义 | 处理 |
|---|---|---|
| A 引擎能力 | 依赖硬件光追，或 URP 14 不存在的引擎数据/阶段 | 排除，不实现，不承诺 |
| B URP 架构 | URP 渲染器、材质或数据模型的限制 | 记录，不承诺；需要时单独立项 |
| C 有意适配 | 为 URP 语义、稳定性或性能主动选择的实现 | 保留，记录理由与验收依据 |
| D 缺陷 | 本轮审计发现、影响正确性的问题 | Fixed 或 Accepted；High 不允许 Accepted |

---

## A. 引擎能力 / 硬件光追（排除）

| 能力 | 影响效果 | 排除原因 |
|---|---|---|
| RTR / RTAO / RTGI / RTSSS，以及 RT 专用降噪分支 | GTAO、SSGI、SSR | 依赖硬件光追与加速结构；URP 14 无对应路径，屏幕空间信号不复用 RT 语义 |
| 逐像素 env-light hierarchy / FPTL / `EnvLightData` StructuredBuffer | SSGI、SSR、折射 | URP 无 FPTL 数据与逐像素光照列表；由反射探针 atlas + 盒权重替代 |
| 粗粒度 stencil 拒绝、透明物 motion vectors、曝光控制 | SSR、透明 SSR、SSGI | URP 14 无对应 stencil bit、透明运动写入或预曝光阶段的等价数据 |
| APV / Probe Volume | SSGI | URP 14.0.12 未集成 |
| 硬件动态分辨率 | 全部 | URP 14.0.12 不驱动 `DynamicResolutionHandler`；静态 render scale 可用（P4-3） |
| Metal / mobile / OpenGLES / 真实 HMD | 全部 | 当前 Windows + D3D11 环境不可测，不声明支持 |

## B. URP 架构限制（记录，不承诺）

| 限制 | 影响效果 | 后果 | 证据 |
|---|---|---|---|
| GTAO 没有 Volume 组件 | GTAO | 参数序列化在 Renderer Feature 上，没有 Volume 混合、逐相机覆盖与运行时插值；单 AO 生产者设计 | [P1-4](validation/P1-4/Report.md) |
| 材质侧没有 HDRP 的 horizon-based specular occlusion 与多弹射变体 | GTAO | 只有间接/直接环境 AO | HANDOFF §4.2 |
| SSGI 探针回退使用线性盒权重 + box projection，上限 64/32，无 range compensation、half16 clamp、per-probe multiplier | SSGI | 与 HDRP influence shape/proxy volume 不同；探针选择也可能与 `unity_SpecCube0/1` 不同 | [P2-4](validation/P2-4/Report.md) |
| SSL 在预曝光阶段运行 | SSGI、SSR | 反射/间接光信号为预曝光值，曝光倍率固定为 1 | T14 |
| Deferred 兼容路径忽略 MSAA | 全部 | Deferred 下不承诺 MSAA 支持；Forward/Forward+ 正常 | [P4-3](validation/P4-3/Report.md) |
| SSR 无对象 motion 拒绝与 stencil 拒绝 | SSR、透明 SSR | 移动物体/透明反射可能滞后 | T14 |
| 透明 SSR 只有 Approximation 路径 | 透明 SSR | 无 PBR 累积；移动透明反射依赖 hit-pixel/相机运动 | [P3-SSR](validation/P3-SSR/Report.md) |
| 折射无 pre-refraction 队列、厚度/透射贴图、预积分 FGD LUT；仅 forward 透明；Deferred GBuffer 不折射 | 折射 | 透明物不出现在折射背景；中粗糙度玻璃能量可不同 | T14、05 HANDOFF |
| 折射最多 7 个有限探针 + 1 个无限回退；旋转探针用保守 AABB；无 per-probe rendering layer mask | 折射 | 密集场景可能丢探针；层过滤不作用于 proxy | T14 |
| 反射/预览相机在 SSL pass 前被 `ScreenSpaceCapability` 拒绝 | 全部 | 探针/预览渲染关闭效果而非调暗；这是拒收路径的预期行为 | [P4-2](validation/P4-2/Report.md) |
| 插件 keyword 在所有构建中保留 | 构建 | URP 预过滤只覆盖 URP keyword；插件级剥离未引入，需单独正确性周期 | T14 |

## C. 有意适配（保留，记录理由）

| 适配 | 效果 | 理由与验收 |
|---|---|---|
| 正交 GTAO | GTAO | URP 新增能力，HDRP 无对照；reversed-Z 感知的线性深度，正交视线固定 `(0,0,-1)`；2 m/20 m 接触 AO 相对差 0.18%、左右差 0.08%（[P1-2](validation/P1-2/Report.md)） |
| GTAO 使用对象 + 相机 motion vectors | GTAO | 比 HDRP 仅相机运动更能抑制动态物体拖影（[P1-3](validation/P1-GTAO/Report.md)） |
| GTAO 与 SSL 共享 packed Hi-Z | GTAO、SSL | 每相机视图 1 次 copy + N-1 次 downsample；**T14 的“重复 Hi-Z 显存”条目作废**（[T14KnownDifferences](../../Done/01-ScreenSpaceLighting/validation/T14/T14KnownDifferences.md)） |
| GTAO 降噪/上采样用整数 `Load` | GTAO | 01 轮条带修复的必要适配；不依赖 compute sampler 绑定 |
| SSGI 材质混合 `lerp(bakedGI, radiance*intensity, weight)` | SSGI | miss 时回退 baked GI；HDRP 直接替换 `bakeDiffuseLighting`（[P2-4](validation/P2-4/Report.md)） |
| SSGI 探针 atlas 默认关闭；开启后天空余量改用 ambient SH，`rayMiss=ReflectionProbes` 在盒外权重 0 | SSGI | Forward/Deferred 的局部探针 opt-in；避免默认路径行为变化（T14、[P2-4](validation/P2-4/Report.md)） |
| SSGI 2×2 深度加权 bilateral upsample | SSGI | 替代 HDRP 6×6 LDS gather；半/全相关 0.9992，无 1 px 硬缝（[P2-4](validation/P2-4/Report.md)） |
| SSGI 光线步进最大 mip 6 | SSGI | URP 的单一最近深度层限制；源码有明确注释（T14） |
| SSGI 调用点 `reflectSky` 恒为 1 | SSGI | `reflectsSky=false` 分支不可达；记录为有意行为（HANDOFF §4.3） |
| SSR PBR 使用移植的 `ReflectionDenoiser` 与独立 R16 sample count | SSR | 替代旧 18 kernel 累积族；history 颜色存 `(radiance, weight)`（T14） |
| SSR 半分辨率只作用于 PBR | SSR | HDRP 的屏幕空间 SSR 无半分辨率对照；切换会重分配并重置 history（T14、[P3-SSR](validation/P3-SSR/Report.md)） |
| SSR Approximation 3×3 空洞填充 | SSR | URP 新增；只接受同一表面、可参与 SSR 且有命中的邻居；覆盖 ≥95%（[P3-5](validation/P3-5/Report.md)） |
| SSR 写非预乘 `(radiance, weight)`，材质 `lerp(probe, radiance, weight)` 后统一乘一次 `EnvironmentBRDF` | SSR | 与 HDRP 预乘 `opacity*outputs/wAll` + 分层权重不同（T14） |
| SSR `intensity` 乘在 weight 上，>1 不增亮 | SSR | URP 无 HDRP SSR intensity；0–1 有效（T14） |
| 透明 SSR opt-in、Approximation-only、`reflectSky=0` | 透明 SSR | `_TRANSPARENT_SSR_RECEIVE` 显式 opt-in；未 opt-in 片段在 prepass `clip`（[P3-5](validation/P3-5/Report.md)） |
| 折射使用对象中心 AABB proxy（7 有限 + 无限回退） | 折射 | HDRP 的 influence volume 默认映射为 AABB；仅显式无限 proxy volume 不可表示（T14） |
| 折射有限 proxy 未命中即视为屏幕 miss | 折射 | HDRP 在有限探针下把 proxy 距离 clamp 到 0，仍采样屏幕；差异只出现在折射物/光线在所有有限 proxy 外时（T14） |
| 无探针时折射投影距离用 65504 | 折射 | HDRP 默认 proxy 通常投影为 0；URP 让无探针场景仍可折射（T14） |
| 折射采样当前不透明+天空颜色金字塔 | 折射 | 不迁移 pre-refraction 透明排序；折射中看不到之后的透明物（T14） |
| 折射不透明度使用 `1-alpha` 透射掩码并强制 alpha=1；关闭/`intensity=0` 恢复 legacy 路径 | 折射 | 保证关闭时逐字节一致；Alpha Blend 已验证（T14） |
| 折射使用 `EnvironmentBRDFSpecular` 与 IOR 修正 F0，不用预积分 FGD LUT；IOR 只影响 metallic 工作流 F0 | 折射 | 中粗糙度能量可不同；`_SPECULAR_SETUP` 保留 specular map（T14） |
| 折射只对表面雾化一次 | 折射 | 颜色金字塔已含大气散射；HDRP 会对合成结果再次雾化（T14） |
| 折射屏幕边缘在 NDC [0,1] 外严格为 0，并 clamp 到半纹素下界 | 折射 | 与 HDRP 拒绝语义一致，只影响零权重边缘像素（T14） |
| ComplexLit clear coat 的 SSR 用基础粗糙度追踪；折射 + clear coat 为近似 | SSR、折射 | HDRP 不支持折射 + clear coat；coat 反射在材质阶段按权重混合（T14） |

## D. 本轮缺陷（最终状态）

| ID | 级别 | 效果 | 状态 | 关键验收 | 证据 |
|---|---|---|---|---|---|
| F-01 | High | GTAO 全分辨率深度中心/采样读取错误区域 | **Fixed** | full↔half 相关 0.99090→0.99983；平均绝对差 1.005→0.144；半分辨率不变 | [P1-1](validation/P1-1/Report.md)、[P1-GTAO](validation/P1-GTAO/Report.md) |
| F-02 | High | SSR 降噪器像素展开角未绑定，移动相机时 history 近似失效 | **Fixed** | SSR-only 读出 0.00160375/0.00320750（误差 0） | [P3-1](validation/P3-1/Report.md) |
| F-03 | Medium | GTAO 正交相机用透视深度参数，视线随屏幕位置变化 | **Fixed** | 2 m/20 m 接触 AO 相对差 0.18%，左右差 0.08% | [P1-2](validation/P1-2/Report.md) |
| F-04 | Medium | SSR 调试视图覆盖正式 `ssrOutput` | **Fixed** | 调试帧 7 dispatch；正式 PBR 链仍写 `SslSsrPbrOriginal` 并被材质读 | [P3-2](validation/P3-2/Report.md) |
| F-05 | Medium | 折射全局 keyword/纹理缺少相机起点清理 | **Fixed** | 注入残留修复前 4 通道全 255，修复后全 0；A→B 正对照为 0 | [P4-1](validation/P4-1/Report.md) |
| F-06 | Medium | 透明深度种子固定 `(1,1,0,0)`，Y 翻转目标上方向相反 | **Fixed**（子分支 Accepted） | 面板/水印外 826,164 像素最大差 0；透明深度 0.006142 > 不透明 0.003819 | [P3-3](validation/P3-3/Report.md) |
| F-07 | Medium | SSGI history stamp 缺少探针 atlas/步数/厚度等开关 | **Fixed** | 7 用例切换后下一帧 validity=0；无关参数不重置 | [P2-2](validation/P2-2/Report.md) |
| F-08 | Medium | 共享 ComputeShader keyword 依赖调用顺序 | **Fixed** | 同帧不透明 trace（144 B cbuffer）与透明 trace（112 B）变体共存 | [P3-3](validation/P3-3/Report.md) |
| F-09 | Medium | SSGI 降噪器 tap 投影缺少 Y 翻转，降噪近似直通 | **Fixed** | tap 接受数 1/16→16/16；输出高频能量 0.505× | [P2-3](validation/P2-3/Report.md) |
| F-10 | Low | GTAO 首帧/空 history 提前返回但仍开启 occlusion keyword | **Fixed** | `intensity=0` 与 feature 禁用 0 像素差；首帧 keyword/纹理一致 | [P1-GTAO](validation/P1-GTAO/Report.md) |
| F-11 | Low | 死代码、重复 ID、过时注释与文档 | **Fixed** | 删除仅测试可见的 history 成员、未读 ID/通道与空实现；全量 EditMode 455/455 | [P4-4](validation/P4-4/Report.md) |
| F-12 | Medium | Forward+ 下 TerrainLit 只收到环境光 | **Fixed**（验证构建缺陷） | 构建期收集 Forward+ 变体后，地形像素 0.184/0.235/0.306 → 0.545/0.529/0.514 | [P4-3](validation/P4-3/Report.md) |

### D.1 Accepted 子项

| 项 | 状态 | 理由 |
|---|---|---|
| F-06 的“源/目标翻转态不同”分支 | **Accepted** | D3D11 验证器中源/目标同为非 backbuffer RT，分支不可达；由 EditMode 规则断言覆盖，不做 GPU 举证 |
| F-12 的运行时归因 | **Fixed / 非运行时缺陷** | 根因是验证构建未收集 Forward+ 变体；运行时路径本身没有 Forward+ 专属错误 |
| XR single-pass smoke | **Accepted（环境限制，另行排期）** | 06 未重跑 Mock HMD；T13/T14 的 XR 证据仍有效。`TEXTURE2D_X`/`blackTexture` 绑定需要单独 smoke，本轮不展开 |
| 构建 warning 基线 | **Accepted（既有噪音）** | P4-4 构建 4001 条 shader warning，其中 3998 条为插件 compute 的 `Load` 隐式截断（SSR 3171、SSGI 746、GTAO 80、Leak Probe 1），3 条来自 URP 核心 shader；无 C# warning，与 P4-2 数量相同 |

## E. 破坏性变更与迁移

| 变更 | 内容 | 迁移影响 |
|---|---|---|
| `ScreenSpaceLightingDebugMode` 枚举重排 | P4-4 前：`SsrPbrParams=14`、`TransparentDepth=15`、`SsrTransparentRaw=16`、`SsrTransparentWeight=17`，13 为空洞；P4-4 后：**13/14/15/16** | 项目内 45 个序列化 `debugMode` 全部为 0，无需迁移；外部若按数值引用 14–17，需重映射到 13–16 |
| 删除死成员/重复 ID | `ScreenSpaceLightingHistory` 的 `validFrameCount`、`resourceGeneration`、`ssrOutputProduced`/`MarkSsrOutputProduced`、`ssrHasValidHistory`、`historyColorPyramidCount`/`previousDepthCount`；`usingPbrAccumulation`；`_GtaoDebugMode`/`_GtaoDebugMip`；`depthPyramidMipCount`；`ScreenSpaceDepthPyramid.SwapBuffers`；重复 ID 合并到 `ScreenSpaceShaderIDs` | 仅内部/测试可见；对应测试已更新或删除 |
| 未读 cbuffer 通道停止写入 | `_SsgiTemporalParams.zw`、`_SsgiDenoiserParams.yzw`、`_SsrPyramidParams.y`、`_SsrPbrParams.x/z/w`、`_GtaoScreenSize.zw` | 无行为变化；§6.6 中仍在使用的 ID/字段已保留 |
| GTAO `_GtaoParams0.x` 删除 | 原先写入 `fullResolution ? 0 : 1`，无人读取 | 无行为变化 |

## F. T14 文档修正

| 条目 | 修正 |
|---|---|
| “GTAO 与 SSL 各自持有深度金字塔，重复显存” | **作废**：两者共享 `ScreenSpaceDepthPyramidSystem` 的 packed Hi-Z；T14KnownDifferences 已改为“共享，旧条目过时” |
| 支持矩阵缺少 06 覆盖 | 已补正交 GTAO、render scale/MSAA 矩阵、keyword/绑定泄漏矩阵、三渲染器回归；详见 [T14SupportMatrix](../../Done/01-ScreenSpaceLighting/validation/T14/T14SupportMatrix.md) |
| T14 的首次开启 `temporary render texture not found` warning | P4-1/P4-2/P4-4 player 日志未复现；T14 记录保留为历史观察，不再作为当前行为 |
| Forward+ 绝对值可比性 | P4-3 起 player 构建收集 Forward+ 变体，**P4-3 及之后的 Forward+ 绝对值不能与之前各轮直接比较**；相对指标不受影响 |

## G. 证据索引

| 目录 | 覆盖 |
|---|---|
| [P0](validation/P0/) | 三渲染器 player、manifest、binding probe、9 例冒烟 |
| [P1-1](validation/P1-1/)、[P1-2](validation/P1-2/)、[P1-GTAO](validation/P1-GTAO/)、[P1-4](validation/P1-4/) | GTAO F-01/F-03/F-10、full/half、正交、材质覆盖、Deferred 双路径 |
| [P2-SSGI](validation/P2-SSGI/)、[P2-2](validation/P2-2/)、[P2-3](validation/P2-3/)、[P2-4](validation/P2-4/) | SSGI dispatch/history/LUT/atlas、F-07、F-09、覆盖/探针/时域 |
| [P3-1](validation/P3-1/)、[P3-2](validation/P3-2/)、[P3-3](validation/P3-3/)、[P3-SSR](validation/P3-SSR/)、[P3-5](validation/P3-5/) | SSR F-02/F-04/F-06/F-08、两条算法/全半分辨率/透明、正确性指标 |
| [P4-1](validation/P4-1/)、[P4-2](validation/P4-2/)、[P4-3](validation/P4-3/)、[P4-4](validation/P4-4/) | 折射 keyword 泄漏、X1 泄漏矩阵、X2 分辨率/MSAA、F-11 清理 |
| [T12](../../Done/01-ScreenSpaceLighting/validation/T12/) | 34/34/35 组合矩阵的历史基准；06 回放待 P6 单独排期 |
| [T14](../../Done/01-ScreenSpaceLighting/validation/T14/) | 发布回归、支持矩阵、XR 三 API、性能/显存历史数据 |
