using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Per-camera scratch; no temporal reuse of HW hits or borrowed scene bindings.
    internal sealed class SsrRequestQueue : IDisposable
    {
        internal readonly int budget, seed;
        internal ComputeBuffer originals, requests, counters;
        internal ComputeBuffer hits;
        ComputeBuffer tiles;
        internal long residentBytes => Bytes(originals) + Bytes(tiles) + Bytes(requests) + Bytes(counters) + Bytes(hits);
        static long Bytes(ComputeBuffer buffer) => buffer == null ? 0 : buffer.count * (long)buffer.stride;
        static readonly ProfilingSampler s_Trace = new ProfilingSampler("SSR DXR Trace and Shade");
        internal static ProfilingSampler traceSampler => s_Trace;
        static readonly int s_Scene = Shader.PropertyToID("_GlxScene");
        static readonly int s_K = Shader.PropertyToID("_R2K");
        static readonly int s_Hits = Shader.PropertyToID("_R2Hits");
        static readonly int s_Counts = Shader.PropertyToID("_R1Counts");
        static readonly int s_Requests = Shader.PropertyToID("_R1Queue");
        static readonly uint[] s_Zero = new uint[11];
        internal SsrRequestQueue(int k, int seed = 20261125)
        {
            budget = Mathf.Clamp(k, 0, 32768);
            this.seed = seed;
        }

        internal void Record(CommandBuffer cmd, ComputeShader cs, int kernel, int width, int height, int frame)
        {
            if (budget == 0) return;
            int gx = (width+7)/8, gy = (height+7)/8;
            int k = Mathf.Min(budget,gx*gy*8,width*height);
            Ensure(ref originals,width*height,80);
            Ensure(ref tiles,gx*gy,8);
            Ensure(ref requests,k,80);
            Ensure(ref counters,11,4);
            cmd.SetBufferData(counters,s_Zero);
            cmd.SetComputeIntParam(cs,"_R1Phase",unchecked(frame+seed));
            cmd.SetComputeIntParam(cs,"_R1K",k);
            cmd.SetComputeBufferParam(cs,kernel,"_R1Original",originals);
            cmd.SetComputeBufferParam(cs,kernel,"_R1Counts",counters);
            cmd.DispatchCompute(cs,kernel,gx,gy,1);
            int tile = cs.FindKernel("R1Tiles"), offsets = cs.FindKernel("R1Offsets"), scatter = cs.FindKernel("R1Scatter");
            cmd.SetComputeBufferParam(cs,tile,"_R1Original",originals);
            cmd.SetComputeBufferParam(cs,tile,"_R1Tile",tiles);
            cmd.DispatchCompute(cs,tile,gx,gy,1);
            cmd.SetComputeBufferParam(cs,offsets,"_R1Tile",tiles);
            cmd.SetComputeBufferParam(cs,offsets,"_R1Counts",counters);
            cmd.DispatchCompute(cs,offsets,1,1,1);
            cmd.SetComputeBufferParam(cs,scatter,"_R1Original",originals);
            cmd.SetComputeBufferParam(cs,scatter,"_R1Tile",tiles);
            cmd.SetComputeBufferParam(cs,scatter,"_R1Queue",requests);
            cmd.DispatchCompute(cs,scatter,gx,gy,1);
        }

        internal void Trace(CommandBuffer cmd, ComputeShader cs, RayTracingShader shader,
            RayTracingSceneResources scene, RayTracingAccelerationStructure snapshot, int width, int height)
        {
            if (budget == 0) return;
            Ensure(ref hits, width * height, 24);
            int clear = cs.FindKernel("R2Clear");
            cmd.SetComputeBufferParam(cs, clear, s_Hits, hits);
            cmd.DispatchCompute(cs, clear, (width + 7) / 8, (height + 7) / 8, 1);
            using (new ProfilingScope(cmd, s_Trace))
            {
                cmd.SetRayTracingShaderPass(shader, "GlxDXR");
                cmd.SetRayTracingAccelerationStructure(shader, s_Scene, snapshot);
                cmd.SetRayTracingIntParam(shader, s_K, budget);
                cmd.SetRayTracingBufferParam(shader, s_Requests, requests);
                cmd.SetRayTracingBufferParam(shader, s_Counts, counters);
                cmd.SetRayTracingBufferParam(shader, s_Hits, hits);
                scene.BindLights(cmd, shader);
                cmd.DispatchRays(shader, "TraceSsrRequests", (uint)budget, 1, 1, null);
            }
        }

        static void Ensure(ref ComputeBuffer buffer,int count,int stride)
        {
            if (buffer != null && buffer.count == count) return;
            var replacement = new ComputeBuffer(count,stride);
            buffer?.Release(); buffer = replacement;
        }
        public void Dispose()
        {
            originals?.Release(); tiles?.Release(); requests?.Release(); counters?.Release();
            hits?.Release();
            originals = tiles = requests = counters = hits = null;
        }
    }
}
