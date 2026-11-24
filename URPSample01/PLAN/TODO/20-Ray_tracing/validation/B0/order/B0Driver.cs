using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class B0Driver
{
    const string Fixture = "Assets/Tests/ScreenSpaceLighting/Renderers/SslT10Forward";

    public static void Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var pipe = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Fixture + "Pipeline.asset");
        var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Fixture + "Renderer.asset");
        if (!pipe || !(data.rendererFeatures.Single() is ScreenSpaceLightingRendererFeature ssl) || !ssl.isActive)
            throw new Exception("Missing active SSL-only fixture");
        // Use real baseline features for the 201 occupants (no fake pass / enqueue probe).
        // The list is modified in memory only, before constructing the renderer in this Editor.
        var decalData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardRenderer.asset");
        var shadowData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Tests/ClusteredDeferred/Renderers/CdScreenShadowForwardPlusRenderer.asset");
        data.rendererFeatures.Add(decalData.rendererFeatures.Single(f => f.GetType().Name == "DecalRendererFeature"));
        data.rendererFeatures.Add(shadowData.rendererFeatures.Single(f => f.GetType().Name == "ScreenSpaceShadows"));
        data.SetDirty();
        GraphicsSettings.defaultRenderPipeline = QualitySettings.renderPipeline = pipe;
        var light = new GameObject("B0 sun").AddComponent<Light>();
        light.type = LightType.Directional;
        light.shadows = LightShadows.Hard;
        RenderSettings.sun = light;
        var volume = new GameObject("B0 global SSGI").AddComponent<Volume>();
        volume.isGlobal = true;
        volume.profile.Add<ScreenSpaceGlobalIllumination>().enabled.Override(true);
        GameObject.CreatePrimitive(PrimitiveType.Cube).transform.position = Vector3.forward * 4;
        var game = new GameObject("B0 Game").AddComponent<Camera>();
        game.enabled = false;
        game.gameObject.AddComponent<UniversalAdditionalCameraData>();
        var sv = ScriptableObject.CreateInstance<SceneView>();
        sv.ShowUtility();
        var rt = new RenderTexture(128, 96, 24);
        rt.Create();
        Debug.Log($"B0|meta|{DateTime.UtcNow:O}|{Application.unityVersion}|{SystemInfo.graphicsDeviceType}|{SystemInfo.graphicsDeviceName}|{SystemInfo.graphicsDeviceVersion}|{SystemInfo.supportsRayTracing}|{pipe.name}|{ssl.name}");
        try
        {
            Capture(game, rt, CameraType.Game);
            Capture(sv.camera, rt, CameraType.SceneView);
        }
        finally { B0Trace.target = null; sv.Close(); rt.Release(); }
    }

    static void Capture(Camera cam, RenderTexture rt, CameraType expected)
    {
        if (!cam || cam.cameraType != expected) throw new Exception("Missing real " + expected + " camera");
        cam.targetTexture = rt;
        cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        for (int i = 0; i < 2; i++)
        {
            cam.transform.position += Vector3.right * 0.125f;
            B0Trace.rows.Clear();
            B0Trace.target = cam;
            void CountError(string message, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    B0Trace.rows.Add("renderError:" + type);
            }
            Application.logMessageReceived += CountError;
            try { cam.Render(); }
            finally { Application.logMessageReceived -= CountError; B0Trace.target = null; }
            Debug.Log($"B0|sample|{cam.cameraType}|{cam.GetInstanceID()}|{cam.TryGetComponent<UniversalAdditionalCameraData>(out _)}|{Time.frameCount}|{i}|{string.Join(",", B0Trace.rows)}");
        }
        cam.targetTexture = null;
    }
}
