// Scratch friend assembly only (same asmdef as T3Player). No renderer feature or scene light.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

public sealed class T3bPlayer : MonoBehaviour
{
    const string Package = "Packages/com.unity.render-pipelines.universal/Shaders/";
    const RayTracingRequests Demand = RayTracingRequests.GI;
    const int Layer = 31, Opaque = 0, Cut = 1, Rear = 2, Transparent = 3, Specular = 4;
    static readonly string[] Names = { "Opaque", "Cutout", "Rear", "Transparent", "Specular" };
    static readonly string[] Maps = { "_BaseMap", "_BumpMap", "_MetallicGlossMap", "_EmissionMap" };
    static readonly Vector2 ScaleUV = new Vector2(1.64f, 1.23f), OffsetUV = new Vector2(-.2f, .09f);
    static readonly Vector3 N = new Vector3(0,.24f,-1).normalized;
    static readonly Vector3 TopN = new Vector3(0,-.9f,-1).normalized;
    [SerializeField] Material[] materials;
    [SerializeField] RayTracingShader library, probe;

    [StructLayout(LayoutKind.Sequential), Serializable] struct Request { public Vector4 originTMin, directionTMax; }
    [StructLayout(LayoutKind.Sequential)] struct Payload { public Vector3 rgb; public float t; public uint flags, mode; }
    [Serializable] class Gate { public string name, decision; public bool accepted, hasSnapshot; public int builds, instances, liveResources, buffersAllocated, submittedDispatches; }
    [Serializable] class Support { public string renderer, shader, classification, keywords; public string[] textures; public int queue, pass; public bool expectedSupported; }
    [Serializable] class Row
    {
        public string name, batch, nonFiniteReadback, comparison;
        public Request input; public Vector3 expectedRgb, actualRgb, relativeRgbError;
        public Vector4 raster, foregroundRaster; public bool sampledRaster, sampledForeground, finite, passed;
        public float actualT, expectedT, absTError, normalDegrees;
        public uint actualFlags, expectedFlags, actualMode, expectedMode, ahsCount, rawMode;
        public int minimumAhs;
    }
    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(), adapter = SystemInfo.graphicsDeviceName,
            driver = SystemInfo.graphicsDeviceVersion, colorSpace = QualitySettings.activeColorSpace.ToString(), error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed, released, gpuTimingMeasured;
        public int buildCount, instances, disabledSubmeshes, buffersAllocated, buffersReleased, submittedDispatches, submittedRequests,
            failedVectors, nonFiniteComponents, liveSceneResourcesAfterRelease, subscribersAfterRelease, instancesAfterRelease, payloadBytes, markerGpuBlocks;
        public string marker = "Glx Basic Hit Trace", markerNote = "No GPU measurement available";
        public float markerGpuMs, rgbTolerance = .03f, tToleranceMeters = .001f, normalToleranceDegrees = 2;
        public List<Gate> gates = new List<Gate>(); public List<Row> vectors = new List<Row>(); public List<Support> materials = new List<Support>();
    }
    static void Check(bool test, string why) { if (!test) throw new InvalidOperationException(why); }
    static string Arg(string key)
    {
        string[] a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a,key);
        if (i < 0 || i+1 >= a.Length) throw new ArgumentException(key); return a[i+1];
    }
    static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    static Vector2 UV(Vector3 p) => new Vector2(.46f+.31f*p.x+.06f*p.y,.42f+.07f*p.x+.36f*p.y);
    // Four-by-four constant cells, independent patterns per channel. Block-aligned emission cells
    // use exactly representable RGB565 endpoints so the CPU oracle doesn't need a BC decoder.
    static Color32 Texel(int mat, int map, int x, int y)
    {
        int k = (3*x+5*y+mat*3)%8;
        if (map == 0) return new Color32((byte)(35+24*k),(byte)(210-18*((x+2*y+mat)%8)),(byte)(42+22*((2*x+y+mat)%8)),(byte)(x<2 ? 140 : 215));
        if (map == 1)
        {
            byte r = (byte)(65+16*k), g = (byte)(63+18*((x+3*y+mat)%8));
            float nx=r/127.5f-1, ny=g/127.5f-1;
            return new Color32(r,g,(byte)Mathf.RoundToInt(127.5f*(1+Mathf.Sqrt(1-nx*nx-ny*ny))),255);
        }
        if (map == 2) return new Color32((byte)(28+26*k),0,0,(byte)(215-22*((x+y*3+mat)%8)));
        int a=5+3*k, b=12+5*((x+2*y+mat)%8), c=27-3*((2*x+y+mat)%8);
        return new Color32((byte)((a<<3)|(a>>2)),(byte)((b<<2)|(b>>4)),(byte)((c<<3)|(c>>2)),255);
    }
    struct Shot
    {
        public string name; public int target, ahs; public bool clipped; public Request ray;
        public Shot(string name,float x,float y,int target,int ahs=0,bool clipped=false,float max=10)
        { this.name=name; this.target=target; this.ahs=ahs; this.clipped=clipped;
          ray=new Request { originTMin=new Vector4(x,y,0,.002f),directionTMax=new Vector4(0,0,1,max) }; }
    }
    static Vector3 Analytic(int mode,int id, Matrix4x4 world, Vector3 origin)
    {
        origin.z=world.m23;
        var uv=Vector2.Scale(UV(world.inverse.MultiplyPoint3x4(origin)),ScaleUV)+OffsetUV;
        int x=Mathf.FloorToInt(4*Mathf.Repeat(uv.x,1)), y=Mathf.FloorToInt(4*Mathf.Repeat(uv.y,1));
        Color b=Texel(id,0,x,y), n=Texel(id,1,x,y), g=Texel(id,2,x,y), e=Texel(id,3,x,y);
        if(mode==16) return new Vector3(b.r,b.g,b.b);
        if(mode==18) return new Vector3(id==Rear?.17f:g.r,id==Rear?.68f:g.a*.68f,b.a*.88f);
        if(mode==19) return id==Rear?Vector3.zero:new Vector3(e.r*1.65f,e.g*.74f,e.b*1.21f);
        var local=world.inverse.MultiplyPoint3x4(origin);
        float topWeight=(local.y+1)*.5f;
        Vector3 normal=Vector3.Lerp(world.inverse.transpose.MultiplyVector(N).normalized,
            world.inverse.transpose.MultiplyVector(TopN).normalized, topWeight);
        if(id==Rear) return normal.normalized;
        Vector3 tangent=world.MultiplyVector(Vector3.right).normalized;
        float nx=n.r*2-1, ny=n.g*2-1;
        return (.72f*nx*tangent-.72f*ny*Mathf.Sign(world.determinant)*Vector3.Cross(normal,tangent)+Mathf.Sqrt(1-nx*nx-ny*ny)*normal).normalized;
    }
    static Vector3 DirectReference(int id, Matrix4x4 world, Vector3 origin, Vector3 light)
    {
        var albedo=Analytic(16,id,world,origin); var normal=Analytic(17,id,world,origin); var gloss=Analytic(18,id,world,origin);
        if(Vector3.Dot(normal,Vector3.back)<0) normal=-normal;
        var halfDir=(light+Vector3.back).normalized;
        float noH=Mathf.Clamp01(Vector3.Dot(normal,halfDir)), loH=Mathf.Clamp01(Vector3.Dot(light,halfDir));
        float rough=(1-gloss.y)*(1-gloss.y), rough2=rough*rough, d=noH*noH*(rough2-1)+1.00001f;
        float spec=rough2/(d*d*Mathf.Max(.1f,loH*loH)*(4*rough+2));
        var brdf=albedo*(.96f*(1-gloss.x))+spec*Vector3.Lerp(.04f*Vector3.one,albedo,gloss.x);
        return Vector3.Scale(brdf,new Vector3(3,2,1))*(Mathf.Clamp01(Vector3.Dot(normal,light))/Mathf.PI);
    }
    IEnumerator Start()
    {
        var r=new Report { payloadBytes=Marshal.SizeOf(typeof(Payload)) };
        var camera=new GameObject("T3b presented frames").AddComponent<Camera>(); camera.cullingMask=0;
        Application.targetFrameRate=60;
        for(int i=0;i<12;++i) yield return null;
        ProfilingSampler marker=null;
        try
        {
            Check(r.development && Application.platform==RuntimePlatform.WindowsPlayer && IntPtr.Size==8 && !string.IsNullOrEmpty(r.guid) &&
                r.api==Arg("-expectedApi") && (r.api=="Direct3D11" || r.api=="Direct3D12") && QualitySettings.activeColorSpace==ColorSpace.Linear,"Wrong player/API/color space");
            Check(probe && library && materials.Length==5 && r.payloadBytes==24 && Marshal.SizeOf(typeof(Request))==32,"Missing assets or wrong ABI");
            Run(r,camera,ref marker); Check(r.released && r.failedVectors==0,"Vector comparison or teardown failed"); r.passed=true;
        }
        catch(Exception e) { r.error=e.ToString(); }
        if(marker!=null)
        {
            for(int i=0;i<32;++i)
            {
                yield return null;
                int blocks=marker.gpuSampleCount; float ms=marker.gpuElapsedTime;
                if(blocks>0 && blocks>=r.markerGpuBlocks && Finite(ms) && ms>0)
                { r.markerGpuBlocks=blocks; r.markerGpuMs=ms; r.gpuTimingMeasured=true; r.markerNote="Single presented frame, production dispatches only"; }
            }
            marker.enableRecording=false;
        }
        Destroy(camera.gameObject);
        int code=1; try { File.WriteAllText(Arg("-result"),JsonUtility.ToJson(r,true)); code=r.passed?0:1; } finally { Application.Quit(code); }
    }
    void Run(Report r,Camera camera,ref ProfilingSampler marker)
    {
        var service=new RayTracingSceneResources(); var members=new List<RayTracingMember>();
        var owned=new List<UnityEngine.Object>(); ComputeBuffer inputs=null, results=null;
        var world=new Matrix4x4[5]; var renderers=new MeshRenderer[5]; bool classificationOK=true;
        try
        {
            var mesh=new Mesh {name="T3b front-facing quad"}; owned.Add(mesh);
            var v=new[]{new Vector3(-1,-1),new Vector3(-1,1),new Vector3(1,1),new Vector3(1,-1)};
            mesh.vertices=v; mesh.uv=Array.ConvertAll(v,UV); mesh.normals=new[]{N,TopN,TopN,N};
            var t=new Vector4(1,0,0,-1); mesh.tangents=new[]{t,t,t,t}; mesh.triangles=new[]{0,1,2,0,2,3}; mesh.RecalculateBounds();
            var positions=new[]{new Vector3(-3,0,3),new Vector3(0,0,2),new Vector3(0,0,4),new Vector3(3,0,2),new Vector3(6,0,2)};
            for(int i=0;i<materials.Length;++i)
            {
                var m=materials[i]; Check(m && m.shader.name=="Universal Render Pipeline/Lit","Missing package Lit material");
                var go=new GameObject(Names[i]); owned.Add(go); go.layer=Layer; go.transform.position=positions[i];
                if(i==Opaque) { go.transform.rotation=Quaternion.Euler(0,0,27); go.transform.localScale=new Vector3(-1.15f,.8f,1.4f); }
                world[i]=go.transform.localToWorldMatrix; go.AddComponent<MeshFilter>().sharedMesh=mesh;
                var renderer=renderers[i]=go.AddComponent<MeshRenderer>(); renderer.sharedMaterial=m; renderer.rayTracingMode=RayTracingMode.Static;
                members.Add(new RayTracingMember(renderer,(uint)(i+1),0xff,false)); // Shadow controls traverse both sides.
                var flags=RayTracingSceneInstances.Classify(m);
                var expected=i>=Transparent?RayTracingSubMeshFlags.Disabled:i==Cut?RayTracingSubMeshFlags.Enabled:RayTracingSubMeshFlags.Enabled|RayTracingSubMeshFlags.ClosestHitOnly;
                classificationOK &= flags==expected;
                r.materials.Add(new Support {renderer=Names[i],shader=m.shader.name,keywords=string.Join(" ",m.shaderKeywords),queue=m.renderQueue,pass=m.FindPass("GlxDXR"),
                    classification=flags.ToString(),expectedSupported=i<Transparent,textures=Array.ConvertAll(Maps,p=> { var tex=(Texture2D)m.GetTexture(p); return p+": "+tex.width+"x"+tex.height+" "+tex.format+" "+tex.graphicsFormat; })});
            }
            var view=new CameraData { camera=camera,cameraType=CameraType.Game,renderType=CameraRenderType.Base,cameraTargetDescriptor=new RenderTextureDescriptor(32,32) };
            service.SetRequests(Demand); RayTracingAccelerationStructure scene=null;
            bool Gate(string label,bool enabled,RayTracingDecision expected)
            {
                var decision=RayTracingSceneGate.Capture(view,enabled,true,library,materials[0]).Evaluate(Demand);
                bool ok=service.TryBuild(view,enabled,true,library,materials[0],1,1<<Layer,Demand,members,out scene);
                r.gates.Add(new Gate {name=label,decision=decision.ToString(),accepted=ok,hasSnapshot=scene!=null,instances=service.instanceCount,
                    builds=service.buildCount,liveResources=service.liveResources,buffersAllocated=r.buffersAllocated,submittedDispatches=r.submittedDispatches});
                Check(decision==expected && ok==(expected==RayTracingDecision.Accepted) && ok==(scene!=null),label+": "+decision);
                if(!ok) Check(service.instanceCount==0 && service.liveResources==0 && service.buildCount==0 && r.buffersAllocated==0 && r.submittedDispatches==0,label+" allocated RT resources");
                return ok;
            }
            Gate("Disabled",false,RayTracingDecision.Off);
            if(r.api=="Direct3D11") { Gate("D3D11 opt-in",true,RayTracingDecision.Backend); return; }
            Gate("D3D12 opt-in",true,RayTracingDecision.Accepted); r.instances=service.instanceCount; r.disabledSubmeshes=service.disabledSubmeshes;
            var shots=new[]{new Shot("Opaque A",-3.48f,-.16f,Opaque),new Shot("Opaque B",-2.57f,.24f,Opaque),new Shot("Solid",.36f,-.21f,Cut,1),
                new Shot("Hole to rear",-.43f,-.21f,Rear,1,true),new Shot("Hole to miss",-.43f,-.21f,-1,1,true,3),
                new Shot("Transparent excluded",3,.12f,-1),new Shot("Specular excluded",6,.12f,-1)};
            int count=shots.Length; inputs=new ComputeBuffer(count,32); ++r.buffersAllocated; results=new ComputeBuffer(count,24); ++r.buffersAllocated;
            inputs.SetData(Array.ConvertAll(shots,s=>s.ray)); var output=new Payload[count];
            void Poison() { for(int i=0;i<count;++i) output[i]=new Payload {t=-999,flags=uint.MaxValue,mode=uint.MaxValue}; results.SetData(output); }
            void Readback() { ++r.submittedDispatches; r.submittedRequests+=count; results.GetData(output); }
            for(int mode=16;mode<=19;++mode)
            {
                Poison(); var cmd=CommandBufferPool.Get();
                try
                {
                    cmd.SetRayTracingShaderPass(probe,"GlxDXR"); cmd.SetRayTracingAccelerationStructure(probe,Shader.PropertyToID("_GlxScene"),scene);
                    cmd.SetRayTracingBufferParam(probe,Shader.PropertyToID("_GlxRays"),inputs); cmd.SetRayTracingBufferParam(probe,Shader.PropertyToID("_GlxResults"),results);
                    cmd.SetRayTracingIntParam(probe,Shader.PropertyToID("_T3bMode"),mode); cmd.DispatchRays(probe,"T3bTrace",(uint)count,1,1,null); Graphics.ExecuteCommandBuffer(cmd);
                } finally { CommandBufferPool.Release(cmd); }
                Readback();
                for(int i=0;i<count;++i)
                {
                    Shot s=shots[i]; int id=s.target; Vector3 origin=s.ray.originTMin;
                    var row=new Row {name=s.name,batch="Material probe",comparison="Analytic and independent raster",input=s.ray,expectedMode=(uint)mode,minimumAhs=s.ahs,
                        expectedT=id<0?-1:positions[id].z,expectedFlags=id<0?1u:2u};
                    if(s.clipped) {row.sampledForeground=true; row.foregroundRaster=T3bRaster.Sample(renderers[Cut],origin,mode);}
                    if(id>=0) { row.expectedRgb=Analytic(mode,id,world[id],origin); row.sampledRaster=true; row.raster=T3bRaster.Sample(renderers[id],origin,mode); }
                    Record(r,row,output[i]);
                }
            }
            marker=GlxBasicHitTrace.sampler; marker.enableRecording=true;
            void Production(string batch,Vector3 light,bool shadows,bool dark,Payload[] baseline,bool blocked)
            {
                Poison(); GlxBasicHitTrace.Dispatch(library,scene,inputs,results,count,light,dark?Vector3.zero:new Vector3(3,2,1),shadows); Readback();
                for(int i=0;i<count;++i)
                {
                    Shot s=shots[i]; int id=s.target; bool hit=id>=0, occluded=blocked && id==Rear;
                    Vector3 emission=hit?Analytic(19,id,world[id],s.ray.originTMin):Vector3.zero;
                    bool lit=hit && !occluded && !dark;
                    var row=new Row {name=s.name,batch=batch,input=s.ray,expectedT=hit?positions[id].z:-1,expectedFlags=!hit?1u:occluded?6u:2u,
                        comparison=lit?(baseline==null?"Analytic direct BRDF plus emission":"Same lighting, shadows off"):"Analytic emission or miss",
                        expectedRgb=lit?(baseline==null?emission+DirectReference(id,world[id],s.ray.originTMin,light):baseline[i].rgb):emission};
                    Record(r,row,output[i]);
                }
            }
            Production("Production primary",Vector3.back,false,true,null,false);
            foreach(bool blocked in new[]{false,true})
            {
                var light=blocked?new Vector3(.35f,0,-1).normalized:Vector3.back;
                Production(blocked?"Angled reference":"Axial reference",light,false,false,null,false);
                var baseline=(Payload[])output.Clone(); Production(blocked?"Shadow through solid":"Shadow through hole",light,true,false,baseline,blocked);
            }
            Check(classificationOK && r.instances==3 && r.disabledSubmeshes==2 && service.buildCount==1,"Incorrect material support or RTAS membership");
        }
        finally
        {
            if(results!=null){results.Release();++r.buffersReleased;} if(inputs!=null){inputs.Release();++r.buffersReleased;}
            r.buildCount=service.buildCount; service.SetRequests(RayTracingRequests.None); r.liveSceneResourcesAfterRelease=service.liveResources;
            r.subscribersAfterRelease=service.subscribers; r.instancesAfterRelease=service.instanceCount; service.Dispose();
            r.released=r.liveSceneResourcesAfterRelease==0 && r.subscribersAfterRelease==0 && r.instancesAfterRelease==0 && r.buffersAllocated==r.buffersReleased;
            for(int i=owned.Count-1;i>=0;--i) Destroy(owned[i]);
        }
    }
    static Vector3 Relative(Vector3 a,Vector3 b) => new Vector3(Mathf.Abs(a.x-b.x)/Mathf.Max(.01f,Mathf.Abs(b.x)),Mathf.Abs(a.y-b.y)/Mathf.Max(.01f,Mathf.Abs(b.y)),Mathf.Abs(a.z-b.z)/Mathf.Max(.01f,Mathf.Abs(b.z)));
    static void Record(Report r, Row row,Payload p)
    {
        row.finite=true;
        float Clean(float v,string field) { if(Finite(v)) return v; row.finite=false; row.nonFiniteReadback+=field+"="+v+"; "; ++r.nonFiniteComponents; return 0; }
        row.actualT=Clean(p.t,"hitT"); row.absTError=Mathf.Abs(row.actualT-row.expectedT);
        for(int i=0;i<3;++i) {row.actualRgb[i]=Clean(p.rgb[i],"rgb"+i); row.expectedRgb[i]=Clean(row.expectedRgb[i],"expected"+i);}
        for(int i=0;i<4;++i) {row.raster[i]=Clean(row.raster[i],"raster"+i);row.foregroundRaster[i]=Clean(row.foregroundRaster[i],"foreground"+i);}
        row.rawMode=p.mode;row.actualMode=p.mode&255u;row.ahsCount=p.mode>>8;row.actualFlags=p.flags;
        bool Match(Vector3 a,Vector3 b)
        {
            if(row.expectedMode==17 && row.expectedFlags==2) {row.normalDegrees=Mathf.Max(row.normalDegrees,Vector3.Angle(a,b));
                return Mathf.Abs(a.magnitude-1)<=.03f && Mathf.Abs(b.magnitude-1)<=.03f && row.normalDegrees<=r.normalToleranceDegrees;}
            var error=Relative(a,b); return Mathf.Max(error.x,Mathf.Max(error.y,error.z))<=r.rgbTolerance;
        }
        row.relativeRgbError=Relative(row.actualRgb,row.expectedRgb);
        bool rgb=Match(row.actualRgb,row.expectedRgb);
        if(row.sampledRaster) rgb &= row.raster.w==1 && Match(row.actualRgb,row.raster) && Match(row.raster,row.expectedRgb);
        row.passed=row.finite && rgb && (!row.sampledForeground || row.foregroundRaster.w==0) && row.actualFlags==row.expectedFlags &&
            row.actualMode==row.expectedMode && (row.minimumAhs==0?row.ahsCount==0:row.ahsCount>=row.minimumAhs) && row.absTError<=r.tToleranceMeters;
        r.vectors.Add(row); if(!row.passed) ++r.failedVectors;
    }
