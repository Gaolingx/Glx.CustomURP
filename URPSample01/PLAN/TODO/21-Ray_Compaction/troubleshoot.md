# R2b TMin 续传评审记录

**状态：** 评审结论，待 Phase 0/1 实测验证
**日期：** 2026-11-26
**代码基线：** HEAD `9f76ebc`（`docs(plan): correct R2b classification and TMin safety rules`）
**关联：** [R2b-RayCompaction-TMin-Research-and-Plan.md](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/TODO/21-Ray_Compaction/R2b-RayCompaction-TMin-Research-and-Plan.md)

## 结论速览

- **StepLimit 允许续传成立**，但“收益主要来源”取决于着色占比与 latch 误伤率（§1）。
- **近平面不续传正确**，且代价比预期更小（§2）。
- **`R1Cold` 的续传在 v1 不可行也不必要**：冷帧根本不派发 SS trace（§3.2）。

## 原始判断与处置

| 原始判断 / 验证点 | 处置 |
|---|---|
| StepLimit 属于“已走段确认空”，是 64 步上限下的常见出口，也是收益主要来源 | 同意安全论证；收益有条件，见 §1 |
| 若 `N_safe/N_miss` 过低，说明 mip0 latch 过于保守 | 指标不足，改用“跳过距离 × 命中率”；并建议用冻结前缀替代整条否决，见 §1.3、§3.3 |
| 近平面（`rayTowardsEye` 时 `bounds.z = 1`）不续传 | 同意；依赖 reversed-Z，且这类射线几乎无收益，见 §2 |
| `clipDist` 必须从 `tMax` 块提升，不能与循环内 cell `dist` 混用 | 必须；补退化情况与分类优先级，见 §3.1 |
| `R1Cold` 也要绑定 TraceState，否则首帧续传行为与主路径不一致 | 修正：冷帧没有 trace，不存在“绑定”；v1 明确不续传，见 §3.2 |
| `everBelowFloor` 收益/保守度用 `N_safe/N_miss` 实测，低则回退为只对 Offscreen 续传 | 回退方案与“StepLimit 是主收益”自相矛盾；阶梯见 §1.4 |

## 1. StepLimit 续传：同意，但“收益主要来源”有三个前提

**同意安全论证**：`t` 走完一段 mip0 空段后在 64 步耗尽，`safeWS` 落在最后一个已证明为空的 cell 边界，续传是合法的。

但有三个反直觉点，决定它是否真是收益来源。

### 1.1 TMin 不减少 launch 数

K 固定，raygen 早退只省未选中线程；TMin 省的只是“被选中 K 条”的遍历。所以收益 ≈ `served/N` 占用率 × 平均跳过距离 × 单位遍历成本，`N_safe/N_miss` 本身不预测 ms。

