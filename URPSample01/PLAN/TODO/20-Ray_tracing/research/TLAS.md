# RT scene / TLAS：构建、维护与共享服务

**状态：T1 门控/资源生命周期已在 Windows Editor 的 [D3D11](../validation/T1/Win64-D3D11/run-04/Tests.xml) 和 [D3D12](../validation/T1/Win64-D3D12/run-04/Tests.xml) 验证；[T2 的 Win64 D3D12 测试 Player](../validation/T2/verified/Player-D3D12.json)已验证显式登记、条件 Build 和解析求交；[T3 Win64 Player](../validation/T3/Summary.json) 已验证包内基础材质的实际派发、直射、自发光与次级阴影；全材质、相机集成和主机不在此证据范围。** 生命周期由 renderer/pipeline 管理，GI 和反射只借用已构建的场景快照；后端失败不能使基础 URP 失效。本文补充 2022.3 可用接口、构建/失效策略与材质命中合同。

## 2022.3 可用接口（已核实）

证据：`D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.xml`。

| API | 行 | 事实 |
|---|---|---|
| `RayTracingAccelerationStructure` | 11325 | `UnityEngine.Experimental.Rendering`；不是 URP 公共 API |
| `AddInstance(Renderer, …)` | 11330–11500 | 支持 submesh 选择、transparent 标记、triangle culling、8 位 mask、instance id |
| `RASSettings(ManagementMode, RayTracingModeMask, int layerMask)` | 11598 | `layerMask` 是 **32 位** GameObject 层过滤；`RayTracingModeMask` 过滤 Renderer 的 `RayTracingMode` |
| `RayTracingMode.Static / DynamicTransform / DynamicGeometry / Off` | 11944 | Static 永不更新；DynamicTransform 只更新 transform；DynamicGeometry 更新 Mesh+transform |
| `UpdateInstanceTransform / UpdateInstanceMask / UpdateInstanceID / UpdateInstancePropertyBlock` | 11667–11700 | 逐实例更新；mask 是 **8 位**，与 ray 的 `TraceRay` mask 对齐 |
| `RemoveInstance / ClearInstances / Dispose` | 11519,11545,11641 | 生命周期由调用方负责 |
| `Update(relativeOrigin)` | 11661 | 更新全部实例 transform |
| `CullInstances(RayTracingInstanceCullingConfig&)` | 11536 | 按 layer/ShadowCastingMode/Material/LOD/plane/sphere 配置过滤；结果含 `transformsChanged`/`materialsCRC`（11819–11824）。HDRP 可选 extended frustum，v1 不搬 |
| `CommandBuffer.BuildRayTracingAccelerationStructure(rtas)` | 35216 | 本项目选用的构建提交入口（另有对象方法 `Build()`/`Build(Vector3)`，11507、11513） |
| `CommandBuffer.DispatchRays(RayTracingShader, string, uint, uint, uint, Camera)` | 35509 | 宽度/高度由 CPU 指定；没有 GPU 计数间接派发 |
| `RayTracingShader.SetAccelerationStructure` | 11989 | 绑定 RTAS；配合 `CommandBuffer.SetRayTracingShaderPass` 选择材质命中 pass |

**不要声称** Unity 暴露了显式 refit、BLAS/TLAS 分离计数或“只在脏帧构建”的自动行为。`Build` 是把待应用的实例/transform 变更提交到 GPU 的唯一入口；是否发生 BLAS 重建/refit 由驱动与 Unity 内部分析决定。能记录的是“提交 Build 的次数、实例数、场景 revision 与 GPU marker”。

## 证据与 API 边界

- HDRP 14 包（根目录见 [研究索引](HDRP14.md)）：`Runtime/RenderPipeline/Raytracing/HDRaytracingManager.cs:538–599,817–854` 按 camera/effect 决定是否需要 RTAS，内部获取接口不是 URP 公共 API。`HDRaytracingManager.HDRTASManager.cs:94–125,276–365`（同目录）复用对象、清实例、按效果和材质 Cull，再 Build；path tracing 的 transform dirty 不是 Build 开关。**不要声称 HDRP 只在脏帧构建。**
- `E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Runtime/DDGIFeature.cs:309–311,451–486,652` 是 Unity 自动管理实例、pass 自持 RTAS，Build → bind → 选命中 pass → DispatchRays，并释放；示例收敛时跳过不能作为动态场景刷新策略。`RASSettings(..., 255)` 的 255 是 **GameObject layer mask 的低 8 层**，不是全部层。
- Unity 2022.3.62f1 本机 Editor：上述公开接口核查中没有找到 GPU 计数器驱动的间接 DispatchRays；**v1 不依赖 inline ray query、GPU 读回计数或间接 DXR**。
- HDRP `Documentation~/Ray-Tracing-Getting-Started.md:60–68` 及 manager 的 Editor build-target 条件不是主机实机证明。后端准入需要白名单构建目标、资产 opt-in、高端档、运行时 supportsRayTracing、compute、所需 UAV 格式、已包含的 RT shader/pass 全部成立。PC 另限 Windows D3D12；专用主机后端单独验证。失败一次诊断、按相机关闭；非目标构建剥离 RT 资源/变体并验证无不支持 shader 报错。

