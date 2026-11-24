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
    }
}
