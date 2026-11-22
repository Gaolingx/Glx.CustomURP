# 15-ForwardClusterLights 验证记录

> 环境：Unity 2022.3.62f1、D3D11 standalone player、RTX 5060 Ti、1280×720。
> 代码：forward 端新增 `_CLUSTERED_DEFERRED_LIGHTS` 变体，CD 下透明/forward-only 读结构化 `URP_ZBins`/`urp_Tiles`；arena 新增 `--cd-forward-cluster-off/on` 与报告字段 `forwardClusterLights`/`forwardClusterKeyword`。
> 三组 sweep 都跑 stencil / clustered / Forward+ 三管线 × 0/8/48/256 灯，并逐档截图。

## 结论

| 闸门 | 结果 | 关键数字 |
|---|---|---|
| G15-1 256 灯 footprint C/F >8 ≤1% | ✅ **PASS** | **0.18%**（209 / 118,630），cluster off 87.92% |
| G15-2 48 灯 footprint C/F >8 ≤1% | ✅ **PASS** | **0.11%**（132 / 116,983），cluster off 15.36% |
| G15-3 0/8 灯基线 | ✅ PASS | 0.02%，与 14 轮一致 |
| G15-4 control 无回归 | ✅ PASS | 48 灯 S/C 0.810 / max 9；C/F 0.209；256 灯 C/F 0.290 |
| G15-5 开关复现分歧 | ✅ PASS | off：48 灯 15.36%、256 灯 87.92%（探针 15.53% / 89.44%） |
| G15-6 成本 | ○ 记录 | CD 256 灯 +0.23 ms GPU；48 灯 +0.03 ms |
| G15-7 测试 | ✅ PASS | EditMode 491/491、PlayMode 41/41 |
| G15-8 GPU 证据 | ✅ PASS | 透明 PS 绑定 `t0/t1` structured(4B) 并 `ld_structured` |

## footprint 内 C/F（主验收口径）

统计：先用同灯数的 `cluster-on/ClusteredDeferred` vs `control/ClusteredDeferred` 差分 >8 取透明 footprint，再在 footprint 内统计 `ClusteredDeferred vs ForwardPlus` 的逐像素通道最大差。

| 灯数 | footprint | 占比 | cluster on mean / >8 / 占比 | cluster off mean / >8 / 占比 |
|---:|---:|---:|---|---|
| 0 | 107,884 | 11.71% | 0.125 / 19 / 0.02% | 0.125 / 19 / 0.02% |
| 8 | 109,229 | 11.85% | 0.103 / 19 / 0.02% | 0.103 / 19 / 0.02% |
| 48 | 116,983 | 12.69% | **0.154 / 132 / 0.11%** | 1.527 / 17,971 / 15.36% |
| 256 | 118,630 | 12.87% | **0.202 / 209 / 0.18%** | 17.184 / 104,301 / 87.92% |

- 0/8 灯每物体上限没有约束，两侧都只剩管线固定差（0.02%）。
- 48/256 灯关闭开关后与 14 轮探针一致（15.53% / 89.44%，本轮 15.36% / 87.92%）；开启后降到 0.1% 量级。

## 全屏 diff（`CdPerf.json`）

| 运行 | 对比 | 48 灯 mean / max / >8 | 256 灯 mean / max / >8 |
|---|---|---|---|
| control（无透明） | Stencil vs Clustered | 0.810 / 9 / 7 | 4.174 / 24 / 386,643 |
| control（无透明） | Clustered vs Forward+ | 0.209 / 81 / 452 | 0.290 / 86 / 622 |
| cluster-on | Stencil vs Clustered | 1.028 / 247 / 21,321 | 7.225 / 239 / 418,334 |
| cluster-on | Clustered vs Forward+ | **0.187 / 82 / 382** | **0.263 / 80 / 556** |
| cluster-off | Stencil vs Clustered | 0.732 / 9 / 2 | 3.881 / 61 / 338,325 |
| cluster-off | Clustered vs Forward+ | 0.463 / 247 / 19,095 | 3.662 / 239 / 108,975 |

- 透明夹具打开后，C/F 的 >8 像素反而略少于无透明对照（382 vs 452、556 vs 622）：透明像素现在与背景共用同一份 cluster 灯集合，差值被同一套固定管线差覆盖。
- `Stencil vs Clustered` 在 cluster on 后变大是**预期**：stencil 的透明仍走 per-object，clustered 已对齐 Forward+（14 轮已确认 stencil 的 256 灯参照受 `R11G11B10f` 逐灯混合失效）。
- cluster-off 的 C/F（19,095 / 108,975 px）与 14 轮 limit4 的矩形 >8（19,055 / 108,927 px）同量级，证明开关复现了旧路径。

## 帧时间（GPU / CPU 均值，ms）

| 灯数 | 模式 | control | cluster-on | cluster-off |
|---:|---|---:|---:|---:|
| 48 | Clustered | 0.420 / 1.176 | 0.430 / 0.984 | 0.399 / 1.183 |
| 48 | Forward+ | 0.446 / 1.337 | 0.484 / 1.412 | 0.490 / 1.318 |
| 256 | Clustered | 1.291 / 1.926 | 1.510 / 2.121 | 1.277 / 2.023 |
| 256 | Forward+ | 1.155 / 2.161 | 1.451 / 2.242 | 1.265 / 2.111 |

256 灯 CD 的 +0.23 ms 与 14 轮 Forward+ 透明组的 +0.17–0.18 ms 同源（三个球逐像素扫 cluster 列表）；48 灯 +0.03 ms 在噪声边缘。跨运行有 ~0.1 ms 级噪声，结论只看同组量级。

## PlayMode 自包含 A/B

