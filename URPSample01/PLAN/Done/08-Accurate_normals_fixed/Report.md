# 08-AccurateNormalsFixed 报告：Deferred + Accurate G-buffer normals 噪点

> 状态：已归档；功能验收通过（§5），AC-6 性能未达标（§6）
> 基线：`a8e5a7e`；本轮提交见 `git log`
> 证据：`validation/Attribution/`（图像 + JSON）、`validation/EditMode/`（NUnit XML）、`validation/T12/`

## 1. 结论

Deferred + `Accurate G-buffer normals` 的地面噪点/闪烁**不是该开关的预期表现**，根因是：**不透明 depth-normals prepass 始终写 signed 法线，而屏幕空间效果在 accurate 模式下按 octahedron 解码**。修复后 oct 与 signed 的整帧亮度差 ≤ 1e-5、地面高频能量差 1.2e-4、帧间差 ≤ 8e-6，地面噪点消失。

## 2. 根因

| 环节 | 事实 |
|---|---|
| 写入（GBuffer2） | Deferred + accurate：GBuffer2 = `R8G8B8A8_UNorm`（`DeferredLights.GetGBufferFormat`），GBuffer pass 用 `_GBUFFER_NORMALS_OCT` 写 oct 编码（`Lit.shader` "GBuffer" 声明了关键字） |
| 写入（prepass） | SSL/GTAO 在 `AfterRenderingPrePasses+4/+5/+6` 运行，早于 GBuffer，读到的是 depth-normals prepass 写入 GBuffer2 的内容；材质的 `DepthNormals` pass **没有声明** `_GBUFFER_NORMALS_OCT` → 该变体永远走 signed 分支（`LitDepthNormalsPass.hlsl:134` 的 oct 分支是死代码） |
| 读取 | `UseOctEncodedNormals` 在 deferred + accurate 时返回 true，`GTAOCommon.hlsl:141`、`ScreenSpaceLightingCommon.hlsl:119`、`ScreenSpaceReflections.compute:108` 按 oct 解码 signed 数据 |
| 后果 | 对 +Y 平地，signed 数据按 oct 解码 ≈ `(0.998, 0, 0.066)`（贴地法线）：GTAO 地平线搜索自我遮挡（整块地面变暗）、SSGI 半球采样跨过地面（光线逐像素命中/未命中）→ 噪点；每帧采样旋转不同，时域累积无法收敛 → 闪烁 |
| 为什么没被发现 | 仓库所有 renderer asset `m_AccurateGbufferNormals: 0`；P3-SSR 报告 `normalsOctKeyword=false`；三个 oct 变体从未端到端运行 |

## 3. 修复

让**所有不透明 `DepthNormals` / `DepthNormalsOnly` pass** 与 GBuffer2 使用同一编码：

| 文件 | 改动 |
|---|---|
| `Shader/Lit.shader` | DepthNormals pass 声明 `_GBUFFER_NORMALS_OCT` |
| `Shader/SimpleLit.shader` | 同上 |
| `Shader/Particles/ParticlesLit.shader`、`ParticlesSimpleLit.shader` | 同上 |
| `Shader/Terrain/WavingGrassBillboard.shader` | 同上 |
| `Shader/Terrain/WavingGrassDepthNormalsPass.hlsl` | oct 分支改用与 signed 分支相同的有限差分法线 |
| `Shader/Terrain/TerrainLit*.shader`（3 个） | DepthNormals pass 声明关键字 |
| `Shader/Terrain/TerrainLitDepthNormalsPass.hlsl` | 补 oct 编码输出 |
| `Shader/Nature/SpeedTree7/8.shader` | DepthNormals pass 声明关键字 |
| `Shader/Nature/SpeedTree7CommonPasses.hlsl`、`SpeedTree8Passes.hlsl` | 补 oct 编码输出 |

不改的路径：`TransparentDepthNormals`（透明 prepass 的消费端固定 `octNormals=false`，保持 signed）。

回归保护：

- `Tests/Editor/.../AccurateGbufferNormalsTests.cs`：所有 `DepthNormals`/`DepthNormalsOnly` pass 必须声明关键字；三个消费端必须保留 oct 解码分支。
- `Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingAccurateNormalsAttribution.cs`：可执行归因/验收脚本，带 AC-1/2/3 阈值，退出码即判定。

