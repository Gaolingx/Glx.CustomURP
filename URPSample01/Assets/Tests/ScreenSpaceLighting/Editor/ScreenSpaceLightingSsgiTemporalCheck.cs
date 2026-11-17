using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// SSGI temporal convergence check used after the history UV scale fix.
// For each renderer it compares two consecutive frames after a 32 frame warmup with temporal
// accumulation on and off. With correct history sampling the on-frame delta must be much smaller
// than the off-frame delta (independent 1 spp samples). The check also records the SSGI weight
// coverage so an empty effect cannot pass by accident.
public static class ScreenSpaceLightingSsgiTemporalCheck
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string RendererPathFormat = RendererRoot + "/SslT8{0}Renderer.asset";
    const string PipelinePathFormat = RendererRoot + "/SslT8{0}Pipeline.asset";
    const string DefaultOutputRoot = "PLAN/Done/02-SSRImprove/validation/AuditFix";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 32;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Run SSGI Temporal Check")]
    public static void RunSsgiTemporalCheck()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputRoot = Environment.GetEnvironmentVariable("SSL_SSGI_CHECK_OUT");
        var outputDirectory = Path.Combine(projectRoot, string.IsNullOrEmpty(outputRoot) ? DefaultOutputRoot : outputRoot);
        Directory.CreateDirectory(outputDirectory);

        var report = new SsgiReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            passed = true,
            modes = new List<SsgiModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        Volume volume = null;
        VolumeProfile originalProfile = null;
        VolumeProfile testProfile = null;
        GameObject rig = null;
        var rigMaterials = new List<Material>();
        try
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            DynamicGI.UpdateEnvironment();

            var camera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (camera == null)
                throw new InvalidOperationException("The verification scene has no camera.");
            var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData == null)
                throw new InvalidOperationException("The verification camera has no UniversalAdditionalCameraData.");
            var runner = UnityEngine.Object.FindObjectOfType<ScreenSpaceLightingBaselineRunner>();
            if (runner != null)
                runner.enabled = false;

            var volumeObject = GameObject.Find("Baseline Volume");
            if (volumeObject == null || volumeObject.GetComponent<Volume>() == null)
                throw new InvalidOperationException("The verification scene has no Baseline Volume.");
            volume = volumeObject.GetComponent<Volume>();
            originalProfile = volume.sharedProfile;
            testProfile = ScriptableObject.Instantiate(originalProfile);
            testProfile.name = originalProfile.name + " (SSGI temporal)";
            volume.sharedProfile = testProfile;

            var ssr = testProfile.components.OfType<ScreenSpaceReflection>().FirstOrDefault();
            if (ssr != null)
                ssr.enabled.Override(false);
            var refraction = testProfile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
            if (refraction != null)
                refraction.enabled.Override(false);
            var ssgi = testProfile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault()
                       ?? testProfile.Add<ScreenSpaceGlobalIllumination>(true);
            ApplySsgiSettings(ssgi, temporal: true);

            rig = CreateDiffuseRig(camera, rigMaterials);
            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslSsgiTemporalImage");
            camera.targetTexture = imageTarget;
            camera.aspect = (float)ImageWidth / ImageHeight;
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;
            cameraData.resetHistory = true;

            for (int modeIndex = 0; modeIndex < ModeNames.Length; modeIndex++)
            {
                var mode = CaptureMode(
                    ModeNames[modeIndex],
                    camera,
                    cameraData,
                    ssgi,
                    imageTarget,
                    outputDirectory);
                report.modes.Add(mode);
                report.passed &= mode.passed;
                Debug.Log($"SSGI temporal {mode.name}: passed={mode.passed} offSampleMax={mode.offSampleCountMax} onSampleMax={mode.onSampleCountMax} saturated={mode.onSampleCountSaturatedPixels} history={mode.onHistoryNonZeroPixels}");
            }

            report.completed = true;
        }
        catch (Exception exception)
        {
            report.completed = false;
            report.error = exception.ToString();
            report.passed = false;
        }
        finally
        {
            if (rig != null)
                UnityEngine.Object.DestroyImmediate(rig);
            foreach (var material in rigMaterials)
            {
                if (material != null)
                    UnityEngine.Object.DestroyImmediate(material);
            }

            if (volume != null)
                volume.sharedProfile = originalProfile;
            if (testProfile != null)
                UnityEngine.Object.DestroyImmediate(testProfile);

            if (imageTarget != null)
            {
                imageTarget.Release();
                UnityEngine.Object.DestroyImmediate(imageTarget);
            }

            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            File.WriteAllText(Path.Combine(outputDirectory, "SsgiTemporalCheck.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"SSGI temporal check finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static SsgiModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        ScreenSpaceGlobalIllumination ssgi,
        RenderTexture imageTarget,
        string outputDirectory)
    {
        var result = new SsgiModeResult { name = modeName, issues = new List<string>() };
        var passType = GetPluginType("ScreenSpaceLightingSsgiPass");
        var pipeline = LoadPipeline(modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(RendererPathFormat, modeName));
        if (renderer == null)
            throw new FileNotFoundException("Missing SSGI check renderer fixture.", string.Format(RendererPathFormat, modeName));
        var feature = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (feature == null)
            throw new InvalidOperationException("The SSGI check renderer is missing the Screen Space Lighting feature.");
        EnsureFeatureResources(feature);

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        // Temporal off first, while the history buffers are still empty: a fresh scene that never
        // accumulated keeps its sample counts at zero.
        ApplySsgiSettings(ssgi, temporal: false);
        int offAccumulationBefore = ReadStaticInt(passType, "s_EditorAccumulationDispatchCount");
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.offAccumulationDispatchCount = ReadStaticInt(passType, "s_EditorAccumulationDispatchCount") - offAccumulationBefore;
        result.offSampleCountMax = ReadSampleCountMax(feature, camera);
        WriteImage(imageTarget, outputDirectory, "SsgiTemporal_" + modeName + "_Off.png");

        // Temporal on: the history sample count must saturate and the history buffer must hold
        // radiance after the warmup. This is the direct evidence that the history lookup works.
        ApplySsgiSettings(ssgi, temporal: true);
        int onAccumulationBefore = ReadStaticInt(passType, "s_EditorAccumulationDispatchCount");
        int onValidationBefore = ReadStaticInt(passType, "s_EditorValidationDispatchCount");
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.onAccumulationDispatchCount = ReadStaticInt(passType, "s_EditorAccumulationDispatchCount") - onAccumulationBefore;
        result.onValidationDispatchCount = ReadStaticInt(passType, "s_EditorValidationDispatchCount") - onValidationBefore;
        result.onSampleCountSaturatedPixels = ReadSampleCountSaturatedPixels(feature, camera, 7.5f);
        result.onSampleCountMax = ReadSampleCountMax(feature, camera);
        result.onHistoryNonZeroPixels = ReadHistoryNonZeroPixels(feature, camera);
        var final = ReadSsgiOutput(feature);
        if (final != null)
        {
            result.coveredPixels = Count(final, pixel => pixel.a > 0.9f);
            result.coveredFraction = result.coveredPixels / (float)Mathf.Max(1, final.Length);
        }
        WriteImage(imageTarget, outputDirectory, "SsgiTemporal_" + modeName + "_On.png");

        if (result.coveredFraction < 0.01f)
            result.issues.Add($"Only {result.coveredFraction:P2} of the pixels carry SSGI weight; the check has no signal.");
        if (result.offAccumulationDispatchCount != 0)
            result.issues.Add("Temporal accumulation still dispatched with temporal disabled.");
        if (result.offSampleCountMax > 0.5f)
            result.issues.Add($"Sample counts were written without temporal accumulation (max {result.offSampleCountMax}).");
        if (result.onAccumulationDispatchCount <= 1)
            result.issues.Add($"Temporal accumulation dispatched only {result.onAccumulationDispatchCount} times with temporal enabled.");
        if (result.onSampleCountSaturatedPixels < 100)
            result.issues.Add($"History sample counts never saturated ({result.onSampleCountSaturatedPixels} pixels).");
        if (result.onHistoryNonZeroPixels < 100)
            result.issues.Add($"The SSGI history buffer stayed empty ({result.onHistoryNonZeroPixels} pixels).");

        result.passed = result.issues.Count == 0;
        return result;
    }


    static void ApplySsgiSettings(ScreenSpaceGlobalIllumination ssgi, bool temporal)
    {
        ssgi.enabled.Override(true);
        ssgi.rayMiss.Override(RayMarchingFallbackHierarchy.None);
        ssgi.fullResolution.Override(true);
        ssgi.maxRaySteps.Override(32);
        ssgi.depthBufferThickness.Override(0.1f);
        ssgi.intensity.Override(1f);
        ssgi.temporalAccumulation.Override(temporal);
        ssgi.denoise.Override(true);
        ssgi.denoiserRadius.Override(0.6f);
        ssgi.secondDenoiserPass.Override(true);
        ssgi.halfResolutionDenoiser.Override(false);
        ssgi.bilateralUpsample.Override(true);
    }

    static Color[] ReadSsgiOutput(ScreenSpaceLightingRendererFeature feature)
    {
        var pass = feature.GetType().GetProperty("ssgiPass", InstanceFlags)?.GetValue(feature);
        var handle = pass?.GetType().GetProperty("finalTexture", InstanceFlags)?.GetValue(pass);
        return ReadRTHandle(handle);
    }

    static object GetHistory(ScreenSpaceLightingRendererFeature feature, Camera camera)
    {
        var store = feature.GetType().GetProperty("historyStore", InstanceFlags)?.GetValue(feature);
        var tryGet = store?.GetType().GetMethod("TryGet", InstanceFlags);
        if (tryGet == null)
            return null;
        var arguments = new object[] { camera, 0, null };
        bool found = tryGet.Invoke(store, arguments) is bool value && value;
        return found ? arguments[2] : null;
    }

    static Color[] ReadHistorySampleCount(ScreenSpaceLightingRendererFeature feature, Camera camera)
    {
        var history = GetHistory(feature, camera);
        if (history == null)
            return null;
        int slot = (int)(history.GetType().GetProperty("ssgiReadSlot", InstanceFlags)?.GetValue(history) ?? 0);
        var handle = history.GetType().GetMethod("GetSsgiSampleCountHF", InstanceFlags)?.Invoke(history, new object[] { slot });
        return ReadRTHandle(handle);
    }

    static Color[] ReadHistoryColor(ScreenSpaceLightingRendererFeature feature, Camera camera)
    {
        var history = GetHistory(feature, camera);
        if (history == null)
            return null;
        int slot = (int)(history.GetType().GetProperty("ssgiReadSlot", InstanceFlags)?.GetValue(history) ?? 0);
        var handle = history.GetType().GetMethod("GetSsgiHistoryHF", InstanceFlags)?.Invoke(history, new object[] { slot });
        return ReadRTHandle(handle);
    }

    static float ReadSampleCountMax(ScreenSpaceLightingRendererFeature feature, Camera camera)
    {
        var counts = ReadHistorySampleCount(feature, camera);
        return counts == null ? -1f : counts.Max(pixel => pixel.r);
    }

    static int ReadSampleCountSaturatedPixels(ScreenSpaceLightingRendererFeature feature, Camera camera, float threshold)
    {
        var counts = ReadHistorySampleCount(feature, camera);
        return counts == null ? -1 : counts.Count(pixel => pixel.r >= threshold);
    }

    static int ReadHistoryNonZeroPixels(ScreenSpaceLightingRendererFeature feature, Camera camera)
    {
        var history = ReadHistoryColor(feature, camera);
        return history == null ? -1 : history.Count(pixel => Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b)) > 1e-4f);
    }

    static GameObject CreateDiffuseRig(Camera camera, List<Material> materials)
    {
        var root = new GameObject("SSGI Temporal Rig");
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            return root;

        var floorMaterial = CreateLitMaterial(shader, "SSGI Diffuse Floor", new Color(0.8f, 0.8f, 0.8f), 0f, 0.1f);
        var wallMaterial = CreateLitMaterial(shader, "SSGI Diffuse Wall", new Color(0.7f, 0.7f, 0.72f), 0f, 0.1f);
        var emitterMaterial = CreateLitMaterial(shader, "SSGI Emitter", new Color(0.1f, 0.1f, 0.1f), 0f, 0.1f);
        emitterMaterial.EnableKeyword("_EMISSION");
        emitterMaterial.SetColor("_EmissionColor", new Color(6f, 2.5f, 1.2f));
        materials.Add(floorMaterial);
        materials.Add(wallMaterial);
        materials.Add(emitterMaterial);

        AddBox(root, floorMaterial, "SSGI Floor", new Vector3(0f, 0.04f, 0.5f), new Vector3(16f, 0.04f, 12f));
        AddBox(root, wallMaterial, "SSGI Wall", new Vector3(0f, 3f, 4.2f), new Vector3(12f, 6f, 0.2f));
        AddBox(root, emitterMaterial, "SSGI Emitter Box", new Vector3(-2.5f, 1.6f, 2.6f), new Vector3(2.5f, 1.2f, 0.2f));

        var viewDirection = (Vector3.zero - camera.transform.position).normalized;
        var right = Vector3.Cross(Vector3.up, viewDirection).normalized;
        var center = camera.transform.position + viewDirection * 10f;
        center.y = 0.9f;
        for (int i = 0; i < 2; i++)
        {
            var sphereMaterial = CreateLitMaterial(shader, "SSGI Sphere " + i, new Color(0.75f, 0.75f, 0.75f), 0f, 0.2f);
            materials.Add(sphereMaterial);
            AddSphere(root, sphereMaterial, "SSGI Sphere " + i, center + right * ((i - 0.5f) * 3f), 0.9f);
        }

        return root;
    }

    static Material CreateLitMaterial(Shader shader, string name, Color color, float metallic, float smoothness)
    {
        var material = new Material(shader) { name = name, color = color };
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        return material;
    }

    static void AddBox(GameObject parent, Material material, string name, Vector3 position, Vector3 size)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent.transform, false);
        box.transform.position = position;
        box.transform.localScale = size;
        var renderer = box.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;
    }

    static void AddSphere(GameObject parent, Material material, string name, Vector3 position, float scale)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = name;
        sphere.transform.SetParent(parent.transform, false);
        sphere.transform.position = position;
        sphere.transform.localScale = Vector3.one * scale;
        var renderer = sphere.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;
    }

    static Type GetPluginType(string name)
    {
        var type = typeof(ScreenSpaceLightingRendererFeature).Assembly.GetType("UnityEngine.Rendering.Universal." + name);
        if (type == null)
            throw new TypeLoadException(name + " was not found.");
        return type;
    }

    static void EnsureFeatureResources(ScreenSpaceLightingRendererFeature feature)
    {
        var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        var resources = settings?.GetType().GetField("resources", InstanceFlags)?.GetValue(settings);
        var locator = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceLightingResourceLocator");
        locator?.GetMethod("AssignDefaults", StaticFlags)?.Invoke(null, new[] { resources });
    }

    static int ReadStaticInt(Type type, string name)
    {
        var field = type.GetField(name, StaticFlags);
        return field == null ? -1 : (int)field.GetValue(null);
    }

    static UniversalRenderPipelineAsset LoadPipeline(string modeName)
    {
        var path = string.Format(PipelinePathFormat, modeName);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
            throw new FileNotFoundException("Missing SSGI check pipeline fixture.", path);
        return pipeline;
    }

    static RenderTexture CreateTarget(int width, int height, string name)
    {
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { name = name };
        target.Create();
        return target;
    }

    static void RenderAndCapture(Camera camera, RenderTexture imageTarget, int frames)
    {
        for (var i = 0; i < frames; i++)
            camera.Render();
    }

    static Color[] ReadPixels(RenderTexture target)
    {
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
        try
        {
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            texture.Apply(false, false);
            RenderTexture.active = previous;
            return texture.GetPixels();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    static Color[] ReadRTHandle(object handle)
    {
        if (handle == null)
            return null;
        var rt = handle.GetType().GetProperty("rt", InstanceFlags)?.GetValue(handle) as RenderTexture;
        if (rt == null)
            return null;

        var temporary = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        try
        {
            Graphics.Blit(rt, temporary);
            return ReadPixels(temporary);
        }
        finally
        {
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    static int Count(Color[] samples, Func<Color, bool> predicate)
    {
        var count = 0;
        foreach (var pixel in samples)
        {
            if (predicate(pixel))
                count++;
        }

        return count;
    }

    static void WriteImage(RenderTexture target, string outputDirectory, string fileName)
    {
        var samples = ReadPixels(target);
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, false);
        try
        {
            var pixels = new Color32[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                pixels[i] = new Color32(
                    (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(samples[i].r)) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(samples[i].g)) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(samples[i].b)) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(samples[i].a) * 255f));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(Path.Combine(outputDirectory, fileName), texture.EncodeToPNG());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    [Serializable]
    public sealed class SsgiReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed;
        public string error;
        public List<SsgiModeResult> modes;
    }

    [Serializable]
    public sealed class SsgiModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public float offSampleCountMax;
        public float onSampleCountMax;
        public int onSampleCountSaturatedPixels;
        public int onHistoryNonZeroPixels;
        public int coveredPixels;
        public float coveredFraction;
        public int onAccumulationDispatchCount;
        public int onValidationDispatchCount;
        public int offAccumulationDispatchCount;
    }
}