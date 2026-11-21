前情提要：用户调研了cluster deferred和stencil deferred 这两 light culling 范式的实现差异、适用场景，并调研了 HDRP 做法

调研结果：

Clustered Deferred（簇状延迟渲染）和 Stencil Deferred（模板/光体积延迟渲染）本质上是延迟渲染为了解决「多光源大规模重叠」而演进出的两代空间裁剪范式；而 Unity HDRP 的实际做法比纯 Clustered 还要精妙——它用的是【FPTL（精细剪枝 Tiled）+ Clustered】的双轨混合架构！

我们分三小段给你拆解（Part 1：Stencil Deferred 与 Clustered Deferred 的底层差异）：

━━━━━━━━━━━━━━
【1. Stencil Deferred（经典光体积 + 模板测试延迟渲染）】
这是传统延迟渲染（如 Unity Built-in 管线、早期 UE3 / CryEngine）的标准做法。
・核心思路：【以光源为中心（Light-Centric），一盏灯画一个 3D 几何体（Light Volume）】。点光源画 3D 球体 Mesh，聚光灯画 3D 圆锥 Mesh。
・为什么要用 Stencil（模板缓冲）？
如果只画光体积球体正面，当相机钻进光球内部时正面就被裁剪没了；如果只画背面并开深度大于等于测试，停在半空中的光球又会把光球前方空无一物的背景几何体误照亮！
所以经典 Stencil Deferred 每盏灯要走 2 个子 Pass：
① 先不写颜色，用光球前后表面与 G-Buffer 深度比较，在 Stencil Buffer 里精准圈出「真正处于这个 3D 光球内部」的像素；
② 再开启 Stencil 测试画一次光球，对通过测试的像素采样 G-Buffer 算光照，用加法混合（Additive Blending）累加到颜色缓冲里。

・三大硬伤：
1) DrawCall 与状态切换随光源数 O(N) 线性爆炸；
2) 多光源重叠时严重 Overdraw 与带宽爆炸：50 盏点光源叠在同一个走廊，同一个像素的 G-Buffer 要被重复读 50 次，输出颜色缓冲要被 Additive Blend 重复读写 50 次，直接把 ROP 和显存带宽打爆！
3) 光体积边缘小三角形浪费 2×2 Pixel Quad，且算出的光源无法给半透明物体和体积雾复用。

━━━━━━━━━━━━━━
【2. Clustered Deferred（簇状延迟渲染，Olsson et al. 2012）】
为了干掉逐光源画 Mesh 的带宽灾难，现代管线转向了【以屏幕空间为中心，先分类光源、再单次全屏着色】。
・它和 2D Tiled Deferred 的区别：
2D Tiled 把屏幕切成 16×16 像素的格子，用格子里的 [Z_min, Z_max] 框出一个长条盒子筛光源。但一旦同一个 16×16 格子里既有近处树叶（Z=1m）、又有远处背景墙（Z=500m），盒子深度就会拉到 1m～500m，把中间半空中根本照不到表面的几百盏废灯全装进来（即深度断层伪影 Depth Discontinuity）！
・Clustered 的解法：【在 2D 分块基础上，再沿相机深度 Z 轴（按指数/对数分布）切成 3D 体素网格（Clusters / Froxels）】！
① 第一步（Compute Light Culling）：GPU 并行算出每个 3D Cluster 和哪些光源相交，生成全局 3D 光源索引表；
② 第二步（Single-Pass Lighting）：全屏只跑 1 次着色 Pass！每个像素根据屏幕坐标 (x, y) 和深度 z 直接 O(1) 定位自己所属的 3D Cluster，在寄存器里用一个 for 循环把该 Cluster 里的所有灯一次性算完，最后只写 1 次输出缓冲！
・三大优势：彻底消灭多光源重叠的 ROP 重复读写；完美免疫深度断层（前景树叶在浅层 Cluster，背景墙在深层 Cluster）；3D 光源表独立于 G-Buffer，半透明物体（Forward+）和体积雾可以直接复用！

---
（Part 2：Unity HDRP 到底是怎么做的？—— FPTL + Clustered 双剑合璧架构）

很多人以为 HDRP 是纯 Clustered Deferred，其实翻开 HDRP 的着色器源码（LightLoop / builddispatchindirect.compute / deferred.compute）就会看到，HDRP 默认在同一帧同时运行了两套裁剪器：【不透明延迟光照用 FPTL（精细剪枝 2D Tiled），透明物体与体积雾用 3D Clustered】！

为什么 HDRP 不对所有物体统一用 Clustered，而要在不透明物体上优先用 FPTL（Fine Pruned Tiled Lighting）？有三个超硬核的工程原因：

