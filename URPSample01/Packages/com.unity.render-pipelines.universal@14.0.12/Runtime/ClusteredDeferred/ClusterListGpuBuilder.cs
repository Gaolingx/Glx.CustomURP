using Unity.Mathematics;
using UnityEngine;

namespace UnityEngine.Rendering.Universal.Internal
{
    internal struct ClusterListGpuParams
    {
        public ClusterItemRecord[] items;
        public int itemCount;
        public int lightCount;
        public int binCount;
        public int wordsPerTile;
        public float zBinScale;
        public float zBinOffset;
        public int zBinWordCount;
        public int tileWordCount;
        public int2 tileCount;
        public int tileWidth;
        public int2 screenResolution;
        public float2 tileScale;
        public Vector4 viewToViewportScaleBias;
        public Matrix4x4 worldToView;
        public float near;
        public bool orthographic;
    }

    // G1 Stage A/B dispatch host. Owns the per-item intermediate buffers and runs the five kernels
    // that fill the two bitmaps ForwardLights publishes; the buffers themselves stay with
    // ForwardLights so the CPU fallback can keep writing them.
    internal sealed class ClusterListGpuBuilder
    {
        // Fallback switch (HANDOFF §7): flipping this to false puts the CPU jobs back in charge
        // without touching shaders. Tests flip it to capture the CPU reference of the same frame.
        internal static bool enabled = true;

        // G1c scheduling switch: when the device has an async queue the five dispatches can run
        // there and overlap the shadow/GBuffer passes. Off by default: on the D3D12 validation
        // machine the queue hand-off costs a constant ~0.11 ms per frame (measured at 0/8/16/32/
        // 48/64 lights in validation/G1/T5-runtime/player-d3d12{,-async}) while the whole list
        // takes ~0.05 ms, so sync wins until a scene is heavy enough to hide the hand-off.
        internal static bool asyncCompute = false;

        // G2 depth refine: after GBuffer, the tile bitmap is narrowed to the volumes that actually
        // intersect each tile's depth slice. Off by default: the measurement (HANDOFF §4.2) shows
        // it clears ~65% of the conservative tile bits but leaves per-pixel work unchanged --
        // the pixel's zbin already rejects those lights -- so it only adds a dispatch. Kept as an
        // A/B path; a real per-pixel win needs the compact per-(tile, zbin) list of G3.
        internal static bool pruneByDepth = false;
        // Isolation switch: depth-only keeps bits whose z range overlaps but whose volume misses
        // the tile slice. Used by the A/B harness to separate the two rejection reasons.
        internal static bool pruneSidePlanes = true;
        // Diagnostic readback of the kept/cleared bit counts; off in normal runs.
        internal static bool pruneStatsEnabled = false;
        internal static int lastPruneKeptBits;
        internal static int lastPruneClearedBits;

        // G3: per-(tile, zbin) compact lists. After GBuffer, one dispatch turns the tile/zbin
        // bitmap intersection inside each tile's depth slice into ascending item lists; the kernel
        // reads one header instead of walking bitmap words. Off by default: HANDOFF §5.3 measured
        // +0.08-0.13 ms at every light count -- the build needs a full-screen depth scan, and at
        // 1-2 words per tile the bitmap scan is already shorter than the cell lists. Kept as an
        // A/B path with the bitmap as reference and as the fallback for cells over budget.
        internal static bool compactLists = false;
        // Cell capacity per (tile, zbin). Published as `_CdClusterCellMax` to both kernels, so the
        // buffer stride and the HLSL loops cannot drift.
        internal static int compactCellMax = 32;
        internal static bool compactStatsEnabled = false;
        internal static int lastCompactEntries;
        internal static int lastCompactCells;
        internal static int lastCompactOverflowCells;
        internal static int lastCompactOverflowTiles;
        internal static int lastCompactScannedCells;

        const int k_BoundsThreads = 64;
        const int k_ClearThreads = 256;
        const int k_ScatterThreads = 64;
        const int k_ExpandThreads = 8;
        static readonly uint[] k_ZeroTotals = { 0u, 0u };
        static readonly uint[] k_ZeroCellTotals = { 0u, 0u, 0u, 0u, 0u };

        readonly ComputeShader m_Compute;
        readonly int m_BoundsKernel;
        readonly int m_ClearKernel;
        readonly int m_ScatterKernel;
        readonly int m_PackKernel;
        readonly int m_ExpandKernel;
        readonly int m_PruneKernel;
        readonly int m_CellKernel;

