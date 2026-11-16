# RenderDoc 操作文档（Unity player 抓帧与 MCP/CLI 分析）

本文是一份可照抄的端到端操作手册：构建 Unity standalone player → 用 RenderDoc 注入并精确抓帧 → 用 renderdoc-cli / renderdoc-mcp 分析 → 输出可提交的证据。示例均来自本仓库已归档的 P1-3 验证（`URPSample01/PLAN/Done/05-ScreenSpaceRefractionImprove/validation/P1-3/`）。

## 0. 快速开始

```bash
# 1) 构建 player（Unity，D3D11，development，1280x720）
"/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingP13PlayerSetup.BuildPlayer \
  -logFile "E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/05-ScreenSpaceRefractionImprove/UnityP13Player.log"

# 2) 用 RenderDoc 注入运行；player 内通过 in-app API 触发抓帧
cd "E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/05-ScreenSpaceRefractionImprove/validation/P1-3/Player"
"/c/Program Files/RenderDoc/renderdoccmd.exe" capture -w --opt-hook-children \
  -c "$(pwd)/manual" -d "$(pwd)" "$(pwd)/SslP13Player.exe" -ssl-p13-out "$(pwd)"

# 3) 列 draw、找目标事件
CLI="/e/GitHub/renderdoc-mcp/_dist/package/renderdoc-mcp-windows-x64-v0.3.0/bin/renderdoc-cli.exe"
"$CLI" "P13_CameraAtA_Both_frame162.rdc" info
"$CLI" "P13_CameraAtA_Both_frame162.rdc" draws | awk '$2==2304{print $1}'

# 4) 读像素 shader 实际输出（RenderDoc 左上原点）
python ../../../../../../../MCP/tools/rdmcp.py debug-pixel \
  "P13_CameraAtA_Both_frame162.rdc" 640 359 --event 786
```

## 1. 前置检查

| 检查项 | 命令 / 位置 |
|---|---|
| RenderDoc 已安装 | `"/c/Program Files/RenderDoc/renderdoccmd.exe" --version` → `renderdoccmd x64 v1.47 ...` |
| renderdoc-mcp 包存在 | `E:\GitHub\renderdoc-mcp\_dist\package\renderdoc-mcp-windows-x64-v0.3.0\bin\` |
| 无残留进程占文件 | `tasklist | grep -iE "renderdoc|SslP13Player"`，必要时 `taskkill //PID <pid> //F` |
| 项目未被 Unity 占用 | `tasklist | grep -i unity.exe` |
| 目标 API | 只支持/优先 D3D11（`PlayerSettings.SetGraphicsAPIs(..., Direct3D11)`） |

**为什么用 player 而不是编辑器**：编辑器 batchmode 下 `FrameTimingManager` 无 GPU 样本、`ScreenCapture` 依赖实际 backbuffer、部分 URP 状态在 `RenderTexture` 路径下与真实呈现不同。player 抓帧拿到的就是最终呈现给玩家的 GPU 帧。

## 2. 抓帧方式

### 2.1 手动抓帧（GUI 调试）

1. 启动 `qrenderdoc.exe` → `Launch Application`，选择 player 可执行文件，勾选 `Capture Child Processes`（若 player 会再启动进程）。
2. 运行后按 **F12**（默认 capture key）抓当前帧；或用 `renderdoccmd.exe capture <exe>` 启动后按 F12。
3. 适合交互式调查，不适合回归证据（帧号/相机姿态不可控）。

### 2.2 自动化抓帧（推荐）

分两步：**player 内触发** + **外部注入**。RenderDoc 的 in-app API 允许程序自己决定抓哪一帧，帧号、相机姿态、probe 配置都可以精确控制。

#### 2.2.1 Player 构建要点

