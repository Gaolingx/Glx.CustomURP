# C0 决策冻结（clean.md D-C1–D-C8）

> 冻结时间：2026-11-22。冻结对象：`pre-cleanup` tag（C0 脚手架提交，渲染代码与 `0ffc46c` 相同）。
> 适用范围：本仓库 embedded URP 14.0.12 fork；按 D-C8，允许破坏性删除，不承诺外部工程 API 兼容。
> 修改本文件的任一冻结值都需要新开决策记录并说明复核原因；C3 之后不得静默改口径。

## 0. 冻结摘要

| ID | 决策 | 冻结值 | 直接后果 | 复核条件 |
|---|---|---|---|---|
| **D-C1** | 设备门槛 | D3D11 / D3D12 / Vulkan / Metal（compute + 灯数组）。GLES/WebGL 与 `useStructuredBuffer=true` **不再有 deferred**，只能走 Forward。 | 删 stencil 后不再为 GLES 保留 deferred 回退；`IsSupportedDevice` 只决定“能否 clustered deferred”。 | 产品重新要求 GLES deferred 时，先做最小 Forward 回退方案，而不是恢复 stencil。 |
| **D-C2** | Deferred 模式 | **只保留 clustered**。删除 `UniversalRendererData.clusteredDeferredLighting` 开关及 stencil deferred 分支。 | C3 可整删 `DeferredPass`、`StencilDeferred.*`、stencil 材质与关键字。 | 无（与 D-C1 绑定）。 |
| **D-C3** | 灯数据来源 | 短期保留 CPU `CullingResults.visibleLights` 作为候选；GPU 灯注册表（C8）**不排期**。 | C4 只删 CPU tile/zbin/job 构建，不删可见光剔除与灯常量上传。 | 可见光上传成为瓶颈且量化成立时，另立 C8。 |
| **D-C4** | 透明 / forward-only | C2 契约（15 轮）有效：Lit / SimpleLit / ComplexLit / ParticlesLit / ParticlesSimpleLit 的 ForwardLit 在 CD 下读 cluster 位图。**Terrain / SpeedTree / ShaderGraph 必须在 C3 前补 `_CLUSTERED_DEFERRED_LIGHTS` 变体**（C0.5）。 | 补齐前它们是已知语义差；补齐后 C2 才算完整。 | 若某 shader 无法安全加变体，记录为 ACCEPTED 语义差并禁止在 A/B 中当参照。 |
| **D-C5** | deferred fog | **迁移后删除**：在 clustered 光照之后保留一个最小全屏 fog pass（等价 `RenderSettings.fog` 语义），再删 stencil。C3 不得在 fog 未迁移时落地。 | 需要新增/保留一个 fog shader 与 pass 调用；`DeferredLights.RenderFog` 的调用点移到 clustered 路径。 | 若迁移成本超过 1 天且所有者接受 deferred 无雾，可改为“接受缺口”，但必须在 KnownDifferences 与 A/B 说明中写明。 |
| **D-C6** | 阴影 | 保留 `AdditionalLightsShadowCasterPass` 与 CPU 逐灯 atlas 设置；只删 stencil 光照。 | C4/C5 不触碰阴影关键字、atlas 与 shadow caster。 | 无。 |
| **D-C7** | Preview / Overlay / XR | 这些相机**回退到 UniversalForward**（per-object 灯路径），不保留 stencil 回退。 | `_AdditionalLightsIndices`、`maxPerObjectAdditionalLightsCount`、`GetPerObjectLightFlags` **保留**；clean.md 2.3 标为 ACCEPTED，不是待办。 | 若禁用了 Forward 路径，才可重开 2.3 的删除。 |
| **D-C8** | 兼容性 | 本仓库 embedded 包允许 break；不对外部工程做 API 兼容。 | 可直接删除公开属性、材质与 renderer data 字段。 | 无。 |

## 1. 附加冻结契约