## 构建、失效与共享策略（项目设计）

1. **场景快照，不是相机资源**。TLAS 以世界坐标和场景 revision 为键；两个消费者（GI/SSR）在同一 revision、同一过滤配置与原点上共享。相机切换不重建静态场景；只在成员/transform 变更时提交 Build。v1 单场景快照，不做多场景拼接或滚动原点。
2. **成员登记**。明确登记可贡献的 Renderer（注册/注销或场景生命周期初次枚举），不要逐相机全场查找，也不能按相机视锥剔除离屏贡献者。区分 **32 位 layer 过滤**（RASSettings）、**8 位效果实例 mask**（AddInstance/likely GI/反射）、以及每个 submesh 的 `RayTracingSubMeshFlags`。登记映射：不支持 → `Disabled`；受支持的不透明表面 → `Enabled | ClosestHitOnly`；cutout → `Enabled`（不加 `ClosestHitOnly`）。只有算法要求每个交点最多调用一次 AHS 时才加 `UniqueAnyHitCalls`。旧 bool 数组重载的等价设置是禁用不支持的 submesh，并仅将 cutout 的 `subMeshTransparencyFlags[i]` 置 true；这是遍历标记，不是透明混合支持（CoreModule XML:11336–11338,12261–12285）。v1 先覆盖有 `GlxDXR` pass 的不透明静态/刚体 Lit、cutout 和自发光；不支持子网格显式排除并统计。透明、蒙皮/动态拓扑仍不宣称支持；cutout 由 AnyHit（`_ALPHATEST_ON` + `IgnoreHit`）支持，但仅限登记材质。`CullInstances` 可以做 layer/material/LOD 过滤，但 **v1 不用 plane/frustum 裁剪**：屏外几何仍可能被 GI/反射射线命中，HDRP 的 extended frustum 方案不搬。
3. **dirty 与 Build**。场景启停/加载卸载/LOD/网格或材质换槽更新成员或命中表；刚体变换走 `UpdateInstanceTransform` + `Build`；灯光/发光参数影响着色或 probe 失效，不应无条件重建几何。用 `CullInstances` 的 `transformsChanged`/`materialsCRC` 做 dirty 判定，不手写全场景每帧扫描。静态成员不重复 Build；动态成员按 revision 只提交一次；`RayTracingMode.Static` 的 Renderer 不因 transform 改动自动更新，需要显式 `UpdateInstanceTransform`。区分“提交 Build 次数”和驱动内部 BLAS/TLAS 工作。
4. **命中材质入口与全材质 Fat CHS**。Unity 的 closest-hit 来自材质 shader 中与 `SetRayTracingShaderPass` 同名的 DXR pass（HDRP 的 `IndirectDXR`/`GBufferDXR`/`PathTracingDXR` 等；**不是** `LightMode="RayTracing"` tag）；URP 内置 Lit/ComplexLit 默认没有这个 pass。v1 给**进入 TLAS 且可被命中**的子集加 `GlxDXR` pass（CHS + AHS）：`UnityRayTracingFetchVertexAttribute3/4` 取 BLAS 顶点，per-material 绑 `_BaseMap/_BumpMap/_MetallicGlossMap/_EmissionMap`，`_ALPHATEST_ON` 走 AnyHit；不需要 bindless/顶点池/atlas。材质属性、贴图与自发光必须能被命中 shader 读到；不重复乘接收面 BRDF。屏内外亮度差用 A/B 量化。方法见 [HitShading](HitShading.md)。
5. **灯光与命中着色**。场景级灯表收录 Directional/Point/Spot，所有选中灯可对命中点算直接光；逐灯阴影受 `LmaxHit/Smax` 预算约束；**不能只依赖主摄像机 visibleLights**。GI probe 反馈只读**上一帧已发布** atlas，不读正在写的 atlas；SSR 不因启用而启动 probe 更新，默认没有 GI 时依然能着色。
6. **失效与清理**。首帧无有效快照回原效果；卸载、管线重建、设备丢失、渲染器销毁时解除绑定并 Dispose；不留上帧全局绑定。每个相机在关闭扩展时显式清空本相机 RT 状态，overlay/preview/reflection 相机走原路径且不得继承有效 flag。重复开关后资源净增长为 0。
7. **预算与诊断**。记录 TLAS CPU 提交时间、GPU marker、实例数、Build 次数、峰值显存、命中/未命中/无效材质计数。TLAS 失败一次后按相机关闭并回原路径，不静默扩大支持平台。

