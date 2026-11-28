using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Per-camera scratch for the fixed-K request budget. The queue itself holds no temporal state:
    // the pass merges each frame's hits with its own resolved-fill history (SsrPass.Temporal) before
    // compositing, so the rotating tile selection converges instead of toggling probe/fill pixels.
    internal sealed class SsrRequestQueue : IDisposable
    {
        internal readonly int budget, seed;
        internal ComputeBuffer originals, requests, counters;
        // B6.2 early screen: 12 bytes per slot (ssShare, requestShare, reason). The per-pixel
        // consumers read this first and only fetch the 64-byte request record when in domain.
        internal ComputeBuffer coverage;
        internal ComputeBuffer hits;
        // C2.7 hit surface: request-indexed, written by the raygen, read by the deferred kernel.
        internal ComputeBuffer surfaces;
        ComputeBuffer tiles, masks;
        // This frame's served stamp. Every publisher writes it into GlxPublishedHit.mode, so the
        // hit buffer no longer needs a per-frame clear: consumers reject a record whose stamp is
        // not this frame's.
        internal int hitFrame;
        internal long residentBytes => Bytes(originals) + Bytes(coverage) + Bytes(tiles) + Bytes(masks) + Bytes(requests) + Bytes(counters) + Bytes(hits) + Bytes(surfaces);
        static long Bytes(ComputeBuffer buffer) => buffer == null ? 0 : buffer.count * (long)buffer.stride;
        static readonly ProfilingSampler s_Trace = new ProfilingSampler("SSR DXR Trace and Shade");
        internal static ProfilingSampler traceSampler => s_Trace;
        // The request build ran inside SsrPass's sampler with no marker of its own, so a GPU
        // capture attributed its time to nothing.
        static readonly ProfilingSampler s_R1Original = new ProfilingSampler("SSR R1 Original");
        static readonly ProfilingSampler s_R1Offsets = new ProfilingSampler("SSR R1 Offsets");
        static readonly ProfilingSampler s_R1Scatter = new ProfilingSampler("SSR R1 Scatter");
        static readonly int s_Scene = Shader.PropertyToID("_GlxScene");
        static readonly int s_K = Shader.PropertyToID("_R2K");
        static readonly int s_Hits = Shader.PropertyToID("_R2Hits");
        static readonly int s_HitFrame = Shader.PropertyToID("_R2HitFrame");
        static readonly int s_Surfaces = Shader.PropertyToID("_R2Surfaces");
        static readonly int s_Counts = Shader.PropertyToID("_R1Counts");
        static readonly int s_Requests = Shader.PropertyToID("_R1Queue");
        static readonly uint[] s_Zero = new uint[11];
        internal SsrRequestQueue(int k, int seed = 20261125)
        {
            budget = Mathf.Clamp(k, 0, 131072);
            this.seed = seed;
        }

        internal void Record(CommandBuffer cmd, ComputeShader cs, int kernel, int width, int height, int frame)
        {
            // The stamp is consumed by the raygen and the hit kernels whether or not this camera
            // spends a budget this frame, so it is set before the early out.
            hitFrame = unchecked(frame + seed);
            if (budget == 0) return;
            int gx = (width+7)/8, gy = (height+7)/8;
            int k = Mathf.Min(budget,gx*gy*8,width*height);
            Ensure(ref originals,width*height,64);
            Ensure(ref coverage,width*height,12);
            Ensure(ref tiles,gx*gy,8);
            Ensure(ref masks,gx*gy,8);
            Ensure(ref requests,k,64);
            Ensure(ref surfaces,k,80);
            Ensure(ref counters,11,4);
            cmd.SetBufferData(counters,s_Zero);
            cmd.SetComputeIntParam(cs,"_R1Phase",unchecked(frame+seed));
            cmd.SetComputeIntParam(cs,"_R1K",k);
            cmd.SetComputeBufferParam(cs,kernel,"_R1Original",originals);
            cmd.SetComputeBufferParam(cs,kernel,"_R1Coverage",coverage);
            cmd.SetComputeBufferParam(cs,kernel,"_R1Counts",counters);
            // The classify kernel owns the per-tile mask and count now; the tile pass is gone.
            cmd.SetComputeBufferParam(cs,kernel,"_R1Tile",tiles);
            cmd.SetComputeBufferParam(cs,kernel,"_R1Mask",masks);
            using (new ProfilingScope(cmd,s_R1Original)) cmd.DispatchCompute(cs,kernel,gx,gy,1);
            int offsets = cs.FindKernel("R1Offsets"), scatter = cs.FindKernel("R1Scatter");
            cmd.SetComputeBufferParam(cs,offsets,"_R1Tile",tiles);
            cmd.SetComputeBufferParam(cs,offsets,"_R1Counts",counters);
            using (new ProfilingScope(cmd,s_R1Offsets)) cmd.DispatchCompute(cs,offsets,1,1,1);
            cmd.SetComputeBufferParam(cs,scatter,"_R1Original",originals);
            cmd.SetComputeBufferParam(cs,scatter,"_R1Tile",tiles);
            cmd.SetComputeBufferParam(cs,scatter,"_R1Mask",masks);
            cmd.SetComputeBufferParam(cs,scatter,"_R1Queue",requests);
            using (new ProfilingScope(cmd,s_R1Scatter)) cmd.DispatchCompute(cs,scatter,gx,gy,1);
        }

        internal void Trace(CommandBuffer cmd, RayTracingShader shader,
            RayTracingSceneResources scene, RayTracingAccelerationStructure snapshot, int width, int height)
        {
            if (budget == 0) return;
            Ensure(ref hits, width * height, 24);
            using (new ProfilingScope(cmd, s_Trace))
            {
                cmd.SetRayTracingShaderPass(shader, "GlxDXR");
                cmd.SetRayTracingAccelerationStructure(shader, s_Scene, snapshot);
                cmd.SetRayTracingIntParam(shader, s_K, budget);
                cmd.SetRayTracingIntParam(shader, s_HitFrame, hitFrame);
                cmd.SetRayTracingBufferParam(shader, s_Requests, requests);
                cmd.SetRayTracingBufferParam(shader, s_Counts, counters);
                cmd.SetRayTracingBufferParam(shader, s_Hits, hits);
                cmd.SetRayTracingBufferParam(shader, s_Surfaces, surfaces);
                scene.BindLights(cmd, shader);
                cmd.DispatchRays(shader, "TraceSsrRequests", (uint)budget, 1, 1, null);
            }
        }

        static void Ensure(ref ComputeBuffer buffer,int count,int stride)
        {
            // The stride is part of the reuse check so a layout change never keeps an old buffer.
            if (buffer != null && buffer.count == count && buffer.stride == stride) return;
            var replacement = new ComputeBuffer(count,stride);
            buffer?.Release(); buffer = replacement;
        }
        public void Dispose()
        {
            originals?.Release(); coverage?.Release(); tiles?.Release(); masks?.Release(); requests?.Release(); counters?.Release();
            hits?.Release(); surfaces?.Release();
            originals = coverage = tiles = masks = requests = counters = hits = surfaces = null;
        }
    }
}
