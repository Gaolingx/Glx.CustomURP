# 20 · 高端平台可选光追：实施与签收入口

2026-11-24｜研究与计划，**未实现、未做本轮 GPU 测量**。结论：在 Unity 2022.3 / URP 14 上，Windows D3D12 有可验证的直接 DXR 派发路径；当前屏幕效果的时序允许接入，但不是移植 HDRP Mixed 的四张命中 GBuffer。主机是候选后端，不是已支持平台。

| 阅读顺序 | 决策归属 |
|---|---|
| [HDRP 14 源码研究](research/HDRP14.md) | RTGI/RTR 的真实数据流、限制与取舍 |
| [共享 RT 场景与 TLAS](research/TLAS.md) | 平台门控、实例、材质、灯光、生命周期、直接派发 |
| [GI 子计划](gi/HANDOFF.md) / [探针数学与参考](gi/report.md) | 动态更新世界空间探针，只填 SSGI 没解决的漫反射 |
| [不透明反射子计划](reflection/HANDOFF.md) | SSR 未解决射线由 DXR 查几何，并保持独立镜面历史 |

## 产品与帧契约

- RT 是**可裁剪的扩展**：默认资产不启用；只允许高端质量档 + 显式启用的合格后端 + 运行时能力全部满足。首发验证目标 Windows x64 D3D12 DXR。PS5、Xbox Series 分开验收（各 SKU/质量档），不把主机 API 当作 Windows D3D12；没有 SDK/设备证据就保持关闭。移动、Web、D3D11、其余 PC 后端和低档位不出队、不分配 RT 资源、不变更原 SSGI/SSR fallback。功能不要求更改项目的默认 Graphics API。
- 两个独立消费者：启用 SSGI 时可选动态 probe fallback；启用 opaque SSR 时可选 DXR。仅反射不得触发探针更新，仅 GI 不派发反射射线。两者都关即为现有 URP。无独立 DDGI 模式、无 GI 接收面逐像素 DXR、无 Surface Cache、无限滚动、多跳递归、透明反射或移动端替代后端。
- 帧序（设计，不是实测）：确定合格 base camera 和场景版本 → **共同**准备实例/灯光并构建 TLAS（在第一个 RT 消费者之前）→ 预算内 probe 更新，完成后发布一致的可读快照 → 既有 Hi-Z 203、SSGI 205、opaque SSR 206（含补追/resolve）→ 207 绑定 → GBuffer 210 / Forward opaque 250 只消费一次 BRDF → opaque 后复位。TLAS/探针属于场景；trace 输出、曝光/历史、关键词属于相机。v1 单视图不透明 game base camera；overlay、preview、反射相机、未验证的 XR 走原路径并显式清空本相机 RT 状态。
- 共享的是场景、材质命中 ABI、ray mask 与线性 HDR 辐亮度单位，而不是方向分布、屏幕历史或滤波器。SS/HW 源切换时不得将噪声、缺预算或黑色当作天空；无资源必须回原路径。基础设施不引用 GI 或 SSR Volume；效果之间无启动依赖。调度失败/失去设备/管线销毁释放资源；不留上帧全局绑定。

## 里程碑与独立放行

各 ID 是可独立重跑的工作单元。每项先冻结测试输入，提交实现、测试、数据摘要和已知差异；依赖未就绪可用确定性 fixture 先验接口，不得以 fixture 代替 Player 集成签收。

| ID / 前置 | 交付 | 可单独复现的通过条件 |
|---|---|---|
| B0 / 无 | 冻结目标设备、版本、场景、灯光、相机轨迹、阈值、光线/毫秒/显存上限 | 同 HEAD 的 D3D11 和 D3D12 各自跑两遍 RT-off；关闭扩展与原路径同种子、同历史长度、同帧逐像素一致；记录现有测试筛选器及计数 |
| T1–T3 / B0 | 后端门控、RTAS 与命中着色 | 见 [TLAS 独立测试](research/TLAS.md)；用已知射线验证，**不要求打开 SSGI 或 SSR** |
| G1–G3 / B0；真实更新依赖 T3 | 数据面、分帧更新、SSGI 合成 | 见 [GI 验收](gi/HANDOFF.md)；关 SSR，能分别验证常量场、动态恢复和出屏转场 |
| R1–R3 / B0；真实补追依赖 T3 | 未解决状态、离屏着色、反射重建 | 见 [反射验收](reflection/HANDOFF.md)；关 GI，用自发光物体验证离屏轮廓 |
| I1 / T、G、R | 四开关组合、相机与资源隔离 | 同一场景分别跑都关/GI/SSR/两者开，Forward、Forward+、Deferred；双开共用同一 TLAS 版本；单开无另一模块 dispatch；切镜、分辨率变化、动态物体、相机堆叠和运行时关闭无上一相机残影/泄漏 |
| I2 / I1 | Player 画质/性能及平台签收 | Windows DXR 实派发 + 同机 D3D12 off + D3D11 off + 非目标构建剥离；记录下述指标。主机按目标分别构建、真机测量和批准，不阻塞 Windows 单独交付，不可据 PC 成绩默认开启 |

## 验证协议与回退

- [迁移的旧 P0](validation/P0/Baseline.json) 是 `720bedd` 的 **D3D11** 记录（EditMode 184、PlayMode 6、T12 34/34/35），不证明本轮 HEAD、DXR 或主机。保留原始记录/哈希和当时命令，不改写 `round`。新结果放 `validation/<ID>/<platform>-<renderer>/`，不覆盖 P0。实施时从 `URPSample01` 运行原有 [EditMode](../../Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14EditModeTests.ps1)、[PlayMode](../../Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14PlayModeTests.ps1)、[T12](../../Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14GraphicsRegression.ps1)；新 DXR 合同另加 development Player，记录构建设置恢复结果。
- 每个证据包记录基线/实现 SHA、设备/SKU/驱动/API/质量档、分辨率、场景版本、种子、曝光、帧号、预热帧数、计时窗口、输入/输出路径、实际值/门槛、pass/fail/blocked。图像对比在线性 HDR、同轨迹同帧；高采样参考与低预算候选用相同材质和光源。GI 比 ROI 误差和出屏亮度跳变；反射比正确轮廓覆盖率和 SS/HW 切换亮度差，而不是只看非黑像素。
- 性能按 TLAS CPU/GPU、probe trace/update、SSR trace/shade、重建分别计，另记总帧差、峰值显存、合格/请求/派发/命中/真 miss/未服务数。建议基线 1080p、固定轨迹、预热后 120 帧 × 3，给中位数和 P95；冷启动另列。B0 在看候选结果**之前**签定数值预算，未定预算不报性能通过。计数为零、全黑、无 NaN 或 Editor 截图都不能单独证明 DXR 生效。
- 每个模块独立关回原路径，无 shader error、无 RT build/dispatch/常驻图集；失败优先收窄支持材质、实例范围或光线预算，不静默扩大适用平台。每个里程碑更新状态和证据指针；只在真实 Player 通过后把“计划”改为“支持”。本轮仅交付 Markdown 和原证据迁移。