- `BuildOptions.Development`；窗口/分辨率固定（示例 1280x720 windowed）。
- `PlayerSettings.SetGraphicsAPIs(StandaloneWindows64, new[]{ GraphicsDeviceType.Direct3D11 })`。
- 构建完成后恢复所有全局设置，避免污染工程。
- **Shader/材质资产必须被场景引用**：player 没有 `Shader.Find` 的运行时补齐能力，运行时 `new Material(Shader.Find(...))` 容易拿到粉色。做法是把材质做成资产并由场景组件字段引用（示例：`P13PlayerFloor.mat`、`P13PlayerBackdrop.mat`、`P13PlayerPattern.mat`、`T11RefractionBox.mat`）。
- 程序参数用自定义开关（示例 `-ssl-p13-out <dir>`）传入输出目录。
- 结束前写 `Report.json` 并 `Application.Quit(0/1)`，方便 CI 判断。

参考实现：[ScreenSpaceLightingP13PlayerSetup.cs](../URPSample01/Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingP13PlayerSetup.cs)。

#### 2.2.2 In-app API（C# P/Invoke）

Unity 没有内置 RenderDoc 绑定；运行时从已注入的 `renderdoc.dll` 取 `RENDERDOC_GetAPI`。API 结构体是**顺序排列的函数指针表**，字段顺序必须与 `renderdoc_app.h` 一致（下表对应 RenderDoc 1.47 / API 1.6.0，cdecl）：

```
GetAPIVersion, SetCaptureOptionU32, SetCaptureOptionF32, GetCaptureOptionU32,
GetCaptureOptionF32, SetFocusToggleKeys, SetCaptureKeys, GetOverlayBits,
MaskOverlayBits, Shutdown, UnloadCrashHandler, SetCaptureFilePathTemplate,
GetCaptureFilePathTemplate, GetNumCaptures, GetCapture, TriggerCapture,
IsTargetControlConnected, LaunchReplayUI, SetActiveWindow, StartFrameCapture,
IsFrameCapturing, EndFrameCapture
```

最小可用代码（节选自 P1-3 实现）：

```csharp
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int GetApiDelegate(uint version, out IntPtr apiPointers);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void TriggerCaptureDelegate();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate uint GetNumCapturesDelegate();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate uint GetCaptureDelegate(uint index, byte[] filename, ref uint pathLength, out ulong timestamp);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void SetCaptureFilePathTemplateDelegate([MarshalAs(UnmanagedType.LPStr)] string path);

[DllImport("kernel32", CharSet = CharSet.Ansi)]
static extern IntPtr GetModuleHandle(string moduleName);
[DllImport("kernel32", CharSet = CharSet.Ansi)]
static extern IntPtr GetProcAddress(IntPtr module, string procName);

// 1) 取模块（必须由 renderdoccmd 启动，否则为 0）
var module = GetModuleHandle("renderdoc.dll");
if (module == IntPtr.Zero) return; // 非注入运行：跳过抓帧，仍可出截图/指标

// 2) 取 API 表（请求 1.6.0 = 10600）
var getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(
    GetProcAddress(module, "RENDERDOC_GetAPI"));
getApi(10600, out var apiPointers);
var table = Marshal.PtrToStructure<ApiTable>(apiPointers); // ApiTable 为 22 个 IntPtr 的顺序结构

// 3) 每次抓帧前设置模板；实际文件名为 <template>_frame<N>.rdc
setTemplate("D:/out/P13_CameraAtA_Both");
triggerCapture();

// 4) 等到抓帧完成（RenderDoc 在下一个 Present 结束抓帧）
while (getNumCaptures() == before && frames < 10) yield return new WaitForEndOfFrame();
// 5) 取真实路径
string path = ReadCapturePath(getCapture, getNumCaptures() - 1);
```

完整实现见 [ScreenSpaceLightingP13PlayerVerification.cs](../URPSample01/Assets/Tests/ScreenSpaceLighting/Runtime/ScreenSpaceLightingP13PlayerVerification.cs) 的 `RenderDocApi` 内部类。

