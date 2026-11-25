# T14 三项回归（HEAD 复跑）

RT 功能与 GPU 计时标记（`c359d37`、`8ecd3cb`、`aceb6ca`）之后，按 T14 的既有入口复跑 EditMode、PlayMode 与 T12 图形矩阵，确认屏幕空间光照没有回归。

**范围**：本目录只覆盖这三项；Done 轮 T14 的 Frame Debugger、玩家构建与 XR 运行不在本次复跑内。

## 命令（脚本仍在 Done 轮，输出改到本目录）

```
Invoke-T14EditModeTests.ps1   -LogPath <本目录>/UnityEditModeTests.log   -ResultsPath <本目录>/UnityEditModeTests.xml
Invoke-T14PlayModeTests.ps1   -LogPath <本目录>/UnityPlayModeTests.log   -ResultsPath <本目录>/UnityPlayModeTests.xml
Invoke-T14GraphicsRegression.ps1 -LogPath <本目录>/Graphics/UnityT14GraphicsRegression.log -OutputRoot <临时目录>
```

T1 包装器把设置恢复报告写到脚本所在目录（Done 轮 T14），三份 `*SettingsRestoration.json` 已移到本目录。T12 的 PNG 抓图写到临时目录，未入库；逐 case 的 SHA256、像素差与非有限值在 `Graphics/T12Verification.json`。

## 结果（HEAD `aceb6ca`，Unity 2022.3.62f1）

| 项 | 结果 | 证据 |
|---|---|---|
| EditMode | 184/184 通过，0 失败 | `UnityEditModeTests.xml` |
| PlayMode | 6/6 通过，0 失败 | `UnityPlayModeTests.xml` |
| T12 Forward | 34/34，0 issues | `Graphics/T12Verification.json` |
| T12 ForwardPlus | 34/34，0 issues | 同上 |
| T12 Deferred | 35/35，0 issues | 同上 |
| 设置恢复 | 3/3 干净（exit 0、`allSettingsRestored=true`、无调用异常） | 三份 `*SettingsRestoration.json` |

`verify.py` 复算门槛（EditMode ≥153、PlayMode ≥6、T12 每模式 case 数 34/34/35 且逐 case 通过、设置恢复干净）并写 `Summary.json`。
