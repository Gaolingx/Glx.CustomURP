# GI 探针 P0：天空输入、距离矩采样方向、八面体接缝边界

**状态：** 实现完成（C# 0 error），设备侧 A/B 与 G1 XML 复跑待取。**这不是签收。**
**范围：** RTXGI-DDGI 借鉴清单的 P0 三项（正确性），不含距离矩窄瓣加权（P1-4）、bias/体积淡出（P1-6）、查询稳健化（P1-5）。
**基线：** 本轮改动前的 HEAD（G2/G3c 已签收版本）。**不覆盖** `PLAN/Done/20-Ray_tracing/validation/P0/`。

## 一、改动

### P0-1 天空 miss 项（含距离矩）

契约（沿用 G1 已定义、CPU 参考已建模的语义）：天空样本是**已测量**样本；只有“射线逃逸了所有已注册几何”才写天空，有限 `TMax` 截断仍记为未解决。

- 新 flag `GlxSky = 8`：`Shaders/RayTracing/GlxBasicPayload.hlsl`。
- 辐照射线 `TMax = FLT_MAX`，维护（固定 32 条）射线保持 `_GlxProbeTMax`（30）：`GlxProbeUpdate.compute` `ProbePrepare`。因此“截断”只可能出现在维护射线，radiance 射线 miss 即逃逸。
- miss shader：`mode == GlxProbeMode` 时沿 `WorldRayDirection()` 采样环境 cubemap，写 `radiance = DecodeHDREnvironment(...)`、`hitT = RayTMax()`、`flags = GlxSky`；其余模式仍 `GlxMiss`：`GlxProbeTrace.raytrace`。
- 卷积把 `GLX_HIT | GLX_SKY` 都当有效样本；距离矩对天空取 `min(hitT, momentCap)`，开放方向不再存 0 均值（否则 Chebyshev 把开阔方向全判遮挡）：`GlxProbeUpdate.compute` 两个 kernel。
- 天空源：批次内**显式**绑定 `ReflectionProbe.defaultTexture` / `defaultTextureHDRDecodeValues` 到 `_GlxProbeSkyCube` / `_GlxProbeSkyCubeHDR`（`RayTracingProbeUpdate.Record`）。原因：每相机 `_GlossyEnvironmentCubeMap` 在 `SetupPerCameraShaderConstants` 只记录进相机 cmd，晚于 `AddRenderPasses` 里本批次的 `Graphics.ExecuteCommandBuffer`，读全局会晚一帧/首帧未绑。
- 计数器：`_GlxProbeCounters[7]` 由 “reserved” 改为 sky 计数（0–4 与 5/6 语义不变）。

### P0-2 距离矩采样方向

距离图按“从 probe 向外”的方向索引。查询处的 `direction` 是**着色点→probe**（wrap 项需要它），可见性测试必须采反方向：`GlxProbeQuery.hlsl` 改为 `GlxSampleProbeMoments(probe, GlxOctEncode(-direction))`。此前 P→Q 采样会取到近似对跖 texel，遮挡判定偏松。

### P0-3 八面体接缝边界

- GPU：删除镜像 `GlxFold`，改为 RTXGI 的 `(srcX, srcY, dstX, dstY)` 拷贝表（6×6 用 28 条、14×14 用 60 条）。先算 6×6/14×14 内区，`AllMemoryBarrierWithGroupSync()` 后由组内线程拷边界；inactive probe 整组跳过（组内一致，无屏障分歧）。
- CPU 参考：`ProbeMathReference` 新增 `Gutter6`/`Gutter14` 与 `Gutter(n)`，`FillGutter` 改为查表；`Query`（合成径向可见性夹具）与 `Convolve` 不变——参考模型本来就按“天空=已测量”建模。
- 测试：`OctahedralInteriorAndFoldedGutters` → `OctahedralInteriorAndSeamGutters`，逐条校验表的源是内区、目标只填一次、边界值等于源内区值；新增 `SkyIsAMeasuredSampleForDistanceMoments` 锁定“天空进入距离矩”。

## 二、门禁

| 门禁 | 结果 |
|---|---|
| `dotnet build Unity.RenderPipelines.Universal.Runtime.csproj` | 0 error |
| `dotnet build Unity.RenderPipelines.Universal.Editor.csproj` | 0 error |
| `dotnet build Unity.RayTracing.Tests.Editor.csproj` | 0 error |
| G1 EditMode XML 复跑 | **PASS 14/14**（[Tests.xml](validation/G1/Win64-Editor/Tests.xml)，SHA-1 `f93d30c4…`） |
| D3D11 编辑器 shader 导入（`GlxProbeTrace`/`GlxProbeUpdate`） | **PASS 0 error**（见 [README](validation/G1/Win64-Editor/README.md)） |
| D3D11/D3D12 Player 生产者 A/B | **待取** |
| GPU ms | 未测 |

实现期修掉的两个编译问题（记录，避免回退）：D3D11 raytracing 不认 `RayTMax()`，天空距离改用共享常量 `GLX_PROBE_SKY_TMAX`（`GlxProbeRanges.hlsl`）；FXC 判定“屏障前提前返回”为可变控制流并报非法同步，两个卷积 kernel 改为无提前返回、屏障置于顶层。

## 三、设备 A/B（用现有 SaveAtlas/ReadProbe）

同一场景、同一构建之外的旧/新 HEAD 各跑一次 G2 风格夹具（常量天空 + 开放场 + 一个封闭盒 + 一面 6 cm 薄墙）：

1. **天空能量**：开放场探针 `ReadProbe` 的 6×6 辐照 tile 由近 0 变为 `E/π = Lsky`（通过 `SaveAtlas` 对比 irr 原始 half 字节）；封闭盒内探针仍为 0。`ReadCounters[7]`（sky）在开放场 >0，在仅维护射线/封闭场为 0。
2. **距离矩**：开放方向 texel 的 `moments.x` 从 0 变为 `momentCap`（`1.5×spacing`），`SaveAtlas` 的 RG16 原始字节可直接比对；封闭方向不变。
3. **遮挡方向（P0-2）**：薄墙背面接收点在两版下的 `E` 对比——旧版因采样对跖方向、遮挡不足，新版本应更暗；同时墙正面不应变暗（方向正负各测一次，避免把“整体变暗”误判为修复）。
4. **接缝（P0-3）**：对单个 probe 逐个比较 28/60 个边界 texel：新值必须等于表源内区 texel；旧值与镜像值不同处应集中在非角边界（角上两者可能相同）。图像侧看八面体接缝方向附近是否还有细线。

所有证据写入 `PLAN/TODO/24-GI-P0Correctness/validation/<G2|G3>/<platform>-<renderer>/`，附 GUID、输入哈希、设置恢复与原始 half 字节。

## 四、已知影响与未覆盖

- radiance 射线由有限 30 变为无限：远于 30 的遮挡现在计入（更正确），单条射线耗时可能略增；`_GlxProbeTMax` 仍用于维护射线与分类。
- 天空依赖 `ReflectionProbe.defaultTexture`（默认反射 = Skybox 源）；无天空/默认反射为黑时天空样本为有效黑色，符合 G1 的“有效黑色 ≠ 未解决”。
- 未改：距离矩权重（仍是余弦，不是 `dot^64`）、查询 bias/体积淡出、Chebyshev crush、固化/可视化、每帧全量调度。
- 本轮不触碰 SSGI 的 `rayMiss` 语义：天空输入只属于生产者，消费者仍按 G3c 的 coverage/ownership 规则。
