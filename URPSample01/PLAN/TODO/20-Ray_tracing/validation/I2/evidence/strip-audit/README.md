# I2 P0-1 剥离审计（非目标构建）

**范围**：把此前的“剥离通过”从 Lit `GlxDXR` Hit set 扩展到全部 RT-only 变体，并复核 D3D11-only 构建不再编译 probe 查询库。

- **RT-only 判定**：任何 `GlxDXR` pass（Lit 及未来的 RT hit shader）与任何含 `_SCREEN_SPACE_GI_PROBE` 的变体（Lit/ComplexLit 的 ForwardLit 与 GBuffer、ShaderGraph Lit 的同名关键词）。
- **实现**：`ShaderScriptableStripper.CanRemoveVariant` 统一判定 RT-only 变体；构建目标不支持 RT（Win64 的图形 API 不含 D3D12、且不是 PS5/Xbox Series）时整体剥离。能力查询按构建缓存一次（`ShaderBuildPreprocessor.OnPreprocessBuild` / `RefreshRayTracingSupport`），不按变体重复查询。
- **审计工具**：`I2VariantAudit` 记录两种构建中所有 RT-only pass 的存活变体集（`GlxDXR` 记全列表；probe keyword 记数量 + SHA256 + 样例，避免证据膨胀）以及打包进构建的 RT shader 资产。D3D11-only 构建现在与签名构建一样把夹具 pipeline 赋给全部 quality 级——此前未赋，`Lit/GBuffer` 被剥成 0 变体，审计偏弱。

## 结果（strip-only 复跑，构建 GUID `5fc6e63e…`，base `c9b3756`）

| 构建 | 图形 API | Lit `GlxDXR` set | probe keyword set | ForwardLit (fp) | GBuffer (fp) |
|---|---|---|---|---|---|
| 签名 Win64 | D3D12 + D3D11 | 16 | 4160（ForwardLit 4096 + GBuffer 64） | 8192 | 128 |
| D3D11-only | D3D11 | 0 | 0 | 4096 | 64 |

- D3D11-only 构建日志：`with _SCREEN_SPACE_GI_PROBE` 编译 0 次，`GlxProbeQuery.hlsl` 警告 0 条（修复前 4 条）；两个 pass 都恰好保留去掉 keyword 后的一半变体。
- 剥离玩家：`Direct3D11` / `Backend` / 0 场景实例 / 0 build / 0 订阅，通过。

## 资产级残留（待 owner 记例外或后续剔除）

序列化引用使 3 个 RT shader 资产在 D3D11-only 构建中仍被打包：`GlxProbeTrace.raytrace`、`RayTracingSsr.raytrace`、`GlxProbeUpdate.compute`（构建尺寸报告合计约 16.8 KB）。运行期由 `SystemInfo.supportsRayTracing` 关闭，不分配资源。变体级剥离完整；资产级剔除需要在构建期改写用户 renderer 资产中的引用，未做。

## 复跑

```
python run.py --scratch <I2 scratch> --refresh --strip-only --out evidence/strip-audit
```

`verify.py` 在 strip-only 模式下只检查审计字段与剥离玩家（不跑 1080p 性能套件）。完整证据仍由不带 `--strip-only` 的 `run.py` 生成。

证据：[Summary.json](Summary.json)、[Win64 审计](RtVariants.json)、[D3D11-only 审计](D3D11OnlyRtVariants.json)、[Lit Hit set](Variants.json)、[D3D11-only Lit Hit set](D3D11OnlyVariants.json)。