SSR 的命中着色是 Fat CHS + 灯表 + 最多 2 条阴影（[GlxHitLights.hlsl:112](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/RayTracing/GlxHitLights.hlsl#L112)）。对 StepLimit 这类“走得远、多半会命中”的射线，shading 可能占大头，跳过遍历的绝对值会比预期小。

### 1.2 StepLimit 恰恰最容易被 latch 误伤

长掠射射线既容易耗尽步数，也最容易在薄物体/厚度 bias 上产生一次瞬时 `belowFloor`。所以“StepLimit 是主收益”和“mip0 latch 太保守”是同一批样本上的两个对立面，必须看联合分布，而不是两个独立计数。

### 1.3 用冻结前缀替代整条否决

文档现在的 veto 丢掉了合法前缀：`everBelowFloor → safeWS = 0` 太粗。第一个 `belowFloor` 之前的最后空点依然是**已证明为空**的前缀，HW 从那里起追会重新穿过遮挡 cell，不可能漏遮挡。

建议把规则从“有遮挡历史就整条放弃”改成**冻结前缀**：

```hlsl
if (mipLevel == 0 && belowFloor)
{
    everBelowFloor = true;      // 只用于诊断/计数
    frozen = true;              // 停止更新 safeWS，保留到此为止的前缀
}
if (!frozen && !belowFloor && !hitFloor) safeWS = ...;
```

这样 `N_safe` 会显著上升，且保守性不变（前缀是同一个 HZB 证据、同一套 pullback）。代价是 `everBelowFloor` 从门控降级为计数器，Phase 1 的“mip0 latch 过保守”回退分支也就不需要了。

### 1.4 回退阶梯

“StepLimit 是主要收益”+“`N_safe` 低就回退为只对 Offscreen 续传” = 把主要收益砍掉，二者矛盾。真要回退，按以下顺序：

1. 先做冻结前缀（§1.3）；
2. 再把 latch 收紧为“`insideFloor` 才算进入遮挡体，单纯 `belowFloor` 不否决”；
3. 最后才是 Offscreen-only，且必须有分项证据。

## 2. 近平面不续传：同意，代价比看起来更小

- **分类正确，但依赖 reversed-Z。** `bounds.z = (rcpRayDir.z >= 0) ? 1 : maxDepth`（[RayMarching.hlsl:69-70](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/RayMarching.hlsl#L69)），只有 reversed-Z 下 `1` 才是近平面。RT 只在 D3D12 运行，结论成立，但文档应显式写这个前提——`SslRayMarch` 同时被 D3D11 的 SSGI 使用，换个后端这个命名会翻转。
- **这类射线本来就不升 mip。** `mipLevel += (hitFloor || belowFloor || rayTowardsEye) ? -1 : 1`（[:123](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/RayMarching.hlsl#L123)），朝相机方向的射线全程 mip0，是开销最高的一类；但它们出口靠近相机，剩余段本来就短，且 `killRay` 已排除背面（`dot(n,v) <= 0`），实际数量应该很少。
- **它只是策略，不是安全要求。** 在冻结前缀模型下，近平面出口的前缀同样是空的，续传也不会漏几何；排除它纯粹是因为收益≈0 且语义容易讲错。如果 Phase 1 发现 `N_safe` 不足，这一桶是最后才该动的。

## 3. 实施时必须验证的点

### 3.1 `clipDist` 提升

同意，且建议把它做成返回值的一部分（`{tMax, clipDist}` 小结构），别在循环外挂全局。

注意退化情况：方向分量为 0 时 `dist` 是 `±inf`，`Min3` 仍可工作；但出口分类必须保持“`iterCount >= stepLimit` 优先于 clip 比较”（现有伪代码顺序是对的），否则 64 步恰好停在边界会被误判成 Offscreen。

### 3.2 `R1Cold` 绑定 TraceState——需要先纠正事实

冷帧根本不派发 SS trace：`cold` 分支在 [SsrPass.cs:315](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs#L315) 直接走到 `return`，而 trace kernel 只在 `Dispatch` 的 [SsrPass.cs:531-540](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs#L531) 里派发。所以“R1Cold 绑定 TraceState”不是加个绑定就能做到，要先在 cold 分支补一次 trace 派发。

可行性是有的：`ssrHitPoint` 在 `EnsureSsrResources` 里就分配了（[ScreenSpaceLightingHistory.cs:288](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingHistory.cs#L288)），trace kernel 只读 depth/normal/depth pyramid、不采样颜色金字塔，冷帧 `PushShaderVariables` 也已执行。因此有两个选项：

- **A（严格一致）**：cold 分支补 trace 派发 + TraceState，R1Cold 走同一续传逻辑。代价：冷帧多一次全屏 trace dispatch。
- **B（推荐 v1）**：R1Cold 明确写 `tMin = 0.002`，加一个 `cold_full_trace` 计数，并用测试断言冷帧不做续传。

推荐 B：冷帧不是“首帧”——分辨率变化、后端切换、历史重置后都会重进 cold。但每次只有一帧，而且冷帧**本来就会派发 DXR**（`TryTrace(hit=null)` 在 :315 分支内），损失只是那一帧的完整遍历。为了一帧的行为一致性加一条长期存在的 cold trace 路径，收益/维护比不划算。

### 3.3 指标：`N_safe/N_miss` 不够

`N_safe/N_miss` 无法回答“省了多少”。Phase 1 至少同时记录：

- 每出口类型 × `everBelowFloor` 的样本数（看 latch 误伤率）；
- **跳过的世界距离**（`distance(s.position, safeWS)`）分布，以及 `skip > 0.5 m` 的样本占比；
- `served/N`、DXR marker ms、续传射线的命中率。

原因见 §1.1：如果续传后的射线大多命中且着色占主导，`N_safe` 再高也换不来 ms。只有“跳过距离 × 命中率”才能判断收益真伪。
