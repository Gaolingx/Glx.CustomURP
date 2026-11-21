using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Player-only driver for the clustered deferred A/B and light-count sweep.
//
// The arena (ground, walls, main light) lives in the scene; this runner spawns the light field
// and the receiving geometry deterministically, then walks:
//   mode (stencil deferred / clustered deferred / forward+) x light count
// with FrameTimingManager sampling and one screenshot per mode at the A/B light count.
// The clustered path and the stencil path use the same deferred renderer settings; only
// UniversalRendererData.clusteredDeferredLighting differs.
public sealed class ClusteredDeferredArenaRunner : MonoBehaviour
{
    public UniversalRenderPipelineAsset stencilDeferredPipeline;
    public UniversalRenderPipelineAsset clusteredDeferredPipeline;
    public UniversalRenderPipelineAsset forwardPlusPipeline;

    // T4 shadow subset: same renderer pair, different pipeline shadow settings.
    public UniversalRenderPipelineAsset shadowStencilPipeline;
    public UniversalRenderPipelineAsset shadowClusteredPipeline;
    public UniversalRenderPipelineAsset screenShadowStencilPipeline;
    public UniversalRenderPipelineAsset screenShadowClusteredPipeline;

    // T4 feature subset: the renderer pair has light layers enabled and a GTAO feature
    // (indirect AO always on, direct-light AO toggled by the runner).
    public UniversalRenderPipelineAsset featureStencilPipeline;
    public UniversalRenderPipelineAsset featureClusteredPipeline;
    public Texture2D cookieTexture;
    public Light arenaMainLight;

    public Camera arenaCamera;
    public Material sphereMaterial;
    public Material metalMaterial;
    public Material receiveShadowsOffMaterial;

    public int warmupFrames = 40;
    public int lightCountChangeWarmupFrames = 15;
    public int sampleFrames = 150;
    public int[] lightCounts = { 0, 8, 16, 32, 48, 64 };
    public int totalLights = 64;
    public int screenshotLightCount = 48;
    public int width = 1280;
    public int height = 720;

    const string StencilMode = "StencilDeferred";
    const string ClusteredMode = "ClusteredDeferred";
    const string ForwardPlusMode = "ForwardPlus";
    const uint kDefaultRenderingLayer = 1u;

    readonly List<Light> m_Lights = new List<Light>();
    readonly List<float> m_LightIntensities = new List<float>();
    readonly List<float> m_LightRanges = new List<float>();
    readonly List<float> m_LightSpotAngles = new List<float>();
    readonly List<float> m_LightInnerSpotAngles = new List<float>();
    readonly List<Vector3> m_LightPositions = new List<Vector3>();
    readonly List<Quaternion> m_LightRotations = new List<Quaternion>();
    readonly List<Renderer> m_ReceiverRenderers = new List<Renderer>();
    readonly List<Material> m_ReceiverMaterials = new List<Material>();
    readonly List<ModeReport> m_Modes = new List<ModeReport>();
    readonly Dictionary<string, Texture2D> m_Screenshots = new Dictionary<string, Texture2D>();

    IEnumerator Start()
    {
#if UNITY_EDITOR
        yield break;
#else
        Application.runInBackground = true;

        var outputRoot = GetArgument("--cd-output");
        if (string.IsNullOrEmpty(outputRoot))
            outputRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ClusteredDeferredOutput"));
        Directory.CreateDirectory(outputRoot);

        if (HasArgument("--cd-t4"))
        {
            var featureReport = new FeatureMatrixReport
            {
                utcTimestamp = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
                entries = new List<FeatureEntry>(),
            };

            // A failing case (for example a locked output file) must end the run; an unhandled
            // coroutine exception stops the routine but leaves the player rendering forever.
            var featureRoutine = RunFeatureMatrix(outputRoot, featureReport);
            while (true)
            {
                bool moveNext;
                try
                {
                    moveNext = featureRoutine.MoveNext();
                }
                catch (Exception exception)
                {
                    featureReport.error = exception.ToString();
                    WriteFeatureMatrix(outputRoot, featureReport);
                    File.WriteAllText(Path.Combine(outputRoot, "CdError.txt"), exception.ToString());
                    Application.Quit(2);
                    yield break;
                }

                if (!moveNext)
                    break;
                yield return featureRoutine.Current;
            }

            Application.Quit(ReportHadFailure(featureReport) ? 2 : 0);
            yield break;
        }

        var captureOnly = HasArgument("--cd-capture");
        var report = new PerfReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
            supportsComputeShaders = SystemInfo.supportsComputeShaders,
            width = width,
            height = height,
            warmupFrames = warmupFrames,
            sampleFrames = sampleFrames,
            lightCounts = lightCounts,
            screenshotLightCount = screenshotLightCount,
            captureOnly = captureOnly,
            modes = m_Modes,
            diffs = new List<DiffReport>(),
        };