## 4. 数据（T3 场景，`SslT5DeferredRenderer`，1280×720，12 帧预热）

`Attribution.before.json`（修复前）与 `Attribution.json`（修复后）：

| 用例 | 修复前亮度 | 修复后亮度 | 修复前全图高频 | 修复后全图高频 | 修复后帧间差 |
|---|---|---|---|---|---|
| Gtao_Signed | 0.25762 | 0.25762 | 0.00180 | 0.00180 | 2e-6 |
| Gtao_Oct | 0.25312 | **0.25763** | 0.00179 | 0.00177 | 2e-6 |
| Ssl_Signed | 0.28306 | 0.28306 | 0.00212 | 0.00212 | 0 |
| Ssl_Oct | 0.27099 | **0.28307** | **0.01245** | **0.00208** | 0 |
| Both_Signed | 0.28128 | 0.28128 | 0.00207 | 0.00207 | 8e-6 |
| Both_Oct | 0.25716 | **0.28129** | 0.00487 | **0.00204** | 8e-6 |

修复后 oct vs signed：`dLuma` ∈ [4e-6, 1e-5]，`dHfBottom` = 1.2e-4（阈值 0.00196），帧间差 ≤ 8e-6（阈值 0.00196）。脚本输出 `completed=true passed=true settingsRestored=true`。

图像：

- `Attribution_Ssl_Oct.png`（修复后与 signed 一致，地面无噪）
- `Attribution_Ssl_Oct_SsgiRaw.png` / `_SsgiDenoised.png`（修复前为逐像素随机命中）
- `Attribution_Gtao_Oct_GtaoAO.png`（修复前整块地面过遮挡，修复后与 signed 一致）

> 注：修复前的 PNG 被修复后重跑覆盖；修复前数值以 `Attribution.before.json` 为准，现象亦见用户截图与 §1.2 的离线测量。

## 5. 回归

| 项 | 结果 | 证据 |
|---|---|---|
| EditMode | 182/182 passed（含新增 2 个契约测试） | `validation/EditMode/UnityEditModeTests.xml` |
| PlayMode | 6/6 passed | `validation/PlayMode/UnityPlayModeTests.xml` |
| T12 矩阵（signed 路径） | Forward 34/34、Forward+ 34/34、Deferred 35/35；`completed=true passed=true`，0 issue | `validation/T12/T12Verification.json` |
| 归因脚本（AC-1/2/3/4） | `completed=true passed=true settingsRestored=true` | `validation/Attribution/Attribution.json` |
| 设置恢复 | `allSettingsRestored=true` | `validation/EditMode/UnityEditModeTestsSettingsRestoration.json`、`validation/PlayMode/UnityPlayModeTestsSettingsRestoration.json` |

## 6. Known differences / limits

| 项 | 说明 |
|---|---|
| WavingGrassBillboard 的 oct 法线来源 | 原先 oct 分支用 `input.normal`，signed 分支用 view-dir 有限差分；本轮统一为有限差分，只在 accurate normals 下生效 |
| Terrain / SpeedTree prepass 编码 | 这两个材质的 GBuffer pass 本来就写 oct，本轮让 prepass 与之对齐；它们的 depth-normals 输出原先没有 oct 分支 |
| 透明 prepass | 保持 signed（消费端固定 `octNormals=false`），不参与本轮 |
| 性能 | AC-6 FAIL：0.940488 → 1.005268 ms（+6.89%，要求 <5%）。RTX 5060 Ti / D3D11 Editor，T3 场景 720p 离屏，固定 Both_Oct，对比 `0f64764` 与 `9915f3f` 的着色器。8 轮 ABBAABBA，每轮预热 128 组、采样 192 组，取各轮 GPU 时间中位数的均值。含下游效果，不代表 Player 帧耗时。[GPU 计时与逐轮数据](validation/AC6-9915f3f/conditions.json) |
| 变体数量 | 受影响的 DepthNormals pass 各 +1 fragment variant（oct 开/关） |

## 7. 复现

```
Unity.exe -batchmode -projectPath <repo>/URPSample01 \
  -executeMethod ScreenSpaceLightingAccurateNormalsAttribution.RunAttribution \
  -logFile PLAN/Done/08-Accurate_normals_fixed/validation/Attribution/UnityRun.log
```

T12：加 `-ssl-t12-output PLAN/Done/08-Accurate_normals_fixed/validation/T12`。