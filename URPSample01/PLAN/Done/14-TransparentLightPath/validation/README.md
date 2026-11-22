# 14-TransparentLightPath 验证记录

> 环境：Unity 2022.3.62f1、D3D11 standalone player、RTX 5060 Ti、1280×720。
> 代码：`ClusteredDeferredArenaRunner` 新增 `--cd-transparent`（3 个半透明 Lit 球）、`--cd-per-object-limit <n>`、`--cd-screenshot-lights 0,8,48,256`（逗号列表），并在 `CdPerf.json` 里记录透明组投影矩形、矩形内 diff、`_ADDITIONAL_LIGHTS` / `_FORWARD_PLUS` 全局关键字。
> 夹具材质：`Assets/Tests/ClusteredDeferred/Resources/CdTransparent.mat`（由 `ClusteredDeferredTestAssetSetup.CreateAll` 从 `SmoothDielectric.mat` 复制并改成 alpha=0.55 的 Transparent Lit）。

## 结论

- **G-T1 差异存在（PASS）**：默认 4 盏上限下，透明像素与 Forward+（cluster 语义）在 48 灯已明显分叉、256 灯几乎完全不同。
- **G-T2 上限不可解（FAIL）**：把 `maxAdditionalLightsCount` 拉到引擎上限 8 只能减半差距，256 灯仍有 66% 的透明像素差异 >8。
- **G-T3 成本取决于取光方式**：per-object 路径（stencil/clustered）没有可分辨增量（|Δ| ≤ 0.06 ms）；Forward+ 256 灯稳定 +0.17–0.18 ms。上限 4→8 无可分辨变化。C2 是正确性/一致性工程，不是性能优化。
- **G-T4 无回归（PASS）**：无透明对照与 13 轮基线一致（48 灯 stencil vs clustered mean 0.806 / max 9；clustered vs Forward+ 0.209）。
- **决定：进入 C2 实现轮**（forward 端读结构化 cluster 位图的关键字/迭代器），而不是只提高每物体上限。

## 夹具

3 个半径 1.5 的半透明球（`_BaseColor.a = 0.55`，SmoothDielectric 的 Specular/Smoothness 保留）位于相机与前排 receiver 之间（x = -3.8 / 0 / 3.8，y = 4.2，z = -8）。投影包围盒为 `(153,192) 974×284`（30.0% 屏幕）；用 on/off 差分（>8）得到的真实透明覆盖面为 11.7%–13.2%（随灯数略变）。

## 透明像素上的差距（C/F = ClusteredDeferred vs ForwardPlus）

统计口径：先用同灯数的 clustered on/off 差分（>8）取透明 footprint，再在 footprint 内统计 C/F 的逐像素通道最大差。

| 灯数 | footprint | footprint 占比 | C/F mean（footprint） | max | >8 像素 | 占 footprint |
|---:|---:|---:|---:|---:|---:|---:|
| 0 | 107,884 | 11.71% | 0.374 | 32 | 19 | 0.02% |
| 8 | 109,229 | 11.85% | 0.310 | 32 | 19 | 0.02% |
| 48 | 117,705 | 12.77% | **4.587** | 243 | 18,276 | **15.53%** |
| 256 | 121,157 | 13.15% | **51.431** | 239 | 108,360 | **89.44%** |

同口径、`--cd-per-object-limit 8`：

| 灯数 | footprint | C/F mean（footprint） | max | >8 像素 | 占 footprint |
|---:|---:|---:|---:|---:|---:|
| 0 | 107,884 | 0.374 | 32 | 19 | 0.02% |
| 8 | 109,229 | 0.310 | 32 | 19 | 0.02% |
| 48 | 117,002 | **1.815** | 240 | 4,958 | **4.24%** |
| 256 | 117,739 | **33.856** | 238 | 78,245 | **66.46%** |

- 0/8 灯下 per-object 上限没有约束（灯都进了每物体列表），两侧一致；48 灯开始分叉，256 灯（每位置 4 盏的最坏重叠场）有 89% 的透明像素受影响。
- 上限拉到 8 把 48 灯的 >8 比例从 15.5% 压到 4.2%，256 灯从 89.4% 压到 66.5%：**能缓解，不能替代 cluster 光表**。

## 矩形内 diff（`CdPerf.json`）

| 运行 | 对比 | 48 灯 mean / max / >8 | 256 灯 mean / max / >8 |
|---|---|---|---|
| control（无透明） | Stencil vs Clustered | 0.806 / 9 / 0 | 4.056 / 70 / 0 |
| control（无透明） | Clustered vs Forward+ | 0.209 / 81 / 0 | 0.290 / 86 / 0 |
| limit4 | Stencil vs Clustered | 0.724 / 9 / 8 | 3.733 / 60 / 88,616 |
| limit4 | Clustered vs Forward+ | 0.463 / 247 / 19,055 | 3.662 / 239 / 108,927 |
| limit8 | Stencil vs Clustered | 0.737 / 9 / 2 | 3.801 / 24 / 91,373 |
| limit8 | Clustered vs Forward+ | 0.262 / 240 / 5,191 | 2.257 / 238 / 80,493 |

（矩形内分母 276,616 px；`>8` 是矩形内通道最大差 >8 的像素数。）

- 256 灯的 stencil 参照依旧因 `R11G11B10f` 逐灯混合而失效（13 轮结论），本轮的语义参照统一用 Forward+。
- control 与 13 轮基线逐项一致（48 灯 0.806/9 对 0.803/9；C/F 0.209/0.290 对 0.209/0.290），夹具未污染对照。

## 帧时间（GPU/CPU 均值，ms）

