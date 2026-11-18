# 07-EditorMVPass Known Differences

> 适用版本：2026-11-18 之后（Editor MV pass + 姿态化深度金字塔）

| 项 | 行为 | 原因 | 影响与建议 |
|---|---|---|---|
| Editor 下物体运动矢量 | 不补写 | 只为“无 ACD 相机”补**相机**运动；URP 的物体 MV 依赖 ACD 的 `MotionVectorsPersistentData` 与 `DepthTextureMode.MotionVectors` | 场景内快速移动/动画物体在 Editor 下仍可能出现 AO 滞后；Player 正常。需要时再扩展为“相机 MV + 物体 MV”组合，不在本轮范围 |
| 同姿态物体移动 + 同帧重渲染 | 深度金字塔仍复用 | 构建 token 只有 `frameIndex + viewProjection`，没有干净的物体/深度 token | 暂停态下移动物体并手动重复渲染时，AO/SSGI/SSR 深度可能滞后一帧；相机移动（本轮场景）已修复 |
| 只开 SSL（SSGI/SSR）的 Scene View | 第一阶段无 MV；第二阶段改为共享 `ScreenSpaceCommon` 的 Editor MV pass，由 SSL feature 在 SSGI/SSR 启用时入队 | 第一阶段 pass 属于 GTAO feature | 第二阶段修复后，SSL-only 的 SSR/SSGI 与 GTAO 使用同一套相机 MV；仍不包含物体运动 |
| XR | 不补 | pass 仅在 `viewCount == 1` 执行；Scene View 不启用 XR | 多视图相机维持 URP 原生行为 |
| 首帧 motion | 不写（保持清黑） | 重置后的第一帧没有可比对的上一姿态；GTAO 该帧本来只 CopyHistory | 域重载/相机 reset 后 1 帧无历史累积，无可见拖影 |
| 相机 cut（F 聚焦、瞬移） | 依赖 history reset | `CameraHistoryStore` 在 `resetHistory` token 变化时清空 prev VP | 若外部工具绕过 `cameraData.resetHistory` 瞬移相机，会有一帧按旧姿态重投影；大运动会被现有 `velWeight` 拒绝历史 |
| 收敛参考比对 | 不作为判定 | 时间累积存在固定反馈延迟，URP 原生 MV 路径与修复路径同样偏离瞬时收敛值 | 验收以“与原生 MV 逐帧一致”与“抑制后显著劣化”为准，见 `Report.md` §5.1 |
| Editor 下运动矢量 RT 深度 | 不写 `SV_Depth` | GTAO 只用颜色通道；motion depth 仅供 URP 物体 MV 绘制 | 本 pass 只在无 ACD 相机执行，不覆盖原生物体 MV；若后续复用给其他消费者需补深度 |

## 明确不在本轮修复

1. Scene View 永久关闭 temporal（会降低静置质量，已否决；第二阶段的 SSL 修复同样不采用）。
2. 把 motion 数据从 `UniversalAdditionalCameraData` 搬进 pipeline 的架构重构。
3. XR 单趟立体 MV。
4. PBR SSR 的 `SSL_PREV_INV_VIEW_PROJ`：相机 MV 修好后 UV 重投影正确，但无 ACD 相机的上一帧 VP 仍回退到当前 VP，历史深度校验在 Scene View 下不精确；作为后续项记录。