━━━━━━━━━━━━━━
【1. 在 G-Buffer 延迟光照上，FPTL 比 Clustered 筛得更准、更快！】
・Clustered 的 3D 体素盒子是为了通用性预先在视锥体里切好的（为了省显存，Cluster 的 XY 粒度通常比较粗，比如 32×32 或 64×64 像素，且不管盒子里到底有没有真实几何体表面）。
・而对不透明延迟光照来说，我们手里已经有精确到每一个像素的 G-Buffer 深度了！
・HDRP 的 FPTL 先按 16×16 像素划分 2D Tile，不仅用 [Z_min, Z_max] 粗筛光源，还会做一步【Fine Pruning（精细逐像素深度剪枝）】：在 Compute Shader 的共享内存里，直接拿候选光源的 3D 包围盒跟该 Tile 内 256 个像素的真实深度做位掩码碰撞测试！
・如果一盏灯处于近处树叶（1m）和远处背景墙（500m）之间的半空中（200m），Fine Pruning 发现这 256 个像素里没有任何一个像素深度落在 200m 附近，当场就把这盏废灯从该 Tile 的列表里踢出去！
・结果就是：在延迟 G-Buffer 阶段，FPTL 筛出来的每个 16×16 Tile 的光源列表比 3D Cluster 还要精准紧凑，真正做到了“零废灯误算”！

━━━━━━━━━━━━━━
【2. HDRP 的隐藏杀手锏：20+ 种 Tile Variants 分类间接派发（Indirect Dispatch）】
在筛完每个 16×16 Tile 的光源后，HDRP 不会直接用一个巨型 Uber-Shader 去算全屏光照，而是根据每个 Tile 里实际包含的【光源类型掩码 + 材质 Feature Flag】（比如：A 块只有简单点光源+标准 Lit 材质；B 块包含矩形面光源+反射探针+次表面散射 SSS），把全屏所有的 16×16 Tile 自动分拣进二十几个专门编译的特化 Compute Shader 桶（Variants）里，用 DispatchIndirect 分类执行！
这样简单区域跑极简 Shader（高占用率、低寄存器压力、零分支发散），复杂区域才跑重型 Shader！

━━━━━━━━━━━━━━
【3. 那 HDRP 里的 Clustered 用来干嘛？】
在同一帧的光源裁剪阶段，HDRP 会同步构建一份按指数深度切片的【3D Clustered 光源表】，专门喂给：
・所有没有单一 G-Buffer 深度、无法使用 FPTL 的【半透明物体（Forward Transparent）】；
・【体积雾与体积光（Froxel Volumetric Lighting）】；
・以及当某个像素位置的光源数量超出了 FPTL 单 Tile 上限时的兜底！

---
（Part 3：三大方案的适用场景终极选型指南）

━━━━━━━━━━━━━━
【1. Stencil Deferred（光体积模板延迟）适合什么场景？】
・中低端移动端，或不支持高效 Compute Shader / UAV 随机读写的旧图形 API；
・场景里动态点光源数量很少（比如同屏只有几盏到十几盏互不重叠的局部小灯，此时无需承担全屏 Compute Culling 的固定启动开销）；
・或者极其空旷的场景里只有零星几盏占屏幕面积很小（几百像素）的局部灯，直接光栅化几个小球比跑全屏分块裁剪更省。

━━━━━━━━━━━━━━
【2. 纯 Clustered（Clustered Deferred 或 Clustered Forward+）适合什么场景？】
・同屏有成百上千盏动态光源、且大量光源在空间上重叠的场景（赛博朋克霓虹街区、满地技能光效的刷宝游戏、室内密集灯光）；
・场景里既有大量不透明物体，又有【大量半透明粒子/玻璃 + 体积雾】需要统一受几百盏动态灯照亮（直接一套 3D Cluster 表通吃 Opaque、Transparent 和 Volumetrics，管线代码最干净统一）；
・需要硬件 MSAA 抗锯齿或材质着色模型高度异构、不想做胖 G-Buffer 的项目（直接上 Clustered Forward+，连 G-Buffer 带宽都省了，比如《毁灭战士 2016 / 永恒》的管线）。

━━━━━━━━━━━━━━
【3. FPTL + Clustered 混合架构（Unity HDRP 模式）适合什么场景？】
・面向现代 PC / 主机（DX12 / Vulkan / Metal）的重度写实 3A 管线：不透明几何体占绝对大头、材质极度复杂（SSS、清漆、各向异性、毛发、布料）且光源种类繁多（矩形/线形面光源 Area Light、盒状/球状反射探针、Decal 贴花）。
・利用 FPTL 的逐像素深度剪枝 + Tile Variant 间接派发，把不透明 G-Buffer 延迟光照的 ALU 与寄存器效率榨到极致，同时用 Clustered 完美解决半透明与体积雾的多光源照明！

---
我们的需求：希望将 Unity HDRP 写的那套 Hybrid Tile/Cluster Culling（FPTL + Clustered + Feature Variant Indirect Dispatch）迁移到URP14，但是保留URP原有的光照模型，而且支持async compute build lightlist，你研究下如何落地