| 灯数 | 模式 | control | limit4 | limit8 |
|---:|---|---:|---:|---:|
| 48 | Stencil | 0.619 / 1.520 | 0.556 / 1.306 | 0.607 / 1.380 |
| 48 | Clustered | 0.410 / 1.126 | 0.429 / 1.130 | 0.389 / 1.028 |
| 48 | Forward+ | 0.455 / 1.446 | 0.455 / 1.135 | 0.485 / 1.325 |
| 256 | Stencil | 1.929 / 4.244 | 1.881 / 3.911 | 1.946 / 4.087 |
| 256 | Clustered | 1.277 / 2.057 | 1.284 / 1.839 | 1.310 / 1.884 |
| 256 | Forward+ | 1.220 / 2.332 | 1.399 / 1.977 | 1.387 / 2.107 |

透明组的增量（control → limit4）：per-object 路径（stencil / clustered）没有可分辨变化（|Δ| ≤ 0.06 ms）；**Forward+ 在 256 灯稳定 +0.17–0.18 ms**（1.220→1.399 / 1.387），因为那三个球在 Forward+ 里逐像素扫完整 cluster 列表。上限 4→8 没有可分辨的帧时间变化。结论：透明像素的成本由取光方式决定（4 盏 per-object 比全 cluster 便宜），C2 的收益是正确性/一致性，不是性能。

## RenderDoc

- `rdc/transparent-clustered-d3d11_frame228.rdc`（12.0 MB）：`--cd-capture --cd-transparent --cd-total-lights 256 --cd-hold`，clustered 渲染器。
- pass 结构：`Render GBuffer` → `CopyDepth` → `Clustered Deferred`（5 次列表/光照 dispatch）→ `Render Opaques Forward Only`（空）→ `DrawTransparentObjects`（3 个 2304-index draw，EID 677/680/683）→ `FinalBlit`。透明球不在 deferred 光照里，而是在透明 pass 里单独绘制。
- 透明 draw（EID 680）的 PS 只绑定常量缓冲（含 16 KB 附加光灯数组）与 3 张纹理，**没有任何 structured buffer**；`Clustered Deferred` 的光照 dispatch（EID 624）才绑定 5 个 cluster/G-buffer SRV（`structuredbuffer5–9`）。采样帧的全局关键字为 `_ADDITIONAL_LIGHTS=1`、`_ADDITIONAL_LIGHTS_VERTEX=0`、`_FORWARD_PLUS=0`（`capsrc/CdPerf.json`）。
- 这同时解释了为什么不能直接启用 `_FORWARD_PLUS`：Forward+ 的 cbuffer 版本位图与 GPU 构图的 structured 版本互斥（`ForwardLights.cs:187-202`、`ForwardLights.cs:464-472`）。

## 证据清单

```text
validation/
├── logs/{build-d3d11,control,limit4,limit8}.log
├── control/            # 12 PNG + CdPerf.json/csv（无透明）
├── limit4/             # 12 PNG + CdPerf.json/csv（透明，默认 4 盏）
├── limit8/             # 12 PNG + CdPerf.json/csv（透明，上限 8 盏）
├── capsrc/CdPerf.json  # 抓帧进程的关键字与模式
├── rdc/transparent-clustered-d3d11_frame228.rdc
├── CdArenaBuildSummary-Direct3D11.json
└── README.md
```

## 复跑命令

> `logs/` 被 `.gitignore` 排除（与 02 轮一致），只在本地保留；入库的 `CdPerf.json/csv`、PNG 与 `.rdc` 是权威证据。

```bash
U="/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe"
P="E:/GitHub/Glx.CustomURP/URPSample01"
OUT="$P/PLAN/Done/14-TransparentLightPath/validation"
EXE="$P/Builds/ClusteredDeferredArena/Direct3D11/CdArena.exe"

"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildD3D11AndExit \
  -logFile "$OUT/logs/build-d3d11.log"

"$EXE" --cd-output "$OUT/control" --cd-total-lights 256 \
  --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
"$EXE" --cd-output "$OUT/limit4" --cd-transparent --cd-total-lights 256 \
  --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
"$EXE" --cd-output "$OUT/limit8" --cd-transparent --cd-per-object-limit 8 --cd-total-lights 256 \
  --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
```

透明 footprint 的统计（Pillow，无 numpy）：

```python
from PIL import Image, ImageChops
def load(run, m, c): return Image.open(f'{run}/{m}_L{c}.png').convert('RGB')
def maxchan(a, b):
    d = ImageChops.difference(a, b); r, g, bl = d.split()
    return ImageChops.lighter(ImageChops.lighter(r, g), bl)
# footprint = maxchan(limitX/Clustered_L{n}, control/Clustered_L{n}).point(lambda v: 255 if v > 8 else 0)
# C/F 统计 = maxchan(limitX/Clustered_L{n}, limitX/ForwardPlus_L{n}).histogram(footprint)
```

> player 构建会重序列化场景、renderer 资产与 ProjectSettings；这些副作用已在提交前还原，提交只包含 runner/setup/build 的 CLI 与夹具改动、`Resources/CdTransparent.mat` 与本目录。

## 边界

- footprint 用 on/off 差分近似（阈值 >8），抗锯齿边缘与低对比像素会漏计；矩形口径（30% 屏幕）包含球间背景，因此矩形 mean 被稀释，决策优先看 footprint 内的 `>8` 比例。
- 只覆盖 Lit 透明；forward-only 不透明物体没有独立夹具（它们与透明共用同一 per-object 路径，结论按代码路径外推，未单独测）。
- 256 灯场是位置复用的最坏重叠压力场，不代表空间均匀分布；只测 D3D11 720p 单卡。
- GPU 帧时间有 ~0.1 ms 级运行间噪声，跨进程的 0.0x ms 差异不具结论性。
