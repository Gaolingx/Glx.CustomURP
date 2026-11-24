# T9 已知差异：HDRP 14.0.12 与本 URP fork

下表按渲染阶段区分**实现选择**、**平台限制**和**证据缺口**。比较对象是 HDRP 的屏幕空间 Contact Shadows，不包含 ray tracing；本轮没有 DXR、TLAS 或 T10 实现。AC 编号对应 [T8 验收报告](report.md)。

| 阶段 | HDRP 对照与本实现 | 使用时的影响 / 验证范围 |
|---|---|---|
| 启用与选灯 | HDRP 使用管线/灯自身的接触阴影配置；URP 需在 Universal Renderer Data 添加 Feature，在 Volume Profile 中启用 `Shadowing/Contact Shadows`，并在同一个 Light GameObject 上启用 `ContactShadowLight`。没有修改已有 Light Inspector 或 `UniversalAdditionalLightData`。 | 父子对象上的标记不能替代本灯标记。Volume 或 Feature 关闭、无可参与灯时不产生暗化；方向光、点光、聚光为目标，Rectangle 不在范围内。没有 HDRP Quality 档位，步数由 Volume 直接指定。见 `validation/T1/FeatureImport.json`、`validation/T4/D3D11/EditMode.xml`。 |
| 相机与深度输入 | HDRP LightLoop 消费深度金字塔；URP 与 SSL/GTAO 共享 packed atlas。取表面用 mip 0，射线先取 mip 1，候选必须在同一个 t 由 mip 0 确认，不重复下采样。 | **AC-14 PARTIAL**：非 XR D3D11 的 Contact-only 与 Contact+SSL+GTAO 各记录一次执行、一次 mip 0 copy；两端均有粗/细采样用例。batch Frame Debugger 没有事件列表；按相机资源隔离不是逐眼 GPU 捕获。见 [共享金字塔证据](validation/T8/Audit/SharedPyramid.json)。 |
| 射线与调度 | 与 HDRP 一样，从可见表面向灯投射屏幕空间短射线，使用 bias、thickness、距离和屏边淡出；无屏外几何信息。URP 使用全分辨率 compute，按参与槽循环，不使用 HDRP 的 tile 灯表来裁剪每像素循环。 | 不替代长距离 shadow map，不提供半分辨率上采样或时域积累。min/max 为近远范围，fadeIn/fade 在范围端点内渐变。命中、画面边缘和视角证据见 `validation/T8/Matrix/D3D12/matrix.json`。 |
| 打包与逐灯隔离 | 全分辨率 `R32_UInt` 的低 24 位是各灯命中位，高 8 位是**该像素所有命中灯共享的最大 fade**，并非每灯 8 位。主光若参与先取 bit 0，否则首个附加光可取 bit 0；总数最多 24。 | 槽位是可见灯的本帧映射，不是固定的灯对象 ID。第 25 个候选没有独立 bit。AC-9：隔离接收区不变，但移走重叠处提供最大 fade 的灯，可改变仍命中的其他灯的衰减。矩阵实测拥挤 ROI 有 3 像素变化；不能把它解释为残留槽，也不能承诺关闭一灯后所有重叠像素不变。 |
| Forward / Forward+ 光照 | 不透明 Lit 从 `Shadows.hlsl` 合并 contact 与常规阴影；主/附加光即使 `Light.shadows=None` 也可产生接触暗部。没有新增材质 shader 关键字。 | `Receive Shadows=Off` 遵循 URP 材质选择，接触阴影也关闭；不承诺与 HDRP 每种材质逐像素相同。透明表面不接收；BakedLit 和不走这些光照函数的自定义 shader 不保证接收。见 `validation/P5/D3D11/Direct3D11/Forward/Receive-False.json` 与矩阵的同位置 Lit 阳性对照。 |
| 聚类 Deferred 光照（D10） | 本 fork 是 **clustered compute**，不是 stencil 逐灯绘制。主光不依赖 shadow map；附加光必须同时满足 Asset 允许附加阴影、该灯有有效实时阴影切片、强度非零。 | 当前灯 None 时另一盏灯投影也不能代其取得资格；全 None、Off 变体保留或 Asset 关闭均不能绕过条件。此限制影响光照消费，不改变三模式的 compute mask。使用 `renderingModeActual` 和 Deferred D10 A/B，不接受 Forward fallback 代验。见 `validation/P5/D3D12/Direct3D12/Deferred/`。 |
| 清理与调试 | 活动帧在不透明光照前绑定实际纹理，在不透明阶段后恢复 1×1 中性纹理并清零掩码；零掩码在纹理读取前退出。URP debug 展示 mask、fade、原始打包和指定槽，不是 HDRP debug 系统。 | 多相机、运行时切换与 Scene View 有各自测试；不能由其推断 XR。历史 debug 图可能共用文件：按 [PNG 别名和 SHA](validation/T8/Audit/PngArchive.json) 取 canonical，不按旧日志中的绝对路径寻找。 |

## 后端、构建与成本

- **平台。** 能力门槛是受支持的深度约定、Compute 与 `R32_UInt` LoadStore。实测为 Windows D3D11/D3D12；Vulkan、Metal 仅在能力 gate 内，未验证。GLES/OpenGL/WebGL 不在支持范围。XR（包括逐眼/切片和动态分辨率组合）未实测。
- **相机矩阵。** 两个 D3D 后端均验证透视场景、正交、Scene View 和实际 4× MSAA。D3D12 请求 75% 后实宽由 256 变 192；D3D11 保持 256，该项 **skipped**，不是以原生宽度通过 DRS。见 [D3D11](validation/T8/Matrix/D3D11/matrix.json) 和 [D3D12](validation/T8/Matrix/D3D12/matrix.json)。
- **性能。** RTX 5060 Ti，1920×1080、4 灯/10 步，仅计接触阴影 compute。Forward / Forward+ / Deferred 的 GPU p95（ms）：D3D11 为 0.494592 / 0.492288 / 0.499968；D3D12 为 0.526336 / 0.520704 / 0.520960。六格均过 T0 的 ≤1.0 ms 参考线；上限 24 槽时不承诺这一预算。CPU 录制、共享金字塔和整帧 GPU 耗时不是同一指标。
- **变体与回归。** 新增 `_CONTACT_SHADOWS_DEFERRED` 是 compute 专用的开/关轴，不是 Lit 材质轴。旧 `ComputeDeclaresTheDocumentedKeywordMatrix` 保留不改：期望八轴 1280，实际九轴 2560。新增独立 `ContactShadowShaderBudgetTests` 分别验证八轴基数与完整声明。接受这个实现差异不等于历史测试通过，故 **AC-15 PARTIAL**。编译/剥离、SRP Batcher 比较和 Release/Development Player 运行是不同证据，不能用 build 成功替代旧预算断言。