        var routine = captureOnly
            ? RunCaptureOnly(outputRoot, report)
            : RunSweep(outputRoot, report);

        while (true)
        {
            bool moveNext;
            try
            {
                moveNext = routine.MoveNext();
            }
            catch (Exception exception)
            {
                report.completed = false;
                report.error = exception.ToString();
                WriteReport(outputRoot, report);
                File.WriteAllText(Path.Combine(outputRoot, "CdError.txt"), exception.ToString());
                Application.Quit(2);
                yield break;
            }

            if (!moveNext)
                yield break;
            yield return routine.Current;
        }
#endif
    }

#if !UNITY_EDITOR
    IEnumerator RunCaptureOnly(string outputRoot, PerfReport report)
    {
        SetupArena();
        SetLightCount(totalLights);
        yield return SetPipeline(clusteredDeferredPipeline);
        yield return WaitFrames(Mathf.Max(warmupFrames, 60));

        report.width = Screen.width;
        report.height = Screen.height;
        report.completed = true;
        WriteReport(outputRoot, report);
        Application.Quit(0);
    }

    IEnumerator RunSweep(string outputRoot, PerfReport report)
    {
        SetupArena();
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 0;
        Screen.SetResolution(width, height, false);

        var pipelines = new[]
        {
            new ModePipelines { name = StencilMode, pipeline = stencilDeferredPipeline },
            new ModePipelines { name = ClusteredMode, pipeline = clusteredDeferredPipeline },
            new ModePipelines { name = ForwardPlusMode, pipeline = forwardPlusPipeline },
        };

        for (var modeIndex = 0; modeIndex < pipelines.Length; modeIndex++)
        {
            var mode = pipelines[modeIndex];
            if (mode.pipeline == null)
                throw new InvalidOperationException("Missing pipeline for " + mode.name);

            yield return SetPipeline(mode.pipeline);
            yield return WaitFrames(warmupFrames);

            var modeReport = new ModeReport { name = mode.name, samples = new List<LightCountSample>() };
            m_Modes.Add(modeReport);
            for (var countIndex = 0; countIndex < lightCounts.Length; countIndex++)
            {
                var lightCount = Mathf.Clamp(lightCounts[countIndex], 0, totalLights);
                SetLightCount(lightCount);
                yield return WaitFrames(lightCountChangeWarmupFrames);

                var sample = new LightCountSample { lightCount = lightCount };
                var cpuTimes = new List<double>(sampleFrames);
                var gpuTimes = new List<double>(sampleFrames);
                var timings = new FrameTiming[1];
                var screenshotRequested = lightCount == screenshotLightCount;

                for (var frame = 0; frame < sampleFrames; frame++)
                {
                    FrameTimingManager.CaptureFrameTimings();
                    yield return new WaitForEndOfFrame();
                    if (FrameTimingManager.GetLatestTimings(1, timings) == 0)
                        continue;
                    if (timings[0].cpuFrameTime > 0)
                        cpuTimes.Add(timings[0].cpuFrameTime);
                    if (timings[0].gpuFrameTime > 0)
                        gpuTimes.Add(timings[0].gpuFrameTime);
                }

                sample.cpuSamples = cpuTimes.Count;
                sample.gpuSamples = gpuTimes.Count;
                sample.cpuMeanMs = Mean(cpuTimes);
                sample.cpuMedianMs = Percentile(cpuTimes, 0.5);
                sample.cpuP95Ms = Percentile(cpuTimes, 0.95);
                sample.gpuMeanMs = Mean(gpuTimes);
                sample.gpuMedianMs = Percentile(gpuTimes, 0.5);
                sample.gpuP95Ms = Percentile(gpuTimes, 0.95);

                if (screenshotRequested)
                {
                    yield return new WaitForEndOfFrame();
                    var key = mode.name + "_L" + lightCount;
                    var texture = ScreenCapture.CaptureScreenshotAsTexture();
                    m_Screenshots[key] = texture;
                    var fileName = key + ".png";
                    File.WriteAllBytes(Path.Combine(outputRoot, fileName), texture.EncodeToPNG());
                    sample.screenshotFile = fileName;
                    sample.magentaPixelFraction = MagentaFraction(texture.GetPixels32());
                }

                modeReport.samples.Add(sample);
                WriteReport(outputRoot, report);
            }
        }

        ComputeDiffs(report.diffs);
        WriteReport(outputRoot, report);
        WriteCsv(outputRoot, report);

        foreach (var pair in m_Screenshots)
            UnityEngine.Object.Destroy(pair.Value);

        report.completed = true;
        WriteReport(outputRoot, report);
        Application.Quit(0);
    }

    IEnumerator RunFeatureMatrix(string outputRoot, FeatureMatrixReport report)
    {
        SetupArena();
        SetLightCount(totalLights);
        // The sweep keeps the default +Z spot orientation; the shadow A/B tilts the spots down so
        // their shadow slices land on the receivers instead of behind them. ResetLightState runs
        // before every capture, so the tilted orientation has to become the recorded baseline.
        for (var i = 0; i < m_Lights.Count; i++)
        {
            if (m_Lights[i].type == LightType.Spot)
            {
                m_Lights[i].transform.rotation = Quaternion.Euler(55f, 0f, 0f);
                m_LightRotations[i] = m_Lights[i].transform.rotation;
            }
        }
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 0;
        Screen.SetResolution(width, height, false);
        yield return SetPipeline(stencilDeferredPipeline);
        yield return WaitFrames(warmupFrames);

        yield return RunFeatureCase(outputRoot, report, "MainShadowCascade",
            shadowStencilPipeline, shadowClusteredPipeline, ApplyMainLightShadows);
        yield return RunFeatureCase(outputRoot, report, "ReceiveShadowsOff",
            shadowStencilPipeline, shadowClusteredPipeline, ApplyReceiveShadowsOff);
        yield return RunFeatureCase(outputRoot, report, "MainShadowScreen",
            screenShadowStencilPipeline, screenShadowClusteredPipeline, ApplyMainLightShadows);
        // Last: this case moves six spot lights above the receiver row.
        yield return RunFeatureCase(outputRoot, report, "AdditionalShadowAtlas",
            shadowStencilPipeline, shadowClusteredPipeline, ApplyAdditionalLightShadows);

        yield return RunFeatureCase(outputRoot, report, "LightCookie",
            featureStencilPipeline, featureClusteredPipeline, ApplyLightCookies);
        yield return RunFeatureCase(outputRoot, report, "LightLayers",
            featureStencilPipeline, featureClusteredPipeline, ApplyLightLayers);
        yield return RunFeatureCase(outputRoot, report, "ScreenSpaceAO",
            featureStencilPipeline, featureClusteredPipeline, ApplyDirectAmbientOcclusion);

        WriteFeatureMatrix(outputRoot, report);
    }

    // One feature at a time: reset every shadow switch, toggle only the feature under test,
    // capture stencil and clustered with the feature off and on.
    IEnumerator RunFeatureCase(string outputRoot, FeatureMatrixReport report, string feature,
        UniversalRenderPipelineAsset stencil, UniversalRenderPipelineAsset clustered, Action<bool> applyFeature)
    {
        if (stencil == null || clustered == null)
            throw new InvalidOperationException("Missing T4 pipeline for " + feature);

        var entry = new FeatureEntry
        {
            feature = feature,
            states = new List<FeatureState>(),
            comparisons = new List<FeatureComparison>(),
        };
        report.entries.Add(entry);

        var shots = new Dictionary<string, Texture2D>();
        yield return CaptureFeatureState(outputRoot, entry, shots, feature, "StencilOff", stencil, applyFeature, false);
        yield return CaptureFeatureState(outputRoot, entry, shots, feature, "ClusteredOff", clustered, applyFeature, false);
        yield return CaptureFeatureState(outputRoot, entry, shots, feature, "StencilOn", stencil, applyFeature, true);
        yield return CaptureFeatureState(outputRoot, entry, shots, feature, "ClusteredOn", clustered, applyFeature, true);

        entry.comparisons.Add(CompareFeature("stencil_vs_clustered_on",
            shots[feature + "_StencilOn"], shots[feature + "_ClusteredOn"]));
        entry.comparisons.Add(CompareFeature("stencil_vs_clustered_off",
            shots[feature + "_StencilOff"], shots[feature + "_ClusteredOff"]));
        entry.comparisons.Add(CompareFeature("clustered_feature_effect",
            shots[feature + "_ClusteredOff"], shots[feature + "_ClusteredOn"]));
        entry.comparisons.Add(CompareFeature("stencil_feature_effect",
            shots[feature + "_StencilOff"], shots[feature + "_StencilOn"]));

        var noMagenta = true;
        foreach (var state in entry.states)
            noMagenta &= state.magentaPixelFraction < 0.001f;

        // Liveness: the feature must move at least some pixels beyond the 8/255 noise band.
        entry.featureActive = entry.comparisons[2].pixelsOverThreshold > 0 ||
            entry.comparisons[2].meanChannelDiff > 0.05;
        entry.passed = noMagenta && entry.featureActive &&
            entry.comparisons[0].passed && entry.comparisons[1].passed;

        foreach (var pair in shots)
            UnityEngine.Object.Destroy(pair.Value);
        shots.Clear();

        WriteFeatureMatrix(outputRoot, report);
        yield return null;
    }

    IEnumerator CaptureFeatureState(string outputRoot, FeatureEntry entry, Dictionary<string, Texture2D> shots,
        string feature, string state, UniversalRenderPipelineAsset pipeline, Action<bool> applyFeature, bool on)
    {
        ResetLightState();
        applyFeature(on);
        yield return SetPipeline(pipeline);
        yield return WaitFrames(Mathf.Max(warmupFrames, 30));
        yield return new WaitForEndOfFrame();

        var texture = ScreenCapture.CaptureScreenshotAsTexture();
        var key = feature + "_" + state;
        shots[key] = texture;
        File.WriteAllBytes(Path.Combine(outputRoot, key + ".png"), texture.EncodeToPNG());
        entry.states.Add(new FeatureState
        {
            name = state,
            file = key + ".png",
            magentaPixelFraction = MagentaFraction(texture.GetPixels32()),
        });
    }

    void ResetLightState()
    {
        if (arenaMainLight != null)
        {
            arenaMainLight.shadows = LightShadows.None;
            arenaMainLight.cookie = null;
            arenaMainLight.GetUniversalAdditionalLightData().renderingLayers = kDefaultRenderingLayer;
        }
        for (var i = 0; i < m_Lights.Count; i++)
        {
            var light = m_Lights[i];
            light.enabled = i < totalLights;
            light.intensity = m_LightIntensities[i];
            light.range = m_LightRanges[i];
            light.spotAngle = m_LightSpotAngles[i];
            light.innerSpotAngle = m_LightInnerSpotAngles[i];
            light.shadows = LightShadows.None;
            light.cookie = null;
            light.GetUniversalAdditionalLightData().renderingLayers = kDefaultRenderingLayer;
            light.transform.SetPositionAndRotation(m_LightPositions[i], m_LightRotations[i]);
        }
        for (var i = 0; i < m_ReceiverRenderers.Count; i++)
        {
            m_ReceiverRenderers[i].sharedMaterial = m_ReceiverMaterials[i];
            m_ReceiverRenderers[i].renderingLayerMask = kDefaultRenderingLayer;
        }
        // The AO case owns direct-light AO; every other case renders with it off.
        SetDirectAoStrength(featureStencilPipeline, 0f);
        SetDirectAoStrength(featureClusteredPipeline, 0f);
    }

    // Spot cookies: isolate six spot lights above the receiver row (the 64-light field dilutes
    // the pattern) and attach the radial checker texture on the "on" side.
    void ApplyLightCookies(bool on)
    {
        var cookieLights = 0;
        for (var i = 0; i < m_Lights.Count; i++)
        {
            var light = m_Lights[i];
            if (light.type != LightType.Spot || cookieLights >= 6)
            {
                light.enabled = false;
                continue;
            }

            light.transform.SetPositionAndRotation(
                new Vector3(-12f + cookieLights * 5f, 6.5f, -5f), Quaternion.Euler(90f, 0f, 0f));
            light.range = 22f;
            light.spotAngle = 60f;
            light.innerSpotAngle = 40f;
            light.intensity = 25f;
            light.cookie = on ? cookieTexture : null;
            cookieLights++;
        }
    }

    // Light layers: receivers all sit on layer 1. With the feature on, half the lights move to
    // layer 2 and are culled per pixel; off, every light is on layer 1.
    void ApplyLightLayers(bool on)
    {
        for (var i = 0; i < m_Lights.Count; i++)
        {
            var light = m_Lights[i];
            light.enabled = i < 8;
            light.GetUniversalAdditionalLightData().renderingLayers = on && (i & 1) == 1 ? 2u : kDefaultRenderingLayer;
        }
    }

    // GTAO direct-light AO is a per-camera feature setting; both feature pipelines get the same
    // strength so the A/B only compares the deferred lighting path.
    void ApplyDirectAmbientOcclusion(bool on)
    {
        SetDirectAoStrength(featureStencilPipeline, on ? 1f : 0f);
        SetDirectAoStrength(featureClusteredPipeline, on ? 1f : 0f);
    }

    // The feature settings class and field are internal to URP; reflection keeps this test asset
    // script independent from the package internals.
    static void SetDirectAoStrength(UniversalRenderPipelineAsset pipeline, float strength)
    {
        if (pipeline == null)
            return;

        var rendererListField = pipeline.GetType().GetField("m_RendererDataList",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var rendererList = rendererListField?.GetValue(pipeline) as ScriptableRendererData[];
        if (rendererList == null || rendererList.Length == 0 || rendererList[0] == null)
            return;

        foreach (var feature in rendererList[0].rendererFeatures)
        {
            if (feature == null || feature.GetType().Name != "GroundTruthAmbientOcclusionRendererFeature")
                continue;

            var settingsField = feature.GetType().GetField("m_Settings",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var settings = settingsField?.GetValue(feature);
            var strengthField = settings?.GetType().GetField("directLightingStrength",
                BindingFlags.Instance | BindingFlags.NonPublic);
            strengthField?.SetValue(settings, strength);
        }
    }

    void ApplyMainLightShadows(bool on)
    {
        if (arenaMainLight != null)
            arenaMainLight.shadows = on ? LightShadows.Soft : LightShadows.None;
    }

    // Main-light shadows stay on; half the receivers get the _RECEIVE_SHADOWS_OFF material. The
    // GBuffer material flag must make the kernel skip shadow sampling for those pixels.
    void ApplyReceiveShadowsOff(bool off)
    {
        ApplyMainLightShadows(true);
        if (receiveShadowsOffMaterial == null)
            return;
        for (var i = 0; i < m_ReceiverRenderers.Count; i++)
            m_ReceiverRenderers[i].sharedMaterial = off && i % 2 == 0 ? receiveShadowsOffMaterial : m_ReceiverMaterials[i];
    }

    // Point-light shadows need six atlas slices each; keep the subset to spots so the 2048 atlas
    // holds every slice. The six casters are moved above the front receiver row and the other
    // punctual lights are turned off: among 64 overlapping lights their shadows would be invisible.
    void ApplyAdditionalLightShadows(bool on)
    {
        var shadowCasters = 0;
        for (var i = 0; i < m_Lights.Count; i++)
        {
            var light = m_Lights[i];
            if (light.type != LightType.Spot || shadowCasters >= 6)
            {
                light.enabled = false;
                light.shadows = LightShadows.None;
                continue;
            }

            light.transform.SetPositionAndRotation(
                new Vector3(-12f + shadowCasters * 5f, 5.5f, -5f), Quaternion.Euler(90f, 0f, 0f));
            light.range = 20f;
            light.spotAngle = 50f;
            light.innerSpotAngle = 30f;
            light.intensity = 40f;
            light.shadows = on ? LightShadows.Soft : LightShadows.None;
            shadowCasters++;
        }
    }

    static FeatureComparison CompareFeature(string name, Texture2D reference, Texture2D candidate)
    {
        var diff = Compare(0, reference, candidate);
        return new FeatureComparison
        {
            name = name,
            pixels = diff.pixels,
            meanChannelDiff = diff.meanChannelDiff,
            meanChannelDiffNormalized = diff.meanChannelDiffNormalized,
            maxChannelDiff = diff.maxChannelDiff,
            pixelsOverThreshold = diff.pixelsOverThreshold,
            pixelsOverThresholdFraction = diff.pixelsOverThresholdFraction,
            passed = diff.passed,
        };
    }

    static bool ReportHadFailure(FeatureMatrixReport report)
    {
        for (var i = 0; i < report.entries.Count; i++)
        {
            if (!report.entries[i].passed)
                return true;
        }

        return false;
    }

    static void WriteFeatureMatrix(string outputRoot, FeatureMatrixReport report)
    {
        File.WriteAllText(Path.Combine(outputRoot, "T4FeatureMatrix.json"), JsonUtility.ToJson(report, true));
    }

    IEnumerator SetPipeline(UniversalRenderPipelineAsset pipeline)
    {
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        yield return null;
        yield return null;
    }

    IEnumerator WaitFrames(int frames)
    {
        var wait = new WaitForEndOfFrame();
        for (var i = 0; i < frames; i++)
            yield return wait;
    }

    void SetupArena()
    {
        if (arenaCamera == null)
            arenaCamera = Camera.main;
        if (arenaCamera == null)
            throw new InvalidOperationException("The arena scene needs a camera.");
        arenaCamera.enabled = true;

        var root = new GameObject("Clustered Deferred Light Field").transform;
        root.SetParent(transform, false);

        var colors = new Color[totalLights];
        for (var i = 0; i < totalLights; i++)
        {
            var column = i % 8;
            var row = (i / 8) % 2;
            var depth = (i / 16) % 4;
            var position = new Vector3(-14f + column * 4f, 1.6f + row * 3.0f, -10f + depth * 6.6f);

            var lightObject = new GameObject("Field Light " + i);
            lightObject.transform.SetParent(root, false);
            lightObject.transform.position = position;
            var light = lightObject.AddComponent<Light>();
            light.type = i % 3 == 2 ? LightType.Spot : LightType.Point;
            light.range = 9f;
            light.intensity = light.type == LightType.Spot ? 12f : 8f;
            light.spotAngle = 55f;
            light.innerSpotAngle = 25f;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            light.color = Color.HSVToRGB((i * 0.61803398875f) % 1f, 0.55f, 1f);
            colors[i] = light.color;
            m_Lights.Add(light);
            m_LightIntensities.Add(light.intensity);
            m_LightRanges.Add(light.range);
            m_LightSpotAngles.Add(light.spotAngle);
            m_LightInnerSpotAngles.Add(light.innerSpotAngle);
            m_LightPositions.Add(position);
            m_LightRotations.Add(light.transform.rotation);

            // Each light gets a small emissive-looking sphere so the field is visible from the
            // camera; the spheres are Lit geometry that receives the same direct lighting.
            if (i % 2 == 0)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = "Emitter " + i;
                marker.transform.SetParent(root, false);
                marker.transform.position = position;
                marker.transform.localScale = Vector3.one * 0.18f;
                UnityEngine.Object.Destroy(marker.GetComponent<Collider>());
                marker.GetComponent<Renderer>().sharedMaterial = metalMaterial != null ? metalMaterial : sphereMaterial;
            }
        }

        // Receiving geometry: spheres of varying roughness and metalness plus tall boxes that
        // catch specular from the whole field.
        for (var i = 0; i < 24; i++)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "Receiver Sphere " + i;
            sphere.transform.SetParent(root, false);
            sphere.transform.position = new Vector3(-12f + (i % 6) * 5f, 1.4f, -5f + (i / 6) * 5.5f);
            sphere.transform.localScale = Vector3.one * (1.2f + (i % 3) * 0.5f);
            UnityEngine.Object.Destroy(sphere.GetComponent<Collider>());
            sphere.GetComponent<Renderer>().sharedMaterial = i % 3 == 0 && metalMaterial != null ? metalMaterial : sphereMaterial;
            m_ReceiverRenderers.Add(sphere.GetComponent<Renderer>());
            m_ReceiverMaterials.Add(sphere.GetComponent<Renderer>().sharedMaterial);
        }

        for (var i = 0; i < 4; i++)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "Receiver Box " + i;
            box.transform.SetParent(root, false);
            box.transform.position = new Vector3(-9f + i * 6f, 2.2f, 6f);
            box.transform.localScale = new Vector3(1.6f, 4.4f, 1.6f);
            UnityEngine.Object.Destroy(box.GetComponent<Collider>());
            box.GetComponent<Renderer>().sharedMaterial = sphereMaterial;
            m_ReceiverRenderers.Add(box.GetComponent<Renderer>());
            m_ReceiverMaterials.Add(box.GetComponent<Renderer>().sharedMaterial);
        }

        SetLightCount(0);
    }

    void SetLightCount(int count)
    {
        for (var i = 0; i < m_Lights.Count; i++)
            m_Lights[i].enabled = i < count;
    }

    void ComputeDiffs(List<DiffReport> diffs)
    {
        foreach (var lightCount in lightCounts)
        {
            Texture2D stencil;
            Texture2D clustered;
            if (!m_Screenshots.TryGetValue(StencilMode + "_L" + lightCount, out stencil) ||
                !m_Screenshots.TryGetValue(ClusteredMode + "_L" + lightCount, out clustered))
            {
                continue;
            }

            diffs.Add(Compare(lightCount, stencil, clustered));
        }
    }

    static DiffReport Compare(int lightCount, Texture2D reference, Texture2D candidate)
    {
        var a = reference.GetPixels32();
        var b = candidate.GetPixels32();
        var length = Mathf.Min(a.Length, b.Length);
        double sum = 0.0;
        var max = 0;
        var overThreshold = 0;

        for (var i = 0; i < length; i++)
        {
            var dr = Mathf.Abs(a[i].r - b[i].r);
            var dg = Mathf.Abs(a[i].g - b[i].g);
            var db = Mathf.Abs(a[i].b - b[i].b);
            var pixelMax = Mathf.Max(dr, Mathf.Max(dg, db));
            sum += dr + dg + db;
            max = Mathf.Max(max, pixelMax);
            if (pixelMax > 8)
                overThreshold++;
        }

        var mean = length > 0 ? sum / (length * 3.0) : 0.0;
        return new DiffReport
        {
            lightCount = lightCount,
            pixels = length,
            meanChannelDiff = mean,
            meanChannelDiffNormalized = mean / 255.0,
            maxChannelDiff = max,
            pixelsOverThreshold = overThreshold,
            pixelsOverThresholdFraction = length > 0 ? (double)overThreshold / length : 0.0,
            passed = mean <= 2.0 && max <= 24,
        };
    }

    static float MagentaFraction(Color32[] pixels)
    {
        if (pixels == null || pixels.Length == 0)
            return 1f;
        var count = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].r > 200 && pixels[i].b > 200 && pixels[i].g < 60)
                count++;
        }

        return (float)count / pixels.Length;
    }

    static double Mean(List<double> values)
    {
        if (values.Count == 0)
            return 0.0;
        double sum = 0.0;
        for (var i = 0; i < values.Count; i++)
            sum += values[i];
        return sum / values.Count;
    }

    static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0.0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Mathf.Clamp(index, 0, sorted.Length - 1)];
    }

    static string GetArgument(string name)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var i = 0; i < arguments.Length - 1; i++)
        {
            if (arguments[i] == name)
                return arguments[i + 1];
        }

        return null;
    }

    static bool HasArgument(string name)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == name)
                return true;
        }

        return false;
    }

    static void WriteReport(string outputRoot, PerfReport report)
    {
        var json = JsonUtility.ToJson(report, true);
        File.WriteAllText(Path.Combine(outputRoot, "CdPerf.json"), json);
    }

    static void WriteCsv(string outputRoot, PerfReport report)
    {
        using (var writer = new StreamWriter(Path.Combine(outputRoot, "CdPerf.csv")))
        {
            writer.WriteLine("Mode,LightCount,CpuSamples,CpuMeanMs,CpuMedianMs,CpuP95Ms,GpuSamples,GpuMeanMs,GpuMedianMs,GpuP95Ms,Screenshot,MagentaFraction");
            foreach (var mode in report.modes)
            {
                foreach (var sample in mode.samples)
                {
                    writer.WriteLine(string.Join(",", new[]
                    {
                        mode.name,
                        sample.lightCount.ToString(CultureInfo.InvariantCulture),
                        sample.cpuSamples.ToString(CultureInfo.InvariantCulture),
                        sample.cpuMeanMs.ToString("F4", CultureInfo.InvariantCulture),
                        sample.cpuMedianMs.ToString("F4", CultureInfo.InvariantCulture),
                        sample.cpuP95Ms.ToString("F4", CultureInfo.InvariantCulture),
                        sample.gpuSamples.ToString(CultureInfo.InvariantCulture),
                        sample.gpuMeanMs.ToString("F4", CultureInfo.InvariantCulture),
                        sample.gpuMedianMs.ToString("F4", CultureInfo.InvariantCulture),
                        sample.gpuP95Ms.ToString("F4", CultureInfo.InvariantCulture),
                        sample.screenshotFile ?? string.Empty,
                        sample.magentaPixelFraction.ToString("F6", CultureInfo.InvariantCulture),
                    }));
                }
            }
        }
    }