1. **缓冲布局不进 C4a**：`URP_ZBinBuffer`、`urp_TileBuffer`、`_FPParams0/1/2` 与 `URP_FP_*` 宏在 forward 透明路径仍被消费。C4 拆为：
   - **C4a**：删 `LightMinMaxZJob` / `ZBinningJob` / `TilingJob` / `TileRangeExpansionJob` / `ReflectionProbeMinMaxZJob` 与 `ForwardLights.PreSetup` 的 CPU 构图，保留缓冲布局与 `_FPParams*`；
   - **C4b**（功能轮，不在本轮清理排期）：新的 GPU 光表布局。
2. **per-object 索引保留**：`_AdditionalLightsIndices` 的上传与 shader 读取、`LightData.maxPerObjectAdditionalLightsCount`、`GetPerObjectLightFlags` 是 D-C7 的回退路径，clean.md §2.3 记为 **ACCEPTED**。
3. **A/B 参照**：C3 之后 `StencilDeferred` 不再是参照；像素参照改为 **Forward+**（与 15 轮一致）。C0 基线同时保留 stencil 图，仅用于清理前的历史对照。
4. **变体预算**：C0 基线记录 Lit ForwardLit 片元 6144、`ClusteredDeferred.compute` 1280 组合；C3/C5 的删除必须报告差值，不得静默放宽。
5. **每步一个可构建回滚点**：C0 tag 保留；C3 起每个子模块单独 commit，不把“删代码 + 改行为”混在同一次提交。

## 2. 与 clean.md 的差异（C0 修正）

| clean.md 原项 | C0 冻结后的处理 |
|---|---|
| C2 后半句“删除 `_AdditionalLightsIndices` / per-object 索引” | **不删**。D-C7 保留 Forward 回退，这部分不是死代码；标 ACCEPTED。 |
| C4 “删 `_FPParams*` 与旧缓冲” | 拆成 C4a（只删 CPU jobs，保留布局）与 C4b（新布局，单独立轮）。 |
| C3 “迁移/接受 deferred fog” | 冻结为**先迁移再删**（D-C5），除非所有者显式接受无雾。 |
| C5 “删除 stencil 测试资产” | 保留 `Cd*Stencil*` 资产到 C5；C3 只删实现与关键字，避免一次改动混淆资产与代码。 |

## C3 验收例外（2026-11-22）

所有者批准 **C3 PASS**（渲染 `646f75a`，证据 `6575585`）。本决定只适用于此次删除 stencil，不改变 Forward+ 参照、像素阈值或原始测试结果。

- **T4 CLOSED**：删除前后相同夹具的同路径图 56/56 通过。两 API 原跨路径矩阵各 0/7；材质断言同样仍失败（1674 px，上限 230），但超阈位置未增减。这些既有跨路径差异不阻塞本次删除。
- **T5 OPEN，不阻塞 C3**：D3D11 的 256 灯同路径对照仍有 4 张失败。后续在独立进程记录并比较可见灯的稳定标识，验证灯集合差异能否解释失败；255 灯对照不替代 256 灯验收。

逐项数据见[验收记录](README.md)。本次不启动 C4a。

## 3. C0 退出条件

- [ ] `pre-cleanup` tag 存在并指向 C0 提交；HEAD 与 tag 记录在 `README.md`。
- [ ] EditMode / PlayMode 全绿。
- [ ] D3D11 / D3D12 的 T5 + T4 构建 0 error，summary 入库。
- [ ] T5 A/B（stencil vs clustered、clustered vs Forward+）与 T4 特性矩阵在阈值内。
- [ ] CPU 分解（`--cd-cpu`）入库，作为 C4 删除后的对照。
- [ ] 至少 1 个 D3D11 `.rdc`，pass 结构与 15 轮一致（GBuffer → Clustered → Forward Only / Transparent → FinalBlit）。
- [ ] 变体基线 JSON 与 `validation/README.md` 入库；ACCEPTANCE 增加本轮条目。