#if UNITY_EDITOR
    public static void Build()
    {
        const BuildTarget target=BuildTarget.StandaloneWindows64; const string path="Assets/T3b.unity";
        PlayerSettings.colorSpace=ColorSpace.Linear; PlayerSettings.SetUseDefaultGraphicsAPIs(target,false);
        PlayerSettings.SetGraphicsAPIs(target,new[]{GraphicsDeviceType.Direct3D12,GraphicsDeviceType.Direct3D11}); T3bRaster.PrepareBuild();
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var driver=new GameObject("T3b Lit validation").AddComponent<T3bPlayer>();
        var shader=AssetDatabase.LoadAssetAtPath<Shader>(Package+"Lit.shader"); driver.library=AssetDatabase.LoadAssetAtPath<RayTracingShader>(Package+"RayTracing/GlxBasicHit.raytrace");
        driver.probe=AssetDatabase.LoadAssetAtPath<RayTracingShader>("Assets/T3bProbe.raytrace"); Check(shader && driver.library && driver.probe,"Missing Lit, library or probe");
        driver.materials=new Material[5];
        for(int i=0;i<5;++i)
        {
            var m=new Material(shader) {name="T3b"+Names[i]};
            AssetDatabase.CreateAsset(m,"Assets/"+m.name+".mat"); driver.materials[i]=m;
            m.SetFloat("_Surface",i==Transparent?1:0); m.SetFloat("_WorkflowMode",i==Specular?0:1); m.SetFloat("_AlphaClip",i==Cut?1:0);
            m.renderQueue=i==Transparent?3000:i==Cut?2450:2000;
            if(i==Specular) m.EnableKeyword("_SPECULAR_SETUP"); if(i==Transparent) m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); if(i==Cut) m.EnableKeyword("_ALPHATEST_ON");
            if(i!=Rear) { m.EnableKeyword("_EMISSION"); m.EnableKeyword("_NORMALMAP"); m.EnableKeyword("_METALLICSPECGLOSSMAP"); }
            m.SetFloat("_Metallic",.17f);
            m.SetVector("_EmissionColor",new Vector4(1.65f,.74f,1.21f,1)); m.SetColor("_BaseColor",new Color(1,1,1,.88f)); m.SetFloat("_Cutoff",.5f); m.SetFloat("_Smoothness",.68f); m.SetFloat("_BumpScale",.72f);
            m.SetTextureScale("_BaseMap",ScaleUV);m.SetTextureOffset("_BaseMap",OffsetUV);
            for(int j=0;j<4;++j)
            {
                int w=(16<<(j%3)),h=(16<<((j+i)%3)); var tex=new Texture2D(w,h,TextureFormat.RGBA32,false,true) {name=m.name+Maps[j],filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Repeat};
                var px=new Color32[w*h]; for(int y=0;y<h;++y) for(int x=0;x<w;++x) px[y*w+x]=Texel(i,j,x*4/w,y*4/h);
                tex.SetPixels32(px);tex.Apply(); if(j==3) {tex.Compress(true);tex.Apply(); Check(tex.format==TextureFormat.DXT1 || tex.format==TextureFormat.DXT5,"Expected BC-compressed emission");}
                AssetDatabase.AddObjectToAsset(tex,m); m.SetTexture(Maps[j],tex);
            }
            EditorUtility.SetDirty(m);
        }
        AssetDatabase.SaveAssets(); Check(EditorSceneManager.SaveScene(scene,path),"Cannot save scratch scene");
        var build=BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes=new[]{path},target=target,locationPathName=Arg("-exe"),options=BuildOptions.Development });
        Check(build.summary.result==BuildResult.Succeeded,"T3b development build failed");
    }
#endif
}
