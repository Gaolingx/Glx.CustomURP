# C5：测试资产与变体（基线 `10ed839`）

## 删除依据与回滚边界

开始时 `git status --short --branch` 只有 `main...origin/main [ahead 2]`；未以 Unity 打开主工程。以下四组为 `Assets/Tests/ClusteredDeferred/Renderers/Cd{前缀}StencilDeferred{Pipeline,Renderer}.asset`，每个资产连同其 `.meta` 删除。按名称与 32 位 GUID 对 Git 跟踪文本做删除前扫描（排除非运行时的 PLAN）：仅命中资产自身 `m_Name`、meta 定义，以及每个 pipeline 对同组 renderer 的一条引用。没有场景、质量设置、Resources、包或生成脚本引用。恢复这 16 个路径时从 `10ed839` 取回原文件及 meta 即可保留 GUID，无需 Unity 重新生成。

| 前缀 | Pipeline GUID | Renderer GUID |
|---|---|---|
| 无 | a6d160f6d440dfd4584dd4a0e795496e | 7553c06a81b24154d8596cd8facf8d10 |
| Shadow | 66e5e90e64aebab478590c1f542a0675 | ee562db8dd460ba4686898f39c6e374e |
| ScreenShadow | 870fea65bdef2164a845d8802701b270 | 41a4941cff44b1e4eae1364ae0f0b780 |
| Feature | b88603cb7ed30ec4f95449d23a8a3026 | 88beb0e4ea081b448b0f74724dea10a2 |

## §2.4 复核：哪些不是本轮删除项

- **生成与消费已迁移**：`ClusteredDeferredTestAssetSetup.CreateAll/CreatePair` 自 `646f75a` 生成 clustered 和 Forward+；Arena 场景有 8 个非 stencil pipeline 槽。T4 构建将三种负载的双 renderer 配置置于 quality slots，T5 将 clustered 与 Forward+ 配置置于 quality slots。不删除独立对照；D3D12 SSL 捕获入口只是注释仍误称 stencil，已纠正。测试夹具内的灯索引、阴影、cookie、AO、材质分类 stencil 保持不变。
- **剥离**：`UniversalRenderPipelineAssetPrefiltering.cs:96–106` 管理 Forward+ 和 `_DEFERRED_MIXED_LIGHTING` / `_GBUFFER_NORMALS_OCT`；`ShaderBuildPreprocessor` 收集 `DeferredShading`、保留 Forward+ 的逐相机 Forward fallback。`ShaderScriptableStripperTests.TestStripUnusedFeatures_DeferredRendering` 验证的是 GBuffer pass，而非 stencil 灯体积。`ShaderPrefilteringTests` 的 Select/SelectOnly 涵盖混合模式；不可因删除无引用资产而移除这些 profile。过渡开关断言已由 `ClusteredDeferredModeGateTests` 中设备、相机与真实 Forward+/Deferred 夹具断言取代。
- **SSL**：13/13 个 `Ssl*DeferredRenderer.asset` 都是 `m_RenderingMode: 1`，都已有相同的非空 cluster builder、compute、mask、resolve GUID；都不含 `m_ClusteredDeferredLighting` 或 `stencilDeferredPS`。不需要开启已删除的开关、不改为 Forward。C5 移除的资源不在这些 renderer 的依赖中；没有依据重建或批准 SSL 金图。这不是重新签收视觉差异。
- **历史数据**：T4ShaderBuild/T5VariantCount/T7PlayerSummary 不覆盖、不移动、不回写 SUPERSEDED；该类档案标注属于 C6。冻结 CPU/图像参照仍是 `ForwardPlusGpuMatchesFrozenCpuReference` 的输入。C6/C7、ACCEPTANCE、提交均不在范围内。

## 测量口径与隔离复现