        GraphicsBuffer m_Items;
        GraphicsBuffer m_MinMaxZ;
        GraphicsBuffer m_TileRanges;
        GraphicsBuffer m_BinMinMax;
        GraphicsBuffer m_PruneTotals;
        GraphicsBuffer m_CellHeaders;
        GraphicsBuffer m_CellItems;
        GraphicsBuffer m_TileCellInfo;
        GraphicsBuffer m_CellTotals;
        int m_ItemCapacity;
        int m_BinCapacity;
        int m_CellCapacity;
        int m_CellMax;

        public ClusterListGpuBuilder(ComputeShader compute)
        {
            m_Compute = compute;
            if (compute == null)
                return;

            m_BoundsKernel = compute.FindKernel("ClusterItemBounds");
            m_ClearKernel = compute.FindKernel("ClusterZBinClear");
            m_ScatterKernel = compute.FindKernel("ClusterZBinScatter");
            m_PackKernel = compute.FindKernel("ClusterZBinPack");
            m_ExpandKernel = compute.FindKernel("ClusterTileExpand");
            m_PruneKernel = compute.FindKernel("ClusterTilePrune");
            m_CellKernel = compute.FindKernel("ClusterCellBuild");
        }

        public bool isCreated => m_Compute != null &&
                                 m_BoundsKernel >= 0 && m_ClearKernel >= 0 && m_ScatterKernel >= 0 &&
                                 m_PackKernel >= 0 && m_ExpandKernel >= 0 &&
                                 m_PruneKernel >= 0 && m_CellKernel >= 0;

        // Records the item upload on the command stream. Kept separate from Dispatch because the
        // async path uploads on the graphics queue (so the data is ready when the async queue
        // starts) and then dispatches on its own command buffer.
        public void Upload(CommandBuffer cmd, ClusterItemRecord[] items, int itemCount)
        {
            if (!isCreated || items == null || itemCount <= 0)
                return;

            EnsureCapacity(itemCount, 0);
            cmd.SetBufferData(m_Items, items, 0, 0, itemCount);
        }

        public void Dispatch(CommandBuffer cmd, in ClusterListGpuParams p, GraphicsBuffer zbins, GraphicsBuffer tiles)
        {
            if (!isCreated || p.items == null)
                return;

            EnsureCapacity(p.itemCount, p.binCount);

            // The kernels read the two published parameters directly instead of the global
            // _FPParams state, so an async command buffer does not depend on graphics-queue
            // commands having executed first.
            cmd.SetComputeVectorParam(m_Compute, "_FPParams0", new Vector4(p.zBinScale, p.zBinOffset, 0f, 0f));
            cmd.SetComputeVectorParam(m_Compute, "_FPParams1", new Vector4(0f, 0f, 0f, p.wordsPerTile));

            cmd.SetComputeIntParam(m_Compute, "_CdClusterItemCount", p.itemCount);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterLightCount", p.lightCount);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterBinCount", p.binCount);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterZBinWordCount", p.zBinWordCount);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterTileWordCount", p.tileWordCount);
            cmd.SetComputeIntParams(m_Compute, "_CdClusterTileCount", p.tileCount.x, p.tileCount.y);
            cmd.SetComputeVectorParam(m_Compute, "_CdClusterTileScale", new Vector4(p.tileScale.x, p.tileScale.y, 0f, 0f));
            cmd.SetComputeVectorParam(m_Compute, "_CdClusterViewToViewportScaleBias", p.viewToViewportScaleBias);
            cmd.SetComputeMatrixParam(m_Compute, "_CdClusterWorldToView", p.worldToView);
            cmd.SetComputeFloatParam(m_Compute, "_CdClusterNear", p.near);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterOrthographic", p.orthographic ? 1 : 0);

            cmd.SetComputeBufferParam(m_Compute, m_BoundsKernel, "_CdClusterItems", m_Items);
            cmd.SetComputeBufferParam(m_Compute, m_BoundsKernel, "_CdClusterMinMaxZ", m_MinMaxZ);
            cmd.SetComputeBufferParam(m_Compute, m_BoundsKernel, "_CdClusterTileRanges", m_TileRanges);

