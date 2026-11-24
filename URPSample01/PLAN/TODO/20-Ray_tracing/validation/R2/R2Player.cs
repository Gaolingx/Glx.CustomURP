using System;
using System.IO;
using System.Linq;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif
public class R2Player : MonoBehaviour {
 public Material mirror, emission, dark, diagnostic;
 public ScreenSpaceLightingRendererFeature feature;
 Camera cam; GameObject target, blocker; RenderTexture rt; string dest;
 static string Arg(string key){var a=Environment.GetCommandLineArgs();return a[Array.IndexOf(a,key)+1];}
 GameObject Box(string label,Vector3 pos,Vector3 size,Material mat,int id){var g=GameObject.CreatePrimitive(PrimitiveType.Cube);g.name=label;g.transform.position=pos;g.transform.localScale=size;var r=g.GetComponent<MeshRenderer>();r.sharedMaterial=mat;r.rayTracingMode=RayTracingMode.DynamicTransform;if(id>=0)g.AddComponent<SsrRayTracingGeometry>().instanceId=id;return g;}
 static void Dump<T>(ComputeBuffer b,string path) where T:struct {var data=new T[b.count];b.GetData(data);var handle=System.Runtime.InteropServices.GCHandle.Alloc(data,System.Runtime.InteropServices.GCHandleType.Pinned);try{byte[] bytes=new byte[b.count*b.stride];System.Runtime.InteropServices.Marshal.Copy(handle.AddrOfPinnedObject(),bytes,0,bytes.Length);File.WriteAllBytes(path,bytes);}finally{handle.Free();}}
 struct Q {public Vector4 a,b,c,d,e;} struct H {public Vector4 a;public Vector2 b;}
 static void Pixels(RenderTexture source,string path){var old=RenderTexture.active;RenderTexture.active=source;var tex=new Texture2D(source.width,source.height,TextureFormat.RGBAHalf,false,true);tex.ReadPixels(new Rect(0,0,source.width,source.height),0,0);tex.Apply();File.WriteAllBytes(path,tex.GetRawTextureData());Destroy(tex);RenderTexture.active=old;}
 [Serializable] class Row {public string stage,api,guid,gpu; public bool queue,produced,rtSupport,owner;public int frame,w,h,screenW,screenH;public bool visible;}
 void Capture(string s){var path=Path.Combine(dest,s);var p=feature.ssrPass;var q=p.requestQueue;var row=new Row{stage=s,api=SystemInfo.graphicsDeviceType.ToString(),gpu=SystemInfo.graphicsDeviceName,guid=Application.buildGUID,frame=Time.frameCount,rtSupport=SystemInfo.supportsRayTracing,queue=q!=null,produced=p.producedOutput,screenW=rt.width,screenH=rt.height,visible=target.activeSelf && GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(cam),target.GetComponent<Renderer>().bounds),owner=typeof(ScreenSpaceLightingRendererFeature).GetField("m_HybridCameras",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(feature)!=null};if(p.outputTexture?.rt){row.w=p.outputTexture.rt.width;row.h=p.outputTexture.rt.height;Pixels(p.outputTexture.rt,path+".ssr");} Pixels(rt,path+".camera");File.WriteAllText(path+".json",JsonUtility.ToJson(row,true));if(q?.counters!=null){Dump<uint>(q.counters,path+".counts");Dump<Q>(q.originals,path+".original");Dump<Q>(q.requests,path+".requests");if(q.hits!=null)Dump<H>(q.hits,path+".hits");}}
 IEnumerator Start(){dest=Arg("-out");QualitySettings.SetQualityLevel(Array.IndexOf(QualitySettings.names,"High Fidelity"),true);Application.runInBackground=true;var cg=new GameObject("R2 camera");cam=cg.AddComponent<Camera>();cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=Color.black;cam.fieldOfView=50;cam.nearClipPlane=.1f;cam.farClipPlane=100;cam.allowMSAA=false;rt=new RenderTexture(160,120,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);cam.targetTexture=rt;cam.GetUniversalAdditionalCameraData().renderPostProcessing=false;
 var vol=new GameObject("R2 volume").AddComponent<Volume>();vol.isGlobal=true;vol.priority=999;var ssr=vol.profile.Add<ScreenSpaceReflection>();ssr.enabled.Override(true);ssr.fullResolution.Override(false);ssr.reflectSky.Override(false);ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
 Box("receiver",new Vector3(0,0,3.05f),new Vector3(8,8,.1f),mirror,-1);Box("persistent witness",new Vector3(500,500,500),Vector3.one,dark,99);
 target=Box("donor",new Vector3(0,0,-2),new Vector3(3,2,.1f),emission,37);blocker=Box("occluder",new Vector3(0,0,-1),new Vector3(4,3,.1f),dark,53);blocker.SetActive(false);
 foreach(string stage in new[]{"cold","warm","id","occluded","blockerID","removed","far","inside","ssOnly"}){
 if(stage=="id")target.GetComponent<Renderer>().sharedMaterial=diagnostic;
 if(stage=="occluded"){target.GetComponent<Renderer>().sharedMaterial=emission;blocker.SetActive(true);}if(stage=="blockerID")blocker.GetComponent<Renderer>().sharedMaterial=diagnostic;
 if(stage=="removed"){blocker.SetActive(false);target.SetActive(false);}if(stage=="far"){target.transform.position=new Vector3(0,0,-58);target.transform.localScale=new Vector3(45,30,.1f);target.SetActive(true);}if(stage=="inside"){target.transform.position=new Vector3(.54f,0,1.5f);target.transform.localScale=new Vector3(.35f,.6f,.1f);}if(stage=="ssOnly"){feature.settings.rayTracingReflections=false;target.transform.position=new Vector3(0,0,-2);target.transform.localScale=new Vector3(3,2,.1f);}
 yield return new WaitForEndOfFrame();if(stage=="ssOnly"||stage=="warm")yield return new WaitForEndOfFrame();try{Capture(stage);}catch(Exception ex){Debug.LogException(ex);Application.Quit(2);yield break;}}
 Application.Quit(0);}
#if UNITY_EDITOR
 public static void Build(){var tgt=BuildTarget.StandaloneWindows64;PlayerSettings.colorSpace=ColorSpace.Linear;PlayerSettings.SetUseDefaultGraphicsAPIs(tgt,false);PlayerSettings.SetGraphicsAPIs(tgt,new[]{GraphicsDeviceType.Direct3D12,GraphicsDeviceType.Direct3D11});var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);var driver=new GameObject("R2").AddComponent<R2Player>();var renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/URP-HighFidelity-Renderer.asset");var f=renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();if(!f){f=ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();f.name="R2 Screen Space Lighting";AssetDatabase.AddObjectToAsset(f,renderer);renderer.rendererFeatures.Add(f);}driver.feature=f;f.SetActive(true);f.settings.rayTracingReflections=true;f.settings.reflectionRayBudget=2048;f.settings.EnsureResources();EditorUtility.SetDirty(renderer);EditorUtility.SetDirty(f);if(!f.settings.resources.rayTracingSsr)throw new Exception("Missing raygen");
 Material Mat(string n,Shader sh,Color e,float metallic,float smooth){var m=new Material(sh){name=n};AssetDatabase.CreateAsset(m,"Assets/Fixture/"+n+".mat");if(m.HasProperty("_BaseColor")){m.SetColor("_BaseColor",Color.white);m.SetFloat("_Metallic",metallic);m.SetFloat("_Smoothness",smooth);m.SetColor("_EmissionColor",e);if(e.maxColorComponent>0)m.EnableKeyword("_EMISSION");}EditorUtility.SetDirty(m);return m;}
 var lit=Shader.Find("Universal Render Pipeline/Lit");driver.mirror=Mat("mirror",lit,Color.black,1,1);driver.emission=Mat("donor",lit,new Color(2,.5f,.125f),0,0);driver.dark=Mat("dark",lit,Color.black,0,0);driver.diagnostic=Mat("id",Shader.Find("Hidden/R2/InstanceDiagnostic"),Color.black,0,0);AssetDatabase.SaveAssets();string path="Assets/Fixture/R2.unity";EditorSceneManager.SaveScene(scene,path);var rep=BuildPipeline.BuildPlayer(new[]{path},Arg("-exe"),tgt,BuildOptions.Development);if(rep.summary.result!=BuildResult.Succeeded)throw new Exception(rep.summary.result.ToString());}
#endif
}