**协程陷阱（本次踩过）**：
- `yield return` 不能写在带 `catch` 的 `try` 块里（CS1626）；用 `try/finally` 或在协程外做异常处理。
- `TriggerCapture` 抓的是**下一个完整帧**，必须等待 `GetNumCaptures()` 增加，不能抓完立刻截图。
- 抓帧后立即 `ScreenCapture.CaptureScreenshotAsTexture()` 取同一配置的 backbuffer；不要在 `RenderTexture` 目标 + 未呈现的路径上取，否则 `.rdc` 与截图对不上。
- `WaitForEndOfFrame` 在 player 内每帧都可用；避免在 `Awake` 内直接开协程等待渲染。

#### 2.2.3 外部注入运行

```bash
"/c/Program Files/RenderDoc/renderdoccmd.exe" capture \
  -w \                       # wait-for-exit
  --opt-hook-children \      # 子进程也注入
  -c "<out>/manual" \        # 默认模板（in-app API 会覆盖）
  -d "<working-dir>" \
  "<exe>" -ssl-p13-out "<out>"
```

- 抓帧由 player 内 API 触发，因此 `-c` 只是兜底模板；实际文件为 `P13_CameraAtA_Both_frame162.rdc` 等。
- 抓帧完成后 `GetCapture()` 返回绝对路径，直接写进报告，避免猜文件名。

## 3. 用 CLI 分析

语法：`renderdoc-cli.exe <capture.rdc> <command> [options]`。

### 3.1 看帧与定位目标 draw

```bash
CLI=".../renderdoc-cli.exe"
"$CLI" capture.rdc info        # API、事件数、draw 数
"$CLI" capture.rdc draws       # EID / Indices / Instances
"$CLI" capture.rdc draws | awk '$2==2304{print $1}'   # 例：Unity Sphere 2304 indices
"$CLI" capture.rdc pipeline -e 786   # 该 EID 的 VS/PS/RT/深度/视口
"$CLI" capture.rdc shader ps -e 786  # DXBC 反汇编（player 中通常无源码/变量名）
```

定位技巧：
- 先看 `Indices` 区分内置几何（Sphere=2304、Cube=36、全屏三角=3）。
- 透明物体在 `Draw Transparency` 标记之后；`pipeline` 的 RenderTarget 是 `_CameraColorAttachmentA_...`。
- **EID 随抓帧内容变化**：同一场景、不同 probe 配置下玻璃 draw 可能从 786 变成 783/785/831。不要跨抓帧复用 EID，每次重新 `draws` 定位。

### 3.2 像素与 shader 输出

```bash
# RenderDoc 左上原点：(x=640, y=359) 对应 Unity 1280x720 画面中心
"$CLI" capture.rdc pick-pixel 640 359 -e 786
"$CLI" capture.rdc debug pixel 640 359 -e 786 --trace
"$CLI" capture.rdc export-rt 0 -o ./rt -e 786
"$CLI" capture.rdc mesh 786 --format json -o mesh.json
"$CLI" capture.rdc assert-pixel 786 640 359 --expect 1 1 0 1 --tolerance 0.01
```

- `debug pixel --trace` 在 player 构建里通常只有寄存器（`r0..r13`）与输入/输出 varying（`v0..v5`、`o0`），因为 Unity 剥离了调试符号；但 **输出颜色** 足以证明 shader 实际走了哪个分支。
- `pick-pixel` 读的是该 EID 时刻的 RT 像素（注意 `--target`）。
- 导出 RT 时 `IDX` 是该 EID 下的 render target 序号（0 = 第一个 RT）。

### 3.3 结构化对比

```bash
"$CLI" diff A.rdc B.rdc --draws        # draw 列表差异
"$CLI" diff A.rdc B.rdc --resources    # 资源差异
"$CLI" diff A.rdc B.rdc --stats        # 统计差异
```