隔离工程 `E:\tmp\C5-verify` 从 `git archive 10ed839` 的 Assets/Packages/ProjectSettings 新建，删除相同 16 个文件，另复制 PlayMode 使用的 `PLAN/Done/16-CleanupBaseline/c4/cpu-5d4bcca`。Unity 2022.3.62f1，分别 `-force-d3d11/12 -batchmode -runTests -testPlatform EditMode/PlayMode`（不设 filter）；玩家入口 `ClusteredDeferredArenaBuild.BuildD3D11AndExit` / `BuildD3D12AndExit`（T5 Development，与 C4b 同口径；不是 release 构建）。原始日志在该隔离目录；构建生成器的默认输出也仅在隔离目录。

源码侧对 `ClusteredDeferred.compute` 逐行提取 `#pragma multi_compile` 实测：1 个 kernel，8 组，`2×4×2×5×2×2×2×2=1280`，相对原 1280 差 0。这是声明空间，不是 player 最终保留数。C4b T5 双 API 的 Lit/ForwardLit(fp) 筛选序列为 `36238786560 → 9437184 → 36864 → 6144`；只比较同 pass/stage/loadout，不能将该数字当作全工程总数。

## 新结果与未实施项

| 最终验证（全量、不设 filter） | D3D11 | D3D12 |
|---|---:|---:|
| EditMode | 489/489 | 489/489 |
| PlayMode | 45/46 | 45/46 |
| T5 新建 Windows player | Succeeded，0 errors | Succeeded，0 errors |
| Lit/ForwardLit(fp) scriptable stripping 后 | 6144（Δ0） | 6144（Δ0） |
| ClusteredDeferred compute 剥离后 | 1280/1280（Δ0） | 1280/1280（Δ0） |

- `variants.json` 内有完整计数、BuildReport 摘要、原日志 SHA-256；`stripping-d3d*.txt.gz` 是可携带的逐 pass 摘录。`python collect.py E:\tmp\C5-verify` 可重提取。两个构建各自 0 条 shader error、0 条 missing-reference/script 诊断、0 条两个过时关键字；各有 723 条 shader warning（重复计数；C4b 为 722，本次多一条 Lit 的 `REQUIRES_WORLD_SPACE_POS_INTERPOLATOR` 宏重定义提示）。未修改 shader 或屏蔽警告。构建调用 `CreateAll()` 后隔离 Renderers 目录仍无 `Cd*Stencil*`。移除的资产原本不在玩家构建输入，因而不期望变体下降。
- 首轮 D3D11 EditMode 原始 487/489 单独保留。两条失败是旧 SSL ProbeAtlas 测试假设构造时即分配，以及假设 Forward+ 忽略 feature 请求。实际 `ForwardLights.PreSetup/Setup` 按相机惰性创建；实际 Forward+ 无需 feature 请求，fallback 则使用记录的请求。更新构造期与请求标志断言，并在现有真实 Forward+ PlayMode 冻结对照渲染后验证发布的 atlas 是 `RenderTexture`。不改运行时行为。修正后的全量 XML 可见 `EditMode-rerun-d3d11.xml`、`EditMode-d3d12.xml`、`PlayMode-rerun-d3d11.xml`、`PlayMode-d3d12.xml`。额外的 `*-rerun-*` 复跑使用同基线和冻结输入，第二个隔离工程为 `E:\tmp\C5-tests`。
- 两 API 的唯一 PlayMode 失败均是 `ClusteredMatchesForwardPlusAcrossMaterialTypes`：1674 个像素差 >8/255，限值 230，与 `../17-C4bLightLayout/PlayMode-d3d{11,12}.xml` 同项同值。保留原始 Failed；未改阈值、未更新金图、未给全绿结论。历史图像差异的修复/裁决仍 OPEN。本轮没有重跑 T4 player、性能或 RenderDoc，也不以构建成功代替视觉验收。
- `git diff --check` 无格式错误；最终未暂存、未提交。工作树范围：16 个已跟踪资产/meta 删除、2 个测试文件、2 处过时注释、本目录证据；历史归档和 ACCEPTANCE 无变更。
