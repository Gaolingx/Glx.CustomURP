using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class ScreenSpaceLightingBaselineRunner : MonoBehaviour
{
    public const int DefaultSampleFrames = 1800;
    public const int TargetFrameRate = 60;

    public UniversalRenderPipelineAsset[] pipelines;
    public Camera baselineCamera;
    public int warmupFrames = 60;
    public int sampleFrames = DefaultSampleFrames;
    public int randomSeed = 14012;

    IEnumerator Start()
    {
#if UNITY_EDITOR
        yield break;
#else
        Application.runInBackground = true;
        var outputRoot = GetOutputRoot();
        Directory.CreateDirectory(outputRoot);
        var report = new BaselineReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
            graphicsMemorySizeMB = SystemInfo.graphicsMemorySize,
            supportsComputeShaders = SystemInfo.supportsComputeShaders,
            randomSeed = randomSeed,
            warmupFrames = warmupFrames,
            sampleFrames = sampleFrames,
            targetFrameRate = TargetFrameRate,
            modes = new List<ModeResult>()
        };
        var routine = CaptureAll(outputRoot, report);

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
                File.WriteAllText(Path.Combine(outputRoot, "BaselineError.txt"), exception.ToString());
                Application.Quit(2);
                yield break;
            }

            if (!moveNext)
                yield break;
            yield return routine.Current;
        }
