using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

public class T2Player : MonoBehaviour
{
    public Shader surface;
    public RayTracingShader ray;
    static string Arg(string key) { var args=Environment.GetCommandLineArgs(); return args[Array.IndexOf(args,key)+1]; }
    [Serializable] class Report { public string guid, api; public bool development, passed; public string[] vectors; }
    void Start()
    {
        var rows=new List<string>(); var report=new Report { guid=Application.buildGUID, api=SystemInfo.graphicsDeviceType.ToString(), development=Debug.isDebugBuild };
        try {
            if (!report.development || IntPtr.Size!=8 || report.api!=Arg("-expectedApi")) throw new Exception("Wrong build or API");
            T2Validation.T2VectorSuite.Run(ray,surface,report.api=="Direct3D12",rows.Add); report.passed=true;
        } catch(Exception ex) { rows.Add(ex.Message); }
        report.vectors=rows.ToArray(); File.WriteAllText(Arg("-result"),JsonUtility.ToJson(report,true)); Application.Quit(report.passed?0:1);
    }
#if UNITY_EDITOR
    public static void Build()
    {
        const BuildTarget target=BuildTarget.StandaloneWindows64;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target,false); PlayerSettings.SetGraphicsAPIs(target,new[]{GraphicsDeviceType.Direct3D12,GraphicsDeviceType.Direct3D11});
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var driver=new GameObject("T2 vectors").AddComponent<T2Player>();
        driver.surface=AssetDatabase.LoadAssetAtPath<Shader>("Assets/T2Fixture.shader"); driver.ray=AssetDatabase.LoadAssetAtPath<RayTracingShader>("Assets/T2Fixture.raytrace");
        EditorSceneManager.SaveScene(scene,"Assets/T2.unity");
        var b=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{"Assets/T2.unity"},target=target,locationPathName=Arg("-exe"),options=BuildOptions.Development});
        if(b.summary.result!=BuildResult.Succeeded) throw new Exception("Development build failed");
    }
#endif
}
