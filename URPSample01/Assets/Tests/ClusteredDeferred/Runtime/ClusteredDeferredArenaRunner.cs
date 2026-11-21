using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    public Camera arenaCamera;
    public Material sphereMaterial;
    public Material metalMaterial;

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

    readonly List<Light> m_Lights = new List<Light>();
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
