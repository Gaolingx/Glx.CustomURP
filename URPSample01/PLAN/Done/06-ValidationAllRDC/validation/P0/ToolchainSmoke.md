# 06 P0 工具链冒烟（2026-11-17）

> 目的：在 P0-1 开始前确认「构建 → 抓帧 → 分析 → 证据」四段工具链可用，并记录版本、路径与已知限制。
> 结论：**分析段已端到端通过**；构建/抓帧段的路径与参数已就绪，实际 player 构建按计划留给 P0-1 首次执行。
> 本轮未修改任何运行时代码、Shader 或测试资产。

## 1. 环境

| 组件 | 实测 |
|---|---|
| OS | Windows 10.0.20348 x64 |
| Unity | `D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe`；工程 `ProjectVersion.txt` = `2022.3.62f1 (4af31df58517)` |
| RenderDoc | `C:\Program Files\RenderDoc`；`renderdoccmd x64 v1.47 built from b0e1370` |
| renderdoc-mcp | `E:\GitHub\renderdoc-mcp\_dist\package\renderdoc-mcp-windows-x64-v0.3.0\bin`（`renderdoc-cli.exe`、`renderdoc-mcp.exe`、`renderdoc.dll`） |
| Python | 3.13.9；`MCP/tools/rdmcp.py`（仅标准库） |
| 抓帧目标 | D3D11、Development、1280×720（沿用 P1-3 player 约定） |

## 2. 检查结果

| 检查 | 命令 | 结果 |
|---|---|---|
| RenderDoc 注入器 | `renderdoccmd --version` | 1.47，x64 |
| Player 构建入口 | `ScreenSpaceLightingP13PlayerSetup.CreateScene/BuildPlayer` | 存在（`Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingP13PlayerSetup.cs:22,83`） |
| 三渲染器资产 | `Assets/Tests/ScreenSpaceLighting/Renderers/` | `SslForward/ForwardPlus/DeferredPipeline.asset` 与对应 Renderer 齐全 |
| 抓帧参数 | `renderdoccmd capture` | `-d`、`-c`、`-w`、`--opt-hook-children` 均可用 |
| CLI 回放 | `renderdoc-cli <rdc> info` | 成功：D3D11，147 events / 65 draws，Degraded=no |
| CLI draw 定位 | `renderdoc-cli <rdc> draws` | 成功；玻璃球 2304 indices 的 EID=786 |
| MCP 客户端 | `python MCP/tools/rdmcp.py tools` | 成功返回工具列表 |
| MCP 打开抓帧 | `python MCP/tools/rdmcp.py open <rdc>` | 成功：D3D11，65 draws / 147 events |
| 像素调试 | `python MCP/tools/rdmcp.py debug-pixel <rdc> 640 359 --event 786` | 成功：PS 输出 `o0 = (1, 1, 2.7e-15, 1)`，382 steps，与归档 `debug_pixel_CameraAtA_Both.json` 的 `(1,1,0,1)` 一致 |

## 3. 端到端分析冒烟

使用已归档的 P1-3 抓帧（只读）：

```bash
CLI="/e/GitHub/renderdoc-mcp/_dist/package/renderdoc-mcp-windows-x64-v0.3.0/bin/renderdoc-cli.exe"
RDC="E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/05-ScreenSpaceRefractionImprove/validation/P1-3/Player/P13_CameraAtA_Both_frame162.rdc"

"$CLI" "$RDC" info
"$CLI" "$RDC" draws | awk '$2==2304{print}'
python "E:/GitHub/Glx.CustomURP/MCP/tools/rdmcp.py" debug-pixel "$RDC" 640 359 --event 786
```

关键输出：

- `info`：`API=D3D11`、`Total events=147`、`Total draws=65`、`Degraded=no`。
- `draws`：`786  2304  1`（玻璃球）。
- `debug-pixel`：`outputs[0].floatValues = [1.0, 1.0, 2.676777426287042e-15, 1.0]`、`totalSteps=382`。

说明：CLI 的 `Total events` 是动作计数，EID 是稀疏动作 ID（本抓帧上界约 1237），不是 `1..eventCount`。后续脚本必须每次重新 `draws` 定位，不能跨抓帧复用 EID。

## 4. 各阶段就绪判断

| 阶段 | 状态 | 说明 |
|---|---|---|
| P0-1 构建 player | **Executed** | `ScreenSpaceLightingValidationPlayerSetup.BuildPlayer`：D3D11 Development 1280×720，0 error；以 Deferred 资产构建以保留 deferred 变体 |
| P0-1 抓帧 | **Executed** | 9 个用例通过 in-app API 各抓 1 帧；`.rdc` 本地保留 |
| P0-2 分析 | **Executed（CLI）** | 新抓帧上重跑 `renderdoc-cli events`，按 marker 统计 dispatch；脚本未产品化 |
| P0-3 Binding probe | **Executed** | 35 字段 × 9 用例，0 不匹配；覆盖 SSGI 8 / SSR 7 / GTAO 14 / 折射 6 |
| P0-4 证据 | **Executed** | `Report.json`、`BindingProbe_*.json`、PNG 已落盘，见 [SmokeReport.md](SmokeReport.md) |

## 5. 已知限制

1. **原生 MCP 未在当前会话暴露**：`mcp_list(keyword="renderdoc")` 返回空；当前可直接使用 `renderdoc-cli` 或 `MCP/tools/rdmcp.py`。若需要 `mcp__renderdoc_mcp__*` 工具，需在新会话中确认注册生效。
2. **v0.3.0 没有 cbuffer 工具**：无法直接 dump 常量缓冲；cbuffer 数值证据继续以 binding probe 为主，RDC 的 PSO/shader hash/像素输出为辅。可选从上游 `main` 源码构建带 cbuffer 工具的版本。
3. **Unity player 剥离 DXBC 反射**：抓帧内常量块显示为 `cbuffer0..4`，变量名为空；不要依赖按名字读取 shader 变量。
4. **`.rdc` 体积**：沿用 HANDOFF 第 7.5 节预算（提交总量 ≤150 MB、代表抓帧 ≤12 个）。
5. **抓帧/回放版本**：抓帧使用系统 RenderDoc 1.47，回放使用 renderdoc-mcp 包内运行时；本冒烟回放成功，后续若出现回放失败，优先用系统 `qrenderdoc` 交叉确认。

## 6. 复现

```bash
# 工具版本
"/c/Program Files/RenderDoc/renderdoccmd.exe" --version
"/e/GitHub/renderdoc-mcp/_dist/package/renderdoc-mcp-windows-x64-v0.3.0/bin/renderdoc-cli.exe"

# 分析冒烟（只读归档抓帧）
CLI="/e/GitHub/renderdoc-mcp/_dist/package/renderdoc-mcp-windows-x64-v0.3.0/bin/renderdoc-cli.exe"
RDC="E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/05-ScreenSpaceRefractionImprove/validation/P1-3/Player/P13_CameraAtA_Both_frame162.rdc"
"$CLI" "$RDC" info
"$CLI" "$RDC" draws | awk '$2==2304{print}'
python "E:/GitHub/Glx.CustomURP/MCP/tools/rdmcp.py" debug-pixel "$RDC" 640 359 --event 786
```

## 7. P0 本轮结果（2026-11-17）

P0-1/P0-3/P0-4 已执行：三渲染器 × GTAO/SSGI/SSR/折射 共 9 个用例全部通过，35 个 binding probe 字段 0 不匹配。构建、抓帧、census、probe 回读与图像差异的完整记录见 [SmokeReport.md](SmokeReport.md)。