# RenderDoc MCP 工具链说明

本目录记录本机 **RenderDoc + renderdoc-mcp** 的安装、配置、可用能力和使用边界，供后续 URP/图形调试与自动化回归直接复用。操作步骤与命令示例见 [RenderDocOperations.md](RenderDocOperations.md)。

## 1. 组件与版本

| 组件 | 本机位置 / 版本 | 说明 |
|---|---|---|
| RenderDoc | `C:\Program Files\RenderDoc`（1.47, x64） | 提供 `renderdoccmd.exe`、`renderdoc.dll`、`qrenderdoc.exe`、`renderdoc_app.h` |
| renderdoc-mcp | `E:\GitHub\renderdoc-mcp` | 上游仓库 [JiaboLi-GitHub/renderdoc-mcp](https://github.com/JiaboLi-GitHub/renderdoc-mcp)，C++ MCP Server + CLI |
| 预编译包 | `E:\GitHub\renderdoc-mcp\_dist\package\renderdoc-mcp-windows-x64-v0.3.0\` | GitHub Release **v0.3.0** 的 Windows 包；v0.3.1 Release 没有附件 |
| CLI | `...\bin\renderdoc-cli.exe` | 一次性命令行分析工具 |
| MCP Server | `...\bin\renderdoc-mcp.exe` | stdio JSON-RPC Server |
| Qoder 注册名 | `renderdoc-mcp`（serverName `renderdoc_mcp`） | 通过 `manage_app_mcp` 注册；新会话生效 |
| 复用客户端 | [tools/rdmcp.py](tools/rdmcp.py) | 本目录自带的最小 stdio MCP 客户端 |
| Unity | `D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe` | 抓帧验证使用 D3D11 standalone player |

**不需要 Python venv**：renderdoc-mcp 是 C++ 二进制，`renderdoc-cli.exe` 直接可用。Python 仅用于编写分析脚本或调用 MCP（`tools/rdmcp.py`，标准库即可，无第三方依赖）。RenderDoc 官方 Python 模块（`renderdoc.pyd`）未随本机安装包提供，也不在 `renderdoc-mcp` 依赖内。

## 2. 安装与验证

### 2.1 获取 renderdoc-mcp

```bash
git clone https://github.com/JiaboLi-GitHub/renderdoc-mcp.git E:/GitHub/renderdoc-mcp
# v0.3.1 的 Release 没有资产；使用 v0.3.0 的 Windows 包
curl -L -o E:/GitHub/renderdoc-mcp/_dist/renderdoc-mcp-windows-x64-v0.3.0.zip \
  https://github.com/JiaboLi-GitHub/renderdoc-mcp/releases/download/v0.3.0/renderdoc-mcp-windows-x64-v0.3.0.zip
cd E:/GitHub/renderdoc-mcp/_dist
unzip -q renderdoc-mcp-windows-x64-v0.3.0.zip -d package
```

包内 `bin/` 自带 `renderdoc.dll`、`renderdoc.json`、`d3dcompiler_47.dll`、`dbghelp.dll`、`symsrv.dll`，无需与系统安装的 RenderDoc 版本一致。抓帧注入使用系统 `renderdoccmd`（见操作文档），回放/分析使用包内运行时。

### 2.2 验证 CLI

```bash
"/e/GitHub/renderdoc-mcp/_dist/package/renderdoc-mcp-windows-x64-v0.3.0/bin/renderdoc-cli.exe"
# 输出 Usage: <capture.rdc> <command> [options]
```

注意参数顺序：**先 `.rdc`，后命令**（与部分文档仓库示例相反）。`diff` 子命令是例外：`renderdoc-cli diff FILE_A FILE_B [--draws|--resources|--stats|...]`。

### 2.3 注册到 Qoder

已在 Qoder 中注册为自定义 MCP：

```json
{
  "command": "E:\\GitHub\\renderdoc-mcp\\_dist\\package\\renderdoc-mcp-windows-x64-v0.3.0\\bin\\renderdoc-mcp.exe",
  "args": [],
  "env": {}
}
```

- 注册名（entry key）：`renderdoc-mcp`；`serverName`：`renderdoc_mcp`；`serverId`：`e1640a47-5283-4b4c-aee3-e5b925bed7fe`。
- 注册完成后 **新开会话** 才会在 `mcp_list` 中出现 `mcp__renderdoc_mcp__*` 工具；当前会话仍可用 CLI 或 `tools/rdmcp.py`。
- 若注册被权限模式拦截，切换到 Full Access 后重试 `manage_app_mcp(add)`；不要通过手改配置文件绕过权限。

## 3. 能力清单（以 v0.3.0 二进制实测为准）

v0.3.0 的 MCP Server 与 CLI 共享核心实现，但 CLI 只暴露其中一部分命令。`tools/list` 实测可用工具：

| 分类 | MCP 工具 |
|---|---|
| 会话 | `open_capture`、`close_capture`、`session_status` |
| 事件 / draw | `list_events`、`goto_event`、`list_draws`、`get_draw_info` |
| 捕获信息 | `get_capture_info`、`get_stats`、`get_log` |
| 资源 | `list_resources`、`get_resource_info`、`export_render_target`、`export_texture`、`export_buffer`、`export_mesh`、`export_snapshot`、`get_resource_usage` |
| Pass | `list_passes`、`get_pass_info`、`get_pass_attachments`、`get_pass_statistics`、`get_pass_deps`、`find_unused_targets` |
| 管线 / 绑定 | `get_pipeline_state`、`get_bindings` |
| Shader | `get_shader`（`disasm` / `reflect`）、`list_shaders`、`search_shaders`、`shader_encodings`、`shader_build`、`shader_replace`、`shader_restore`、`shader_restore_all` |
| 像素 / 调试 | `pixel_history`、`pick_pixel`、`debug_pixel`、`debug_vertex`、`debug_thread`、`get_texture_stats` |
| 断言 | `assert_pixel`、`assert_state`、`assert_image`、`assert_count`、`assert_clean` |
| 抓帧 | `capture_frame` |
| Diff | `diff_open`、`diff_close`、`diff_summary`、`diff_draws`、`diff_resources`、`diff_stats`、`diff_pipeline`、`diff_framebuffer` |

CLI 命令（v0.3.0 `renderdoc-cli` Usage 原文）：

```
info
events [--filter TEXT]
draws  [--filter TEXT]
pipeline [-e EID]
shader STAGE [-e EID]        (vs|hs|ds|gs|ps|cs)
resources [--type TYPE]
export-rt IDX -o DIR [-e EID]
capture EXE [-w DIR] [-a ARGS] [-d N] [-o PATH]
pixel X Y [-e EID] [--target N]
pick-pixel X Y [-e EID] [--target N]
debug pixel X Y -e EID [--trace] [--primitive N]
debug vertex VTX -e EID [--trace] ...
debug thread GX GY GZ TX TY TZ -e EID [--trace]
tex-stats RES_ID [-e EID] [--mip N] [--slice N] [--histogram]
shader-encodings
shader-build FILE --stage STAGE --encoding ENC [--entry NAME]
shader-replace EID STAGE --with SHADER_ID
shader-restore EID STAGE
shader-restore-all
mesh EID [--stage vs-out|gs-out] [--format obj|json] [-o FILE]
snapshot EID -o DIR
usage RES_ID
assert-pixel EID X Y --expect R G B A [--tolerance T] [--target N]
assert-state EID PATH --expect VALUE
assert-image EXPECTED ACTUAL [--threshold T] [--diff-output PATH]
assert-count WHAT --expect N [--op eq|gt|lt|ge|le]
assert-clean [--min-severity high|medium|low|info]
diff FILE_A FILE_B [--draws|--resources|--stats|--pipeline MARKER|--framebuffer]
pass-stats
pass-deps
unused-targets
```

**已知缺口**：上游 `main` 源码已包含 `list_cbuffers` / `get_cbuffer_contents`（`src/mcp/tools/cbuffer_tools.cpp`），但 **v0.3.0 二进制没有**；CLI 的 `cbuffer` 命令也只存在于源码。需要读取常量缓冲区数值时，可选：从源码构建新版（需 RenderDoc 源码与构建产物），或改用本文的间接验证方法（见第 4 节）。

## 4. 使用边界与已知限制

1. **Unity player 会剥离 DXBC 反射**：抓帧里 PS 常量块显示为 `cbuffer0..4`，变量列表为空，无法按名字取 `_SslRefractionProbe*`。即使升级工具，Unity 发布构建也拿不到变量名；开发构建同样不保证保留。
2. **编辑器 vs player**：`ScreenCapture`、`FrameTimingManager` 与部分 URP 状态在编辑器 batchmode 下不可用/失真；需要 GPU 侧证据时使用 standalone player（见操作文档）。
3. **D3D11 优先**：RenderDoc 对 D3D11 的抓帧与像素调试最稳定；D3D12/Vulkan 需要对应后端扩展，且本机 player 验证只使用 D3D11。
4. **抓帧文件较大**：本仓库 P1-3 证据 6 个 `.rdc` 共约 35 MB。提交前确认仓库策略；player 可执行文件与 `_Data` 不入库，按文档重建。
5. **文件占用**：`renderdoc-cli.exe` / `qrenderdoc.exe` / 残留的 `renderdoc-mcp.exe` 会持有 `.rdc`，导致移动/删除目录报 `Device or resource busy`；先结束进程或关闭回放。
6. **v0.3.0 的 `diff --framebuffer`**：本机实测长时间无输出（两抓帧加载较慢/行为不稳定），自动化对比优先用 `pick-pixel` / `debug_pixel` / 导出 RT 后自行比较。
7. **坐标原点**：RenderDoc 像素坐标原点在左上；Unity `WorldToViewportPoint` 原点在左下，换算 `y_rdc = height - 1 - y_unity`。
8. **抓帧与回放版本**：抓帧由系统 RenderDoc 1.47 生成；`renderdoc-mcp` 包内自带运行时用于回放，版本不一致时可能出现回放失败，优先保持两者同源或使用系统 `qrenderdoc` 打开验证。

## 5. 与本项目的结合点

- P1-3 代理选择验证（已归档）：
  - 场景/构建：[ScreenSpaceLightingP13PlayerSetup.cs](../URPSample01/Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingP13PlayerSetup.cs)
  - player 内抓帧：[ScreenSpaceLightingP13PlayerVerification.cs](../URPSample01/Assets/Tests/ScreenSpaceLighting/Runtime/ScreenSpaceLightingP13PlayerVerification.cs)
  - 证据与报告：`URPSample01/PLAN/Done/05-ScreenSpaceRefractionImprove/validation/P1-3/Player/`
- 该案例覆盖了：双 probe 代理表、相机移动稳定性、Through-GPU 像素输出对比、`.rdc` 内 `debug_pixel` 取 shader 输出。
- 后续任何"编辑器截图不够"的 GPU 验证，按 [RenderDocOperations.md](RenderDocOperations.md) 的模板复用即可。

## 6. 升级与维护

- **更换版本**：下载新 release zip 到 `_dist/package/...`，更新 Qoder MCP 配置中的 `command` 路径并重启会话；保留旧版本目录便于回退。
- **从源码构建**（需要 RenderDoc 源码及其构建产物）：

  ```bash
  cmake -B build -DRENDERDOC_DIR=<renderdoc-source> -DRENDERDOC_BUILD_DIR=<renderdoc-build>
  cmake --build build --config Release
  ```

  构建后可获得包含 cbuffer 工具的 CLI/MCP；注意与系统 RenderDoc 版本匹配。
- **不要提交**：`_dist/` 下的压缩包、构建目录、抓帧回放缓存。
