using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Frozen by B0 (validation/B0/Freeze.json); the volume-facing knobs arrive with G3 wiring.
    internal struct RayTracingProbeConfig
    {
        internal Vector3 origin;
        internal float spacing;
        internal int countX, countY, countZ;
        internal int budget;
        internal int raysPerProbe;
        internal bool feedback, relocation, classification, freezeDirections;
        internal float previousWeight, relocationLimit, minFrontDistance, insideBackfaceFraction, momentCap;
        internal float feedbackAlbedoCap;
        internal float tMin, tMax;

        internal int count => countX * countY * countZ;

        // The producer captures the config at construction, so a settings change has to replace it.
        internal bool Matches(in RayTracingProbeConfig other)
        {
            return origin == other.origin && spacing == other.spacing &&
                countX == other.countX && countY == other.countY && countZ == other.countZ &&
                budget == other.budget && raysPerProbe == other.raysPerProbe &&
                feedback == other.feedback && relocation == other.relocation &&
                classification == other.classification && freezeDirections == other.freezeDirections &&
                previousWeight == other.previousWeight && relocationLimit == other.relocationLimit &&
                minFrontDistance == other.minFrontDistance &&
                insideBackfaceFraction == other.insideBackfaceFraction && momentCap == other.momentCap &&
                feedbackAlbedoCap == other.feedbackAlbedoCap && tMin == other.tMin && tMax == other.tMax;
        }

        internal static RayTracingProbeConfig Default => new RayTracingProbeConfig
        {
            origin = new Vector3(-3.5f, 0.5f, -3.5f), spacing = 1f,
            countX = 8, countY = 4, countZ = 8,
            budget = 16, raysPerProbe = 128,
            feedback = false, relocation = true, classification = true, freezeDirections = false,
            previousWeight = 0.75f, relocationLimit = 0.45f, minFrontDistance = 0.2f,
            insideBackfaceFraction = 0.25f, momentCap = 1.5f, feedbackAlbedoCap = 0.9f,
            tMin = 0.002f, tMax = 30f
        };
    }

    // Scene-owned probe producer for the GI probe cache. One batch (B probes x R rays) per
    // scene frame; the first 32 rays are fixed maintenance rays and never enter the
    // irradiance/distance convolution. Publication is double buffered: the batch reads the
    // previously published generation and the swap happens only after every dispatch of the
    // batch, so a consumer never sees a half-updated atlas.
    internal sealed class RayTracingProbeUpdate : IDisposable
    {
        const int ProbesPerRow = 8;
        const int IrrInterior = 6, IrrTile = 8;
        const int DistInterior = 14, DistTile = 16;
        const int FixedRays = 32;
        const int CounterCount = 8;
        static readonly ProfilingSampler s_BatchSampler = new ProfilingSampler("G2 Probe Batch");

        struct ProbeMeta { public uint published, state; }

        static readonly int s_Rays = Shader.PropertyToID("_GlxProbeRays");
        static readonly int s_Results = Shader.PropertyToID("_GlxProbeResults");
        static readonly int s_Counters = Shader.PropertyToID("_GlxProbeCounters");
        static readonly int s_Data = Shader.PropertyToID("_GlxProbeData");
        static readonly int s_DataNext = Shader.PropertyToID("_GlxProbeDataNext");
        static readonly int s_Published = Shader.PropertyToID("_GlxProbePublished");
        static readonly int s_PublishedNext = Shader.PropertyToID("_GlxProbePublishedNext");
        static readonly int s_Updated = Shader.PropertyToID("_GlxProbeUpdated");
        static readonly int s_Batch = Shader.PropertyToID("_GlxProbeBatch");
        static readonly int s_IrrWrite = Shader.PropertyToID("_GlxProbeIrrWrite");
        static readonly int s_IrrPrev = Shader.PropertyToID("_GlxProbeIrrPrev");
        static readonly int s_DistWrite = Shader.PropertyToID("_GlxProbeDistWrite");
        static readonly int s_DistPrev = Shader.PropertyToID("_GlxProbeDistPrev");
        static readonly int s_ReadbackIrr = Shader.PropertyToID("_GlxProbeReadbackIrr");
        static readonly int s_ReadbackDist = Shader.PropertyToID("_GlxProbeReadbackDist");
        static readonly int s_ReadbackMeta = Shader.PropertyToID("_GlxProbeReadbackMeta");
        static readonly int s_ReadbackProbe = Shader.PropertyToID("_GlxProbeReadbackProbe");
        static readonly int s_Scene = Shader.PropertyToID("_GlxScene");
        // Published generation, read by the probe closest hit for the 1-hop feedback term and by
        // the material-side consumer. Kept as globals so one binding serves ray tracing and draws.
        static readonly int s_IrrSnapshot = Shader.PropertyToID("_GlxProbeIrrSnapshot");
        static readonly int s_DistSnapshot = Shader.PropertyToID("_GlxProbeDistSnapshot");
        static readonly int s_StateSnapshot = Shader.PropertyToID("_GlxProbeStateSnapshot");
        static readonly int s_PublishedSnapshot = Shader.PropertyToID("_GlxProbePublishedSnapshot");
        static readonly int s_IrrSize = Shader.PropertyToID("_GlxProbeIrrSize");
        static readonly int s_DistSize = Shader.PropertyToID("_GlxProbeDistSize");
        static readonly int s_CountX = Shader.PropertyToID("_GlxProbeCountX");
        static readonly int s_CountY = Shader.PropertyToID("_GlxProbeCountY");
        static readonly int s_CountZ = Shader.PropertyToID("_GlxProbeCountZ");
        static readonly int s_Origin = Shader.PropertyToID("_GlxProbeOrigin");
        static readonly int s_Spacing = Shader.PropertyToID("_GlxProbeSpacing");
        static readonly int s_SnapshotValid = Shader.PropertyToID("_GlxProbeSnapshotValid");
        static readonly int s_FeedbackEnabled = Shader.PropertyToID("_GlxProbeFeedbackEnabled");
        static readonly int s_FeedbackCap = Shader.PropertyToID("_GlxProbeFeedbackAlbedoCap");

        readonly RayTracingProbeConfig m_Config;
        readonly RayTracingShader m_Trace;
        readonly ComputeShader m_Update;
        readonly uint[] m_ZeroCounters = new uint[CounterCount];
        readonly uint[] m_ZeroProbes;
        readonly Vector4[] m_InitialData;
        readonly uint[] m_BatchIndices;
        readonly int m_ProbeRows;

        RenderTexture[] m_Irr, m_Dist;
        ComputeBuffer[] m_Data, m_Published;
        ComputeBuffer m_Updated, m_Batch, m_Rays, m_Results, m_Counters;
        ComputeBuffer m_ReadbackIrr, m_ReadbackDist, m_ReadbackMeta;
        RayTracingSceneResources m_Scene;
        RayTracingAccelerationStructure m_Snapshot;
        int m_Publish, m_Rotation, m_BatchVersion, m_UpdateCount, m_LastFrame = int.MinValue;
        bool m_Allocated, m_Disposed;

        internal RayTracingProbeUpdate(in RayTracingProbeConfig config, RayTracingShader trace, ComputeShader update)
        {
            if (trace == null || update == null) throw new ArgumentNullException(trace == null ? nameof(trace) : nameof(update));
            m_Config = config;
            m_Config.countX = Mathf.Clamp(m_Config.countX, 1, 16);
            m_Config.countY = Mathf.Clamp(m_Config.countY, 1, 16);
            m_Config.countZ = Mathf.Clamp(m_Config.countZ, 1, 16);
            m_Config.budget = Mathf.Clamp(m_Config.budget, 1, m_Config.count);
            m_Config.raysPerProbe = Mathf.Clamp(m_Config.raysPerProbe, FixedRays, 512);
            m_Trace = trace;
            m_Update = update;
            m_ZeroProbes = new uint[m_Config.count];
            m_InitialData = new Vector4[m_Config.count];
            for (int i = 0; i < m_InitialData.Length; ++i) m_InitialData[i] = new Vector4(0, 0, 0, 1);
            m_BatchIndices = new uint[m_Config.budget];
            m_ProbeRows = (m_Config.count + ProbesPerRow - 1) / ProbesPerRow;
        }

        internal int updateCount => m_UpdateCount;
        internal int generation => m_BatchVersion;
        internal int lastUpdateFrame => m_LastFrame;
        internal bool allocated => m_Allocated;
        internal ComputeBuffer counters => m_Counters;
        internal static ProfilingSampler batchSampler => s_BatchSampler;
        // I2 timing hooks: the batch split into trace/resolve and the relocation/classification
        // maintenance pair, each recorded only when the sampler is enabled.
        static readonly ProfilingSampler s_TraceUpdateSampler = new ProfilingSampler("G2 Probe Trace And Update");
        static readonly ProfilingSampler s_MaintainSampler = new ProfilingSampler("G2 Relocate Classify");
        internal static ProfilingSampler traceUpdateSampler => s_TraceUpdateSampler;
        internal static ProfilingSampler maintainSampler => s_MaintainSampler;
        internal long residentBytes { get; private set; }
        internal int probeCount => m_Config.count;
        internal uint[] lastBatch => m_BatchIndices;
        internal bool directionsFrozen => m_Config.freezeDirections;
        internal bool ready => m_Allocated && m_BatchVersion > 0;

        // One batch per scene frame. A second eligible camera in the same frame reuses the
        // published generation and must not dispatch again.
        internal bool TryUpdate(int frame, in CameraData camera, RayTracingSceneResources scene, Material witness,
            int revision, int layers, System.Collections.Generic.IReadOnlyList<RayTracingMember> members,
            out RayTracingAccelerationStructure snapshot)
        {
            snapshot = null;
            if (m_Disposed) return false;
            if (m_UpdateCount > 0 && frame == m_LastFrame) return false;
            if (!scene.TryBuild(camera, true, m_Trace, witness, revision, layers, RayTracingRequests.GI,
                    members, out snapshot) || snapshot == null) return false;
            m_Scene = scene;
            m_Snapshot = snapshot;
            EnsureResources();
            m_LastFrame = frame;
            var cmd = CommandBufferPool.Get("G2 probe batch");
            try
            {
                Record(cmd);
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally { CommandBufferPool.Release(cmd); }
            ++m_UpdateCount;
            ++m_BatchVersion;
            return true;
        }

        void Record(CommandBuffer cmd)
        {
            using (new ProfilingScope(cmd, s_BatchSampler))
            {
            int publish = m_Publish, write = publish ^ 1;
            cmd.CopyTexture(m_Irr[publish], m_Irr[write]);
            cmd.CopyTexture(m_Dist[publish], m_Dist[write]);

            int last = (m_Rotation + m_Config.budget - 1) % m_Config.count;
            for (int i = 0; i < m_BatchIndices.Length; ++i)
                m_BatchIndices[i] = (uint)((m_Rotation + i) % m_Config.count);
            m_Batch.SetData(m_BatchIndices);
            m_Rotation = (last + 1) % m_Config.count;

            cmd.SetBufferData(m_Counters, m_ZeroCounters);
            cmd.SetBufferData(m_Updated, m_ZeroProbes);

            Configure();
            var cs = m_Update;

            using (new ProfilingScope(cmd, s_TraceUpdateSampler))
            {
            int copy = cs.FindKernel("ProbeCopyState");
            BindCopyState(cmd, copy, publish, write);
            int probeGroups = (m_Config.count + 63) / 64;
            cmd.DispatchCompute(cs, copy, probeGroups, 1, 1);

            int prepare = cs.FindKernel("ProbePrepare");
            BindPrepare(cmd, prepare, publish);
            int rayCount = m_Config.budget * m_Config.raysPerProbe;
            cmd.DispatchCompute(cs, prepare, (rayCount + 63) / 64, 1, 1);

            cmd.SetRayTracingShaderPass(m_Trace, "GlxDXR");
            cmd.SetRayTracingAccelerationStructure(m_Trace, s_Scene, m_Snapshot);
            cmd.SetRayTracingBufferParam(m_Trace, s_Rays, m_Rays);
            cmd.SetRayTracingBufferParam(m_Trace, s_Results, m_Results);
            cmd.SetRayTracingBufferParam(m_Trace, s_Counters, m_Counters);
            m_Scene.BindLights(cmd, m_Trace);
            // The batch traces from the previously published offset/state, so the hit shader can
            // read that same generation for one 8-neighbour feedback query (never this batch's
            // write targets). First batch has no published generation.
            BindSnapshotGlobals(cmd, publish);
            cmd.DispatchRays(m_Trace, "TraceProbeRays", (uint)rayCount, 1, 1, null);

            int irradiance = cs.FindKernel("ProbeIrradiance");
            BindConvolve(cmd, irradiance, publish);
            cmd.SetComputeTextureParam(cs, irradiance, s_IrrWrite, m_Irr[write]);
            cmd.SetComputeTextureParam(cs, irradiance, s_IrrPrev, m_Irr[publish]);
            cmd.DispatchCompute(cs, irradiance, 1, 1, m_Config.budget);

            int distance = cs.FindKernel("ProbeDistance");
            BindConvolve(cmd, distance, publish);
            cmd.SetComputeTextureParam(cs, distance, s_DistWrite, m_Dist[write]);
            cmd.SetComputeTextureParam(cs, distance, s_DistPrev, m_Dist[publish]);
            cmd.DispatchCompute(cs, distance, 1, 1, m_Config.budget);
            }

            using (new ProfilingScope(cmd, s_MaintainSampler))
            {
            int maintain = cs.FindKernel("ProbeMaintain");
            BindMaintain(cmd, maintain, publish);
            cmd.DispatchCompute(cs, maintain, (m_Config.budget + 63) / 64, 1, 1);

            int masks = cs.FindKernel("ProbePublishMask");
            BindMasks(cmd, masks, publish);
            cmd.DispatchCompute(cs, masks, (m_Config.budget + 63) / 64, 1, 1);
            }

            m_Publish = write;
            }
        }

        void BindCopyState(CommandBuffer cmd, int kernel, int publish, int write)
        {
            cmd.SetComputeBufferParam(m_Update, kernel, s_Data, m_Data[publish]);
            cmd.SetComputeBufferParam(m_Update, kernel, s_DataNext, m_Data[write]);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Published, m_Published[publish]);
            cmd.SetComputeBufferParam(m_Update, kernel, s_PublishedNext, m_Published[write]);
        }

        void BindPrepare(CommandBuffer cmd, int kernel, int publish)
        {
            cmd.SetComputeBufferParam(m_Update, kernel, s_Rays, m_Rays);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Counters, m_Counters);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Data, m_Data[publish]);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Batch, m_Batch);
        }

        void BindConvolve(CommandBuffer cmd, int kernel, int publish)
        {
            cmd.SetComputeBufferParam(m_Update, kernel, s_Rays, m_Rays);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Results, m_Results);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Data, m_Data[publish]);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Published, m_Published[publish]);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Updated, m_Updated);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Batch, m_Batch);
        }

        void BindMaintain(CommandBuffer cmd, int kernel, int publish)
        {
            cmd.SetComputeBufferParam(m_Update, kernel, s_Rays, m_Rays);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Results, m_Results);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Data, m_Data[publish]);
            cmd.SetComputeBufferParam(m_Update, kernel, s_DataNext, m_Data[publish ^ 1]);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Batch, m_Batch);
        }

        void BindSnapshotGlobals(CommandBuffer cmd, int publish)
        {
            var irr = m_Irr[publish];
            var dist = m_Dist[publish];
            cmd.SetGlobalTexture(s_IrrSnapshot, irr);
            cmd.SetGlobalTexture(s_DistSnapshot, dist);
            cmd.SetGlobalBuffer(s_StateSnapshot, m_Data[publish]);
            cmd.SetGlobalBuffer(s_PublishedSnapshot, m_Published[publish]);
            cmd.SetGlobalVector(s_IrrSize, new Vector4(irr.width, irr.height, 1f / irr.width, 1f / irr.height));
            cmd.SetGlobalVector(s_DistSize, new Vector4(dist.width, dist.height, 1f / dist.width, 1f / dist.height));
            cmd.SetGlobalInt(s_CountX, m_Config.countX);
            cmd.SetGlobalInt(s_CountY, m_Config.countY);
            cmd.SetGlobalInt(s_CountZ, m_Config.countZ);
            cmd.SetGlobalVector(s_Origin, m_Config.origin);
            cmd.SetGlobalFloat(s_Spacing, m_Config.spacing);
            // "Published" means a whole generation exists; the per-probe mask still gates lookups.
            cmd.SetGlobalInt(s_SnapshotValid, m_BatchVersion > 0 ? 1 : 0);
            cmd.SetGlobalInt(s_FeedbackEnabled, m_Config.feedback ? 1 : 0);
            cmd.SetGlobalFloat(s_FeedbackCap, m_Config.feedbackAlbedoCap);
        }

        // Consumer binding of the published generation. Returns false before the first batch has
        // published anything, so the material keeps the exact baked-GI path.
        internal bool BindPublishedGlobals(CommandBuffer cmd)
        {
            if (cmd == null || !m_Allocated || m_BatchVersion == 0) return false;
            BindSnapshotGlobals(cmd, m_Publish);
            return true;
        }

        void BindMasks(CommandBuffer cmd, int kernel, int publish)
        {
            cmd.SetComputeBufferParam(m_Update, kernel, s_Published, m_Published[publish]);
            cmd.SetComputeBufferParam(m_Update, kernel, s_PublishedNext, m_Published[publish ^ 1]);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Updated, m_Updated);
            cmd.SetComputeBufferParam(m_Update, kernel, s_Batch, m_Batch);
        }

        void EnsureResources()
        {
            if (m_Allocated) return;
            m_Allocated = true;
            m_Irr = new[]
            {
                NewAtlas(ProbesPerRow * IrrTile, m_ProbeRows * IrrTile, GraphicsFormat.R16G16B16A16_SFloat),
                NewAtlas(ProbesPerRow * IrrTile, m_ProbeRows * IrrTile, GraphicsFormat.R16G16B16A16_SFloat)
            };
            m_Dist = new[]
            {
                NewAtlas(ProbesPerRow * DistTile, m_ProbeRows * DistTile, GraphicsFormat.R16G16_SFloat),
                NewAtlas(ProbesPerRow * DistTile, m_ProbeRows * DistTile, GraphicsFormat.R16G16_SFloat)
            };
            m_Data = new[] { NewData(), NewData() };
            m_Published = new[] { NewPublished(), NewPublished() };
            m_Updated = new ComputeBuffer(m_Config.count, sizeof(uint));
            m_Batch = new ComputeBuffer(m_Config.budget, sizeof(uint));
            m_Rays = new ComputeBuffer(m_Config.budget * m_Config.raysPerProbe, 48);
            m_Results = new ComputeBuffer(m_Config.budget * m_Config.raysPerProbe, 24);
            m_Counters = new ComputeBuffer(CounterCount, sizeof(uint));
            m_ReadbackIrr = new ComputeBuffer(IrrTile * IrrTile, 16);
            m_ReadbackDist = new ComputeBuffer(DistTile * DistTile, 16);
            m_ReadbackMeta = new ComputeBuffer(1, 8);
            residentBytes = 2L * ProbesPerRow * IrrTile * m_ProbeRows * IrrTile * 8 +
                2L * ProbesPerRow * DistTile * m_ProbeRows * DistTile * 4 +
                (2L * 16 + 2L * 4 + 4) * m_Config.count +
                (4L + 48 * m_Config.raysPerProbe + 24 * m_Config.raysPerProbe) * m_Config.budget +
                CounterCount * 4 + IrrTile * IrrTile * 16 + DistTile * DistTile * 16 + 8;
        }

        ComputeBuffer NewData()
        {
            var buffer = new ComputeBuffer(m_Config.count, 16);
            buffer.SetData(m_InitialData);
            return buffer;
        }

        ComputeBuffer NewPublished()
        {
            var buffer = new ComputeBuffer(m_Config.count, sizeof(uint));
            buffer.SetData(m_ZeroProbes);
            return buffer;
        }

        static RenderTexture NewAtlas(int width, int height, GraphicsFormat format)
        {
            var descriptor = new RenderTextureDescriptor(width, height, format, 0)
            {
                dimension = TextureDimension.Tex2D, enableRandomWrite = true, msaaSamples = 1,
                useMipMap = false, sRGB = false
            };
            var texture = new RenderTexture(descriptor)
            {
                name = "G2 probe atlas", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                // Only managed code holds the atlas, so an unused-asset unload (e.g. building while in
                // Play Mode) would destroy it underneath the producer.
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
            return texture;
        }

        void Configure()
        {
            var cs = m_Update;
            cs.SetInt("_GlxProbeBudget", m_Config.budget);
            cs.SetInt("_GlxProbeRaysPerProbe", m_Config.raysPerProbe);
            cs.SetInt("_GlxProbeFrame", m_Config.freezeDirections ? 0 : m_BatchVersion);
            cs.SetInt("_GlxProbeCountX", m_Config.countX);
            cs.SetInt("_GlxProbeCountY", m_Config.countY);
            cs.SetInt("_GlxProbeCountZ", m_Config.countZ);
            cs.SetInt("_GlxProbeCount", m_Config.count);
            cs.SetVector("_GlxProbeOrigin", m_Config.origin);
            cs.SetFloat("_GlxProbeSpacing", m_Config.spacing);
            cs.SetFloat("_GlxProbeTMin", m_Config.tMin);
            cs.SetFloat("_GlxProbeTMax", m_Config.tMax);
            cs.SetFloat("_GlxProbePreviousWeight", m_Config.previousWeight);
            cs.SetFloat("_GlxProbeRelocationLimit", m_Config.spacing * m_Config.relocationLimit);
            cs.SetFloat("_GlxProbeMinFront", m_Config.minFrontDistance);
            cs.SetFloat("_GlxProbeInsideBackface", m_Config.insideBackfaceFraction);
            cs.SetFloat("_GlxProbeMomentCap", m_Config.spacing * m_Config.momentCap);
            cs.SetInt("_GlxProbeRelocate", m_Config.relocation ? 1 : 0);
            cs.SetInt("_GlxProbeClassify", m_Config.classification ? 1 : 0);
        }

        internal void ReadCounters(uint[] destination)
        {
            if (!m_Allocated) { Array.Clear(destination, 0, destination.Length); return; }
            m_Counters.GetData(destination, 0, 0, Mathf.Min(destination.Length, CounterCount));
        }

        // Synchronous readback for validation only (not part of the render path).
        internal void ReadProbe(int probe, Vector4[] irradiance, Vector4[] distance, out uint published, out uint state)
        {
            published = 0;
            state = 0;
            if (!m_Allocated || probe < 0 || probe >= m_Config.count) return;
            var cs = m_Update;
            var cmd = CommandBufferPool.Get("G2 probe readback");
            try
            {
                cs.SetInt(s_ReadbackProbe, probe);
                int irr = cs.FindKernel("ProbeReadbackIrr");
                cmd.SetComputeBufferParam(cs, irr, s_ReadbackIrr, m_ReadbackIrr);
                cmd.SetComputeTextureParam(cs, irr, s_IrrPrev, m_Irr[m_Publish]);
                cmd.DispatchCompute(cs, irr, 1, 1, 1);
                int dist = cs.FindKernel("ProbeReadbackDist");
                cmd.SetComputeBufferParam(cs, dist, s_ReadbackDist, m_ReadbackDist);
                cmd.SetComputeTextureParam(cs, dist, s_DistPrev, m_Dist[m_Publish]);
                cmd.DispatchCompute(cs, dist, 1, 1, 1);
                int meta = cs.FindKernel("ProbeReadbackMeta");
                cmd.SetComputeBufferParam(cs, meta, s_ReadbackMeta, m_ReadbackMeta);
                cmd.SetComputeBufferParam(cs, meta, s_Published, m_Published[m_Publish]);
                cmd.SetComputeBufferParam(cs, meta, s_Data, m_Data[m_Publish]);
                cmd.DispatchCompute(cs, meta, 1, 1, 1);
                Graphics.ExecuteCommandBuffer(cmd);
                m_ReadbackIrr.GetData(irradiance, 0, 0, irradiance.Length);
                m_ReadbackDist.GetData(distance, 0, 0, distance.Length);
                var metaData = new ProbeMeta[1];
                m_ReadbackMeta.GetData(metaData);
                published = metaData[0].published;
                state = metaData[0].state;
            }
            finally { CommandBufferPool.Release(cmd); }
        }

        internal Vector4[] ReadProbeData()
        {
            if (!m_Allocated) return Array.Empty<Vector4>();
            var data = new Vector4[m_Config.count];
            m_Data[m_Publish].GetData(data);
            return data;
        }

        internal uint[] ReadPublishedMask()
        {
            if (!m_Allocated) return Array.Empty<uint>();
            var data = new uint[m_Config.count];
            m_Published[m_Publish].GetData(data);
            return data;
        }

        internal void SaveAtlas(string irrPath, string distPath)
        {
            if (!m_Allocated) return;
            var previous = RenderTexture.active;
            var irr = new Texture2D(m_Irr[m_Publish].width, m_Irr[m_Publish].height, TextureFormat.RGBAHalf, false, true);
            var dist = new Texture2D(m_Dist[m_Publish].width, m_Dist[m_Publish].height, TextureFormat.RG16, false, true);
            try
            {
                if (irrPath != null)
                {
                    RenderTexture.active = m_Irr[m_Publish];
                    irr.ReadPixels(new Rect(0, 0, irr.width, irr.height), 0, 0);
                    irr.Apply();
                    System.IO.File.WriteAllBytes(irrPath, irr.GetRawTextureData());
                }
                if (distPath != null)
                {
                    RenderTexture.active = m_Dist[m_Publish];
                    dist.ReadPixels(new Rect(0, 0, dist.width, dist.height), 0, 0);
                    dist.Apply();
                    System.IO.File.WriteAllBytes(distPath, dist.GetRawTextureData());
                }
            }
            finally
            {
                RenderTexture.active = previous;
                CoreUtils.Destroy(irr);
                CoreUtils.Destroy(dist);
            }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            if (m_Irr != null) foreach (var t in m_Irr) if (t) { t.Release(); CoreUtils.Destroy(t); }
            if (m_Dist != null) foreach (var t in m_Dist) if (t) { t.Release(); CoreUtils.Destroy(t); }
            if (m_Data != null) foreach (var b in m_Data) b?.Release();
            if (m_Published != null) foreach (var b in m_Published) b?.Release();
            m_Updated?.Release();
            m_Batch?.Release();
            m_Rays?.Release();
            m_Results?.Release();
            m_Counters?.Release();
            m_ReadbackIrr?.Release();
            m_ReadbackDist?.Release();
            m_ReadbackMeta?.Release();
            m_Irr = null; m_Dist = null; m_Data = null; m_Published = null;
            m_Updated = m_Batch = m_Rays = m_Results = m_Counters = null;
            m_ReadbackIrr = m_ReadbackDist = m_ReadbackMeta = null;
        }
    }
}
