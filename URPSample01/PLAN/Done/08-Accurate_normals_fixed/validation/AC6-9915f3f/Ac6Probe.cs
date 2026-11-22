using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Copy to Assets/Tests/ScreenSpaceLighting/Editor alongside the unmodified
// 9915f3f attribution script (on BOTH revisions); set AC6_OUTPUT to a fresh CSV path.
public static class Ac6Probe {
    [DllImport("Ac6Clock")] static extern IntPtr callback();
    [DllImport("Ac6Clock")] static extern long nanos();
    public static void Run() {
        const string root="Assets/Tests/ScreenSpaceLighting/";
        EditorSceneManager.OpenScene(root+"Scenes/ScreenSpaceLightingT3Verification.unity");
        DynamicGI.UpdateEnvironment();
        var camera=UnityEngine.Object.FindObjectOfType<Camera>();
        var runner=UnityEngine.Object.FindObjectOfType<ScreenSpaceLightingBaselineRunner>();
        if(runner) runner.enabled=false;
        var volume=GameObject.Find("Baseline Volume"); volume.SetActive(true);
        var r=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(root+"Renderers/SslT5DeferredRenderer.asset");
        var p=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(root+"Renderers/SslT5DeferredPipeline.asset");
        var g=r.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>().Single();
        var s=r.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().Single();
        var attr=typeof(ScreenSpaceLightingAccurateNormalsAttribution);
        attr.GetMethod("ApplyReferenceGtao",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{g});
        attr.GetMethod("SetSslDebug",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{s,ScreenSpaceLightingDebugMode.Off,0});
        g.SetActive(true);s.SetActive(true);r.accurateGbufferNormals=true;r.SetDirty();
        GraphicsSettings.defaultRenderPipeline=p;QualitySettings.renderPipeline=p;
        var data=camera.GetUniversalAdditionalCameraData();data.renderPostProcessing=false;data.antialiasing=AntialiasingMode.None;data.resetHistory=true;
        var rt=new RenderTexture(1280,720,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);rt.Create();camera.targetTexture=rt;camera.aspect=1280f/720;
        var path=Environment.GetEnvironmentVariable("AC6_OUTPUT");
        using(var w=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write))) {
            w.WriteLine("# "+DateTime.UtcNow.ToString("O")+","+Application.unityVersion+","+SystemInfo.graphicsDeviceName+","+SystemInfo.graphicsDeviceVersion);
            attr.GetMethod("RenderAndCapture",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{camera,rt,16});
            var enumerate=attr.GetMethod("EnumerateComputes",BindingFlags.Static|BindingFlags.NonPublic);
            var shaders=new ScriptableRendererFeature[]{g,s}.SelectMany(f=>(System.Collections.Generic.IEnumerable<ComputeShader>)enumerate.Invoke(null,new object[]{f})).ToArray();
            foreach(var k in new[]{"GTAO_NORMALS_OCT","SSGI_NORMALS_OCT","SSR_NORMALS_OCT"}) {
                var active=shaders.Where(x=>x.IsKeywordEnabled(k)).Select(x=>x.name).ToArray();
                w.WriteLine("# "+k+"="+string.Join(";",active));
                if(active.Length==0)throw new Exception("Missing oct keyword: "+k);
            }
            w.WriteLine("sample,gpu_ns");
            if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D11 || SystemInfo.graphicsDeviceVendorID!=0x10de) throw new Exception("Unexpected device");
            var cb=callback();
            for(int i=-128;i<192;i++) {
                // Queue identical untimed work first rather than start a timestamp on an idle GPU.
                camera.Render();camera.Render();
                callback();GL.IssuePluginEvent(cb,1);camera.Render();GL.IssuePluginEvent(cb,2);GL.Flush();
                var deadline=DateTime.UtcNow.AddSeconds(10);long n;
                while((n=nanos())==-1 && DateTime.UtcNow<deadline) System.Threading.Thread.Yield();
                if(n<=0) throw new Exception("Invalid D3D11 timestamp query: "+n);
                if(i>=0) w.WriteLine(i.ToString()+","+n.ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}
