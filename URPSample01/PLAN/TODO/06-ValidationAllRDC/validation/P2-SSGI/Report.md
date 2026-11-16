# P2-1 SSGI 生效链 GPU 验收（dispatch / history / LUT / atlas）（2026-11-17）

> 任务：P2-1。目标：对 SSGI 的 trace → reproject → history validate → temporal accumulation → copy → denoise → gather/upsample 链条，证明调度数量/尺寸、history 双缓冲、blue-noise LUT 与反射探针 atlas 的真实绑定。
> 结论：**10/10 用例通过**。CPU 侧每个 kernel 的 dispatch 计数与组维度逐项匹配配置；GPU 抓帧的 dispatch 顺序、kernel shader、LUT/atlas 绑定与 history 读写槽全部与 CPU 统计一致；binding probe 的 8 个 SSGI 字段 0 不匹配；所有用例 `nonFinitePixels=0`。
> 未改动 SSGI 算法；本轮只新增 DEV 构建的调度统计和验证资产。F-07（history stamp）与 F-09（denoiser Y 翻转）仍属于 P2-2/P2-3。

## 1. 产物与复现

| 项 | 值 |
|---|---|
| Manifest | [P2Manifest.json](P2Manifest.json) |
| 分析脚本 | [analyze.py](analyze.py)（renderdoc-mcp stdio + `MCP/tools/rdmcp.py`） |
| 原始证据 | [Player/Report.json](Player/Report.json)、`Player/BindingProbe_*.json`、`Player/*.png` |
| GPU census | [P2Census.json](P2Census.json) |
| Player | `Builds/SslValidationPlayer/SslValidationPlayer.exe`（D3D11、Development、1280×720；以 Deferred 资产构建） |

```bash
# 抓帧
"/c/Program Files/RenderDoc/renderdoccmd.exe" capture -w --opt-hook-children \
  --opt-disallow-fullscreen --opt-disallow-vsync \
  "E:/GitHub/Glx.CustomURP/URPSample01/Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest "…/validation/P2-SSGI/P2Manifest.json" \
  -ssl-validation-out "…/validation/P2-SSGI/Player" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0

# GPU census
python analyze.py Player/*.rdc --report Player/Report.json --out .
```

`Player/Report.json`：`passed=true`、`completed=true`、10/10 用例通过。

## 2. CPU 层：dispatch 数量与尺寸

`ScreenSpaceLightingSsgiPass` 新增 `#if UNITY_EDITOR || DEVELOPMENT_BUILD` 的 `s_LastDispatchStats` 快照（release player 编译为空）。验证 player 在预热结束后读取上一帧快照，与由 manifest 推导的期望值逐项比较；任何计数、组维度、keyword 或 history 槽位不符都会写入 `issues`。

| 用例 | 渲染器 | trace | 分辨率 | trace 组 | screen 组 | t/r/v/a/c/d/g/u | keyword half/bn/atlas/list | 结果 |
|---|---|---|---|---|---|---|---|---|---|
| `forward_ssgi_full` | Forward | 1280×720 | full | 160×90 | 160×90 | 1/1/1/2/2/2/0/0 | F/T/F/F | ✅ |
| `forward_ssgi_full_parity` | Forward | 1280×720 | full | 160×90 | 160×90 | 1/1/1/2/2/2/0/0 | F/T/F/F | ✅ |
| `forward_ssgi_half` | Forward | 640×360 | half | 80×45 | 160×90 | 1/1/1/2/2/2/0/1 | T/T/F/F | ✅ |
| `forward_ssgi_half_denoiser` | Forward | 640×360 | half | 80×45 | 160×90 | 1/1/1/2/2/2/2/1 | T/T/F/F | ✅ |
| `forward_ssgi_notemporal` | Forward | 1280×720 | full | 160×90 | — | 1/1/0/0/0/1/0/0 | F/T/F/F | ✅ |
| `forward_ssgi_nodenoise` | Forward | 1280×720 | full | 160×90 | 160×90 | 1/1/1/1/1/0/0/0 | F/T/F/F | ✅ |
| `forward_ssgi_atlas` | Forward | 1280×720 | full | 160×90 | 160×90 | 1/1/1/2/2/2/0/0 | F/T/F/**T** | ✅ |
| `forwardplus_ssgi_full` | Forward+ | 1280×720 | full | 160×90 | 160×90 | 1/1/1/2/2/2/0/0 | F/T/**T**/F | ✅ |
| `deferred_ssgi_full` | Deferred | 1280×720 | full | 160×90 | 160×90 | 1/1/1/2/2/2/0/0 | F/T/F/F | ✅ |
| `deferred_ssgi_half` | Deferred | 640×360 | half | 80×45 | 160×90 | 1/1/1/2/2/2/0/1 | T/T/F/F | ✅ |

