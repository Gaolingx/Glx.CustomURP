# C7 独立验收：FAIL，不签全绿

对象是 `6023d2941c91cf446a851286de847bf97be209f7`，文档观察点 `4049733`（仅五个 Markdown）。C0 源为 `pre-cleanup` / `f0c7ec4`。Windows、Unity 2022.3.62f1、RTX 5060 Ti；以下仅指 D3D11/12。没有打开主工程、更新金图或阈值、修改产品源码或提交。证据文件均在本目录。

| clean.md §7 | 原始判定与依据 |
|---|---|
| 1 死代码、Shader.Find | **PASS**。`source-audit.txt` 保存 Git 树、搜索式、完整 stdout 和退出码：URP Runtime、Editor、Shaders、ShaderLibrary 和项目 Assets 的旧 stencil 灯体积实现/关键字零命中（git grep 退出 1）。真正执行精确 shader 名查找：`removed=null positive=Universal Render Pipeline/Lit api=Direct3D11`。`authority.zip` 有探针源码与完整 Editor 日志；这是隔离 Editor 的实际查找，不是由文件不存在推断。材质分类 `StencilUsage.MaterialMask`、Lit/SimpleLit 写入和 mask shader 的 Ref/ReadMask/Comp 保留。旧 CPU jobs 与旧列表缓冲不同于仍被 GPU builder 与读取方消费的 `_FPParams*`；普通 Forward 的逐物体索引不属于死代码。 |
| 2 双 API 构建与变体 | **PASS（Development T5，非 release）**。复核 C5 两份完整 build 日志的 SHA-256 均匹配权威档；各 Succeeded、0 build error、0 shader error、0 missing-reference/script、0 旧关键字，723 条 shader warnings，不是零警告。C0→C5 两 API 各自 graphics shader/pass/stage 的 after-scriptable 行之和 **8716→8462（−254）**，完整解析 412/412→398/398 块；其中被删 shader 350→0。不是去重 GPU 程序数，也不混入 compute。Lit/ForwardLit(fp) 6144→6144，ClusteredDeferred compute 1280→1280；没有放宽预算。`authority.json` 与 `authority.zip`（逐行 TSV、C5 构建摘要、XML）。 |
| 3 全量测试 | **FAIL**。权威 C5 全量双 API 各 EditMode **489/489**、PlayMode **45/46**。各自唯一失败 `ClusteredMatchesForwardPlusAcrossMaterialTypes`：1674 像素 >8/255，限 230。相机剔除前回退、Forward+ 冻结 CPU 对照、GPU 调度、透明、ForwardOnly、资源释放均有通过的命名用例；不抵扣失败。四份完整 rerun XML 已随包。 |
| 4 冻结图像、GPU 结构 | **FAIL**。逐图及 SSL 见下。**结构 PASS**：此项没有拿 C4b 的 Forward+ 帧推断 deferred。C7 直接抓 C5 的双 API、透明、256 灯 clustered 玩家。GBuffer → CopyDepth → Clustered Deferred → Forward Only → Transparent → FinalBlit 的 EID 分别为 **98/563/583/644/647/698** 和 **140/1344/1355/1385/1387/1445**。无 stencil 灯体积事件。`authority.zip` 含两个实际 RDC、events/info、玩家报告和抓帧启动记录；CLI 未认出自动加帧号的文件名，随后按实际文件回放导出事件。单帧结构不等于全相机覆盖或每像素的灯 ID 鉴定。 |
| 5 性能不低于 C0 | **FAIL**。24 个独立玩家进程，双 API × 有无透明 × 两版本 × 3 轮，顺序 C0/C5、C5/C0、C0/C5；每格同路径、同负载 0/8/48/**256**，40 帧预热、150 帧采样。所有构建退出后才计时；约每 2 秒检查 Unity Editor、shader compiler、Bee、csc，24 次运行均无命中。该抽查非连续系统追踪。以三轮各自的帧中位数再取中位数，32 个匹配格中 CPU 有 10 格、GPU 有 22 格变慢；未设噪声豁免。最明显为 **D3D12 透明 Forward+ 256**：CPU 1.3230→2.2693 ms（+0.9463），GPU 1.2457→2.1742 ms（+0.9285），三轮都慢；同负载 clustered 的 CPU/GPU 则分别 −0.0040/−0.0102 ms。不能解释为所有路径都退化，也没有证明原因。`performance.csv` 列出了每轮值、Δ 和原始判定。 |
| 6 文档与工作树 | 五处口径 **PASS**：ACCEPTANCE 的 `c6-current` 与 HANDOFF、KnownDifferences、report、clean 的 superseded/回链；17/18 仍在 TODO。按直接阶段目录名，TODO/Done 交集为空。**字面 clean status FAIL**：开始时主树无改动，交付时只有 `?? URPSample01/PLAN/TODO/19-C7Final/`；不为制造 clean 状态而暂存或提交。见 `source-audit.txt`。 |

## 图像矩阵：同路径结果与缺口

| 对照 | D3D11 | D3D12 | 判读 |
|---|---:|---:|---|
| T4，冻结 C0 原图 vs 最终 clustered，7 功能 × 开关 | 13/14 | 13/14 | **FAIL**。两 API 唯一失败都是 `ReceiveShadowsOff_ClusteredOn`，peak 240，5432 px >8。原 C0 与最终夹具不同，这是字面冻结差异，不归因为渲染器回归。原 C0 T4 没有 Forward+ 半边，该路径相对原 C0 **未能验证**。 |
| T4，`5ab7626` 匹配材质/独立 Forward+ 的历史夹具 vs 最终，两路径 | 28/28 | 28/28 | **PASS**，最大差 1，无像素 >8。两组最终 T4 玩家均新建；矩阵自身跨路径 `passed` 则各为 0/7，不与同路径判定混用。 |
| T5，冻结 C0 原图 vs 最终，两路径 × 有无透明 × 4 灯数 | 12/16 | 14/16 | **FAIL**。正式三轮第一轮的 0/8/48 全过；D3D11 256 四图 peak 102/103（control）、73/73（透明）；D3D12 256 control 此次通过，透明两图 peak 30/30。首轮独立采集 D3D12 control 不过，不能挑绿帧签收。无同帧灯 ID 归因。 |
| SSL T12，重建 C0 vs 最终，同 API 同案例同路径 | 85/85 | 85/85 | **PASS**。各 API 两版本都执行 Forward 34、Forward+ 34、Deferred 35 个内部检查，各 103/103；每版本每 API 实出 85 张可匹配 PNG。两 API 各自整组 peak≤1、0 px >8，最大 mean 分别为 0.01926/0.01927。不是把内部 case 通过当作图像相等，也未把历史 D3D11 图套给 D3D12。 |

`matrix.csv` 给出每个文件的双 SHA-256、mean、peak、>8 像素数和比例。`raw.zip` 保存原始 T4/T12 JSON、逐进程 CdPerf JSON/CSV、BuildReport、运行命令、起止时间与编译进程抽查。同包 `image-manifest.json` 把逻辑路径映射到两份 `pixels-*.zip` 中的 `png/<SHA256>.png`；以哈希查图即可脱离工程复算。`authority.zip` 含原始 RDC 与权威测试证据。`run.py --paired` 为正式配对采集；`collect.py` 从采集目录复算表和未拆分的图像包。

## C7 后续：Forward+ 性能与屏幕边界（不改历史判定）

同机 D3D12、无透明、256 灯：C0→C4a 的原生 GPU 帧中位数 1.0826→1.7894 ms。六个回放帧的同一个内部像素实际循环 31→40 盏，新增九盏均已在 C0 上传且通过 z-bin，差别在 tile。隔离 C4a 保持 shader、structured buffer 和五次建表 dispatch，同进程等量上传宽/窄掩码，六对原生帧中位数节省 0.7462–0.7849 ms；不把回放事件时间相加当原生整帧。

不移植 FPTL 的固定容量分类索引；先在已有位图生成时，对点光范围球和聚光扇形包围球做 tile 侧面测试，不使用不透明深度。隔离 C4a 的 256 灯实验，D3D11/12 × 有无透明共 16 对节省 0.49–0.69 ms，peak≤3/255。0 灯无一致方向，8 灯约省 0.014 ms，48 灯省 0.070–0.080 ms。这些不是 `main` 的验收结果。

独立 CPU 几何样本发现旧粗筛把 `tileCount-1`（最后一个格子的起点）当成连续投影范围的终点。257×129、16 像素 tile 的最后一列覆盖 [256,257)，小光源可只照到其中一个像素。修正后，相同 24,514 个已点亮的采样点从漏掉 9,197 个变为 0；采样刻意偏向边缘，不代表线上发生率。下一闸门：在 D3D11/12 玩家用真实 GPU 灯表验证末列、末行与控制格，旧版必须红，候选必须绿；独立复测主线原生性能，不替代 C7 的图像、材质和全套门槛。

历史调查摘要表、回放分段、采样脚本与 CPU 反例：[tile-investigation.zip](tile-investigation.zip)。玩家回归命令：`CdArena.exe -force-d3d11 --cd-edge-tiles --cd-output <独立目录>`，D3D12 用 `-force-d3d12`。夹具使用正交 257×129 参数直接派发产品 bounds / expand 核并读回 tile 位；不是最终像素颜色测试。末列、末行、角点、内部各应仅置一个指定 tile，屏外应全零。失败写 `CdEdgeTiles.json` 并退出 2；通过退出 0。

<a id="main-1bcc731"></a>

### 主线 `1bcc731` 复验（父版 `61e3859`，冻结 C0 `f0c7ec4`）

- **死代码：PASS。** 对候选 Git 树复跑相同搜索，旧 stencil deferred 实现与关键字零命中；隔离 D3D11 Editor 实际返回 `removed=null positive=Universal Render Pipeline/Lit`。[搜索命令及退出码](main-source.json) · [探针与完整 Editor 日志](main-find.zip)。

- **共享粗筛 + 常规 expand：PASS。** D3D11、D3D12 原生玩家各有一组红绿对照。父版产品代码搭载相同测试入口，两端退出 2：末行、末列、角点都漏掉唯一应置位的 tile，内部正确命中、屏外全零。候选两端退出 0，五项均符合预期，没有额外置位。[四份原始 JSON、玩家日志、退出记录与源/玩家指纹](main-edge.zip)。
- **Forward+ 专用 expand：PASS（所测几何夹具，不是全场景无漏灯证明）。** 在候选玩家的独立副本中只替换测试程序集及符号，保留实际序列化 shader 和原生二进制；直接派发 `ClusterItemBounds` 与两种 expand，读回 bitmap。旋转相机下的点光/旋转聚光、近裁面相交、正交和五个边界/控制案例，双 API 各 11/11。每端 626,525 个物理受光采样给出 213 个案例/tile 正例，零漏位；Forward+ 位集都是常规 expand 的子集，共少置 284 位，两端逐案例掩码一致。不是通过“位越少越好”来判正确。[D3D11](forward-expand/Direct3D11/CdForwardExpand.json) · [D3D12](forward-expand/Direct3D12/CdForwardExpand.json) · [玩家文件哈希与命令](forward-expand/provenance.json)。
- **Development T5 构建与变体：PASS（现有命名预算）。** 双 API 各自 C0/父版/候选的 graphics after-scriptable 总数 8716/8462/8462，Lit/ForwardLit(fp) 均 6144，ClusteredDeferred compute 均 1280。ClusterBuilder 7/7/8；每个候选玩家确实多一个 `ClusterTileExpandForwardPlus` GPU 程序，15 项 compute 合计 1608/1608/1609。7 是 C0 实测而非显式硬上限；不把命中缓存的 8 个变体说成新编译了 8 个。父版和候选四次构建均成功、0 build error、0 shader error，各有 722 行 shader warning。[原始输入](main-build/inputs.zip) · [可复算判定](main-build/verdicts.json)。

#### 原生性能：父版改善，冻结 C0 门槛 FAIL

同机 RTX 5060 Ti，独立进程、三轮、双 API、透明开关、1280×720，40 帧预热 / 150 帧采样；每次运行均采 0/8/48/256 灯、Forward+ 与 clustered（C0 另有 stencil，不混入比较）。每格内顺序为 C0/父/候、候/父/C0、父/候/C0。36 次退出 0，共 115 次编译进程抽查无命中，不代表连续追踪。三轮进程帧中位数再取中位数，单位 ms；Δ=候选减参照，负为更快。以下只列 256 灯 GPU：

| API / 透明 | Forward+ C0 | 父版 | 候选 | Δ父 | ΔC0 | clustered Δ父 | clustered ΔC0 |
|---|---:|---:|---:|---:|---:|---:|---:|
| D3D11 / 关 | 1.0870 | 1.9228 | 1.2959 | −0.6269 | +0.2089 | +0.0010 | −0.0041 |
| D3D11 / 开 | 1.2050 | 2.0897 | 1.4062 | −0.6835 | +0.2012 | −0.0043 | −0.0028 |
| D3D12 / 关 | 1.0980 | 1.9049 | 1.2956 | −0.6093 | +0.1976 | −0.0013 | −0.0185 |
| D3D12 / 开 | 1.2401 | 2.1143 | 1.4390 | −0.6753 | +0.1989 | +0.0007 | −0.0126 |

Forward+ GPU 相对父版：0 灯 Δ −0.0046～+0.0013；8 灯节省 0.0123～0.0239；48 灯节省 0.0728～0.0914；256 灯节省 0.6093～0.6835，回收父版相对 C0 损失的 75.0%～77.3%；四格共 12 个单轮配对均比父版快、均比 C0 慢。256 灯 CPU 节省 0.5790～0.6926，仍比 C0 慢 0.1923～0.2762。在全部 32 格，候选相对父版有 11 格 CPU、11 格 GPU 更慢；相对 C0 有 11 格 CPU、16 格 GPU 更慢。不放宽阈值，不以 C4a 隔离收益抵扣。[完整逐格三轮表](main-performance.csv) · [原生报告、命令、环境抽查与图像索引](main-native.zip) · [去重原图](main-pixels.zip) · [采集脚本](main-perf.py)。

#### 图像（与性能独立）

原生计时玩家没有同帧灯 ID，不对这些 PNG 倒推漏灯或灯选择原因。与冻结原 C0 PNG 对照，首轮 24/32：0/8/48 灯全通过，256 灯八张全失败。三轮合计 0/8/48 灯 72/72 通过（peak≤1）；256 灯仅 2/24 通过。换成同轮重建 C0，256 灯 4/24 通过；换成父版，2/24。没有挑帧或改阈值。[逐图哈希、mean、peak、>8 面积](main-images.csv) · [图差复算](main-images.py)。历史固定帧和材质原图的独立复核另存 [历史帧复核包](historical-frame-audit.zip)，不把 C4a 或旧 C7 的灯 ID 当作 `1bcc731` 的帧回执。

**256 灯同帧诊断（新采，不是追认上一批 PNG）。** 在冻结 C0 与候选各自的玩家副本中，只替换测试程序集/符号。在截图的同一帧末回调内取 `_AdditionalLightsPosition`、`_AdditionalLightsColor`，以运行 UUID、`Time.frameCount` 和双 SHA-256 绑定 PNG、数组；这是 Unity 全局上传镜像，不是回放 GPU 描述符。20 份回执中，两条被比路径均解出 255 个唯一夹具 ID + 1 个空槽，C0 stencil 校验对照为 256 个唯一 ID。先比集合，再比图像：

| API / 透明 | C0 独有 → 候选独有 ID | 改变的槽位数 | 同路径 RGB8 peak：clustered / Forward+ |
|---|---|---:|---:|
| D3D11 / 关 | 183 → 247 | 193 | 93 / 93 |
| D3D11 / 开 | 119 → 247 | 205 | 78 / 78 |
| D3D12 / 关 | 183 → 240 | 184 | 72 / 71 |
| D3D12 / 开 | 48 → 112 | 187 | 31 / 31 |

八对跨版本失败图全都同时改变了一盏灯，没有一个跨版本等 ID 对；不能据此断言是 tile 漏灯、CPU 剔除原因、或灯替换解释了全部图差。同一版本各自的两条路径 ID 槽序完全相同，跨路径 peak 仍为 control 86 / 透明 80；那也不是下述 24 灯材质夹具。[逐槽 ID 与逐图指标](main-ids/results.json) · [同帧采集入口](main-ids/acquire.py)。

**全量测试 FAIL；材质独立追踪。** 两端的 `ForwardPlusGpuMatchesFrozenCpuReference` 均通过，和只派发 expand 的几何样本分开计。 独立工程从 `git archive 1bcc731` 取 Assets/Packages/ProjectSettings 和冻结 CPU 夹具，仅复制 Library 作缓存。双 API 各 EditMode **490/490**、PlayMode **45/46**；仅 `ClusteredMatchesForwardPlusAcrossMaterialTypes` 失败，各 1674 px >8/255，限 230。两端在失败断言前写出的 640×360 RGBA32F 原帧重算后，超阈掩码与历史 `5ab7626` 匹配夹具逐字节相同（SHA-256 `8601f3ffffb28d5b70924b842b04f9e360737f602a66547d6f29c81b3eab694b`）。未扩大容差、未更新金图、不归因给 256 灯选择差异。[四份完整 XML、日志、命令、浮点原帧与掩码摘要](main-tests.zip)。

**玩家 GPU 结构 PASS（候选双 API，256 灯、透明、clustered）。** 使用未替换测试程序集的原始玩家。各帧均有五次建表 dispatch、134 次 GBuffer draw、CopyDepth、clustered lighting、resolve、Forward Only 空 pass、三次透明 draw 和 FinalBlit；无旧 stencil 灯体积绘制。D3D11/D3D12 的 lighting dispatch EID 为 628/1298（80×45 组，各 16×16）；resolve 读取该 dispatch 的输出，透明像素阶段读取同一建表缓冲。实际绑定的 lighting CS 常量缓冲每帧解出 255 个唯一灯 ID 加一个空槽，分别缺 240/48；这些 RDC 并非上述原生诊断的同一帧，不用来追认原生截图的灯序。[事件与资源绑定](main-structure/proof.txt) · [两份 RDC 和回放导出](main-structure/candidate-structure.zip)。

**SSL T12 PASS。** `1bcc731` 在 D3D11/12 各通过 103/103 个内部检查；另与哈希核过的冻结 C0 同路径图像逐张比较，两端各 85/85，RGB8 peak≤1、零像素 >8。**T4 对冻结 C0：FAIL。** 双 API 均实际构建成功（0 errors），原 C0 的 clustered 同路径图各 13/14；两端唯一失败均为 `ReceiveShadowsOff_ClusteredOn`，peak 240/255，5432 px >8。原 C0 没有 Forward+ 图。另与 `5ab7626` 匹配夹具比，各 API 两条路径合计 28/28，peak≤1、零像素 >8。这排除了所测匹配夹具的同路径新图差，但不能令冻结比较过关。两端玩家自身的跨路径功能矩阵各 0/7，均退出 2；不与上述同路径计数相加。[两端构建、玩家与逐项判定](main-extra/results.json) · [逐图指标](main-extra/images.csv) · [原始日志与报告](main-extra/raw.zip)。

**本轮交付范围。** 仅增加玩家回归、采集/复算和报告，没有修改产品核、FPTL 索引、金图或阈值。`clean.md` §7.6 的 TODO/Done 直接阶段目录无重名；验收采集结束时归档尚未提交；随后按要求提交回归与证据。工作树状态不是渲染门槛，不改变 C7 的 FAIL 判定。旧 `SHA256SUMS.txt` 固定于 `e52985e` 的原始交付（其中 `report.md` 对应历史版本）。

## 证据口径

- 性能首轮发现默认旧 `Builds/ClusteredDeferredArena` 指向的不是 C0：输出只有 ClusteredDeferred 和 ForwardPlus，而冻结原始报告有第三个 StencilDeferred。**整轮不计性能**。重新从 `git archive f0c7ec4` 建独立工程，用历史 Library 的物理副本作为编译缓存而不复用其产品源码/配置，重建双 API；记录中 `C0-*` 是被排除的首轮，`frozen-*` 才是重建冻结代码的结果。重建玩家不宣称和已覆盖的原 EXE 逐字节相同。`provenance.json` 比对关键文件与 Git blob。
- 图差使用 1280×720 的 RGB8：所有通道绝对差的平均值、任一通道绝对差的最大值、最大通道差 >8 的像素数/比例。阈值 mean≤2、peak≤24。对完全一致的冻结图自比，>8 比例为 0；非零单列，不因 mean/peak 合格就说比例没有上升。`compare.py` 可对两个同名图片目录复算。T4 原冻结无 Forward+ 状态，不拿旧 Stencil 当 Forward+。不设 cap 例外，不以 255 代 256。
- `frozen-images.csv` 额外记录首次 C5 T5 捕获 vs 原 C0 PNG（24/32，八个 L256 全失败），及历史 08 D3D11 SSL vs C5 的 85 张（仅 37 张通过 mean/peak）。这是历史交叉检查，不能拿 08 图替代同 API、同版本 C0，也不能据 T12 的内部 passed 批准金图。