#endif

    [Serializable]
    sealed class ModePipelines
    {
        public string name;
        public UniversalRenderPipelineAsset pipeline;
    }

    [Serializable]
    public sealed class LightCountSample
    {
        public int lightCount;
        public int cpuSamples;
        public int gpuSamples;
        public double cpuMeanMs;
        public double cpuMedianMs;
        public double cpuP95Ms;
        public double gpuMeanMs;
        public double gpuMedianMs;
        public double gpuP95Ms;
        public string screenshotFile;
        public float magentaPixelFraction;
    }

    [Serializable]
    public sealed class ModeReport
    {
        public string name;
        public List<LightCountSample> samples;
    }

    [Serializable]
    public sealed class DiffReport
    {
        public int lightCount;
        public int pixels;
        public double meanChannelDiff;
        public double meanChannelDiffNormalized;
        public int maxChannelDiff;
        public int pixelsOverThreshold;
        public double pixelsOverThresholdFraction;
        public bool passed;
    }

    [Serializable]
    public sealed class FeatureState
    {
        public string name;
        public string file;
        public float magentaPixelFraction;
    }

    [Serializable]
    public sealed class FeatureComparison
    {
        public string name;
        public int pixels;
        public double meanChannelDiff;
        public double meanChannelDiffNormalized;
        public int maxChannelDiff;
        public int pixelsOverThreshold;
        public double pixelsOverThresholdFraction;
        public bool passed;
    }

    [Serializable]
    public sealed class FeatureEntry
    {
        public string feature;
        public bool featureActive;
        public bool passed;
        public List<FeatureState> states;
        public List<FeatureComparison> comparisons;
    }

    [Serializable]
    public sealed class FeatureMatrixReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public string graphicsDeviceVersion;
        public string error;
        public List<FeatureEntry> entries;
    }

    [Serializable]
    public sealed class PerfReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public string graphicsDeviceVersion;
        public bool supportsComputeShaders;
        public int width;
        public int height;
        public int warmupFrames;
        public int sampleFrames;
        public int[] lightCounts;
        public int screenshotLightCount;
        public bool captureOnly;
        public bool completed;
        public string error;
        public List<ModeReport> modes;
        public List<DiffReport> diffs;
    }
}