v0.3.0 的 `--framebuffer` 在本机实测长时间无输出，图像级对比建议改走下面的 MCP 像素调试或导出 RT 后用脚本比较。

## 4. 用 MCP 分析

### 4.1 在 Qoder 内

MCP 已注册为 `renderdoc-mcp`（工具前缀 `mcp__renderdoc_mcp__`）。**注册后新开会话** 才会出现工具。典型调用顺序：

1. `open_capture { "path": "绝对路径.rdc" }`
2. `list_draws` / `list_events` / `get_pipeline_state { "eventId": 786 }`
3. `debug_pixel { "eventId": 786, "x": 640, "y": 359, "mode": "trace" }`
4. `pick_pixel`、`get_texture_stats`、`export_render_target`

`debug_pixel` 返回结构：

```json
{
  "eventId": 786,
  "stage": "ps",
  "totalSteps": 382,
  "inputs":  [{"name": "v0", "floatValues": [...]}, ...],
  "outputs": [{"name": "o0", "floatValues": [1.0, 1.0, 0.0, 1.0]}],
  "trace":   [{"step": 0, "instruction": 12, "changes": [...]}]
}
```

### 4.2 用 `MCP/tools/rdmcp.py`（不依赖 Qoder 会话）

```bash
python MCP/tools/rdmcp.py tools                          # 列出该版本可用工具
python MCP/tools/rdmcp.py open capture.rdc               # 打开并打印捕获信息
python MCP/tools/rdmcp.py call capture.rdc list_draws '{"filter":"glass"}'
python MCP/tools/rdmcp.py call capture.rdc get_pipeline_state '{"eventId":786}'
python MCP/tools/rdmcp.py debug-pixel capture.rdc 640 359 --event 786 --out debug.json
```

- 默认 server 路径为本机 v0.3.0；用 `--server` 或环境变量 `RENDERDOC_MCP_EXE` 覆盖。
- 协议：每行一个 JSON-RPC 2.0 消息；先 `initialize` + `notifications/initialized`，再 `tools/call`。工具返回 `result.content[0].text` 为 JSON 字符串。
- 关闭时先关 stdin 再等待进程退出；异常退出时 kill，避免残留进程占用 `.rdc`。

## 5. 端到端案例：P1-3 代理选择

场景：probe B（8³，包含玻璃球中心，importance 1）与 probe A（4³，围绕相机，importance 0）；玻璃 `_Ior=1`、opacity 0，图案面板只进相机颜色金字塔、不进 probe cubemap。

步骤：

1. 构建 player（第 2.2.1 节）。
2. `renderdoccmd capture` 运行；player 对 `CameraAtA` / `CameraMovedToB` × `Both/ProbeAOnly/ProbeBOnly` 各抓一帧，并同时输出 PNG/指标。
3. 期望判定：
   - `Both` 与 `ProbeBOnly` 的玻璃内圈均值差 ≤ 0.005；与 `ProbeAOnly` ≥ 0.01。
   - `Both` 与 `ProbeAOnly` 的内圈差异像素 > 50%；与 `ProbeBOnly` ≤ 3%（余差来自球缘 Fresnel/`unity_SpecCube0` 耦合）。
4. `.rdc` 侧复核（本仓库结果）：

| 抓帧 | 玻璃 draw EID | `debug_pixel(640,359)` 输出 |
|---|---|---|
| `P13_CameraAtA_Both_frame162.rdc` | 786 | `(1, 1, 0, 1)` |
| `P13_CameraAtA_ProbeBOnly_frame184.rdc` | 783 | `(1, 1, 0, 1)` |
| `P13_CameraAtA_ProbeAOnly_frame173.rdc` | 785 | `(0.138, 0.119, 0.104, 1)` |
| `P13_CameraMovedToB_Both_frame195.rdc` | 831 | `(1, 1, 0, 1)` |
| `P13_CameraMovedToB_ProbeBOnly_frame217.rdc` | 831 | `(1, 1, 0, 1)` |
| `P13_CameraMovedToB_ProbeAOnly_frame206.rdc` | 835 | `(0.138, 0.119, 0.104, 1)` |