`ForwardTransparentObjectsReadTheClusterList`（640×360、48 灯、同一构建/同一运行）：

```text
[CD C2] transparent A/B vs Forward+: cluster over8=648 mean=0.455;
        per-object over8=17809 mean=11.056 (230400 px)
```

- 开启后 >8 像素是关闭时的 1/27；测试同时断言（1）cluster 模式已启用 `_CLUSTERED_DEFERRED_LIGHTS`，（2）关闭后关键字消失且差异回升。
- 648 px 的残差是 CD/Forward+ 的固定管线差（反射探针、几何边缘），无透明对照同量级。

## RenderDoc

- `rdc/forward-cluster-d3d11_frame305.rdc`（12.0 MB）：`renderdoc-cli capture ... --cd-capture --cd-transparent --cd-total-lights 256 --cd-light-counts 256 --cd-screenshot-lights 256 --cd-hold -d 200`。
- 198 events / 144 draws；pass 结构：`Render GBuffer` → `Clustered Deferred` → `Render Opaques Forward Only`（空）→ `DrawTransparentObjects`（EID 679/682/685，2304 indices）→ `FinalBlit`。
- EID 682 的 PS（shader 8690）反汇编见 `rdc/ps_682.txt`：
  - `dcl_resource_structured t0, 4` / `dcl_resource_structured t1, 4`（每元素 4 B，即 `uint` word）；
  - `ld_structured ... t0` 读 zbin header，随后 `ld_structured ... t1` / `t0` 做 tile/zbin 掩码与 `+32 & ~31` 步进——与 `Clustering.hlsl` 的 `ClusterInit/ClusterNext` 一一对应；
  - `_FPParams0`（zbin scale/offset）在 `cb0[15]`、tile scale 在 `cb0[16]`，由 ForwardLights 每帧上传。
- 抓帧进程 `capsrc/CdPerf.json`：`forwardClusterLights=true`、`forwardClusterKeyword=true`、`additionalLightsPixelKeyword=true`、`forwardPlusKeyword=false`、`perObjectLightLimit=4`。

## 变体与构建

- Lit ForwardLit 片元变体：3072 → **6144**（新增独立关键字组；未与 `_FORWARD_PLUS` 共组，避免 prefiltering/stripper 把 CD 变体当 Forward+ off 变体删除）。
- 冷构建 **918.6 s**（改动了 `Input.hlsl`/`Clustering.hlsl`，全部 URP 变体重编）；`CdArenaBuildSummary-Direct3D11.json` 0 error；player 139.6 → 143.5 MB。
- 后续构建恢复 shader 缓存命中，不再付这笔成本。

## 测试

| 套件 | 结果 | 证据 |
|---|---|---|
| EditMode 全量 | **491/491**（490 基线 + `ForwardLitShadersDeclareTheClusteredDeferredVariant`） | `tests/EditMode.xml` |
| PlayMode 全量 | **41/41**（40 基线 + `ForwardTransparentObjectsReadTheClusterList`） | `tests/PlayMode.xml` |

PlayMode 日志中的 `CommandBuffer: temporary render texture not found (SetComputeTextureParam)` 出现在既有的 `DepthPruneKeepsPixelsIdentical`，非本轮引入。

## 证据清单

```text
validation/
├── logs/                      # gitignored；本地构建/运行日志
├── control/                   # 14 文件：12 PNG + CdPerf.json/csv
├── cluster-on/                # 同上（默认开启 forward cluster）
├── cluster-off/               # 同上（--cd-forward-cluster-off）
├── capsrc/CdPerf.json         # 抓帧进程的关键字/开关
├── rdc/forward-cluster-d3d11_frame305.rdc
├── rdc/ps_682.txt
├── tests/{EditMode,PlayMode}.xml
├── CdArenaBuildSummary-Direct3D11.json
└── README.md
```

## 复跑命令

```bash
U="/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe"
P="E:/GitHub/Glx.CustomURP/URPSample01"
OUT="$P/PLAN/Done/15-ForwardClusterLights/validation"
EXE="$P/Builds/ClusteredDeferredArena/Direct3D11/CdArena.exe"

"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildD3D11AndExit \
  -logFile "$OUT/logs/build-d3d11.log"

"$EXE" --cd-output "$OUT/control" --cd-total-lights 256 \
  --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
"$EXE" --cd-output "$OUT/cluster-on" --cd-transparent --cd-total-lights 256 \
  --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
"$EXE" --cd-output "$OUT/cluster-off" --cd-transparent --cd-forward-cluster-off --cd-total-lights 256 \
  --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
```

footprint 统计（Pillow，无 numpy）：

```python
from PIL import Image, ImageChops
def load(run, m, c): return Image.open(f'{run}/{m}_L{c}.png').convert('RGB')
def maxchan(a, b):
    d = ImageChops.difference(a, b); r, g, bl = d.split()
    return ImageChops.lighter(ImageChops.lighter(r, g), bl)
# footprint = maxchan(cluster-on/ClusteredDeferred_L{n}, control/ClusteredDeferred_L{n}).point(lambda v: 255 if v > 8 else 0)
# C/F      = maxchan(run/ClusteredDeferred_L{n}, run/ForwardPlus_L{n}).histogram(footprint)
```

## 边界

- 只覆盖 Lit 透明夹具；SimpleLit/ComplexLit/Particles 的变体已编译但无独立像素夹具。
- footprint 用 on/off 差分近似（阈值 >8），含少量球间背景；256 灯为位置复用的最坏重叠场。
- 只测 D3D11 720p 单卡；GPU 帧时间跨运行噪声 ~0.1 ms。
- Preview/Overlay/XR、GLES、`useStructuredBuffer=true`、stencil/Forward+ 默认路径保持 per-object。