            cmd.SetComputeBufferParam(m_Compute, m_ClearKernel, "URP_ZBins", zbins);
            cmd.SetComputeBufferParam(m_Compute, m_ClearKernel, "urp_Tiles", tiles);
            cmd.SetComputeBufferParam(m_Compute, m_ClearKernel, "_CdClusterBinMinMax", m_BinMinMax);

            cmd.SetComputeBufferParam(m_Compute, m_ScatterKernel, "URP_ZBins", zbins);
            cmd.SetComputeBufferParam(m_Compute, m_ScatterKernel, "_CdClusterMinMaxZ", m_MinMaxZ);
            cmd.SetComputeBufferParam(m_Compute, m_ScatterKernel, "_CdClusterBinMinMax", m_BinMinMax);

            cmd.SetComputeBufferParam(m_Compute, m_PackKernel, "URP_ZBins", zbins);
            cmd.SetComputeBufferParam(m_Compute, m_PackKernel, "_CdClusterBinMinMax", m_BinMinMax);

            cmd.SetComputeBufferParam(m_Compute, m_ExpandKernel, "urp_Tiles", tiles);
            cmd.SetComputeBufferParam(m_Compute, m_ExpandKernel, "_CdClusterTileRanges", m_TileRanges);

            cmd.DispatchCompute(m_Compute, m_BoundsKernel, Groups(p.itemCount, k_BoundsThreads), 1, 1);
            var clearCount = math.max(math.max(p.zBinWordCount, p.tileWordCount), p.binCount * 4);
            cmd.DispatchCompute(m_Compute, m_ClearKernel, Groups(clearCount, k_ClearThreads), 1, 1);
            cmd.DispatchCompute(m_Compute, m_ScatterKernel, Groups(p.binCount, k_ScatterThreads), math.max(1, p.itemCount), 1);
            cmd.DispatchCompute(m_Compute, m_PackKernel, Groups(p.binCount, k_BoundsThreads), 1, 1);
            cmd.DispatchCompute(m_Compute, m_ExpandKernel,
                Groups(p.tileCount.x, k_ExpandThreads), Groups(p.tileCount.y, k_ExpandThreads), 1);
        }

        // G2: runs on the graphics queue after the GBuffer depth is available and after the GPU
        // build fence, before the lighting dispatch. It narrows the published tile bitmap in place;
        // the zbin bitmap and every layout/param the kernel reads stay untouched.
        public bool Prune(CommandBuffer cmd, in ClusterListGpuParams p, RenderTargetIdentifier depthTexture,
            Vector4 zBufferParams, GraphicsBuffer tiles)
        {
            if (!isCreated || !pruneByDepth || p.items == null)
                return false;

            EnsureCapacity(p.itemCount, p.binCount);

            cmd.SetComputeIntParam(m_Compute, "_CdClusterItemCount", p.itemCount);
            cmd.SetComputeIntParams(m_Compute, "_CdClusterTileCount", p.tileCount.x, p.tileCount.y);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterTileWidth", p.tileWidth);
            cmd.SetComputeVectorParam(m_Compute, "_CdClusterScreenSize",
                new Vector4(p.screenResolution.x, p.screenResolution.y,
                    1f / math.max(1, p.screenResolution.x), 1f / math.max(1, p.screenResolution.y)));
            cmd.SetComputeVectorParam(m_Compute, "_CdClusterTileScale", new Vector4(p.tileScale.x, p.tileScale.y, 0f, 0f));
            cmd.SetComputeVectorParam(m_Compute, "_CdClusterViewToViewportScaleBias", p.viewToViewportScaleBias);
            cmd.SetComputeMatrixParam(m_Compute, "_CdClusterWorldToView", p.worldToView);
            cmd.SetComputeVectorParam(m_Compute, "_CdClusterZBufferParams", zBufferParams);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterOrthographic", p.orthographic ? 1 : 0);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterPruneSidePlanes", pruneSidePlanes ? 1 : 0);
            cmd.SetComputeVectorParam(m_Compute, "_FPParams1", new Vector4(0f, 0f, 0f, p.wordsPerTile));

            cmd.SetComputeTextureParam(m_Compute, m_PruneKernel, "_CdClusterDepthTexture", depthTexture);

            cmd.SetComputeBufferParam(m_Compute, m_PruneKernel, "_CdClusterItems", m_Items);
            cmd.SetComputeBufferParam(m_Compute, m_PruneKernel, "_CdClusterMinMaxZ", m_MinMaxZ);
            cmd.SetComputeBufferParam(m_Compute, m_PruneKernel, "_CdClusterPruneTotals", m_PruneTotals);
            cmd.SetComputeBufferParam(m_Compute, m_PruneKernel, "urp_Tiles", tiles);

            cmd.SetBufferData(m_PruneTotals, k_ZeroTotals);
            cmd.DispatchCompute(m_Compute, m_PruneKernel, p.tileCount.x, p.tileCount.y, 1);

            if (pruneStatsEnabled)
                cmd.RequestAsyncReadback(m_PruneTotals, OnPruneStatsReadback);

            return true;
        }

        static void OnPruneStatsReadback(AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
                return;

            var totals = request.GetData<uint>();
            lastPruneKeptBits = (int)totals[0];
            lastPruneClearedBits = (int)totals[1];
        }

        // G3: publishes the cells the lighting kernel reads. Only called while the feature is on and
        // the item stream exists; the disabled path binds the host's placeholder instead, so the
        // default renderer does not pay for ~15 MB of cell buffers.
        public void BindCompact(CommandBuffer cmd, in ClusterListGpuParams p)
        {
            if (!isCreated || p.items == null)
                return;

            EnsureCellCapacity(p.tileCount.x * p.tileCount.y);
            cmd.SetGlobalBuffer("_CdCellHeaders", m_CellHeaders);
            cmd.SetGlobalBuffer("_CdCellItems", m_CellItems);
            cmd.SetGlobalBuffer("_CdTileCellInfo", m_TileCellInfo);
            cmd.SetGlobalInt("_CdClusterCellMax", compactCellMax);
        }

        // G3: runs on the graphics queue after the GBuffer depth copy and after the bitmap build
        // fence, before the lighting dispatch. One 8x8 group per tile measures the tile's depth
        // slice, maps it to a zbin range and fills one cell per (tile, zbin) with the item indices
        // present in both bitmaps. Tiles/cells over the capacity keep the bitmap path.
        public bool BuildCells(CommandBuffer cmd, in ClusterListGpuParams p, RenderTargetIdentifier depthTexture,
            Vector4 zBufferParams, GraphicsBuffer zbins, GraphicsBuffer tiles)
        {
            if (!isCreated || !compactLists || p.items == null)
                return false;

            EnsureCellCapacity(p.tileCount.x * p.tileCount.y);

            cmd.SetComputeVectorParam(m_Compute, "_FPParams0", new Vector4(p.zBinScale, p.zBinOffset, 0f, 0f));
            cmd.SetComputeVectorParam(m_Compute, "_FPParams1", new Vector4(0f, 0f, 0f, p.wordsPerTile));
            cmd.SetComputeIntParam(m_Compute, "_CdClusterItemCount", p.itemCount);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterBinCount", p.binCount);
            cmd.SetComputeIntParams(m_Compute, "_CdClusterTileCount", p.tileCount.x, p.tileCount.y);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterTileWidth", p.tileWidth);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterCellMax", compactCellMax);
            cmd.SetComputeVectorParam(m_Compute, "_CdClusterScreenSize",
                new Vector4(p.screenResolution.x, p.screenResolution.y,
                    1f / math.max(1, p.screenResolution.x), 1f / math.max(1, p.screenResolution.y)));
            cmd.SetComputeVectorParam(m_Compute, "_CdClusterTileScale", new Vector4(p.tileScale.x, p.tileScale.y, 0f, 0f));
            cmd.SetComputeVectorParam(m_Compute, "_CdClusterViewToViewportScaleBias", p.viewToViewportScaleBias);
            cmd.SetComputeMatrixParam(m_Compute, "_CdClusterWorldToView", p.worldToView);
            cmd.SetComputeVectorParam(m_Compute, "_CdClusterZBufferParams", zBufferParams);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterOrthographic", p.orthographic ? 1 : 0);
            cmd.SetComputeIntParam(m_Compute, "_CdClusterPruneSidePlanes", pruneSidePlanes ? 1 : 0);

            cmd.SetComputeTextureParam(m_Compute, m_CellKernel, "_CdClusterDepthTexture", depthTexture);
            cmd.SetComputeBufferParam(m_Compute, m_CellKernel, "URP_ZBins", zbins);
            cmd.SetComputeBufferParam(m_Compute, m_CellKernel, "urp_Tiles", tiles);
            cmd.SetComputeBufferParam(m_Compute, m_CellKernel, "_CdClusterItems", m_Items);
            cmd.SetComputeBufferParam(m_Compute, m_CellKernel, "_CdCellHeaders", m_CellHeaders);
            cmd.SetComputeBufferParam(m_Compute, m_CellKernel, "_CdCellItems", m_CellItems);
            cmd.SetComputeBufferParam(m_Compute, m_CellKernel, "_CdTileCellInfo", m_TileCellInfo);
            cmd.SetComputeBufferParam(m_Compute, m_CellKernel, "_CdCellTotals", m_CellTotals);

            cmd.SetBufferData(m_CellTotals, k_ZeroCellTotals);
            cmd.DispatchCompute(m_Compute, m_CellKernel, p.tileCount.x, p.tileCount.y, 1);

            if (compactStatsEnabled)
                cmd.RequestAsyncReadback(m_CellTotals, OnCellStatsReadback);

            return true;
        }

        static void OnCellStatsReadback(AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
                return;

            var totals = request.GetData<uint>();
            lastCompactEntries = (int)totals[0];
            lastCompactCells = (int)totals[1];
            lastCompactOverflowCells = (int)totals[2];
            lastCompactOverflowTiles = (int)totals[3];
            lastCompactScannedCells = (int)totals[4];
        }

        static int Groups(int count, int threads) => math.max(1, (count + threads - 1) / threads);

        void EnsureCapacity(int itemCount, int binCount)
        {
            if (m_ItemCapacity < itemCount)
            {
                m_Items?.Dispose();
                m_MinMaxZ?.Dispose();
                m_TileRanges?.Dispose();
                m_ItemCapacity = math.max(itemCount, 64);
                m_Items = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_ItemCapacity, ClusterItemRecord.Stride) { name = "CD Cluster Items" };
                m_MinMaxZ = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_ItemCapacity, 8) { name = "CD Cluster MinMaxZ" };
                m_TileRanges = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_ItemCapacity, 16) { name = "CD Cluster Tile Ranges" };
            }

            if (m_BinCapacity < binCount)
            {
                m_BinMinMax?.Dispose();
                m_BinCapacity = math.max(binCount, 256);
                m_BinMinMax = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_BinCapacity * 4, 4) { name = "CD Cluster Bin Headers" };
            }

            // Two counters, kept for the whole builder lifetime so the readback target never moves.
            if (m_PruneTotals == null)
                m_PruneTotals = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 4) { name = "CD Cluster Prune Totals" };
        }

        void EnsureCellCapacity(int tileCount)
        {
            if (m_CellTotals == null)
                m_CellTotals = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_ZeroCellTotals.Length, 4) { name = "CD Cell Totals" };

            if (m_CellHeaders != null && m_CellCapacity >= tileCount && m_CellMax == compactCellMax)
                return;

            m_CellHeaders?.Dispose();
            m_CellItems?.Dispose();
            m_TileCellInfo?.Dispose();
            m_CellCapacity = math.max(tileCount, 16);
            m_CellMax = compactCellMax;
            int cellCount = m_CellCapacity * compactCellMax;
            m_CellHeaders = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cellCount, 4) { name = "CD Cell Headers" };
            m_CellItems = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cellCount * compactCellMax, 4) { name = "CD Cell Items" };
            m_TileCellInfo = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_CellCapacity, 8) { name = "CD Tile Cell Info" };
        }

        public void Dispose()
        {
            m_Items?.Dispose();
            m_MinMaxZ?.Dispose();
            m_TileRanges?.Dispose();
            m_BinMinMax?.Dispose();
            m_PruneTotals?.Dispose();
            m_CellHeaders?.Dispose();
            m_CellItems?.Dispose();
            m_TileCellInfo?.Dispose();
            m_CellTotals?.Dispose();
            m_Items = null;
            m_MinMaxZ = null;
            m_TileRanges = null;
            m_BinMinMax = null;
            m_PruneTotals = null;
            m_CellHeaders = null;
            m_CellItems = null;
            m_TileCellInfo = null;
            m_CellTotals = null;
            m_ItemCapacity = 0;
            m_BinCapacity = 0;
            m_CellCapacity = 0;
            m_CellMax = 0;
        }
    }
}
