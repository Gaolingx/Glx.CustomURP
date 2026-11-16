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

// SSGI miss-fallback check for the Forward+ reflection probe atlas.
//
// A runtime reflection probe with a solid-colour custom cubemap covers the right half of the view.
// For each renderer the check measures the SSGI signal for the four rayMiss tiers:
// - None must keep the hit-only coverage;
// - Sky must fill every miss with the ambient probe;
// - ReflectionProbes must pick up the probe colour (Forward+ atlas; default cubemap elsewhere);
// - ProbesAndSky must combine the two, so it differs from both.
// The probe colour is then swapped from green to magenta to prove the Forward+ atlas follows the
// probe contents rather than a constant.
public static class ScreenSpaceLightingSsgiFallbackCheck
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string RendererPathFormat = RendererRoot + "/SslT8{0}Renderer.asset";
    const string PipelinePathFormat = RendererRoot + "/SslT8{0}Pipeline.asset";
    const string DefaultOutputRoot = "PLAN/TODO/03-SSRImprove2/validation/T4/SsgiFallback";
    const string TempScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/~SsgiFallbackCheckTemp.unity";
    const string ProbeCubemapPath = "Assets/Tests/ScreenSpaceLighting/Scenes/~SsgiFallbackProbeCubemap.asset";

    const int ImageWidth = 320;
    const int ImageHeight = 180;
    const int WarmupFrames = 10;
    const int ProbeFaceSize = 32;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly RayMarchingFallbackHierarchy[] MissModes =
    {
        RayMarchingFallbackHierarchy.None,
        RayMarchingFallbackHierarchy.Sky,
        RayMarchingFallbackHierarchy.ReflectionProbes,
        RayMarchingFallbackHierarchy.ReflectionProbesAndSky
    };

    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Run SSGI Fallback Check")]
    public static void RunSsgiFallbackCheck()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputRoot = Environment.GetEnvironmentVariable("SSL_SSGI_FALLBACK_OUT");
        var outputDirectory = Path.Combine(projectRoot, string.IsNullOrEmpty(outputRoot) ? DefaultOutputRoot : outputRoot);
        Directory.CreateDirectory(outputDirectory);

        var report = new FallbackReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            passed = true,
            modes = new List<FallbackModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        Volume volume = null;
        VolumeProfile originalProfile = null;
        VolumeProfile testProfile = null;
        GameObject probeRoot = null;
        Cubemap probeCubemap = null;
        ReflectionProbe probe = null;

        try
        {
            // A probe created at runtime is not part of Unity's reflection probe culling list in the
            // editor (visibleReflectionProbes stays empty), so author it into a temporary scene copy
            // and reopen that copy: culling then treats it like any scene probe.
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var baselineRunner = UnityEngine.Object.FindObjectOfType<ScreenSpaceLightingBaselineRunner>();
            if (baselineRunner != null)
                baselineRunner.enabled = false;

            var probeCamera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (probeCamera == null)
                throw new InvalidOperationException("The verification scene has no camera.");

            AssetDatabase.DeleteAsset(ProbeCubemapPath);
            probeCubemap = new Cubemap(ProbeFaceSize, TextureFormat.RGBAFloat, false) { name = "SsgiFallbackProbeCubemap" };
            AssetDatabase.CreateAsset(probeCubemap, ProbeCubemapPath);
            FillCubemap(probeCubemap, new Color(0.15f, 3.0f, 0.15f));
            AssetDatabase.SaveAssets();

            probeRoot = CreateProbe(probeCamera, probeCubemap, out probe);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), TempScenePath, true);
            EditorSceneManager.OpenScene(TempScenePath, OpenSceneMode.Single);
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
            probe = UnityEngine.Object.FindObjectOfType<ReflectionProbe>();
            if (probe == null)
                throw new InvalidOperationException("The temporary scene lost the fallback reflection probe.");

            var volumeObject = GameObject.Find("Baseline Volume");
            if (volumeObject == null || volumeObject.GetComponent<Volume>() == null)
                throw new InvalidOperationException("The verification scene has no Baseline Volume.");
            volume = volumeObject.GetComponent<Volume>();
            originalProfile = volume.sharedProfile;
            testProfile = ScriptableObject.Instantiate(originalProfile);
            testProfile.name = originalProfile.name + " (SSGI fallback)";
            volume.sharedProfile = testProfile;

            DisableOtherScreenSpaceEffects(testProfile);
            var ssgi = testProfile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault()
                       ?? testProfile.Add<ScreenSpaceGlobalIllumination>(true);
            ApplySsgiSettings(ssgi);

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslSsgiFallbackImage");
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
                    probe,
                    probeCubemap,
                    imageTarget,
                    outputDirectory);
                report.modes.Add(mode);
                report.passed &= mode.passed;
                Debug.Log($"SSGI fallback {mode.name}: passed={mode.passed} issues={string.Join("; ", mode.issues)}");
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
            if (probeRoot != null)
                UnityEngine.Object.DestroyImmediate(probeRoot);

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
            else
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(TempScenePath);
            AssetDatabase.DeleteAsset(ProbeCubemapPath);
            File.WriteAllText(Path.Combine(outputDirectory, "SsgiFallbackCheck.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"SSGI fallback check finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static FallbackModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        ScreenSpaceGlobalIllumination ssgi,
        ReflectionProbe probe,
        Cubemap probeCubemap,
        RenderTexture imageTarget,
        string outputDirectory)
    {
        var result = new FallbackModeResult { name = modeName, issues = new List<string>(), misses = new List<FallbackMissResult>() };
        var pipeline = LoadPipeline(modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(RendererPathFormat, modeName));
        if (renderer == null)
            throw new FileNotFoundException("Missing SSGI fallback renderer fixture.", string.Format(RendererPathFormat, modeName));
        var feature = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (feature == null)
            throw new InvalidOperationException("The SSGI fallback renderer is missing the Screen Space Lighting feature.");
        EnsureFeatureResources(feature);

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        probe.enabled = true;
        result.probeTextureAssigned = probe.texture != null;
        result.visibleProbeCount = CountVisibleProbes(camera);

        // Green probe for the rayMiss sweep.
        FillCubemap(probeCubemap, new Color(0.15f, 3.0f, 0.15f));

        foreach (var missMode in MissModes)
        {
            ssgi.rayMiss.Override(missMode);
            cameraData.resetHistory = true;
            RenderAndCapture(camera, imageTarget, WarmupFrames);
            var samples = ReadSsgiOutput(feature);
            var miss = Summarize(missMode, samples);
            result.misses.Add(miss);

            WriteImage(imageTarget, outputDirectory, $"SsgiFallback_{modeName}_{missMode}.png");
            WriteColors(samples, ImageWidth, ImageHeight, outputDirectory, $"SsgiFallback_{modeName}_{missMode}_Ssgi.png");
        }

        var none = FindMiss(result, RayMarchingFallbackHierarchy.None);
        var sky = FindMiss(result, RayMarchingFallbackHierarchy.Sky);
        var probes = FindMiss(result, RayMarchingFallbackHierarchy.ReflectionProbes);
        var both = FindMiss(result, RayMarchingFallbackHierarchy.ReflectionProbesAndSky);

        if (none == null || sky == null || probes == null || both == null)
        {
            result.issues.Add("A rayMiss capture is missing.");
            result.passed = false;
            return result;
        }

        result.noneCoveredFraction = none.coveredFraction;
        result.skyCoveredFraction = sky.coveredFraction;
        result.probesCoveredFraction = probes.coveredFraction;
        result.probesAndSkyCoveredFraction = both.coveredFraction;
        result.noneVsSkyDelta = PremultipliedDelta(none, sky);
        result.skyVsProbesDelta = PremultipliedDelta(sky, probes);
        result.probesVsBothDelta = PremultipliedDelta(probes, both);
        result.skyVsBothDelta = PremultipliedDelta(sky, both);
        result.noneVsProbesDelta = PremultipliedDelta(none, probes);

        bool forwardPlus = modeName == "ForwardPlus";

        if (none.nonFinitePixels > 0 || sky.nonFinitePixels > 0 || probes.nonFinitePixels > 0 || both.nonFinitePixels > 0)
            result.issues.Add("A rayMiss capture contains NaN/Inf pixels.");
        if (none.coveredFraction >= sky.coveredFraction * 0.5f)
            result.issues.Add($"None coverage {none.coveredFraction:P2} is not clearly below Sky {sky.coveredFraction:P2}.");
        if (sky.coveredFraction < 0.2f)
            result.issues.Add($"Sky fills only {sky.coveredFraction:P2} of the pixels; the fallback has no signal.");
        if (result.skyVsProbesDelta < 0.005f)
            result.issues.Add($"ReflectionProbes and Sky are nearly identical (delta {result.skyVsProbesDelta:F4}).");
        if (result.noneVsSkyDelta < 0.002f)
            result.issues.Add($"None and Sky are nearly identical (delta {result.noneVsSkyDelta:F4}).");

        // ProbesAndSky only mixes in Forward+, where the atlas reports a partial probe weight.
        // Forward/Deferred keep the default cubemap at weight 1, so they match ReflectionProbes.
        if (forwardPlus)
        {
            if (probes.coveredFraction < 0.2f)
                result.issues.Add($"ReflectionProbes covers only {probes.coveredFraction:P2} of the pixels; the atlas has no probe.");
            if (result.probesVsBothDelta < 0.002f)
                result.issues.Add($"ProbesAndSky does not add the sky remainder (delta {result.probesVsBothDelta:F4}).");
            if (result.skyVsBothDelta < 0.002f)
                result.issues.Add($"ProbesAndSky does not use the probe (delta {result.skyVsBothDelta:F4}).");

            float tint = probes.meanG - Mathf.Max(probes.meanR, probes.meanB);
            result.probeGreenTint = tint;
            if (tint < 0.005f)
                result.issues.Add($"ReflectionProbes is not tinted by the green probe (G-max(R,B)={tint:F4}).");

            ssgi.rayMiss.Override(RayMarchingFallbackHierarchy.ReflectionProbes);
            FillCubemap(probeCubemap, new Color(3.0f, 0.15f, 1.2f));
            cameraData.resetHistory = true;
            RenderAndCapture(camera, imageTarget, WarmupFrames);
            var magenta = Summarize(RayMarchingFallbackHierarchy.ReflectionProbes, ReadSsgiOutput(feature));
            result.magentaMeanR = magenta.meanR;
            result.magentaMeanG = magenta.meanG;
            result.magentaMeanB = magenta.meanB;
            result.greenVsMagentaDelta = Mathf.Abs(magenta.meanR - probes.meanR) +
                                         Mathf.Abs(magenta.meanG - probes.meanG) +
                                         Mathf.Abs(magenta.meanB - probes.meanB);
            WriteImage(imageTarget, outputDirectory, $"SsgiFallback_{modeName}_ReflectionProbes_Magenta.png");
            if (result.greenVsMagentaDelta < 0.01f)
                result.issues.Add($"Atlas colour did not follow the probe swap (delta {result.greenVsMagentaDelta:F4}).");
            if (!(magenta.meanR > magenta.meanG && magenta.meanB > magenta.meanG))
                result.issues.Add("The magenta probe is not magenta-dominant in the fallback.");

            FillCubemap(probeCubemap, new Color(0.15f, 3.0f, 0.15f));
        }
        else
        {
            // Forward and Deferred read the default cubemap, so no colour assertion is made here.
            result.probesMean = new Float3(probes.meanR, probes.meanG, probes.meanB);
        }

        result.passed = result.issues.Count == 0;
        return result;
    }

    static FallbackMissResult Summarize(RayMarchingFallbackHierarchy missMode, Color[] samples)
    {
        var result = new FallbackMissResult { missMode = (int)missMode, missName = missMode.ToString() };
        if (samples == null)
        {
            result.missName += " (missing output)";
            return result;
        }

        double sumR = 0.0, sumG = 0.0, sumB = 0.0, sumWeight = 0.0, sumPremul = 0.0;
        int covered = 0;
        int nonFinite = 0;
        foreach (var pixel in samples)
        {
            if (!IsFinite(pixel.r) || !IsFinite(pixel.g) || !IsFinite(pixel.b) || !IsFinite(pixel.a))
            {
                nonFinite++;
                continue;
            }

            float weight = Mathf.Clamp01(pixel.a);
            sumWeight += weight;
            sumPremul += weight * (pixel.r + pixel.g + pixel.b) / 3.0;
            if (weight > 0.01f)
            {
                sumR += pixel.r;
                sumG += pixel.g;
                sumB += pixel.b;
                covered++;
            }
        }

        result.nonFinitePixels = nonFinite;
        result.coveredPixels = covered;
        result.coveredFraction = covered / (float)Mathf.Max(1, samples.Length);
        result.meanWeight = (float)(sumWeight / Mathf.Max(1, samples.Length));
        result.premultipliedMean = (float)(sumPremul / Mathf.Max(1, samples.Length));
        if (covered > 0)
        {
            result.meanR = (float)(sumR / covered);
            result.meanG = (float)(sumG / covered);
            result.meanB = (float)(sumB / covered);
        }

        return result;
    }

    static FallbackMissResult FindMiss(FallbackModeResult mode, RayMarchingFallbackHierarchy missMode)
    {
        return mode.misses.FirstOrDefault(miss => miss.missMode == (int)missMode);
    }

    static float PremultipliedDelta(FallbackMissResult a, FallbackMissResult b)
    {
        return Mathf.Abs(a.premultipliedMean - b.premultipliedMean);
    }

    static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    static void ApplySsgiSettings(ScreenSpaceGlobalIllumination ssgi)
    {
        ssgi.enabled.Override(true);
        ssgi.rayMiss.Override(RayMarchingFallbackHierarchy.None);
        ssgi.fullResolution.Override(true);
        ssgi.maxRaySteps.Override(24);
        ssgi.depthBufferThickness.Override(0.1f);
        ssgi.intensity.Override(1f);
        // The raw reprojection is the fallback: no history and no denoiser may mix modes.
        ssgi.temporalAccumulation.Override(false);
        ssgi.denoise.Override(false);
        ssgi.secondDenoiserPass.Override(false);
        ssgi.halfResolutionDenoiser.Override(false);
        ssgi.bilateralUpsample.Override(true);
    }

    static void DisableOtherScreenSpaceEffects(VolumeProfile profile)
    {
        var ssr = profile.components.OfType<ScreenSpaceReflection>().FirstOrDefault();
        if (ssr != null)
            ssr.enabled.Override(false);
        var refraction = profile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
        if (refraction != null)
            refraction.enabled.Override(false);
    }

    static GameObject CreateProbe(Camera camera, Cubemap cubemap, out ReflectionProbe probe)
    {
        var root = new GameObject("SSGI Fallback Probe");
        var viewDirection = camera.transform.forward;
        var right = Vector3.Cross(Vector3.up, viewDirection).normalized;
        // Cover the right half of the view so ProbesAndSky has sky-only pixels to fill.
        root.transform.position = camera.transform.position + viewDirection * 12f + right * 6f;
        probe = root.AddComponent<ReflectionProbe>();
        probe.mode = ReflectionProbeMode.Custom;
        probe.customBakedTexture = cubemap;
        probe.size = new Vector3(14f, 12f, 40f);
        probe.center = Vector3.zero;
        probe.blendDistance = 0.01f;
        probe.boxProjection = true;
        probe.importance = 1;
        probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        probe.cullingMask = ~0;
        probe.enabled = true;
        return root;
    }

    static void FillCubemap(Cubemap cubemap, Color color)
    {
        var pixels = new Color[cubemap.width * cubemap.width];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = color;
        for (int face = 0; face < 6; face++)
            cubemap.SetPixels(pixels, (CubemapFace)face);
        cubemap.Apply(false, false);
    }

    static UniversalRenderPipelineAsset LoadPipeline(string modeName)
    {
        var path = string.Format(PipelinePathFormat, modeName);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
            throw new FileNotFoundException("Missing SSGI fallback pipeline fixture.", path);
        return pipeline;
    }

    static void EnsureFeatureResources(ScreenSpaceLightingRendererFeature feature)
    {
        var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        var resources = settings?.GetType().GetField("resources", InstanceFlags)?.GetValue(settings);
        var locator = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceLightingResourceLocator");
        locator?.GetMethod("AssignDefaults", StaticFlags)?.Invoke(null, new[] { resources });
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

    static int CountVisibleProbes(Camera camera)
    {
        int count = -1;
        void Handler(ScriptableRenderContext context, Camera renderedCamera)
        {
            if (renderedCamera != camera)
                return;
            if (!camera.TryGetCullingParameters(out var parameters))
                return;
            parameters.cullingOptions |= CullingOptions.NeedsLighting | CullingOptions.NeedsReflectionProbes;
            parameters.reflectionProbeSortingCriteria = ReflectionProbeSortingCriteria.ImportanceThenSize;
            count = context.Cull(ref parameters).visibleReflectionProbes.Length;
        }

        RenderPipelineManager.beginCameraRendering += Handler;
        try
        {
            camera.Render();
        }
        finally
        {
            RenderPipelineManager.beginCameraRendering -= Handler;
        }

        return count;
    }

    static Color[] ReadSsgiOutput(ScreenSpaceLightingRendererFeature feature)
    {
        var pass = feature.GetType().GetProperty("ssgiPass", InstanceFlags)?.GetValue(feature);
        var handle = pass?.GetType().GetProperty("finalTexture", InstanceFlags)?.GetValue(pass);
        return ReadRTHandle(handle);
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

    static void WriteImage(RenderTexture target, string outputDirectory, string fileName)
    {
        WriteColors(ReadPixels(target), target.width, target.height, outputDirectory, fileName);
    }

    static void WriteColors(Color[] samples, int width, int height, string outputDirectory, string fileName)
    {
        if (samples == null)
            return;

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        try
        {
            var pixels = new Color32[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                float weight = Mathf.Clamp01(samples[i].a);
                pixels[i] = new Color32(
                    ToByte(samples[i].r * weight),
                    ToByte(samples[i].g * weight),
                    ToByte(samples[i].b * weight),
                    255);
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

    static byte ToByte(float linear)
    {
        return (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(linear)) * 255f);
    }

    [Serializable]
    public sealed class FallbackReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed;
        public string error;
        public List<FallbackModeResult> modes;
    }

    [Serializable]
    public sealed class FallbackModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public List<FallbackMissResult> misses;
        public float noneCoveredFraction;
        public float skyCoveredFraction;
        public float probesCoveredFraction;
        public float probesAndSkyCoveredFraction;
        public float noneVsSkyDelta;
        public float noneVsProbesDelta;
        public float skyVsProbesDelta;
        public float skyVsBothDelta;
        public float probesVsBothDelta;
        public float probeGreenTint;
        public float magentaMeanR;
        public float magentaMeanG;
        public float magentaMeanB;
        public float greenVsMagentaDelta;
        public Float3 probesMean;
        public bool probeTextureAssigned;
        public int visibleProbeCount;
    }

    [Serializable]
    public sealed class FallbackMissResult
    {
        public int missMode;
        public string missName;
        public int coveredPixels;
        public float coveredFraction;
        public float meanWeight;
        public float premultipliedMean;
        public float meanR;
        public float meanG;
        public float meanB;
        public int nonFinitePixels;
    }

    [Serializable]
    public struct Float3
    {
        public float x;
        public float y;
        public float z;

        public Float3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }
}