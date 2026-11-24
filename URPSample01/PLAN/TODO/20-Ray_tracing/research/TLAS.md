# RT scene / TLAS：共享服务而非效果私有资源

研究与拟议实现，未测真机。生命周期由 renderer/pipeline 管理，GI 和反射只借用已构建的场景快照；后端失败不能使基础 URP 失效。

## 证据与 API 边界

- HDRP 14 包（根目录见 [研究索引](HDRP14.md)）：`Runtime/RenderPipeline/Raytracing/HDRaytracingManager.cs:538–599,817–854` 按 camera/effect 决定是否需要 RTAS，内部获取接口不是 URP 公共 API。`HDRaytracingManager.HDRTASManager.cs:94–125,276–365`（同目录）复用对象、清实例、按效果和材质 Cull，再 Build；path tracing 的 transform dirty 不是 Build 开关。**不要声称 HDRP 只在脏帧构建。**
- `E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Runtime/DDGIFeature.cs:309–311,451–486,652` 是 Unity 自动管理实例、pass 自持 RTAS，Build → bind → 选命中 pass → DispatchRays，并释放；示例收敛时跳过不能作为动态场景刷新策略。`RASSettings(..., 255)` 的 255 是 **GameObject layer mask 的低 8 层**，不是全部层。
- Unity 2022.3.62f1 本机 Editor 的 `Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.xml:11330–11347,11563–11624,11641–11688,11949–11957,35216–35223,37941–37950`：`UnityEngine.Experimental.Rendering.RayTracingAccelerationStructure`、实例增删、mask/transform、`CommandBuffer.BuildRayTracingAccelerationStructure` 与直接 `DispatchRays`。变换更新不是蒙皮网格更新，Build 也不保证底层一定是 refit。在该版公开接口核查中没有找到 GPU 计数器驱动的间接 DispatchRays；**v1 不依赖 inline ray query、GPU 读回计数或间接 DXR**。
- HDRP `Documentation~/Ray-Tracing-Getting-Started.md:60–68` 及 manager 的 Editor build-target 条件不是主机实机证明。后端准入需要白名单构建目标、资产 opt-in、高端档、运行时 supportsRayTracing、compute、所需 UAV 格式、已包含的 RT shader/pass 全部成立。PC 另限 Windows D3D12；专用主机后端单独验证。失败一次诊断、按相机关闭；非目标构建剥离 RT 资源/变体并验证无不支持 shader 报错。

## 收录、维护与着色合同（项目设计）

1. v1 单场景快照、统一世界坐标。明确登记可贡献的 Renderer（注册/注销或场景生命周期初次枚举），不要逐相机全场查找，也不能按相机视锥直接剔除离屏贡献者。区分 **32 位游戏层选择**、**8 位效果实例位**（GI/反射/可选阴影），以及 submesh enable/alpha 策略。v1 先覆盖有 RT pass 的不透明静态/刚体 Lit 和自发光；不支持子网格显式排除并统计。透明、蒙皮/动态拓扑、alpha test 没有专门的 any-hit/变形用例就不宣称支持。
2. 建立实例集合与场景 revision。启停/加载卸载/LOD/网格或材质换槽更新成员或命中表，刚体变换走 transform 更新；灯光/发光参数影响着色或 probe 失效，不应无条件重建几何。合格订阅者 + 脏版本才请求 Build；建成后在相同过滤配置、原点及 revision 的相机/消费者间共享，不兼容时分开或降级。区分“提交 Build 次数”和驱动 BLAS/TLAS 内部行为。首帧无有效快照回原效果，卸载或管线重建解除绑定并 Dispose；跨相机不要复用 ray 输出。
3. 共同 ABI：请求为世界空间 origin、归一化 direction、TMin/TMax、实例 mask；屏幕像素/探针索引由调用方做映射。输出区分几何命中（距离、材质有效性、沿返回方向的线性 HDR 辐亮度）、范围内无交点、无效材质/未服务。`TMin` 只做与尺度相符的表面偏移。距离截断不证明天空可见。对不支持材质/背面约定显式计数，不把无效结果当黑色命中。
4. 最小 Hit Lighting：命中点 UV/贴图、法线、albedo、emission，先支持确定性的一盏方向光和可选一条遮挡射线；外加局部灯须有世界空间命中点可查询的灯表，**不能只依赖主摄像机 visibleLights**。不重复乘接收面 albedo/BRDF。GI probe 更新不读取自己正在写的 atlas，v1 无反馈；SSR 不因启用而启动 probe 更新，默认没有 GI 时依然能着色。材质/灯光近似与屏内结果的差异用 A/B 量化。

## 可独立签收

| ID | 输入与执行 | 通过标准 / 留存 |
|---|---|---|
| T1 门控与资源 | 相机/质量/后端组合；订阅者 0→1→2→0、场景卸载、管线重载 | 关或不支持时 RTAS/图集分配和派发均为 0；双开只持有一个兼容快照；无效相机不继承有效 flag，重复开关后无净资源增长。输出 capability decision 与资源计数 |
| T2 实例与调度 | 固定几何和已知射线；移入/出视锥、移动、禁用、卸载、layer 高位和多材质换槽；两个相机 | 预期对象/距离与 CPU 解析交点一致到 B0 容差；离屏仍命中、禁用或错 mask 不命中；静止不重复请求 Build，动态同版本一次，灯光改变不改实例集合。记录 frame/revision/build/实例计数及命中 ID |
| T3 命中着色 Player | 单色自发光板、方向光白板和遮挡物；关闭 GI/SSR 单独发已知射线；比较 emit-only、direct-only、shadow | Windows DXR Player 证明确有 Build/bind/直接 DispatchRays；曝光对齐后与解析/高采样参考误差在 B0 容差内、非有限值 0；遮挡开关影响正确，miss 与无效分开。证据含材质列表、GPU marker、输出和资源释放；主机另测同套向量 |

若无法在目标后端证明正确派发，G/R 的合成仍可用 fixture 验证，但真实 RT 路径保持禁用；预算与发布门禁见[总计划](../HANDOFF.md)。
