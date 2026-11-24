using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

// Installed exclusively in the scratch project; the production pass is not instrumented or changed.
public sealed class T8Performance : MonoBehaviour
{
    static readonly string[] Modes = { "Forward", "ForwardPlus", "Deferred" };
    const int Warm = 60, Samples = 120;
    public ScreenSpaceContactShadowsRendererFeature[] features;
    public UniversalRenderPipelineAsset[] pipelines;
#if UNITY_EDITOR
    public static void Build()
    {
        const string root = "Assets/Tests/ContactShadow/", path = root + "Scenes/ContactShadowStress.unity";
        var scene = EditorSceneManager.OpenScene(path);
        var probe = new GameObject("T8 timings").AddComponent<T8Performance>();
        probe.features = new ScreenSpaceContactShadowsRendererFeature[3];
        probe.pipelines = new UniversalRenderPipelineAsset[3];
        for (int i = 0; i < 3; i++) {
            var prefix = root + "Pipelines/" + Modes[i];
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(prefix + ".asset");
            probe.features[i] = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(prefix + "Renderer.asset").rendererFeatures.OfType<ScreenSpaceContactShadowsRendererFeature>().Single();
            probe.pipelines[i]=asset;
            QualitySettings.SetQualityLevel(i,true); QualitySettings.renderPipeline = asset;
            if (i == 0) GraphicsSettings.defaultRenderPipeline = asset;
        }
        EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target,false);
        PlayerSettings.SetGraphicsAPIs(target,new[]{GraphicsDeviceType.Direct3D11,GraphicsDeviceType.Direct3D12});
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { locationPathName="Player/T8.exe",target=target,scenes=new[]{path},options=BuildOptions.Development });
        if (report.summary.result!=BuildResult.Succeeded) throw new Exception("Build failed");
    }
#endif
    [Serializable] class Row {
        public string mode; public int lights, slots, steps;
        public List<float> gpuMs=new List<float>(), cmdCpuMs=new List<float>(), inlineCpuMs=new List<float>();
    }
    [Serializable] class Report {
        public string unity, cpu, gpu, driver, api, os, build; public int warmup=Warm, sampled=Samples, width=1920,height=1080;
        public List<Row> rows=new List<Row>();
    }
    readonly Report report=new Report();
    Camera cam; Light[] lights; ContactShadowsVolume volume; int cell, frame; Row row; ProfilingSampler sampler; object pass;
    static object Field(object obj,string key)=>obj.GetType().GetField(key,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(obj);
    static object Prop(object obj,string key)=>obj.GetType().GetProperty(key,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(obj);
    void Start() {
        try {
            report.unity=Application.unityVersion; report.cpu=SystemInfo.processorType; report.gpu=SystemInfo.graphicsDeviceName;
            report.driver=SystemInfo.graphicsDeviceVersion; report.api=SystemInfo.graphicsDeviceType.ToString(); report.os=SystemInfo.operatingSystem;report.build=Application.buildGUID;
            cam=GameObject.Find("ContactCamera").GetComponent<Camera>();cam.enabled=false;cam.allowMSAA=false;cam.allowDynamicResolution=false;
            cam.targetTexture=new RenderTexture(report.width,report.height,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);cam.targetTexture.Create();cam.aspect=1920f/1080;
            cam.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.EveryFrame);
            if(!GameObject.Find("ContactVolume").GetComponent<Volume>().profile.TryGet(out volume))throw new Exception("Missing Volume");
            lights=FindObjectsOfType<Light>(true).OrderBy(l=>l.name=="Main"?"0":l.name).ToArray();
            if(lights.Length<25)throw new Exception("Missing stress lights");
        }catch(Exception e){Fail(e);}
    }
    static void Fail(Exception e){Debug.LogException(e);Application.Quit(1);}
    static void Sample(List<float> v,int hits,float ms){if(hits==1&&ms>0)v.Add(ms);}
    void Update() {
        try {
            int mode=cell/12;
            if(frame==0){
                row=new Row {mode=Modes[mode],lights=new[]{1,4,24,25}[(cell/3)%4],steps=new[]{4,10,64}[cell%3]};
                QualitySettings.SetQualityLevel(mode,true); QualitySettings.renderPipeline=pipelines[mode]; QualitySettings.vSyncCount=0; Application.targetFrameRate=-1;
                for(int i=0;i<lights.Length;i++){var l=lights[i]; l.gameObject.SetActive(true);l.enabled=i<row.lights;
                    var marker=l.GetComponent<ContactShadowLight>()??l.gameObject.AddComponent<ContactShadowLight>();marker.enabled=true;marker.castContactShadows=true;}
                volume.enabled.Override(true);volume.sampleCount.Override(row.steps);
            }
            cam.Render(); // Headless batch players do not automatically render the attached camera.
            if(frame==3){
                if(Prop(cam.GetUniversalAdditionalCameraData().scriptableRenderer,"renderingModeActual").ToString()!=row.mode)throw new Exception("Mode fallback");
                pass=Field(features[mode],"m_Pass");sampler=(ProfilingSampler)Field(pass,"m_Sampler");sampler.enableRecording=true;
            }
            if(frame>=Warm){
                row.slots=(int)Prop(Field(pass,"m_LightData"),"count");
                if(row.slots!=Math.Min(24,row.lights)||(int)Field(pass,"m_SampleCount")!=row.steps)throw new Exception($"Unexpected dispatch inputs {row.mode} {row.slots}/{row.lights}, steps {row.steps}");
                Sample(row.gpuMs,sampler.gpuSampleCount,sampler.gpuElapsedTime);Sample(row.cmdCpuMs,sampler.cpuSampleCount,sampler.cpuElapsedTime);Sample(row.inlineCpuMs,sampler.inlineCpuSampleCount,sampler.inlineCpuElapsedTime);
            }
            if(++frame==Warm+Samples){report.rows.Add(row);frame=0;
                if(++cell==36){File.WriteAllText(Path.Combine(Application.dataPath,"../timings.json"),JsonUtility.ToJson(report));enabled=false;Application.Quit(0);}}
        }catch(Exception e){enabled=false;Fail(e);}
    }
}
