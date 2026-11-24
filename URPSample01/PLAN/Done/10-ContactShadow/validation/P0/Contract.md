# T0 · Contact Shadows 实施契约

调研基线 `cedd1bb`；实施与回归比较基线 `cfbb3d44d46250f81665e163b6752b8235f6b50d`（启动时工作树干净）。不回退两者之间的 clustered lighting 改动。运行结果见同目录 [Baseline.json](Baseline.json)。

按所有者“按计划开始任务”执行 [HANDOFF §0 D1–D12](../../HANDOFF.md#0-一页结论) 及 §7 AC-1…16，不扩展 T10。

- D1–D6：包内 Feature、Volume、同一 GameObject 上的逐灯组件；复用现有共享金字塔（mip 1 首采、同一 t 用 mip 0 确认）。已有 URP C# 零修改；已有 shader 仅在 T5 修改 Shadows.hlsl 的主光和附加光函数，引入新 ContactShadows.hlsl。包内 EditMode 测试归现有 Unity.RenderPipelines.Universal.Editor.Tests，不新增 asmdef。
- D7–D9：全分辨率 R32_UInt，低 24 位逐灯、主光若参与优先，高 8 位 fade；未标记附加光仍消耗索引。材质不新增变体；关闭时不 Load，透明物与 Receive Shadows Off 排除；opaque 后归零。
- D10–D12：Deferred 附加光仅在该灯 draw 的附加阴影变体启用时应用；不改核心代码解除限制。运行 gate 为 D3D11/12、Vulkan、Metal + compute/R32_UInt LoadStore；计划内实测 D3D11/12，不把代码支持记作平台实测。无光追、新增捕获 0。
- 2026-11-24 T5 前所有者确认的 D5/D6/D10 修订（不改变上述历史基线）：当前 fork 已删除 stencil Deferred。额外允许最小的聚类 Deferred shader 接口；不改既有核心 C#、不加材质变体、不允许用 Forward fallback 验收。Deferred 主光不依赖 shadow map，附加光限 Asset 开启且当前灯有有效阴影切片；不执行 T10。
- Volume 默认关闭；length / opacity / distanceScaleFactor = 0.15 / 1 / 0.5，max / min / fade / fadeIn 距离 = 50 / 0 / 5 / 0，rayBias / thicknessScale / sampleCount = 0.2 / 0.15 / 10。
- 冻结阈值：关闭、透明与 ReceiveOff 的 A/B 像素差为零；三模式 mask 相同（仅可解释的 1px 深度边缘差异 <1%）；每相机每帧一份 pyramid；第 25 灯不分槽。既有测试与新增测试全绿、零 shader error；不放宽断言。性能参考 1920×1080、4 灯、10 samples、每模式 GPU ≤1.0 ms，超出单列，不遮盖正确性结论。
- PNG ≤80 张且 ≤45 MB，validation ≤50 MB；不提交日志或 player。历史已跟踪捕获超过原 1024 MiB 预算：保留原件，本轮增量预算锁为零。每个任务门禁通过再独立提交，T9 之前不归档。