#endif
    }

    IEnumerator CaptureAll(string outputRoot, BaselineReport report)
    {
        if (pipelines == null || pipelines.Length != 3 || baselineCamera == null)
            throw new InvalidOperationException("The T1 baseline requires three Pipeline Assets and one fixed camera.");

        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = TargetFrameRate;
        UnityEngine.Random.InitState(randomSeed);
        Screen.SetResolution(1280, 720, false);
        baselineCamera.enabled = true;

        for (var i = 0; i < 5; i++)
            yield return new WaitForEndOfFrame();

        report.width = Screen.width;
        report.height = Screen.height;
        report.initialGraphicsDriverBytes = Profiler.GetAllocatedMemoryForGraphicsDriver();
        report.initialAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong();
        report.initialReservedBytes = Profiler.GetTotalReservedMemoryLong();

        for (var i = 0; i < pipelines.Length; i++)
            yield return CaptureMode(pipelines[i], outputRoot, report);

        foreach (var mode in report.modes)
        {
            if (!mode.screenshotCaptured || mode.magentaPixelFraction > 0.01f || !mode.gpuTimingAvailable || mode.cpuSampleCount < sampleFrames / 2 || mode.graphicsDriverBytes <= 0)
                throw new InvalidOperationException("Incomplete baseline evidence for " + mode.name + ". See BaselineReport.json.");
        }

        report.finalGraphicsDriverBytes = Profiler.GetAllocatedMemoryForGraphicsDriver();
        report.finalAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong();
        report.finalReservedBytes = Profiler.GetTotalReservedMemoryLong();
        report.completed = true;
        WriteReport(outputRoot, report);
        Application.Quit(0);
    }

    IEnumerator CaptureMode(UniversalRenderPipelineAsset pipeline, string outputRoot, BaselineReport report)
    {
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        var waitForEndOfFrame = new WaitForEndOfFrame();

        for (var i = 0; i < warmupFrames; i++)
            yield return waitForEndOfFrame;

        var result = new ModeResult
        {
            name = pipeline.name,
            graphicsDriverBytes = Profiler.GetAllocatedMemoryForGraphicsDriver(),
            allocatedBytes = Profiler.GetTotalAllocatedMemoryLong(),
            reservedBytes = Profiler.GetTotalReservedMemoryLong()
        };
        var cpuTimes = new List<double>(sampleFrames);
        var gpuTimes = new List<double>(sampleFrames);
        var rawTimings = new List<RawTiming>(sampleFrames);
        var timings = new FrameTiming[1];

        for (var i = 0; i < sampleFrames; i++)
        {
            FrameTimingManager.CaptureFrameTimings();
            yield return waitForEndOfFrame;
            var timingReturned = FrameTimingManager.GetLatestTimings(1, timings) != 0;
            rawTimings.Add(new RawTiming
            {
                frame = i,
                timingReturned = timingReturned,
                cpuMs = timingReturned ? timings[0].cpuFrameTime : 0.0,
                gpuMs = timingReturned ? timings[0].gpuFrameTime : 0.0,
                utcTicks = DateTime.UtcNow.Ticks
            });
            if (!timingReturned)
                continue;
            if (timings[0].cpuFrameTime > 0)
                cpuTimes.Add(timings[0].cpuFrameTime);
            if (timings[0].gpuFrameTime > 0)
                gpuTimes.Add(timings[0].gpuFrameTime);
        }
        WriteRawTimings(outputRoot, pipeline.name, rawTimings);

        result.cpuSampleCount = cpuTimes.Count;
        result.gpuSampleCount = gpuTimes.Count;
        result.cpuMeanMs = Mean(cpuTimes);
        result.cpuMedianMs = Percentile(cpuTimes, 0.5);
        result.cpuP95Ms = Percentile(cpuTimes, 0.95);
        result.gpuMeanMs = Mean(gpuTimes);
        result.gpuMedianMs = Percentile(gpuTimes, 0.5);
        result.gpuP95Ms = Percentile(gpuTimes, 0.95);
        result.gpuTimingAvailable = gpuTimes.Count >= sampleFrames / 2;

        var screenshotName = pipeline.name.Replace("Ssl", string.Empty).Replace("Pipeline", string.Empty) + ".png";
        var screenshotPath = Path.Combine(outputRoot, screenshotName);
        yield return waitForEndOfFrame;
        var screenshot = ScreenCapture.CaptureScreenshotAsTexture();
        try
        {
            File.WriteAllBytes(screenshotPath, screenshot.EncodeToPNG());
            var pixels = screenshot.GetPixels32();
            var magentaPixels = 0;
            for (var i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                if (pixel.r > 200 && pixel.b > 200 && pixel.g < 60)
                    magentaPixels++;
            }
            result.magentaPixelFraction = (float)magentaPixels / pixels.Length;
        }
        finally
        {
            UnityEngine.Object.Destroy(screenshot);
        }
        result.screenshotFile = screenshotName;
        result.screenshotCaptured = File.Exists(screenshotPath) && new FileInfo(screenshotPath).Length > 0;
        report.modes.Add(result);
        WriteReport(outputRoot, report);
    }

    static double Mean(List<double> values)
    {
        return values.Count == 0 ? 0.0 : values.Average();
    }

    static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0.0;
        var sorted = values.OrderBy(value => value).ToArray();
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Mathf.Clamp(index, 0, sorted.Length - 1)];
    }

    static string GetOutputRoot()
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var i = 0; i < arguments.Length - 1; i++)
        {
            if (arguments[i] == "--ssl-baseline-output")
                return Path.GetFullPath(arguments[i + 1]);
        }
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BaselineOutput"));
    }

    static void WriteRawTimings(string outputRoot, string mode, List<RawTiming> rawTimings)
    {
        var path = Path.Combine(outputRoot, "RawFrameTimes.csv");
        using (var writer = new StreamWriter(path, true))
        {
            if (writer.BaseStream.Length == 0)
                writer.WriteLine("Mode,Frame,TimingReturned,CpuMs,GpuMs,UtcTicks");
            foreach (var timing in rawTimings)
            {
                writer.WriteLine(string.Join(",", new[]
                {
                    mode,
                    timing.frame.ToString(CultureInfo.InvariantCulture),
                    timing.timingReturned.ToString(CultureInfo.InvariantCulture),
                    timing.cpuMs.ToString("F6", CultureInfo.InvariantCulture),
                    timing.gpuMs.ToString("F6", CultureInfo.InvariantCulture),
                    timing.utcTicks.ToString(CultureInfo.InvariantCulture)
                }));
            }
        }
    }

    static void WriteReport(string outputRoot, BaselineReport report)
    {
        File.WriteAllText(Path.Combine(outputRoot, "BaselineReport.json"), JsonUtility.ToJson(report, true));
        using (var writer = new StreamWriter(Path.Combine(outputRoot, "FrameTimes.csv")))
        {
            writer.WriteLine("Mode,CpuSamples,CpuMeanMs,CpuMedianMs,CpuP95Ms,GpuSamples,GpuMeanMs,GpuMedianMs,GpuP95Ms,GraphicsDriverBytes,AllocatedBytes,ReservedBytes,ScreenshotCaptured,MagentaPixelFraction");
            foreach (var mode in report.modes)
            {
                writer.WriteLine(string.Join(",", new[]
                {
                    mode.name,
                    mode.cpuSampleCount.ToString(CultureInfo.InvariantCulture),
                    mode.cpuMeanMs.ToString("F4", CultureInfo.InvariantCulture),
                    mode.cpuMedianMs.ToString("F4", CultureInfo.InvariantCulture),
                    mode.cpuP95Ms.ToString("F4", CultureInfo.InvariantCulture),
                    mode.gpuSampleCount.ToString(CultureInfo.InvariantCulture),
                    mode.gpuMeanMs.ToString("F4", CultureInfo.InvariantCulture),
                    mode.gpuMedianMs.ToString("F4", CultureInfo.InvariantCulture),
                    mode.gpuP95Ms.ToString("F4", CultureInfo.InvariantCulture),
                    mode.graphicsDriverBytes.ToString(CultureInfo.InvariantCulture),
                    mode.allocatedBytes.ToString(CultureInfo.InvariantCulture),
                    mode.reservedBytes.ToString(CultureInfo.InvariantCulture),
                    mode.screenshotCaptured.ToString(CultureInfo.InvariantCulture),
                    mode.magentaPixelFraction.ToString("F6", CultureInfo.InvariantCulture)
                }));
            }
        }
    }

    struct RawTiming
    {
        public int frame;
        public bool timingReturned;
        public double cpuMs;
        public double gpuMs;
        public long utcTicks;
    }

    [Serializable]
    sealed class BaselineReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public string graphicsDeviceVersion;
        public int graphicsMemorySizeMB;
        public bool supportsComputeShaders;
        public int randomSeed;
        public int warmupFrames;
        public int sampleFrames;
        public int targetFrameRate;
        public int width;
        public int height;
        public long initialGraphicsDriverBytes;
        public long initialAllocatedBytes;
        public long initialReservedBytes;
        public long finalGraphicsDriverBytes;
        public long finalAllocatedBytes;
        public long finalReservedBytes;
        public bool completed;
        public string error;
        public List<ModeResult> modes;
    }

    [Serializable]
    sealed class ModeResult
    {
        public string name;
        public int cpuSampleCount;
        public int gpuSampleCount;
        public double cpuMeanMs;
        public double cpuMedianMs;
        public double cpuP95Ms;
        public double gpuMeanMs;
        public double gpuMedianMs;
        public double gpuP95Ms;
        public bool gpuTimingAvailable;
        public long graphicsDriverBytes;
        public long allocatedBytes;
        public long reservedBytes;
        public string screenshotFile;
        public bool screenshotCaptured;
        public float magentaPixelFraction;
    }
}
