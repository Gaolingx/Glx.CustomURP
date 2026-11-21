前情提要：我记得urp原方案是在cpu的jobs上跑lightculling的，到时候这些东西全部搞完也就成了死代码（包括原有的那套stencil deferred shader+c# urp死代码/功能块(pass、相关的bind、test)清理、URP 的 `CullingResults`/cullingMask/layer 语义用不到也得删（也许保留 CPU 可见光剔除这个fallback可以删掉） 你是不是应该考虑把善后工作也补充下呢）
当然了现在不做没关系 我们只是先把plan提前规划好，可以写到善后清单里

---
调研结果：
善后计划已写入 [clean.md](clean.md)（commit `4287c36`，仅文档、未动代码），并随本轮从 `PLAN/TODO/11-LightCulling` 并入 `PLAN/TODO/12-GpuLightList`；11-LightCulling 本体已归档在 `PLAN/Done`。

计划里包含删除清单（文件/符号/规模）、C0–C8 顺序、每步验证配方、风险和明确不删表。先纠正了四个容易误删的前提：

1. **CPU jobs 现在还不是死代码**——`URP_ZBinBuffer`/`urp_TileBuffer`/`_FPParams*` 仍是当前 kernel 的输入；只有 GPU 光表成为默认后才失效。
2. **stencil deferred 仍承担四件事**——本轮全部 A/B 的参照、不支持 compute/GLES 的回退、Preview/Overlay/XR 路径、deferred legacy fog，所以 C3 之前不算死代码。
3. **`CullingResults`/`cullingMask`/layer 不能整体删**——`CullingResults` 是引擎类型，还承载 renderer、probe、shadow caster；`Camera.cullingMask` 决定物体渲染；`Light.cullingMask`/`renderingLayerMask` 仍要被 GPU 光表消费（`IsMatchingLightLayer`）。能删的只是 URP 侧“灯列表构建”和“逐物体灯过滤”对它们的消费。
4. **“删掉 CPU 可见光剔除”是另一轮工作**——GPU 光表仍需要候选灯数据；除非把灯注册表也搬到 GPU，否则 `visibleLights` 作为输入得留着。计划里单列为可选 C8。

另外盘点时发现一个可以现在做、且不依赖 GPU 光表的点（计划里的 C1）：clustered 模式下 `DeferredLights.SetupLights` 仍在跑 `PrecomputeLights` 构建 stencil 灯数组，只是没人消费。跳过它、保留关键字/常量设置即可，属于低风险 CPU 收益。