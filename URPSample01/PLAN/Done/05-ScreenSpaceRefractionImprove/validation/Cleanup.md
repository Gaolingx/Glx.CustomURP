# 05 交付清理记录

> 2026-11-16，ScreenSpaceRefractionImprove P0 里程碑交付
> 范围：P0-1/P0-2/P0-3 实现并验证；P1-1/P1-2 实现并静态/解析验证；P1-3/P2-* 仍在 TODO。

## 验证结果

- T11：`completed=True passed=True`，Forward / Forward+ / Deferred 0 issue（[Logs/UnityT11Verification.log](Logs/UnityT11Verification.log)）。
- EditMode：`passed=170 failed=0 skipped=0`（[Logs/UnityEditModeTests.log](Logs/UnityEditModeTests.log)）。
- Shader 编译：最终代码在 T11/EditMode 运行中完成导入，无 shader error（[Logs/UnityCompile.log](Logs/UnityCompile.log) 为脚本编译检查）。
- 指标与证据：见 [Report.md](Report.md)、[Report.json](Report.json)。

## 工作区整理

- `ProjectSettings/GraphicsSettings.asset`、`ProjectSettings/QualitySettings.asset` 被 Unity 打开工程时的 serializedVersion 迁移改写；已恢复为 HEAD，`git status --short -- URPSample01/ProjectSettings` 为空。
- 最终日志保留在 `validation/Logs/`；失败的中间编译/运行日志移到 `validation/Logs/Intermediate/` 便于追溯 ShaderGraph 修复过程。
- P0 阶段未创建 Player；P1-3 阶段创建了 D3D11 player，保留了 6 个 `.rdc`（见 `P1-3/Player/`）与 `P13PlayerBuildSummary.json`。
- `git diff --check` 通过（退出码 0）。

## 提交前 git status（节选）

> 下方 git status 为归档前快照；其中 `PLAN/TODO/05-...` 已在归档时移到 `PLAN/Done/05-...`，快照保留原样，不代表当前路径。

```text
M  Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT11Verification.cs
M  Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingTestRunner.cs
M  Assets/Tests/ScreenSpaceLighting/Materials/T11Refraction*.mat
M  Assets/Tests/ScreenSpaceLighting/Renderers/SslT11*Renderer.asset
M  PLAN/Done/01-ScreenSpaceLighting/HANDOFF.md
M  PLAN/Done/01-ScreenSpaceLighting/validation/T14/T14KnownDifferences.md
M  PLAN/TODO/05-ScreenSpaceRefractionImprove/HANDOFF.md
M  Packages/.../Editor/ShaderGraph/Includes/PBRForwardPass.hlsl
M  Packages/.../Runtime/RendererFeatures/ScreenSpaceLighting/Passes/RefractionPass.cs
M  Packages/.../ShaderLibrary/BRDF.hlsl
M  Packages/.../ShaderLibrary/Lighting.hlsl
M  Packages/.../ShaderLibrary/ScreenSpaceLighting/ScreenSpaceRefraction.hlsl
M  Packages/.../Shaders/LitForwardPass.hlsl
M  Packages/.../Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingMaterialTests.cs
?? PLAN/TODO/05-ScreenSpaceRefractionImprove/validation/
?? Packages/.../ScreenSpaceLightingRefractionImproveTests.cs(.meta)
```

## 行为变化

- 折射材质的 opacity 现在是 transmittance mask：opacity=1 不再显示透射，opacity=0 才把 diffuse/emission 桶替换为折射背景；输出 alpha 固定为 1。
- 无 box-projected probe 不再退回 legacy 透明：任意 probe AABB 或 far-projection 代理（URP 扩展）继续提供折射；仅 Volume 关闭或 intensity=0 恢复 legacy。
- `_Ior` 在效果开启时进入 F0，同一材质的反射率与 (1-F) 透射随 IOR 改变。
- T11 折射测试材质改为 opacity 0，以匹配新的 mask 语义。

## 未完成

- P2-2、P2-3、P2-4 未开始（P2-1 已复核关闭）；P1-1/P1-2 的 HDRP 视觉对照、P0-1 的 screen-miss 证据待补。P1-3 已完成并留证（见 HANDOFF §16–§18 与 `P1-3/Player/`）。
- 计划目录已归档到 `PLAN/Done/05-ScreenSpaceRefractionImprove/`（本节上方 git status 为归档前的历史快照）。
