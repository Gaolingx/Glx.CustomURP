# 11-LightCulling 已知差异

> **SUPERSEDED（旧 stencil 基线）**：历史 stencil 与 clustered 的差异不代表当前回退行为。双 API 256 灯跨版本图差与 1674 像素材质差已按 [D-C7-1](../../Done/19-C7Final/DECISIONS.md) 记为 ACCEPTED 已知差异（1674 为冻结上限，256 灯跨版本比较因缺灯索引逐次随机而不作门槛）。见[当前验收口径](../../ACCEPTANCE.md#c7-decision)。

Clustered Deferred (URP BRDF) 与 stencil deferred / Forward+ 的已知差异、边界与未验证项。验收阈值与证据见 `report.md`、`validation/`。

## 1. 与 stencil deferred 的差异

| 项 | 差异 | 原因 / 处理 |
|---|---|---|
| Legacy fog | clustered 路径不绘制 fog；stencil deferred 的 fog pass 仍会执行 | 本轮的 compute 替换的是 deferred lighting pass，不含 fog。A/B 场景关闭 fog；列入后续工作。 |
| 材质类型 | Unlit、ComplexLit、forward-only 不参与 deferred 直接光（mask=0），由原 forward-only pass 着色 | 与 stencil deferred 的 `MaterialUnlit` 跳过语义一致，不是缺失。 |
| 逐像素 A/B 残差 | 普通场景 mean ≤ 0.8/255、max ≤ 9/255；GTAO 用例 max ≤ 9/255、>8 至多 1 px | 浮点累加顺序、灯体积光栅化边缘、GTAO 去噪采样位置（stencil 用插值 screenUV，clustered 用像素中心）。阈值 mean ≤ 2、max ≤ 24。 |
| 阴影 | 主光 cascade/screen-space、附加光 atlas、`_RECEIVE_SHADOWS_OFF` 均已对齐 | 逐灯质量（low/medium/high/soft）按 `Shadows.hlsl` 的质量宏选择。 |

## 2. 功能覆盖边界

| 项 | 状态 | 说明 |
|---|---|---|
| mixed lighting / shadowmask | **PARTIAL** | kernel 的 baked shadow 混合、min-lerp/fade-lerp、subtractive 早退、`_DEFERRED_MIXED_LIGHTING` 变体与绑定已完成并有编译记录；但工程没有 baked lightmap/probe，`Light.bakingOutput` 在运行时无法伪造，shadowmask 恒白，运行时 A/B 不可得。 |
| cookie | spot 路径端到端验证；point（octahedral）与 directional（主光/附加方向光）UV 随全部 `_LIGHT_COOKIES` 变体编译，运行时未触发 | 与 `LightCookie.hlsl` 逐行对照；需要额外场景资产才能覆盖。 |
| GTAO | 直接光系数 + 间接光取消系数（`SrcAlpha`）两段 | 只做直接光会与 stencil 的取消语义不一致（见 report §3.2）。 |
| Light layers | 主光/附加方向光/cluster 光三处统一 `IsMatchingLightLayer` | shadow 管线需在 quality level 上打开 `m_SupportsLightLayers`，否则 prefiltering 剥离变体导致全黑。 |
| MSAA | 不支持 | deferred 本身不支持；clustered 沿用。 |
| native render pass | 强制关闭并告警 | mask 读 stencil、compute/resolve 需要显式 RT 绑定；在渲染器构造期决定，避免逐帧状态泄漏。 |
| XR / foveated / Preview / Overlay | 回退 stencil deferred | `ClusteredDeferredPass.IsEligibleCamera` 只允许 Game + Base + 非 XR。 |
| GLES / WebGL | 不支持 | 无 compute 或格式不保证；`IsSupportedDevice` 返回 false 并告警回退。 |
| `useStructuredBuffer = true` | 禁用并回退 | kernel 只读常量灯数组。 |
| 透明物体 | 仍走 forward 路径 | 与 URP/HDRP 一致；cluster 表已构建，可供后续 Forward+ 透明使用。 |

## 3. 平台与证据边界

| 项 | 状态 |
|---|---|
| D3D11 / D3D12 | 已验证：player A/B、特性矩阵、D3D12 RenderDoc 抓帧、编辑期测试。 |
| Vulkan / Metal | **未验证**（本机无对应 player 构建）。API gate 只做能力判断，不声称通过。 |
| 非 Development player | 未单独构建；变体剥离由同一关键字集合与构建日志（0 compute error）间接覆盖。 |
| player 内运行时开关 | 未验证；运行时 off/on 在编辑器 PlayMode 中验证（x86 编辑器与 player 走同一 `SetDirty → InternalCreateRenderer` 路径）。 |
| 性能 | 仅 D3D11、720p、RTX 5060 Ti；D3D12 只用于正确性与抓帧。 |
| job 完成顺序 | 测试在编辑器暴露了 PreSetup jobs 与 `ResolveMixedLightingMode` 的并发访问；已改为在其之前完成 jobs。player 证据产生于修复前，但修复不改变 GPU 输入（上传仍在完成之后），PlayMode A/B 在修复后重新通过。 |
| 异步 compute | 本轮未做；cluster 构建仍是 CPU jobs（D11）。当前修复把完成点前移到 deferred setup 内，缩小了与 Setup 其余部分的并行窗口；GPU 光表/async 两段式列入下一轮。 |
| `.rdc` 数量 | 本轮保留 2 个（clustered 14.2 MiB、stencil 基线 7.4 MiB），均 ≤20 MiB，但超过“最多 1 个”的口径；用于两条路径的 pass 结构对照。 |

## 4. 未纳入本轮

HDRP 面光源/线光源/LTC、HD 阴影 atlas、体积雾、物理曝光、SSS、StackLit/Hair/Fabric、7 张 GBuffer、material feature、tile variant 间接派发、GPU FPTL 细剔除。理由见 `HANDOFF.md` §1.2。
