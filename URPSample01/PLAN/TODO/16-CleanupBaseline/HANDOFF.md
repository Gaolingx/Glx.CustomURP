# C4 接手：退出运行时 CPU 灯表构建

2026-11-22。起点：渲染检查点 `646f75a`，C3 证据 `6575585`；[C3 验收决定](DECISIONS.md)为 PASS，T4 CLOSED、T5 OPEN，不重开 C3。本轮完成的是源码调研和交付规划，尚未修改 C4 运行时代码、构建玩家或取得 C4 抓帧。

## 目标与边界

支持的显式 Forward+ 与 clustered deferred 都用 GPU 生成 zbin/tile 位图；普通 Forward 不建表；玩家运行时没有 CPU 建表、等待或位图上传。保持位图字格式、`_FPParams0/1/2`、灯和探针索引语义；允许把显式 Forward+ 的表绑定从 constant buffer 改为 structured buffer。保留 CPU 可见灯、探针排序、灯常量、阴影及普通 Forward 的 per-object 索引。不引入 HDRP FPTL、depth prepass、C4b 紧凑表或 GPU 灯注册表。

## 本轮证据：源码观察，不是 C4 运行结果

HDRP 取自本机 `E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/`。以下路径相对此包：

| 观察 | 可复核位置 |
|---|---|
| CPU 上传候选项的包围体；GPU 生成共用的屏幕 AABB，可选先做 big-tile 粗筛，再派发逐 tile 和逐 voxel 的列表构建。 | `Runtime/Lighting/LightLoop/LightLoop.cs:1991`；`Runtime/RenderPipeline/HDRenderPipeline.LightLoop.cs:178–289` |
| Forward 只有在 opaque 且启用 FPTL 时才选 FPTL，否则选 clustered；透明不拿不透明深度精剪表代用。像素端两种表示都经 `GetCountAndStart` / `FetchIndex` 迭代灯索引。 | `Runtime/RenderPipeline/HDRenderPipeline.RenderGraph.cs:835–839`；`Runtime/Lighting/LightLoop/LightLoopDef.hlsl:241–315` |

本 fork 的差距不是缺少 GPU 算法，而是显式 Forward+ 尚未接入：

- [ForwardLights.cs](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/ForwardLights.cs)，187–201、458–465、652–666 行：Forward+ 分配 constant buffer，GPU 条件排除 Forward+，CPU `SetData` 后绑定。只删判定不改着色器绑定不成立。
- [ClusterListGpuBuilder.cs](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/ClusteredDeferred/ClusterListGpuBuilder.cs)，104–116 行：已有 bounds、zbin clear/scatter/pack、tile expand kernels。[既有 GPU 证据](../../ACCEPTANCE.md) §19 G15-8 已验证 clustered 模式下透明 PS 读 structured 位图，**未验证**显式 `_FORWARD_PLUS` 模式。

## 单 session 分工与执行顺序

| 顺序 / 负责人 | 工作与交付 | 何时可以合流 |
|---|---|---|
| 1 · 验证负责人 C | 切换前从旧 Forward+ 保存确定性夹具输入、CPU 表快照及同路径帧，标注提交/API/相机。覆盖零灯、局部多灯、反射探针、透明接收体。旧版证据可复用，但不能拿两条都用新 builder 的路径互比充当独立参照。 | 基线可离线读取后才切生产者。 |
| 2A · 运行时 A | 接通两种模式的 builder、缓冲创建与同步 dispatch，确保首个读表 draw 前已写表。建议 builder 不可用或未支持的多视图在剔除前回退普通 Forward；这会改变显式 Forward+ 的行为，先确认支持范围。 | 与 B 先约定绑定名、字步长、索引和关键字；A 独占运行时接线。 |
| 2B · Shader B，与 A 并行 | 让显式 `_FORWARD_PLUS` 的灯和反射探针循环读 structured 位图；保留方向光前缀、探针偏移、透明深度索引和普通 Forward 变体。 | A/B 合流后，先跑路由、零灯和探针定向测试，再构建玩家。 |
| 3 · C 验证，A 清理 | C 独占 Unity 工程，串行构建，核对 D3D11/D3D12：显式 Forward+ 的 compute 写表 → 首个 Forward draw 读同一资源，旧 CPU 覆盖不丢，图像按原阈值对照。通过后 A 删除五个建表 jobs、对应等待、CPU 位图数组/上传和运行时 CPU 开关；仍需要的相机参数准备不能整段删除。 | 删除后重新编译、跑 EditMode/双 API PlayMode 及最终双 API 玩家回归；记录 T4/T5、变体差值与 CPU/GPU 耗时，不以抓到一次 dispatch 代替回归。 |

**交付**：运行时和 shader 修改、独立基线及测试、双 API 最终证据、删除符号与调用点检查。若老参照缺失、回退范围未定或出现新漏灯，停在相应步骤并记录阻塞，不宣称全局删除完成。既有 T5 D3D11 256 灯差异单列，不放宽阈值，也不当作新路径的免责项。
