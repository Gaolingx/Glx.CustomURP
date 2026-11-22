```
前情提要：
用户最初认为 Forward+ 保留现有 CPU jobs；不是把“全局删除 CPU jobs”改称完成。原因是 [ForwardLights.cs:458](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/ForwardLights.cs#L458) 明确把 Forward+ 排除在 GPU builder 之外，而 [同文件第 652 行](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/ForwardLights.cs#L652) 正在把 CPU 结果绑定给 Forward+。直接删 jobs 会切断一条仍在用的渲染路径。

调研结果：**可以放心保留 Forward+，同时彻底删除运行时的 CPU light-list build。** HDRP 14.0.12 的分界是：**CPU 提供这台相机有哪些候选灯和探针；GPU 决定每个 tile 或深度簇里有哪些灯。** 建议借这个分工，不照搬整套 HDRP FPTL。

### HDRP 的 Forward 光照怎么做

| 阶段 | 本地源码里的实际做法 |
|---|---|
| CPU 准备输入 | CPU 把每个候选项的包围体上传给 GPU，而不是上传已经填好的逐 tile 灯表。见 [HDRP `LightLoop.cs:1991`](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/Runtime/Lighting/LightLoop/LightLoop.cs#L1991)。 |
| GPU 建表 | Compute 先生成屏幕空间 AABB，可选做 big-tile 粗筛，然后分别构建 **FPTL 的逐 tile 表**和 **clustered 的分深度层列表**。这是共享候选数据后的两种输出，不是先建一张 CPU 表再交给 GPU 精修。见 [AABB 与 big-tile dispatch](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/Runtime/RenderPipeline/HDRenderPipeline.LightLoop.cs#L178)、[FPTL dispatch](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/Runtime/RenderPipeline/HDRenderPipeline.LightLoop.cs#L217)、[clustered dispatch](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/Runtime/RenderPipeline/HDRenderPipeline.LightLoop.cs#L261)。 |
| Forward 像素着色 | 不透明 Forward 可以读 FPTL；透明使用 clustered，按片元自己的深度选层，不能拿不透明表面深度精剪后的灯表代替。两种读法都封装成 `GetCountAndStart` / `FetchIndex`，材质拿到的是灯索引。见 [HDRP `LightLoopDef.hlsl:241`](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/Runtime/Lighting/LightLoop/LightLoopDef.hlsl#L241)。 |

### 对我们这版 URP，我建议怎么做

**把显式 Forward+ 接到现有 GPU builder，而不是再实现一套 HDRP FPTL。** 我们已经有[构建 zbin/tile 位图的 compute kernels](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/ClusteredDeferred/ClusterListGpuBuilder.cs#L110)，也已经验证过[前向透明 shader 能读这份结构化位图](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/ACCEPTANCE.md#L575)。缺的不是算法，而是两处接线：[GPU builder 目前显式排除了 Forward+](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/ForwardLights.cs#L458)，而 [Forward+ 目前绑定的是 CPU 填充的 constant buffer](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/ForwardLights.cs#L652)。**只去掉前一个判断是不够的。**

一个 session 建议分三步，顺序是**先留参照，再切生产者，最后删 jobs**：

1. **验证负责人先冻住旧 Forward+ 参照。** 保存相同夹具的输入、CPU 灯表快照和最终图像，覆盖零灯、多灯、反射探针及透明接收体。新旧位图不必逐字相同，但新表不能漏掉应覆盖的灯，最终画面也要对旧 Forward+ 验证。切换后两条路径共用 builder，不能只靠它们互比。
2. **运行时与 shader 各一人并行。** 运行时让 Forward+ 和 clustered deferred 都调用现有 GPU builder，首版用同步 dispatch；shader 让 `_FORWARD_PLUS` 读取同一份 structured zbin/tile，保留原有灯索引、探针顺序和 `_FPParams*`。这一步改变的是**缓冲绑定方式**，不是位图格式，也不需要增加 depth prepass。唯一需要明确的行为取舍是：目前 builder 只支持单视图；若本轮不补 XR，建议对不支持的视图或设备在剔除前明确回退普通 Forward，不偷偷保留 CPU 建表。
3. **双 API 验证后删运行时 CPU 链。** D3D11/D3D12 都确认显式 Forward+ 是 **compute 写表 → 首个 Forward draw 读表**，并与冻结参照对照，再删五个 jobs、等待点及 CPU 位图上传。**保留** `visibleLights`、探针排序、灯常量和阴影准备：这些是建表输入，不是 CPU build lightlist。

我的建议是把 C4 的完成标准定成一句话：**支持的 Forward+ 和 clustered deferred 都由 GPU 建表；普通 Forward 不建表；玩家运行时不存在 CPU tile/zbin 构建路径。** 不把 HDRP 式 FPTL 精剪或新的紧凑列表布局绑进这次交付。
```
计划：
已把本轮 HDRP 源码证据、URP 接入缺口和执行计划写入 [C4 HANDOFF](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/16-CleanupBaseline/HANDOFF.md)，并在[清理计划首页](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/16-CleanupBaseline/README.md#L8)加了入口。**C4 的交付目标是删除运行时 CPU 灯表构建，不是删除 CPU 可见灯准备**：Forward+ 和 clustered deferred 由 GPU 建表，普通 Forward 不建表；不移植 HDRP FPTL，也不进入 C4b。

| 执行顺序 | 分工 | 交付 |
|---|---|---|
| **1. 先冻旧参照** | C：验证 | 保存旧 Forward+ 的夹具输入、CPU 灯表快照和同路径图像，覆盖零灯、多灯、探针和透明物体。这样新 Forward+ 与 deferred 共用 builder 后，仍有独立参照能发现两边一起漏灯。 |
| **2. 并行接入** | A：运行时；B：Shader | A 让显式 Forward+ 也用现有 GPU builder，在首个读表 draw 前完成写表；B 让 `_FORWARD_PLUS` 的灯和探针循环读取 structured 位图，保留索引和 `_FPParams*` 语义。开工前只需定一项：builder 不可用或多视图尚不支持时，是否按建议回退普通 Forward。 |
| **3. 先验证，再删 jobs** | C 独占 Unity 验证；A 负责删除 | D3D11/D3D12 确认 **compute 写表 → Forward draw 读同一资源**，再对旧参照检查灯覆盖和图像。通过后，删除五个 CPU 建表 jobs、对应等待、位图上传和运行时 CPU 开关。 |
| **4. 在删除后的版本上验收** | C 回归；A/B 修复各自路径 | 重新编译，跑 EditMode、双 API PlayMode 和玩家回归，报告图像差异、变体及 CPU/GPU 耗时。T5 原有差异继续单列，不重开 C3，也不掩盖新差异。 |

## 2026-11-22 · 切换前的独立参照已冻结

运行时代码为 `5d4bcca5f333f503b51fa9514a919462abf49cbc`。PlayMode 夹具 `FreezeForwardPlusCpuReference` 在 640×360 的 Cd Camera 上显式渲染，断言 `_FORWARD_PLUS`、局部灯数与探针数，再导出同帧位图、候选项、FP 参数和 PNG。两组都含主方向灯和透明接收体：L0-P0 无局部灯/探针；L24-P1 有 24 个局部灯、1 个自定义反射探针。

[D3D11 结果](cpu-d3d11.xml) 与 [D3D12 结果](cpu-d3d12.xml) 各 1/1 Passed。参照在 [cpu-5d4bcca](cpu-5d4bcca/) 下按 API 分目录；后续候选只对相同 API 比较。复跑时设置 `C4_FORWARD_PLUS_OUTPUT` 为新目录，分别用 `-force-d3d11` / `-force-d3d12 -runTests -testPlatform PlayMode -testFilter FreezeForwardPlusCpuReference` 运行，不覆盖旧参照。

上述导出入口属于切换前的夹具；当前版本改用 `ForwardPlusGpuMatchesFrozenCpuReference` 只读这些参照。显式 Forward+ 已接同步 GPU 建表和 structured 位图读取；不支持设备、多视图、缺失/禁用 builder 或实验 RenderGraph 在剔除前切普通 Forward，并按 renderer/原因输出一次含相机名的 warning。

[候选 D3D11](gpu-d3d11.xml)、[候选 D3D12](gpu-d3d12.xml) 的 clustered PlayMode 套件各 16 通过、1 失败。冻结表覆盖及图像对照、禁用 builder 回退/恢复均通过；既有材质跨路径断言仍是 1674 像素超过 8/255（限值 230），与 C3 记录相同，不改判。该阶段未删 CPU jobs，T5 仍单列。

## 玩家写读证据与删除后验证

删除前双 API 均通过首个不透明 Lit draw 的同资源门槛：[D3D11](player/D3D11-binding.json) EID 75/89 写 zbin 8583 / tile 8586，541 的 PS 读；[D3D12](player/D3D12-binding.json) EID 157/162 写 794/795，668 barrier，694 的 PS 读。未把 depth-normal prepass 计作首个 Forward Lit draw。

随后移除五个运行时 CPU jobs、对应调度/等待、CPU 位图分配和上传及切换开关。普通 Forward 保留 per-object 索引。删除后的 [clustered EditMode](final-edit.xml) 19/19；[D3D11 PlayMode](final-d3d11.xml) 和 [D3D12 PlayMode](final-d3d12.xml) 各 16/17，唯一失败仍为上述 1674 像素材质断言。[最终玩家对照](final-comparison.json)已完成：与 C3 同 API 同路径比，T4 56/56，T5 28/32。仅 D3D11 L256 的 control/CD、control/Forward+、透明/CD、透明/Forward+ 超 max≤24（55、56、31、31），mean 均 ≤0.133。既有跨路径失败 44 项单列。两 API 的 Lit/ForwardLit/fp 均保持 6144，compute 声明组合仍为 1280；32 行耗时见报告。control L256 Forward+ 的 CPU/GPU 中位数差（C4−C3，ms）为 D3D11 +0.1630/+0.4756、D3D12 +0.0256/+0.3576，单次采样不能归因。批处理非零退出后的文件核查确认两 API 产物齐全（T4 真实文件名 T4FeatureMatrix.json）。原始逐帧判定保留，最终签收如下。

## C4a 最终验收：PASS（限定例外）

仅对 D3D11 L256 已知上限夹具的四张图，在原 peak >24 时允许 meanRGB ≤0.2/255（8-bit 数值 ≤0.2）、超过 24/255 的像素 ≤0.1%、最大通道差 ≤64/255；此条不适用于其他场景，不放宽灯表覆盖、fallback、跨 API 写读或 shader 变体要求。两种模式 control 分别 705/707 像素超过 24（共 921600），透明分别 27/30，四图 mean ≤0.133，peak ≤56。两条路径中同夹具的超差区域边界相同，不把局部相关差异称作独立随机噪声。

[同一最终玩家新增边界复测](cap-control/CdPerf.json)：创建 256 个局部灯，仅切换启用 255/256。255 灯时对 C3 cap-a 和 cap-b 的最大通道差都是 CD=3、Forward+=2；256 灯时对 cap-a 是 72/71，对 cap-b 是 2/2。与 [C3 同二进制跨进程上限波动](../validation/c3/T5D3D11L256RootCause.json)一致，未观测具体被剔除的灯 ID。原 T5 28/32、既有跨路径失败及 PlayMode 材质断言均不改写；T5 历史问题继续独立跟踪，不阻塞 C4a。未扩展到 C4b。