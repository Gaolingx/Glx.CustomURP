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

        const int k_BoundsThreads = 64;
        const int k_ClearThreads = 256;
        const int k_ScatterThreads = 64;
        const int k_ExpandThreads = 8;

        readonly ComputeShader m_Compute;
        readonly int m_BoundsKernel;
        readonly int m_ClearKernel;
        readonly int m_ScatterKernel;
        readonly int m_PackKernel;
        readonly int m_ExpandKernel;

        GraphicsBuffer m_Items;
        GraphicsBuffer m_MinMaxZ;
        GraphicsBuffer m_TileRanges;
        GraphicsBuffer m_BinMinMax;
        int m_ItemCapacity;
        int m_BinCapacity;

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
        }

        public bool isCreated => m_Compute != null &&
                                 m_BoundsKernel >= 0 && m_ClearKernel >= 0 && m_ScatterKernel >= 0 &&
                                 m_PackKernel >= 0 && m_ExpandKernel >= 0;

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
        }

        public void Dispose()
        {
            m_Items?.Dispose();
            m_MinMaxZ?.Dispose();
            m_TileRanges?.Dispose();
            m_BinMinMax?.Dispose();
            m_Items = null;
            m_MinMaxZ = null;
            m_TileRanges = null;
            m_BinMinMax = null;
            m_ItemCapacity = 0;
            m_BinCapacity = 0;
        }
    }
}
