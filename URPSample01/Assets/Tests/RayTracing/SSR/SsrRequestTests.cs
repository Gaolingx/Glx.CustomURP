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
            public uint slot, reason;
            public Vector3 position; public float roughness;
            public Vector3 normal; public float tMin;
            public Vector3 direction; public float tMax;
            public float ss, request;
        }
        struct Coverage
        {
            public float ss, request; public uint reason;
        }
        struct Hit
        {
            public Vector3 radiance; public float hitT; public uint flags; public uint mode;
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
                cmd.SetComputeFloatParam(cs,"_SsrRayLength",37.5f);
                cmd.SetComputeVectorParam(cs,"_SsrColorPyramidScaleAndLimitPrev", Vector4.one);
                cmd.SetComputeTextureParam(cs,kernel,"_SsrDepthTexture",Tex(W,H,(x,y)=>Color.white*(empty || x==0?0:.5f)));
                cmd.SetComputeTextureParam(cs,kernel,"_SsrNormalTexture",Tex(W,H,(x,y)=>new Color(0,0,1,x==2?.82499f:x==4?.875f:x==10?.825f:1)));
                if(history) {
                    cmd.SetComputeTextureParam(cs,kernel,"_SsrHitPointTextureRW",Tex(w,h,(x,y)=>new Color(x==2?.5f:x==3?.05f:x==4?.8f:99,.5f,0,0)));
                    cmd.SetComputeTextureParam(cs,kernel,"_SsrMotionTexture",Tex(W,H,(x,y)=>new Color(x>W*.7f?2:0,0,0,0)));
                    cmd.SetComputeTextureParam(cs,kernel,"_SsrColorPyramid",Tex(W,H,(x,y)=>Color.black));
                }
                q.Record(cmd,cs,kernel,w,h,frame); Graphics.ExecuteCommandBuffer(cmd);
                var rc=AsyncGPUReadback.Request(q.counters); var ro=AsyncGPUReadback.Request(q.originals);
                var rq=AsyncGPUReadback.Request(q.requests); var rg=AsyncGPUReadback.Request(q.coverage);
                while(!rc.done || !ro.done || !rq.done || !rg.done) yield return null;
                Assert.IsFalse(rc.hasError || ro.hasError || rq.hasError || rg.hasError);
                var counts=rc.GetData<uint>(); var originals=ro.GetData<Sample>(); var queue=rq.GetData<Sample>();
                var coverage=rg.GetData<Coverage>();
                uint[] golden=new uint[11]; var expected=new List<int>();
                for(int i=0;i<w*h;i++) {
                    int x=i%w,y=i/w,sx=x*W/w,sy=y*H/h;
                    int reason=empty || x==0?0:x==1?1:!history?3:x==2?2:x==3?6:x==4?5:4;
                    var cov=coverage[i];
                    Assert.AreEqual(reason,cov.reason,"coverage reason "+i);
                    golden[4+reason]++; if(reason>=2) golden[0]++; if(reason>=3) golden[1]++;
                    if(reason>=2) {
                        var s=originals[i]; Assert.AreEqual(reason,s.reason,"slot "+i); Assert.AreEqual(i,s.slot);
                        Assert.AreEqual(s.ss,cov.ss,"coverage ss "+i);
                        Assert.AreEqual(s.request,cov.request,"coverage request "+i);
                        var pos=new Vector3((sx+.5f)*2/W-1,1-(sy+.5f)*2/H,.5f);
                        Assert.Less((s.position-pos).magnitude,.0001f);
                        var dir=Vector3.Reflect((pos-new Vector3(0,0,3)).normalized,Vector3.forward);
                        if (!pbr) Assert.Less((s.direction-dir).magnitude,.0001f);
                        else {
                            Assert.AreEqual(1,s.direction.magnitude,.0001f); Assert.Greater(s.direction.z,0);
                            if (x==2) Assert.Greater((s.direction-dir).magnitude,.0001f,"rough GGX must not use the mirror fallback");
                        }
                        Assert.AreEqual(.002f,s.tMin); Assert.AreEqual(37.5f,s.tMax);
                        float fade=x==2?.5f:x==5?.216f:1f;
                        // B6.1: the x==3 hit lands next to the background column, so the silhouette
                        // confidence is 0 and its share moves to the hardware request.
                        bool silhouetteZero=x==3;
                        Assert.AreEqual(silhouetteZero?0f:reason==2?fade:reason==6?.352f:0,s.ss,.0001f);
                        Assert.AreEqual(reason==2?0:reason==6?(silhouetteZero?fade:.648f):fade,s.request,.0001f);
                    } else {
                        // Only reason >= 2 publishes the 64-byte geometry record; the coverage
                        // record is written for every slot and is zero outside the hybrid domain.
                        Assert.AreEqual(0f,cov.ss,1e-6f,"coverage ss "+i);
                        Assert.AreEqual(0f,cov.request,1e-6f,"coverage request "+i);
                    }
                }
                // R1Rank permutes the pixel each lane inspects and R1Scatter writes the first quota
                // wanted lanes in lane order, tile by tile in the rotating tile order. B6.1 made the
                // quota per tile (one reserved slot plus a wanted-weighted share of the remainder),
                // so the mirror computes the same per-tile quota and walks with that stride.
                int gx=(w+7)/8, tiles=gx*((h+7)/8);
                int[] wantedPerTile=new int[tiles];
                for(int i=0;i<w*h;i++) {
                    int x=i%w,y=i/w;
                    int reason=empty || x==0?0:x==1?1:!history?3:x==2?2:x==3?6:x==4?5:4;
                    if(reason>=3) wantedPerTile[(y/8)*gx+x/8]++;
                }
                int totalWanted=0; foreach(var c in wantedPerTile) totalWanted+=c;
                int QuotaFor(int t) {
                    int wn=wantedPerTile[t];
                    if(wn==0) return 0;
                    if(k<=tiles) return Mathf.Min(Mathf.Max(1,k/tiles),wn);
                    int quota=1+(totalWanted>0?(k-tiles)*wn/totalWanted:0);
                    return Mathf.Min(quota,wn);
                }
                for(int j=0;j<tiles;j++) {
                    int t=(frame+j)%tiles,n=0,quota=QuotaFor(t);
                    // The window strides by the tile's own served count.
                    int stride=quota;
                    for(int lane=0;lane<64 && n<quota && expected.Count<k;lane++) {
                        int order=(lane+stride*frame+t*17)%64,x=t%gx*8+order%8,y=t/gx*8+order/8;
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
                q.coverage = new ComputeBuffer(w * h, 12);
                var coverage = new Coverage[w * h];
                void Set(int i, uint reason, float request) { coverage[i].reason = reason; coverage[i].request = request; coverage[i].ss = Ss(i); }
                Set(0, 0, 0f); Set(1, 4, 1f); Set(2, 6, 0.5f); Set(3, 2, 0f);
                Set(4, 4, 1f); Set(5, 4, 1f); Set(6, 4, 1f); Set(7, 4, 1f);
                q.coverage.SetData(coverage);

                int kernel = cs.FindKernel("SsrPbrHwMerge");
                cmd.SetComputeVectorParam(cs, "_SsrTraceSize", new Vector4(w, h, 1f / w, 1f / h));
                cmd.SetComputeBufferParam(cs, kernel, "_R1Coverage", q.coverage);
                cmd.SetComputeTextureParam(cs, kernel, "_SsrMergeSource",
                    Make(w, h, (x, y) => { int i = y * w + x; return new Color(1, 1, 1, Ss(i)); }));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrHwHistory",
                    Make(w, h, (x, y) => Hw(y * w + x)));
                // B6.1: the merge kernel now reads the receiver geometry for the neighbourhood
                // borrow, so every dispatch has to bind it even when no slot needs the borrow.
                cmd.SetComputeTextureParam(cs, kernel, "_SsrDepthTexture",
                    Make(w, h, (x, y) => new Color(0.5f, 0, 0, 0)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrNormalTexture",
                    Make(w, h, (x, y) => new Color(0, 0, 1, 1)));
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
        // B6.1: a slot with no coverage (screen-space miss not yet served by the hardware fill)
        // borrows a partial sample from its same-surface cardinal neighbours, so the coverage
        // boundary is a ramp before the denoiser. A depth step must not leak radiance across it.
        [UnityTest] public IEnumerator PbrHwMergeFillsMissedFromNeighbours()
        {
            const int W = 8, H = 6;
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

            // Column 2 is a missed centre on the flat surface (two horizontal neighbours are valid
            // full-coverage samples). The strip from column 6 on is one world unit closer and
            // entirely uncovered, so no neighbour can be borrowed across the depth step.
            bool UncoveredColumn(int x) => x >= 6;

            using (var q = new SsrRequestQueue(W * H, 0))
            using (var cmd = new CommandBuffer())
            {
                q.coverage = new ComputeBuffer(W * H, 12);
                var coverage = new Coverage[W * H];
                for (int i = 0; i < coverage.Length; i++)
                {
                    coverage[i].reason = 2; coverage[i].request = 0f; coverage[i].ss = 1f;
                }
                coverage[2 * W + 2].reason = 4; coverage[2 * W + 2].request = 1f; coverage[2 * W + 2].ss = 0f;
                coverage[2 * W + 6].reason = 4; coverage[2 * W + 6].request = 1f; coverage[2 * W + 6].ss = 0f;
                q.coverage.SetData(coverage);

                int kernel = cs.FindKernel("SsrPbrHwMerge");
                cmd.SetGlobalMatrix("unity_MatrixInvVP", Matrix4x4.identity);
                cmd.SetComputeVectorParam(cs, "_SsrCameraPositionWS", new Vector4(0, 0, 3, 0));
                cmd.SetComputeVectorParam(cs, "_SsrScreenSize", new Vector4(W, H, 1f / W, 1f / H));
                cmd.SetComputeVectorParam(cs, "_SsrTraceSize", new Vector4(W, H, 1f / W, 1f / H));
                cmd.SetComputeBufferParam(cs, kernel, "_R1Coverage", q.coverage);
                cmd.SetComputeTextureParam(cs, kernel, "_SsrMergeSource",
                    Make(W, H, (x, y) => UncoveredColumn(x) ? new Color(0, 0, 0, 0) : new Color(2, 2, 2, x == 2 ? 0f : 1f)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrHwHistory", Make(W, H, (x, y) => Color.clear));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrDepthTexture",
                    Make(W, H, (x, y) => new Color(x >= 6 ? step : flat, 0, 0, 0)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrNormalTexture", Make(W, H, (x, y) => new Color(0, 0, 1, 1)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrOutputTextureRW", output);
                cmd.DispatchCompute(cs, kernel, (W + 7) / 8, (H + 7) / 8, 1);
                Graphics.ExecuteCommandBuffer(cmd);

                var readback = AsyncGPUReadback.Request(output, 0, TextureFormat.RGBAFloat);
                while (!readback.done) yield return null;
                Assert.IsFalse(readback.hasError);
                var data = readback.GetData<Vector4>();
                // Two accepted neighbours with a = 1 each: radiance is the borrowed 2.0, alpha is
                // 0.5 * mean coverage (the probe keeps the other half).
                var filled = data[2 * W + 2];
                Assert.AreEqual(2f, filled.x, 1e-3f, "fill r");
                Assert.AreEqual(2f, filled.y, 1e-3f, "fill g");
                Assert.AreEqual(2f, filled.z, 1e-3f, "fill b");
                Assert.AreEqual(0.5f, filled.w, 1e-3f, "fill alpha");
                var isolated = data[2 * W + 6];
                Assert.AreEqual(0f, isolated.x, 1e-3f, "depth step r");
                Assert.AreEqual(0f, isolated.w, 1e-3f, "depth step alpha");
                TestContext.Out.WriteLine("merge fill: " + filled + "; depth step: " + isolated);
            }
            foreach (var o in owned) Object.DestroyImmediate(o);
        }

        // P3 reads the SS hit radiance from _R2Hits. When the hit surface has no current-frame
        // GBuffer source (a forward-only material, or a forward renderer that skipped SsrHitSs) no
        // SS flag is published; the reproject must keep the R1 coverage and the previous-colour
        // radiance instead of zeroing the screen-space share.
        [UnityTest]
        public IEnumerator P3FallbackKeepsPreviousColourWhenTheSsHitIsMissing()
        {
            const int W = 4, H = 2;
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

            using (var coverage = new ComputeBuffer(W * H, 12))
            using (var hits = new ComputeBuffer(W * H, 24))
            using (var cmd = new CommandBuffer())
            {
                cs.DisableKeyword("SSR_APPROX");
                cs.EnableKeyword("_GLX_SSR_SHADE_SS");
                int kernel = cs.FindKernel("ScreenSpaceReflectionsReprojection");

                var cov = new Coverage[W * H];
                var hitData = new Hit[W * H];
                for (int i = 0; i < cov.Length; i++) { cov[i].reason = 2; cov[i].ss = 0.5f; cov[i].request = 0f; }
                // Slot 0 keeps a published SS hit (GlxHit | SS bit); the other slots have none, so
                // they must fall back to the pyramid instead of writing zero coverage. The record
                // is accepted only when its served stamp matches this dispatch.
                hitData[0].radiance = new Vector3(1f, 2f, 3f);
                hitData[0].flags = 2u | 8u;
                hitData[0].mode = 7u;
                coverage.SetData(cov);
                hits.SetData(hitData);

                cmd.SetGlobalMatrix("unity_MatrixInvVP", Matrix4x4.identity);
                cmd.SetComputeVectorParam(cs, "_SsrCameraPositionWS", new Vector4(0, 0, 3, 0));
                cmd.SetComputeVectorParam(cs, "_SsrScreenSize", new Vector4(W, H, 1f / W, 1f / H));
                cmd.SetComputeVectorParam(cs, "_SsrTraceSize", new Vector4(W, H, 1f / W, 1f / H));
                cmd.SetComputeVectorParam(cs, "_SsrRoughnessFadeParams", new Vector4(2, 1, 0.5f, 4));
                cmd.SetComputeVectorParam(cs, "_SsrPyramidParams", Vector4.zero);
                cmd.SetComputeVectorParam(cs, "_SsrColorPyramidScaleAndLimitPrev", Vector4.one);
                cmd.SetComputeIntParam(cs, "_R2HitFrame", 7);
                cmd.SetComputeBufferParam(cs, kernel, "_R1Coverage", coverage);
                cmd.SetComputeBufferParam(cs, kernel, "_R2Hits", hits);
                cmd.SetComputeTextureParam(cs, kernel, "_SsrNormalTexture", Make(W, H, (x, y) => new Color(0, 0, 1, 1)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrDepthTexture", Make(W, H, (x, y) => new Color(0.5f, 0, 0, 0)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrHitPointTextureRW", Make(W, H, (x, y) => new Color(0.5f, 0.5f, 0, 0)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrMotionTexture", Make(W, H, (x, y) => Color.clear));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrColorPyramid", Make(W, H, (x, y) => new Color(0.25f, 0.5f, 0.75f, 1)));
                cmd.SetComputeTextureParam(cs, kernel, "_SsrReprojectTextureRW", output);
                cmd.DispatchCompute(cs, kernel, (W + 7) / 8, (H + 7) / 8, 1);
                Graphics.ExecuteCommandBuffer(cmd);

                var readback = AsyncGPUReadback.Request(output, 0, TextureFormat.RGBAFloat);
                while (!readback.done) yield return null;
                Assert.IsFalse(readback.hasError);
                var data = readback.GetData<Vector4>();
                // Slot 0 publishes the current-frame SS radiance; the rest sample the pyramid with
                // edge fade 1 and keep the R1 share as the weight.
                for (int i = 0; i < data.Length; i++)
                {
                    float r = i == 0 ? 1f : 0.25f;
                    float g = i == 0 ? 2f : 0.5f;
                    float b = i == 0 ? 3f : 0.75f;
                    Assert.AreEqual(r, data[i].x, 1e-3f, "radiance r " + i);
                    Assert.AreEqual(g, data[i].y, 1e-3f, "radiance g " + i);
                    Assert.AreEqual(b, data[i].z, 1e-3f, "radiance b " + i);
                    Assert.AreEqual(0.5f, data[i].w, 1e-3f, "weight " + i);
                }
                TestContext.Out.WriteLine("P3 fallback: " + data[0]);
            }
            foreach (var o in owned) Object.DestroyImmediate(o);
        }
    }
}