结论：两个相机位姿下 `Both` 的 GPU 输出与 `ProbeBOnly` 完全一致，且相机进入 A 包围盒后不切换到 A —— 代理按物体中心选择，不随相机变化。

复现命令（在证据目录内）：

```bash
CLI="/e/GitHub/renderdoc-mcp/_dist/package/renderdoc-mcp-windows-x64-v0.3.0/bin/renderdoc-cli.exe"
"$CLI" P13_CameraAtA_Both_frame162.rdc draws | awk '$2==2304{print $1}'
python <repo>/MCP/tools/rdmcp.py debug-pixel P13_CameraAtA_Both_frame162.rdc 640 359 --event 786
```

## 6. 故障排查

| 现象 | 原因 | 处理 |
|---|---|---|
| `renderdoc.dll not loaded` / 无抓帧 | player 不是由 `renderdoccmd` 启动，或 DLL 被禁用 | 用 `renderdoccmd capture` 启动；确认 `GetModuleHandle("renderdoc.dll") != 0` |
| `GetNumCaptures` 一直不增加 | `TriggerCapture` 后没有 Present（窗口最小化/暂停）或抓帧被另一抓帧阻塞 | 确保窗口可见、连续渲染；一次只挂一个抓帧；等待 10 帧超时后报错 |
| `debug_pixel` 报 `No debuggable fragment` | 用了另一抓帧的 EID / 坐标不在三角形内 | 每个抓帧重新 `draws` 找 EID；确认像素在玻璃投影内 |
| 抓帧里 PS 只有 `cbuffer0..4`、无变量名 | Unity player 剥离 DXBC 反射 | 属预期；用像素输出/断言间接验证，或从源码构建带 cbuffer 工具的新版（见 README） |
| `.rdc` 无法删除/移动（Device or resource busy） | `renderdoc-cli`/`qrenderdoc`/残留 MCP server 持有文件，或 shell 的 cwd 在目录内 | 结束进程；把 shell `cd` 到仓库根再移动；必要时逐项移动内容 |
| PNG 与 `.rdc` 内容对不上 | 截图取自 `RenderTexture` 或抓帧后状态已切换 | 抓帧后在同一配置下再截 backbuffer；避免在 `WaitForEndOfFrame` 之外取图 |
| 抓到黑帧/不完整帧 | 相机渲染到 RT 而非 backbuffer；或抓帧窗口在加载阶段 | 抓帧前预热若干帧；使用 present 路径；核对 `info` 的 RT 列表 |
| D3D12/Vulkan 抓帧失败 | 后端/驱动支持差异 | 验证环境固定 D3D11；其它 API 单独评估 |
| MCP 注册后当前会话看不到工具 | MCP 工具在会话启动时枚举 | 新建会话，或先用 `tools/rdmcp.py`/CLI |

## 7. 证据与提交约定

- 目录：`PLAN/Done/<round>/validation/<task>[/Player]/`。
- 每次抓帧保留：`.rdc`、抓帧对应 PNG、`Report.json`（含 passed、相机位姿、probe 参数、像素指标、抓帧路径）、以及 `debug_pixel_*.json` 等分析结果。
- 报告里记录**绝对路径**会随归档失效；归档后以报告文件名 + 目录内文件为准（历史价值大于路径正确性）。
- **不提交** player 可执行文件与 `_Data`/`UnityPlayer.dll` 等构建产物；`.rdc` 体积较大（本次 6 个约 35 MB），提交前确认仓库策略。
- 固定判定阈值时写明理由：像素级对比要排除 Fresnel 边缘（`unity_SpecCube0` 反射耦合），否则会把已知差异误判成回归。