## 共同 ABI（请求/输出）

请求为世界空间 origin、归一化 direction、TMin/TMax、实例 mask；屏幕像素/探针索引由调用方做映射。命中材质由 `GlxDXR` CHS/AHS 提供；payload 含 hitT/valid/flags，阴影射线用统一 payload 的 mode/segment 区分，不为阴影另做材质 pass。输出区分几何命中（距离、材质有效性、沿返回方向的线性 HDR 辐亮度）、范围内无交点、无效材质/未服务。`TMin` 只做与尺度相符的表面偏移。距离截断不证明天空可见。对不支持材质/背面约定显式计数，不把无效结果当黑色命中。成员登记、材质入口与灯光合同见上一节；材质/灯光近似与屏内结果的差异用 A/B 量化。

## 可独立签收

| ID | 输入与执行 | 通过标准 / 留存 |
|---|---|---|
| T1 门控与资源 | 相机/质量/后端组合；订阅者 0→1→2→0、场景卸载、管线重载 | 关或不支持时 RTAS/图集分配和派发均为 0；双开只持有一个兼容快照；无效相机不继承有效 flag，重复开关后无净资源增长。输出 capability decision 与资源计数 |
| T2 实例与调度 | 固定几何和已知射线；移入/出视锥、移动、禁用、卸载、layer 高位和多材质换槽；两个相机 | 预期对象/距离与 CPU 解析交点一致到 B0 容差；离屏仍命中、禁用或错 mask 不命中；静止不重复请求 Build，动态同版本一次，灯光改变不改实例集合。记录 frame/revision/build/实例计数及命中 ID。另用绕序已知的三角形从两侧发射，再从封闭盒内发射固定 probe 射线；固定 `frontTriangleCounterClockwise`，交叉测试 `enableTriangleCulling` true（默认）/false 与 ray 不裁面/裁背面，记录 flags、`HitKind`、`hitT`。同一 TLAS 配置须同时通过 probe 背面距离与反射背面策略的验证，不预设某组 flag 必然有效 |
| T3 基础命中着色 Player | 单色自发光板、方向光白板和遮挡物；关闭 GI/SSR 单独发已知射线；比较 emit-only、direct-only、shadow | Windows DXR Player 证明确有 Build/bind/直接 DispatchRays；曝光对齐后与解析/高采样参考误差在 B0 容差内、非有限值 0；遮挡开关影响正确，miss 与无效分开。证据含材质列表、GPU marker、输出和资源释放；主机另测同套向量 |
| T3b 全材质 pass | 登记 Lit（不同贴图分辨率/压缩）、法线、metallic/smoothness、cutout、自发光；已知射线 | BLAS 顶点属性/贴图/法线与光栅一致到 B0；对同一组射线记录 opaque/cutout/不支持子网格的实际 flags 与 AHS 调用计数；opaque 不调用 AHS，cutout 实心处命中、孔洞可见后方目标，不支持子网格无交点；payload 读回符合布局；记录 payload 字节、pass/variant 与 supported/unsupported |
| T4 灯表与阴影 | 多 Directional/Point/Spot；移动/删除/禁用；阴影开关；登记数超过 `Lmax`，每命中灯数/阴影射线超过 `LmaxHit/Smax` | 灯表不依赖 `visibleLights`；直接光/阴影与解析参考一致到 B0；移动/删除后更新；上传、命中选灯、阴影预算三层溢出不混计，分别记录 |
| T2b 材质覆盖（可选，并入 T2/T3b） | Lit/cutout/自发光/不支持材质/多 submesh；切换 LOD、禁用 Renderer | 只有已登记子集参与；不支持材质与 submesh 显式计数；LOD 切换后命中几何与光栅一致到 B0 容差；删除/禁用后不再命中 |

若无法在目标后端证明正确派发，G/R 的合成仍可用 fixture 验证，但真实 RT 路径保持禁用；预算与发布门禁见[总计划](../HANDOFF.md)。
