using System;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Borrowed by pass 206; the diagnostic/future consumer owns one per camera and
    // disposes it on disable/teardown. No feature creates this by default.
    internal sealed class SsrRequestQueue : IDisposable
    {
        internal readonly int budget, seed;
        internal ComputeBuffer originals, requests, counters;
        ComputeBuffer tiles;
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

        static void Ensure(ref ComputeBuffer buffer,int count,int stride)
        {
            if (buffer != null && buffer.count == count) return;
            buffer?.Release(); buffer = new ComputeBuffer(count,stride);
        }
        public void Dispose()
        {
            originals?.Release(); tiles?.Release(); requests?.Release(); counters?.Release();
            originals = tiles = requests = counters = null;
        }
    }
}