列含义：`t/r/v/a/c/d/g/u` = trace / reproject / validate / accumulation / copyHistory / diffuseDenoise / gather / upsample。`2` 表示 second denoiser pass 在同一帧内再跑一遍 accumulation/copy/denoise。

- 组维度：trace/reproject/accumulation/copy/denoise/gather 取 `ceil(trace/8)`；validate 与 upsample 取 `ceil(screen/8)`；z=1（非 XR）。
- `forward_ssgi_notemporal` 没有 validate/accumulation/copy，最终信号为 projection → denoiser；`forward_ssgi_nodenoise` 的最终信号为 accumulation。
- `SSL_PROBE_ATLAS` 只在 Forward+ 为真（cluster list 路径）；`SSL_PROBE_LIST` 只在 Forward/Deferred 且开启 `publishProbeAtlas` 时为真。
- `SSGI_BLUE_NOISE` 在所有用例为真；包内 3 张 LUT 资源存在。

## 3. GPU 层：dispatch 顺序与 kernel

`P2Census.json` 记录每个抓帧在 `Screen Space Global Illumination` marker 内的 dispatch。kernel 按 `ScreenSpaceLightingSsgiPass.Dispatch` 的提交顺序标注，并用 `get_pipeline_state` 的 CS shader id 交叉确认。

| 用例 | dispatch 数 | 顺序 |
|---|---|---|
| `forward_ssgi_full` | 9 | trace, reproject, validate, accumulate, copyHistory, diffuseDenoise, accumulate, copyHistory, diffuseDenoise |
| `forward_ssgi_full_parity` | 9 | 同上 |
| `forward_ssgi_half` | 10 | 同上 + upsample |
| `forward_ssgi_half_denoiser` | 12 | 每轮 denoise 后 + gather，末尾 + upsample |
| `forward_ssgi_notemporal` | 3 | trace, reproject, diffuseDenoise |
| `forward_ssgi_nodenoise` | 5 | trace, reproject, validate, accumulate, copyHistory |
| `forward_ssgi_atlas` | 9 | 同 full |
| `forwardplus_ssgi_full` | 9 | 同 full |
| `deferred_ssgi_full` | 9 | 同 full |
| `deferred_ssgi_half` | 10 | 同 half |

三渲染器共享 validate/accumulate/copyHistory/diffuseDenoise 的 CS shader id（例如 `ResourceId::9855/9858/9861/9864`），trace/reproject 因 keyword 变体各自不同；这与 keyword 驱动的变体结构一致。

## 4. GPU 层：history 双缓冲

每个抓帧的 History HF/LF 纹理用法（`CS_Resource` = SRV 读，`CS_RWResource` = UAV 写）：

| 用例 | accumulate 读取 | copyHistory 写入 |
|---|---|---|
| `forward_ssgi_full` | `HistoryHF1` / `HistoryLF1` | `HistoryHF0` / `HistoryLF0` |
| `forward_ssgi_full_parity`（晚 1 帧） | `HistoryHF0` / `HistoryLF0` | `HistoryHF1` / `HistoryLF1` |
| `forward_ssgi_half` / `deferred_ssgi_half` | `HistoryHF1_640x360` / `HistoryLF1_640x360` | `HistoryHF0_640x360` / `HistoryLF0_640x360` |

- 同一用例内，read 与 write 槽互斥；相邻帧（parity 用例）角色完全交换，证明双缓冲逐帧乒乓。
- CPU 报告中的最近 8 帧槽位序列为 `read=[0,1,0,1,0,1,0,1]`、`write=[1,0,1,0,1,0,1,0]`；抓帧帧的 `historyReadSlot/historyWriteSlot` 与 GPU 绑定一致。
- `forward_ssgi_notemporal` 没有 history 纹理绑定，与配置一致。

## 5. GPU 层：blue-noise LUT

三个 LUT 在 trace 与 reproject dispatch 上均以 `CS_Resource` 绑定：

| 资源 | 绑定事件（所有启用 SSGI 的用例） |
|---|---|
| `OwenScrambledNoise256` (256×256) | trace、reproject |
| `ScramblingTile8SPP` (128×1024) | trace、reproject |
| `RankingTile8SPP` (128×1024) | trace、reproject |

CPU 侧 `SSGI_BLUE_NOISE` keyword 在 10 个用例全部为 true；GPU 侧资源确实进入 dispatch。denoiser 的 `GeneratePointDistribution` 只在资源代数变化后的首帧运行，本轮抓帧均为预热后的稳定帧，因此 LUT 的 denoiser 绑定不在抓帧内；trace/reproject 是蓝色噪声序列的主要消费者。

## 6. GPU 层：反射探针 atlas / list

| 用例 | CPU keyword | GPU 绑定 |
|---|---|---|
| `forward_ssgi_atlas` | `SSL_PROBE_LIST=true` | `URP Reflection Probe Atlas` 在 reproject dispatch 以 `CS_Resource` 绑定 |
| `forwardplus_ssgi_full` | `SSL_PROBE_ATLAS=true` | 同上 |
| 其余 Forward/Deferred 用例 | 两者皆 false | 无 atlas 绑定 |

