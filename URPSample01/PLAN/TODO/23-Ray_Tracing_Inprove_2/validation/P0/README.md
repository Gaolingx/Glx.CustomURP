# P0 证据：切换前基线

**状态：** D3D12 基线已捕获（2026-11-27），三变体各两进程逐位一致。R1 计数器与 DXR dispatch 直接证据仍为 OPEN。
**原则：** 本目录只记录"SSR 挪位 + 命中统一着色"之前的夹具与输出；P3 的对比一律以这里为参照。

## 变体

| 变体 | Volume 差异 | 用途 |
|---|---|---|
| `base` | SSR on、PBR、半分辨率、denoise on | 主基线 |
| `nodenoise` | `denoise = false` | 区分着色缝与采样噪声 |
| `ssroff` | `enabled = false` | probe/sky 参考，"SSR 贡献 = base − ssroff" |

## 目录

```
validation/P0/
├── Freeze.json          # 夹具、平台、阈值、实测值
├── Fixture.json         # 构建器写出的资产路径与 SHA256
├── Comparison.json      # compare.py 输出（确定性、SSR 贡献、denoise 差）
├── compare.py
├── editor-check.png     # Editor 端夹具自检（非 Player 证据）
├── Player/              # SsrP3Baseline.exe（勿提交）
└── Win64-D3D12/<variant>/<run-XX>/
    ├── Player.json      # 环境、相机、Volume、feature、dispatch、SHA256
    ├── frame.raw        # 1920×1080 RGBA16F 线性（权威数据，约 16.6 MB）
    └── frame.png        # 审查用 tonemap
```

## 实测结果

- 同变体两进程 `frame.raw` SHA256 完全一致（`firstUnityFrame = 1`，第 121 次渲染捕获）。
- `frameSHA256`：`base 96b9c197…`、`nodenoise 693c54de…`、`ssroff 1295029d…`。
- dispatch（Player 可见快照）：`base/nodenoise` 的 `trace=1, hwMerge=1`；`base` 的 `temporal=1, historyValid=true`；`ssroff` 全 0。
- 地面 ROI（归一化 top-down x 0.15–0.85 / y 0.45–0.95，采样步长 2）：
  - `base − ssroff`：meanAbs 8.4e-4、p99 0.030、max 0.353、2.5% 像素 > 0.01。
  - `base − nodenoise`：meanAbs 4.0e-4、p99 0.003、max 5.93（少量高光/萤火虫）、0.33% 像素 > 0.01。
- 无 NaN/Inf；`supportsRayTracing = true`、`rendererModeActual = Deferred`。

## 判定口径

1. 同一 API、同一构建的两个全新进程：`frame.raw` SHA256 必须一致；不一致记录差异像素数与最大通道差并判 BLOCKED。
2. `base` 与 `ssroff` 必须存在非零差异（证明 SSR 生效）；差异集中在 ROI。
3. `base` 与 `nodenoise` 的差异用于标注"噪声 vs 着色"；不得把 denoise 拉开后的差异算作着色缝。
4. 本目录不签收 P3；P3 的阈值见 [plan.md](../plan.md) §1 P3 验收。

## 已知约束

- Player 构建必须在构建期把 fixture 的 Deferred 管线设为默认并覆盖所有质量档，否则 URP 按工程默认 Forward 剔除 Deferred 变体，Player 会渲染成"天空盒 + 全黑几何"。构建器已自动处理并恢复。
- 工程的 Windows API 是 Automatic，等价于只编 D3D11。构建器在构建期间显式带上 D3D11+D3D12，结束后把 `ProjectSettings.asset` 还原到提交值；手动构建时若省略这一步，`-force-d3d12` 会因 D3D12 compute kernel 缺失而回退 Forward。
- 场景网格必须带 `SsrRayTracingGeometry`（稳定实例 ID，`RayTracingMode.Static`），否则 DXR 混合静默不启用。
- `frame.raw` 体积较大；是否随提交入库待 owner 决定。
