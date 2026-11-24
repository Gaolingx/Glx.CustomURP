# 20 · 光追研究索引与证据口径

2026-11-24｜本目录只回答“能不能做、从哪里接、哪些不能搬”，不代替实现或真机验收。总计划与里程碑见 [HANDOFF](../HANDOFF.md)。

## 文档分工

| 文档 | 回答的问题 |
|---|---|
| [HDRP14.md](HDRP14.md) | HDRP 14 的 SSGI/RTGI/RTR 如何交接屏幕与世界空间，哪些做法值得借鉴，哪些与 URP 不兼容 |
| [TLAS.md](TLAS.md) | Unity 2022.3 的 RTAS 构建、实例维护、材质命中合同、生命周期与可独立测试项 |
| [SSR-DXR.md](SSR-DXR.md) | 现有 opaque SSR 的未解决样本如何接入 DXR，预算、派发、合成与历史如何不破坏原契约 |
| [../gi/report.md](../gi/report.md) | 动态更新探针作为 SSGI 漫反射回退的数学、存储、更新调度与参考实现 |

## 已核实的运行环境

| 事实 | 证据 |
|---|---|
| Unity `2022.3.62f1`；URP 为仓库内嵌 `Packages/com.unity.render-pipelines.universal@14.0.12` | `URPSample01/ProjectSettings/ProjectVersion.txt`；`URPSample01/Packages/` |
| 公开 API 存在 `RayTracingAccelerationStructure`、`RayTracingShader`、`CommandBuffer.BuildRayTracingAccelerationStructure`、`CommandBuffer.DispatchRays`、`SystemInfo.supportsRayTracing` | `D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.xml`：11325、11969、35216、35509、49424 |
| RTAS 实例支持 8 位 ray mask、实例 ID、逐实例 transform/mask 更新、`CullInstances` | 同上：11330–11536、11641–11704 |
| 2022.3 公开接口中没有找到 GPU 计数驱动的间接 `DispatchRays` 或 inline ray query 的能力查询 | 同 XML 全量检索无 `InlineRayTracing`；v1 因此固定宽度派发 + GPU 早退 |
| 项目 Windows 默认图形 API 为 D3D12（`m_APIs: 12000000` = `Direct3D12`） | `URPSample01/ProjectSettings/ProjectSettings.asset:531–533` |
| 现有质量档为 Performant / Balanced / High Fidelity | `URPSample01/ProjectSettings/QualitySettings.asset` |
| 现有回归基线为 `720bedd` 的 **D3D11** 证据，不能证明本 HEAD 或 DXR | [../validation/P0/Baseline.json](../validation/P0/Baseline.json) |
| 本机已有 D3D12 构建/捕获脚本，可复用到 RT Player 验证 | `URPSample01/Assets/Tests/ClusteredDeferred/Editor/*D3D12*.cs` |

## 结论等级

- **已验证**：在本地源码/API XML/项目设置中直接读到，可指到文件与行。
- **推断**：由已验证事实组合得出，尚无运行证据；实施前必须用最小 fixture 闭环。
- **未验证**：主机、驱动、特定 GPU、性能数字与画面结果；不得写进“支持”结论。

本轮没有做 GPU 测量；所有性能上限都是待 B0 冻结的输入，不是已达成指标。

## 维护规则

1. 行号只对当前包快照有效。升级 Unity/URP/HDRP 后先重新 grep 符号，再改结论。
2. 研究文档只记录可复核事实与设计取舍；不复制实现代码，不把计划写成“已支持”。
3. 新证据放 `validation/<ID>/<platform>-<renderer>/`，不覆盖 P0；失败/阻塞同样入库。
4. 影响产品的结论先落到 [HANDOFF](../HANDOFF.md) 的里程碑或非目标，再在本文档展开，避免两份口径漂移。