Atlas 只在 miss 回退的 reproject kernel 读取，与 `ReflectionProbeFallback.hlsl` 的调用点一致。多探针颜色/权重正确性（S4）留给 P2-4 的专门场景；P2-1 只证明 atlas 路径与绑定。

## 7. Binding probe（cbuffer 数值）

每个用例回读 8 个 SSGI 字段，`expected == actual`，最大误差 0：

| 字段 | full-res | half-res |
|---|---|---|
| `_SsgiScreenSize` | 1280,720,1/1280,1/720 | 同左 |
| `_SsgiTraceSize` | 1280,720,1/1280,1/720 | 640,360,1/640,1/360 |
| `_SsgiRayMarchingParams` | 32, 0.90909, -9.1e-5, 1 | 同左 |
| `_SsgiRayMarchingParams2` | 1, frame%16, historyValidity, 0 | 2, frame%16, 1, 0 |
| `_SsgiTemporalParams` | 1,1,0.2,0 | 0.5,2,0.2,0 |
| `_SsgiDenoiserParams` | 0.00160375, -1, 0, 0 | 0.00320750, -1, 0, 0 |
| `_SsgiCameraPositionWS` | (0,2,-7,0) | 同左 |
| `_SsgiZBufferParams` | 999,1,9.99,0.01 | 同左 |

`_SsgiDenoiserParams.x` 在 half-res 下翻倍，证明像素展开角按 trace 尺寸计算；`_SsgiRayMarchingParams2.z` 在 notemporal 用例为 0，其余预热后为 1。

## 8. 验收映射

| 标准 | 结果 |
|---|---|
| S1 dispatch 数量/尺寸与配置一致 | ✅ CPU 逐 kernel 计数与组维度匹配；GPU dispatch 顺序和数量一致 |
| S1 history 双缓冲交替 | ✅ CPU 8 帧槽位序列 + GPU 相邻帧读写角色交换 |
| S1 blue-noise 纹理已绑定 | ✅ 3 张 LUT 在 trace/reproject 绑定，keyword 为真 |
| S4 atlas 路径（部分） | ✅ Forward list / Forward+ atlas 均真实绑定；多探针权重留给 P2-4 |
| S2/S3/S5（覆盖/时域/半分辨率质量） | 不在 P2-1 范围，留给 P2-4 |
| 无 NaN/Inf | ✅ 10/10 用例 `nonFinitePixels=0` |

## 9. 已知边界

1. **dispatch 组维度只能来自 CPU。** renderdoc-mcp v0.3.0 与 `renderdoc-cli` 都不导出 D3D11 的 dispatch group count；GPU 侧能证明的是 dispatch 是否存在、顺序、kernel shader 与绑定。CPU 统计在 dispatch 调用点直接记录参数，二者互补。
2. **cbuffer 数值来自 binding probe。** Unity player 剥离 DXBC 反射名，v0.3.0 没有 cbuffer 工具；probe 证明 C# → HLSL 名称/类型/打包契约，GPU 绑定与像素输出作为第二证据。
3. **`.rdc` 不入库。** 10 个抓帧约 320 MB，超出单轮预算；本地保留在 `validation/P2-SSGI/Player/`，SHA-256 如下。

| 抓帧 | 字节 | SHA-256 |
|---|---|---|
| `deferred_ssgi_full_frame16235.rdc` | 39,768,667 | `db16bf54…1ad6579` |
| `deferred_ssgi_half_frame16253.rdc` | 30,917,235 | `fcc42e44…df14382` |
| `forward_ssgi_atlas_frame16193.rdc` | 33,500,851 | `7dcbcd35…e4f4deb` |
| `forward_ssgi_full_frame16084.rdc` | 39,617,787 | `f6a2ac12…fd64b` |
| `forward_ssgi_full_parity_frame16103.rdc` | 40,116,134 | `4bafd970…5701ae9e9` |
| `forward_ssgi_half_denoiser_frame16139.rdc` | 30,886,710 | `8cc3c10b…3640ecf` |
| `forward_ssgi_half_frame16121.rdc` | 30,837,765 | `8589904c…3f3cfad944` |
| `forward_ssgi_nodenoise_frame16175.rdc` | 30,293,422 | `12c7571b…e2ffff54` |
| `forward_ssgi_notemporal_frame16157.rdc` | 26,421,477 | `68e9f929…5e22e59e` |
| `forwardplus_ssgi_full_frame16214.rdc` | 30,923,143 | `a55a7fa7…7a7172f9` |

## 10. 文件

```text
validation/P2-SSGI/
  P2Manifest.json
  analyze.py
  P2Census.json
  Report.md
  Player/
    Report.json
    BindingProbe_<case>.json ×10
    <case>.png ×10
    *.rdc ×10（本地，不入库）
```