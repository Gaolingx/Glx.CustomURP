using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class SsrRequestTests
    {
        struct Sample
        {
            public uint slot, frame, reason, pixel;
            public Vector3 position; public float roughness;
            public Vector3 normal; public float tMin;
            public Vector3 direction; public float tMax;
            public float ss, request; public uint material, instance;
        }
        [Test] public void ZeroBudgetDoesNotRecordOrAllocate()
        {
            using(var q=new SsrRequestQueue(0)) {
                q.Record(null,null,-1,17,9,0);
                Assert.IsNull(q.originals); Assert.IsNull(q.requests); Assert.IsNull(q.counters);
            }
        }
        [UnityTest] public IEnumerator Cold() => Check(false,0);
        [UnityTest] public IEnumerator Mixed() => Check(true,0);
        [UnityTest] public IEnumerator Rotated() => Check(true,7);

        [UnityTest] public IEnumerator GgxSequence() => Check(true,7,true);
        [UnityTest] public IEnumerator NoCandidates() => Check(false,0,false,true);

        static IEnumerator Check(bool history,int frame,bool pbr=false,bool empty=false)
        {
            const int W=35,H=19,w=17,h=9,k=11;
            var cs=Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.unity.render-pipelines.universal/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute"));
            var textures=new List<Texture2D>();
            Texture2D Tex(int x,int y,System.Func<int,int,Color> f) {
                var tex=new Texture2D(x,y,TextureFormat.RGBAFloat,false,true); textures.Add(tex);
                var pixels=new Color[x*y]; for(int j=0;j<y;j++) for(int i=0;i<x;i++) pixels[i+j*x]=f(i,j);
                tex.SetPixels(pixels); tex.Apply(); return tex;
            }
            using(var cmd=new CommandBuffer()) using(var q=new SsrRequestQueue(k,0)) try {
                if (pbr) cs.DisableKeyword("SSR_APPROX"); else cs.EnableKeyword("SSR_APPROX");
                cs.EnableKeyword("SSR_HALF_RES");
                int kernel=cs.FindKernel(history?"R1Original":"R1Cold");
                cmd.SetGlobalMatrix("unity_MatrixInvVP", Matrix4x4.identity);
                cmd.SetComputeVectorParam(cs,"_SsrCameraPositionWS",new Vector4(0,0,3,0));
                cmd.SetComputeVectorParam(cs,"_SsrPbrFlags",new Vector4(frame,0,0,0));
                cmd.SetComputeVectorParam(cs,"_SsrScreenSize",new Vector4(W,H,1f/W,1f/H));
                cmd.SetComputeVectorParam(cs,"_SsrTraceSize",new Vector4(w,h,1f/w,1f/h));
                cmd.SetComputeVectorParam(cs,"_SsrRoughnessFadeParams",new Vector4(2,1,.5f,4));
                cmd.SetComputeVectorParam(cs,"_SsrColorPyramidScaleAndLimitPrev", Vector4.one);
                cmd.SetComputeTextureParam(cs,kernel,"_SsrDepthTexture",Tex(W,H,(x,y)=>Color.white*(empty || x==0?0:.5f)));
                cmd.SetComputeTextureParam(cs,kernel,"_SsrNormalTexture",Tex(W,H,(x,y)=>new Color(0,0,1,x==2?.82499f:x==4?.875f:x==10?.825f:1)));
                if(history) {
                    cmd.SetComputeTextureParam(cs,kernel,"_SsrHitPointTextureRW",Tex(w,h,(x,y)=>new Color(x==2?.5f:x==3?.05f:x==4?.8f:99,.5f,0,0)));
                    cmd.SetComputeTextureParam(cs,kernel,"_SsrMotionTexture",Tex(W,H,(x,y)=>new Color(x>W*.7f?2:0,0,0,0)));
                    cmd.SetComputeTextureParam(cs,kernel,"_SsrColorPyramid",Tex(W,H,(x,y)=>Color.black));
                }
                q.Record(cmd,cs,kernel,w,h,frame); Graphics.ExecuteCommandBuffer(cmd);
                var rc=AsyncGPUReadback.Request(q.counters); var ro=AsyncGPUReadback.Request(q.originals); var rq=AsyncGPUReadback.Request(q.requests);
                while(!rc.done || !ro.done || !rq.done) yield return null;
                Assert.IsFalse(rc.hasError || ro.hasError || rq.hasError);
                var counts=rc.GetData<uint>(); var originals=ro.GetData<Sample>(); var queue=rq.GetData<Sample>();
                uint[] golden=new uint[11]; var expected=new List<int>();
                for(int i=0;i<w*h;i++) {
                    int x=i%w,y=i/w,sx=x*W/w,sy=y*H/h;
                    int reason=empty || x==0?0:x==1?1:!history?3:x==2?2:x==3?6:x==4?5:4;
                    var s=originals[i]; Assert.AreEqual(reason,s.reason,"slot "+i); Assert.AreEqual(i,s.slot); Assert.AreEqual(sy*W+sx,s.pixel);
                    golden[4+reason]++; if(reason>=2) golden[0]++; if(reason>=3) golden[1]++;
                    if(reason>=2) {
                        var pos=new Vector3((sx+.5f)*2/W-1,1-(sy+.5f)*2/H,.5f);
                        Assert.Less((s.position-pos).magnitude,.0001f);
                        var dir=Vector3.Reflect((pos-new Vector3(0,0,3)).normalized,Vector3.forward);
                        if (!pbr) Assert.Less((s.direction-dir).magnitude,.0001f);
                        else {
                            Assert.AreEqual(1,s.direction.magnitude,.0001f); Assert.Greater(s.direction.z,0);
                            if (x==2) Assert.Greater((s.direction-dir).magnitude,.0001f,"rough GGX must not use the mirror fallback");
                        }
                        Assert.AreEqual(frame,s.frame);
                        Assert.AreEqual(.002f,s.tMin); Assert.AreEqual(50f,s.tMax); Assert.AreEqual(uint.MaxValue,s.instance);
                        float fade=x==2?.5f:x==5?.216f:1f;
                        Assert.AreEqual(reason==2?fade:reason==6?.352f:0,s.ss,.0001f);
                        Assert.AreEqual(reason==2?0:reason==6?.648f:fade,s.request,.0001f);
                    }
                }
                for(int j=0;j<6;j++) {
                    int t=(frame+j)%6,n=0;
                    for(int l=0;l<64 && n<8 && expected.Count<k;l++) {
                        int lane=(l+frame%6+frame/6+t*17)%64,x=t%3*8+lane%8,y=t/3*8+lane/8;
                        if(empty || x>=w || y>=h || x<2 || (history && x==2)) continue;
                        expected.Add(y*w+x); n++;
                    }
                }
                golden[2]=(uint)expected.Count; golden[3]=golden[1]-golden[2];
                CollectionAssert.AreEqual(golden,counts.ToArray());
                for(int i=0;i<expected.Count;i++) Assert.AreEqual(originals[expected[i]],queue[i]);
                TestContext.Out.WriteLine("R1 counts: "+string.Join(",",golden)+"; slots="+string.Join(",",expected));
            } finally { foreach(var t in textures) Object.DestroyImmediate(t); Object.DestroyImmediate(cs); }
        }

        // SsrPbrHwMerge folds the screen-space reprojection and the resolved hardware fill into one
        // un-premultiplied sample before the PBR reflection denoiser. The share math must match
        // R2Composite: the domain gate (reason) discards the fill outside the hybrid set, the
        // uncovered share clamps it and invalid history (zero weight, negative or non-finite
        // radiance) must never contribute.
        [UnityTest] public IEnumerator PbrHwMerge()
        {
            const int w = 4, h = 2;
            var cs = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.unity.render-pipelines.universal/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute"));
            var owned = new List<Object> { cs };
            Texture2D Make(int x, int y, System.Func<int, int, Color> f)
            {
                var tex = new Texture2D(x, y, TextureFormat.RGBAFloat, false, true);
                var pixels = new Color[x * y];
                for (int j = 0; j < y; j++) for (int i = 0; i < x; i++) pixels[i + j * x] = f(i, j);
                tex.SetPixels(pixels); tex.Apply(); owned.Add(tex); return tex;
            }
            var output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                { enableRandomWrite = true, useMipMap = false };
            output.Create(); owned.Add(output);

            // Row 0: domain gate, full coverage, partial coverage, no request.
            // Row 1: empty history, negative history, non-finite history, pure hardware coverage.
            float Ss(int i) => i == 3 ? 1f : i == 7 ? 0f : i == 1 || i == 4 || i == 5 || i == 6 ? 0.25f : 0.5f;
            Color Hw(int i) => i == 4 ? new Color(0, 0, 0, 0) : i == 5 ? new Color(-1, 2, 2, 1) :
                i == 6 ? new Color(float.NaN, 2, 2, 1) : i == 7 ? new Color(4, 4, 4, 1) : new Color(2, 2, 2, 1);

            using (var q = new SsrRequestQueue(w * h, 0))
            using (var cmd = new CommandBuffer())
            {
                q.originals = new ComputeBuffer(w * h, 80);
                var samples = new Sample[w * h];
                void Set(int i, uint reason, float request) { samples[i].slot = (uint)i; samples[i].reason = reason; samples[i].request = request; samples[i].ss = Ss(i); }
                Set(0, 0, 0f); Set(1, 4, 1f); Set(2, 6, 0.5f); Set(3, 2, 0f);
                Set(4, 4, 1f); Set(5, 4, 1f); Set(6, 4, 1f); Set(7, 4, 1f);
                q.originals.SetData(samples);

                int kernel = cs.FindKernel("SsrPbrHwMerge");
                cmd.SetComputeVectorParam(cs, "_SsrTraceSize", new Vector4(w, h, 1f / w, 1f / h));
                cmd.SetComputeBufferParam(cs, kernel, "_R1Original", q.originals);
                cmd.SetComputeTextureParam(cs, kernel, "_SsrMergeSource",
                    Make(w, h, (x, y) => { int i = y * w + x; return new Color(1, 1, 1, Ss(i)); }));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrHwHistory",
                    Make(w, h, (x, y) => Hw(y * w + x)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrOutputTextureRW", output);
                cmd.DispatchCompute(cs, kernel, (w + 7) / 8, (h + 7) / 8, 1);
                Graphics.ExecuteCommandBuffer(cmd);

                var readback = AsyncGPUReadback.Request(output, 0, TextureFormat.RGBAFloat);
                while (!readback.done) yield return null;
                Assert.IsFalse(readback.hasError);
                var data = readback.GetData<Vector4>();
                // rgb = (s*ss + hw*hwRgb) / a, a = s + hw, hw = min(1 - s, requestShare, history.a).
                var expected = new[]
                {
                    new Vector4(1f, 1f, 1f, 0.5f),   // reason 0: fill outside the domain
                    new Vector4(1.75f, 1.75f, 1.75f, 1f),
                    new Vector4(1.5f, 1.5f, 1.5f, 1f),
                    new Vector4(1f, 1f, 1f, 1f),     // requestShare 0: full screen-space coverage
                    new Vector4(1f, 1f, 1f, 0.25f),  // zero history weight
                    new Vector4(1f, 1f, 1f, 0.25f),  // negative history radiance
                    new Vector4(1f, 1f, 1f, 0.25f),  // non-finite history radiance
                    new Vector4(4f, 4f, 4f, 1f),     // no screen-space sample: the fill is the sample
                };
                for (int i = 0; i < expected.Length; i++)
                {
                    var v = data[i];
                    Assert.AreEqual(expected[i].x, v.x, 1e-3f, "rgb.r slot " + i);
                    Assert.AreEqual(expected[i].y, v.y, 1e-3f, "rgb.g slot " + i);
                    Assert.AreEqual(expected[i].z, v.z, 1e-3f, "rgb.b slot " + i);
                    Assert.AreEqual(expected[i].w, v.w, 1e-3f, "weight slot " + i);
                }
                TestContext.Out.WriteLine("R2 PBR merge: " + string.Join("; ", data.ToArray()));
            }
            foreach (var o in owned) Object.DestroyImmediate(o);
        }

        // The PBR reprojection gathers around the centre sample only when that sample hit, so a missed
        // centre used to leave a one pixel hole until the hardware fill served it. It now reuses the
        // Approximation same-surface fill, which must only borrow from neighbours on its own surface.
        [UnityTest] public IEnumerator PbrReprojectMissFillsFromNeighbours()
        {
            const int W = 32, H = 16;
            const float flat = -47f, step = -46f;
            var cs = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.unity.render-pipelines.universal/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute"));
            var owned = new List<Object> { cs };
            Texture2D Make(int x, int y, System.Func<int, int, Color> f)
            {
                var tex = new Texture2D(x, y, TextureFormat.RGBAFloat, false, true);
                var pixels = new Color[x * y];
                for (int j = 0; j < y; j++) for (int i = 0; i < x; i++) pixels[i + j * x] = f(i, j);
                tex.SetPixels(pixels); tex.Apply(); owned.Add(tex); return tex;
            }
            var output = new RenderTexture(W, H, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                { enableRandomWrite = true, useMipMap = false };
            output.Create(); owned.Add(output);

            // Two columns of missed centre samples. The x == 8 column lies on the flat left half, so
            // its two horizontal neighbours are valid hits on the same surface. The x == 24 column is
            // an isolated strip one world unit closer than the surface around it, so its neighbours
            // are a depth step away and nothing may be borrowed. Misses are asserted per column, which
            // keeps the expectations independent of the readback row order.
            bool Miss(int x) => x == 8 || x == 24;

            using (var cmd = new CommandBuffer())
            {
                cs.DisableKeyword("SSR_APPROX");
                cs.EnableKeyword("SSR_HALF_RES");
                int kernel = cs.FindKernel("ScreenSpaceReflectionsReprojection");
                cmd.SetGlobalMatrix("unity_MatrixInvVP", Matrix4x4.identity);
                cmd.SetComputeVectorParam(cs, "_SsrCameraPositionWS", new Vector4(0, 0, 3, 0));
                cmd.SetComputeVectorParam(cs, "_SsrScreenSize", new Vector4(W, H, 1f / W, 1f / H));
                cmd.SetComputeVectorParam(cs, "_SsrTraceSize", new Vector4(W, H, 1f / W, 1f / H));
                cmd.SetComputeVectorParam(cs, "_SsrRoughnessFadeParams", new Vector4(2, 1, 0.5f, 4));
                cmd.SetComputeVectorParam(cs, "_SsrColorPyramidScaleAndLimitPrev", Vector4.one);
                // Identity invVP maps a pixel to world x in [-1, 1] and world z to the stored depth.
                cmd.SetComputeTextureParam(cs, kernel, "_SsrDepthTexture",
                    Make(W, H, (x, y) => new Color(x == 24 || x < 16 ? flat : step, 0, 0, 0)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrNormalTexture", Make(W, H, (x, y) => new Color(0, 0, 1, 1)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrHitPointTextureRW",
                    Make(W, H, (x, y) => Miss(x) ? new Color(99, 0, 0, 0) : new Color(0.5f, 0.5f, 0, 0)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrMotionTexture", Make(W, H, (x, y) => Color.clear));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrColorPyramid", Make(W, H, (x, y) => new Color(0.25f, 0.5f, 0.75f, 1)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrReprojectTextureRW", output);
                cmd.DispatchCompute(cs, kernel, (W + 7) / 8, (H + 7) / 8, 1);
                Graphics.ExecuteCommandBuffer(cmd);

                var readback = AsyncGPUReadback.Request(output, 0, TextureFormat.RGBAFloat);
                while (!readback.done) yield return null;
                Assert.IsFalse(readback.hasError);
                var data = readback.GetData<Vector4>();
                // A missed centre has two accepted horizontal neighbours with edge fade 1 and
                // perceptual roughness 0, so the fill weight is saturate(2 * 1 / 4) and the radiance is
                // the pyramid colour every accepted neighbour reads at its (0.5, 0.5) hit.
                for (int y = 0; y < H; y++)
                {
                    var filled = data[y * W + 8];
                    Assert.AreEqual(0.25f, filled.x, 1e-3f, "fill r row " + y);
                    Assert.AreEqual(0.5f, filled.y, 1e-3f, "fill g row " + y);
                    Assert.AreEqual(0.75f, filled.z, 1e-3f, "fill b row " + y);
                    Assert.AreEqual(0.5f, filled.w, 1e-3f, "fill weight row " + y);
                    var isolated = data[y * W + 24];
                    Assert.AreEqual(0f, isolated.x, 1e-3f, "depth step r row " + y);
                    Assert.AreEqual(0f, isolated.y, 1e-3f, "depth step g row " + y);
                    Assert.AreEqual(0f, isolated.z, 1e-3f, "depth step b row " + y);
                    Assert.AreEqual(0f, isolated.w, 1e-3f, "depth step weight row " + y);
                }
                TestContext.Out.WriteLine("PBR reproject fill: " + data[8] + "; depth step: " + data[24]);
            }
            foreach (var o in owned) Object.DestroyImmediate(o);
        }
    }
}
