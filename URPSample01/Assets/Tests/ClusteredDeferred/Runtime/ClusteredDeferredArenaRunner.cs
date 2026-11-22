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
//   mode (Forward+ / clustered deferred) x light count
// with FrameTimingManager sampling and screenshots at the requested light counts.
public sealed class ClusteredDeferredArenaRunner : MonoBehaviour
{
    public UniversalRenderPipelineAsset clusteredDeferredPipeline;
    public UniversalRenderPipelineAsset forwardPlusPipeline;
    // Wired into the build's scene copy, never the source scene.
    public UniversalRenderPipelineAsset parityAlternateClusteredPipeline;

    // T4 shadow subset: same renderer pair, different pipeline shadow settings.
    public UniversalRenderPipelineAsset shadowForwardPlusPipeline;
    public UniversalRenderPipelineAsset shadowClusteredPipeline;
    public UniversalRenderPipelineAsset screenShadowForwardPlusPipeline;
    public UniversalRenderPipelineAsset screenShadowClusteredPipeline;

    // T4 feature subset: the renderer pair has light layers enabled and a GTAO feature
    // (indirect AO always on, direct-light AO toggled by the runner).
    public UniversalRenderPipelineAsset featureForwardPlusPipeline;
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
    public int cpuBreakdownFrames = 120;
    public int[] lightCounts = { 0, 8, 16, 32, 48, 64 };
    public int totalLights = 64;
    public int screenshotLightCount = 48;
    public int width = 1280;
    public int height = 720;

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
    readonly List<Renderer> m_TransparentRenderers = new List<Renderer>();
    readonly HashSet<int> m_ScreenshotCounts = new HashSet<int>();
    RectInt m_TransparentRect;
    Material m_TransparentMaterial;
    bool m_TransparentEnabled;
    int m_PerObjectLightLimit = -1;
    readonly List<ModeReport> m_Modes = new List<ModeReport>();
    readonly Dictionary<string, Texture2D> m_Screenshots = new Dictionary<string, Texture2D>();
#if !UNITY_EDITOR
    ClusterListAccess m_ClusterAccess;
#endif

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

        var totalLightsArgument = GetArgument("--cd-total-lights");
        if (!string.IsNullOrEmpty(totalLightsArgument) && int.TryParse(totalLightsArgument, out var parsedTotalLights))
            totalLights = Mathf.Max(1, parsedTotalLights);

        var lightCountsArgument = GetArgument("--cd-light-counts");
        if (!string.IsNullOrEmpty(lightCountsArgument))
        {
            var counts = new List<int>();
            foreach (var token in lightCountsArgument.Split(','))
            {
                if (int.TryParse(token, out var count))
                    counts.Add(count);
            }
            if (counts.Count > 0)
                lightCounts = counts.ToArray();
        }

        var screenshotLightsArgument = GetArgument("--cd-screenshot-lights");
        if (!string.IsNullOrEmpty(screenshotLightsArgument))
        {
            var counts = new List<int>();
            foreach (var token in screenshotLightsArgument.Split(','))
            {
                if (int.TryParse(token, out var count) && count >= 0)
                    counts.Add(count);
            }
            if (counts.Count > 0)
            {
                screenshotLightCount = counts[0];
                m_ScreenshotCounts.UnionWith(counts);
            }
        }
        if (m_ScreenshotCounts.Count == 0)
            m_ScreenshotCounts.Add(screenshotLightCount);

        m_TransparentEnabled = HasArgument("--cd-transparent");
        if (m_TransparentEnabled)
            m_TransparentMaterial = Resources.Load<Material>("CdTransparent");
        var perObjectLimitArgument = GetArgument("--cd-per-object-limit");
        if (!string.IsNullOrEmpty(perObjectLimitArgument) && int.TryParse(perObjectLimitArgument, out var parsedPerObjectLimit))
            ApplyPerObjectLightLimit(Mathf.Clamp(parsedPerObjectLimit, 0, UniversalRenderPipeline.maxPerObjectLights));
        m_PerObjectLightLimit = clusteredDeferredPipeline != null ? clusteredDeferredPipeline.maxAdditionalLightsCount : 0;

        var samplesArgument = GetArgument("--cd-samples");
        if (!string.IsNullOrEmpty(samplesArgument) && int.TryParse(samplesArgument, out var parsedSamples))
            sampleFrames = Mathf.Max(1, parsedSamples);

        var warmupArgument = GetArgument("--cd-warmup");
        if (!string.IsNullOrEmpty(warmupArgument) && int.TryParse(warmupArgument, out var parsedWarmup))
            warmupFrames = Mathf.Max(0, parsedWarmup);

        var clusterAccess = ClusterListAccess.Create();
        m_ClusterAccess = clusterAccess;
        if (clusterAccess != null && HasArgument("--cd-prune-on"))
            clusterAccess.pruneByDepth = true;
        if (clusterAccess != null && HasArgument("--cd-prune-off"))
            clusterAccess.pruneByDepth = false;
        if (clusterAccess != null && HasArgument("--cd-prune-zonly"))
            clusterAccess.pruneSidePlanes = false;
        if (clusterAccess != null && HasArgument("--cd-prune-stats"))
            clusterAccess.pruneStatsEnabled = true;
        if (clusterAccess != null && HasArgument("--cd-compact-on"))
            clusterAccess.compactLists = true;
        if (clusterAccess != null && HasArgument("--cd-compact-off"))
            clusterAccess.compactLists = false;
        if (clusterAccess != null && HasArgument("--cd-compact-stats"))
            clusterAccess.compactStatsEnabled = true;
        if (clusterAccess != null && HasArgument("--cd-forward-cluster-off"))
            clusterAccess.forwardClusterLights = false;
        if (clusterAccess != null && HasArgument("--cd-forward-cluster-on"))
            clusterAccess.forwardClusterLights = true;

        if (HasArgument("--cd-parity"))
        {
            var parityReport = new BufferParityReport
            {
                utcTimestamp = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                lightCount = totalLights,
                supportsAsyncCompute = SystemInfo.supportsAsyncCompute,
            };

            var parityRoutine = RunBufferParity(outputRoot, parityReport, clusterAccess);
            while (true)
            {
                bool moveNext;
                try
                {
                    moveNext = parityRoutine.MoveNext();
                }
                catch (Exception exception)
                {
                    parityReport.error = exception.ToString();
                    WriteParity(outputRoot, parityReport);
                    File.WriteAllText(Path.Combine(outputRoot, "CdError.txt"), exception.ToString());
                    Application.Quit(2);
                    yield break;
                }

                if (!moveNext)
                    break;
                yield return parityRoutine.Current;
            }

            Application.Quit(parityReport.completed && parityReport.passed ? 0 : 2);
            yield break;
        }

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

        if (HasArgument("--cd-cpu"))
        {
            var cpuReport = new CpuBreakdownReport
            {
                utcTimestamp = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                lightCount = totalLights,
                width = width,
                height = height,
                sampleFrames = cpuBreakdownFrames,
                supportsAsyncCompute = SystemInfo.supportsAsyncCompute,
                modes = new List<CpuModeBreakdown>(),
            };

            var cpuRoutine = RunCpuBreakdown(outputRoot, cpuReport);
            while (true)
            {
                bool moveNext;
                try
                {
                    moveNext = cpuRoutine.MoveNext();
                }
                catch (Exception exception)
                {
                    cpuReport.error = exception.ToString();
                    WriteCpuBreakdown(outputRoot, cpuReport);
                    File.WriteAllText(Path.Combine(outputRoot, "CdError.txt"), exception.ToString());
                    Application.Quit(2);
                    yield break;
                }

                if (!moveNext)
                    break;
                yield return cpuRoutine.Current;
            }

            Application.Quit(string.IsNullOrEmpty(cpuReport.error) ? 0 : 2);
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
            supportsAsyncCompute = SystemInfo.supportsAsyncCompute,
            clusterListPrune = clusterAccess != null && clusterAccess.pruneByDepth,
            clusterListCompact = clusterAccess != null && clusterAccess.compactLists,
            forwardClusterLights = clusterAccess != null ? clusterAccess.forwardClusterLights : true,
            width = width,
            height = height,
            warmupFrames = warmupFrames,
            sampleFrames = sampleFrames,
            lightCounts = lightCounts,
            screenshotLightCount = screenshotLightCount,
            screenshotLightCounts = SortedScreenshotCounts(),
            transparentFixture = m_TransparentEnabled,
            perObjectLightLimit = m_PerObjectLightLimit,
            captureOnly = captureOnly,
            modes = m_Modes,
            forwardPlusDiffs = new List<DiffReport>(),
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
        bool forwardPlus = HasArgument("--cd-capture-forward-plus");
        var requestedPipeline = forwardPlus ? forwardPlusPipeline : clusteredDeferredPipeline;
        yield return SetPipeline(requestedPipeline);
        yield return WaitFrames(Mathf.Max(warmupFrames, 60));
        var active = UniversalRenderPipeline.asset;
        if (active == null || active != requestedPipeline)
            throw new InvalidOperationException("Capture pipeline asset is not active.");
        report.clusterListAsync = active.asyncLightListBuild;
        if (Shader.IsKeywordEnabled("_FORWARD_PLUS") != forwardPlus)
            throw new InvalidOperationException("Capture camera did not enter the requested rendering mode.");

        report.width = Screen.width;
        report.height = Screen.height;
        // Global keywords drive the variant selection for every forward draw, including the
        // transparent group; record them next to the capture so the .rdc's pass structure can be
        // tied to the light path that was actually compiled.
        report.additionalLightsPixelKeyword = Shader.IsKeywordEnabled("_ADDITIONAL_LIGHTS");
        report.additionalLightsVertexKeyword = Shader.IsKeywordEnabled("_ADDITIONAL_LIGHTS_VERTEX");
        report.forwardPlusKeyword = Shader.IsKeywordEnabled("_FORWARD_PLUS");
        report.forwardClusterKeyword = Shader.IsKeywordEnabled("_CLUSTERED_DEFERRED_LIGHTS");
        report.completed = true;
        WriteReport(outputRoot, report);

        // External capture tools attach after a fixed frame delay; keep the warmed-up clustered
        // frame presenting instead of quitting immediately.
        if (HasArgument("--cd-hold"))
        {
            while (true)
                yield return new WaitForEndOfFrame();
        }

        Application.Quit(0);
    }

    // Compare independent readbacks of the same light field under each GPU scheduling policy.
    IEnumerator RunBufferParity(string outputRoot, BufferParityReport report, ClusterListAccess access)
    {
        if (access == null)
            throw new InvalidOperationException("ClusterListGpuBuilder/ClusterListParityCapture are missing from the URP runtime assembly.");

        SetupArena();
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 0;
        Screen.SetResolution(width, height, false);
        SetLightCount(totalLights);
        var alternate = parityAlternateClusteredPipeline;
        if (alternate == null || alternate.asyncLightListBuild == clusteredDeferredPipeline.asyncLightListBuild)
            throw new InvalidOperationException("Parity needs clustered assets with opposite scheduling requests.");
        var asyncPipeline = alternate.asyncLightListBuild ? alternate : clusteredDeferredPipeline;
        var syncPipeline = alternate.asyncLightListBuild ? clusteredDeferredPipeline : alternate;
        var previousDefault = GraphicsSettings.defaultRenderPipeline;
        var previousQuality = QualitySettings.renderPipeline;
        var originalCapture = access.captureEnabled;
        ClusterWordSnapshot asyncSnapshot, syncSnapshot;
        try
        {
            yield return SetPipeline(asyncPipeline);
            yield return WaitFrames(warmupFrames);
            RequestArenaCapture(access);
            yield return WaitForGpuCapture(access);
            asyncSnapshot = access.ReadGpu();
            if (asyncSnapshot == null)
                throw new InvalidOperationException("No GPU list for the async scheduling request.");
            report.asyncLightListBuildRequested = UniversalRenderPipeline.asset.asyncLightListBuild;

            yield return SetPipeline(syncPipeline);
            yield return WaitFrames(warmupFrames);
            RequestArenaCapture(access);
            yield return WaitForGpuCapture(access);
            syncSnapshot = access.ReadGpu();
            if (syncSnapshot == null)
                throw new InvalidOperationException("The sync GPU list was not captured.");
        }
        finally
        {
            access.captureEnabled = originalCapture;
            GraphicsSettings.defaultRenderPipeline = previousDefault;
            QualitySettings.renderPipeline = previousQuality;
        }

        var difference = CompareSchedules(asyncSnapshot, syncSnapshot);
        report.zBinWords = asyncSnapshot.zBins.Length;
        report.tileWords = asyncSnapshot.tileMasks.Length;
        report.zBinUsedWords = asyncSnapshot.ZBinUsedWords;
        report.tileUsedWords = asyncSnapshot.TileUsedWords;
        report.binCount = asyncSnapshot.binCount;
        report.wordsPerTile = asyncSnapshot.wordsPerTile;
        report.tileWidth = asyncSnapshot.tileWidth;
        report.tileResolutionX = asyncSnapshot.tileResolution.x;
        report.tileResolutionY = asyncSnapshot.tileResolution.y;
        report.layoutMatches = difference.layoutMatches;
        report.asyncSyncZBinMismatchWords = difference.asyncSyncZBinMismatchWords;
        report.asyncSyncZBinMismatchBits = difference.asyncSyncZBinMismatchBits;
        report.asyncSyncTileMismatchWords = difference.asyncSyncTileMismatchWords;
        report.asyncSyncTileMismatchBits = difference.asyncSyncTileMismatchBits;
        report.asyncSyncFirstMismatch = difference.asyncSyncFirstMismatch;
        report.gpuTileBits = difference.gpuTileBits;
        report.itemCount = difference.itemCount;
        report.fullScreenItems = difference.fullScreenItems;
        report.minItemTiles = difference.minItemTiles;
        report.maxItemTiles = difference.maxItemTiles;
        report.medianItemTiles = difference.medianItemTiles;
        report.completed = true;
        report.passed = difference.passed;
        WriteParity(outputRoot, report);
    }

    void RequestArenaCapture(ClusterListAccess access)
    {
        // Request exactly one frame. A queued readback from the first schedule must not
        // satisfy the wait for the second schedule.
        access.ClearGpuCapture();
        access.captureEnabled = true;
        arenaCamera.Render();
        access.captureEnabled = false;
    }

    static IEnumerator WaitForGpuCapture(ClusterListAccess access)
    {
        for (var i = 0; i < 30 && access.ReadGpu() == null; i++)
            yield return null;
    }

    struct BufferParityDifference
    {
        public bool layoutMatches;
        public int asyncSyncZBinMismatchWords;
        public int asyncSyncZBinMismatchBits;
        public int asyncSyncTileMismatchWords;
        public int asyncSyncTileMismatchBits;
        public int asyncSyncFirstMismatch;
        public int gpuTileBits;
        public int itemCount;
        public int fullScreenItems;
        public int minItemTiles;
        public int maxItemTiles;
        public int medianItemTiles;

        public bool passed => layoutMatches &&
                              asyncSyncZBinMismatchWords == 0 && asyncSyncTileMismatchWords == 0 && gpuTileBits > 0;
    }

    static BufferParityDifference CompareSchedules(ClusterWordSnapshot asyncSnapshot, ClusterWordSnapshot syncSnapshot)
    {
        var difference = new BufferParityDifference
        {
            layoutMatches = asyncSnapshot.zBins.Length == syncSnapshot.zBins.Length &&
                            asyncSnapshot.tileMasks.Length == syncSnapshot.tileMasks.Length &&
                            asyncSnapshot.zBinScale == syncSnapshot.zBinScale &&
                            asyncSnapshot.zBinOffset == syncSnapshot.zBinOffset &&
                            asyncSnapshot.binCount == syncSnapshot.binCount &&
                            asyncSnapshot.wordsPerTile == syncSnapshot.wordsPerTile &&
                            asyncSnapshot.viewCount == syncSnapshot.viewCount &&
                            asyncSnapshot.tileWidth == syncSnapshot.tileWidth &&
                            asyncSnapshot.tileResolution == syncSnapshot.tileResolution,
            asyncSyncFirstMismatch = -1,
        };

        // Scheduling must not change a single word. Extra words outside the used prefix count too,
        // because the kernels clear the whole buffers.
        var zBinSchedule = DiffWords(asyncSnapshot.zBins, syncSnapshot.zBins, int.MaxValue);
        var tileSchedule = DiffWords(asyncSnapshot.tileMasks, syncSnapshot.tileMasks, int.MaxValue);
        difference.asyncSyncZBinMismatchWords = zBinSchedule.mismatchWords;
        difference.asyncSyncZBinMismatchBits = zBinSchedule.mismatchBits;
        difference.asyncSyncTileMismatchWords = tileSchedule.mismatchWords;
        difference.asyncSyncTileMismatchBits = tileSchedule.mismatchBits;
        difference.asyncSyncFirstMismatch = zBinSchedule.firstMismatch < 0 ? tileSchedule.firstMismatch :
            tileSchedule.firstMismatch < 0 ? zBinSchedule.firstMismatch : Mathf.Min(zBinSchedule.firstMismatch, tileSchedule.firstMismatch);

        difference.gpuTileBits = CountSetBits(asyncSnapshot.tileMasks, asyncSnapshot.TileUsedWords);
        ItemCoverage(asyncSnapshot, out difference.itemCount, out difference.fullScreenItems,
            out difference.minItemTiles, out difference.maxItemTiles, out difference.medianItemTiles);
        return difference;
    }

    // How loose the conservative tile AABB is: per-item tile coverage, including items that hit the
    // near-plane branch and therefore claim the whole screen.
    static void ItemCoverage(ClusterWordSnapshot snapshot, out int itemCount, out int fullScreenItems,
        out int minTiles, out int maxTiles, out int medianTiles)
    {
        itemCount = snapshot.wordsPerTile * 32;
        fullScreenItems = 0;
        minTiles = int.MaxValue;
        maxTiles = 0;
        medianTiles = 0;
        if (snapshot.tileMasks == null || snapshot.wordsPerTile <= 0)
        {
            minTiles = 0;
            return;
        }

        var tileCount = snapshot.tileResolution.x * snapshot.tileResolution.y;
        var coverage = new int[itemCount];
        for (var item = 0; item < itemCount; item++)
        {
            var word = item >> 5;
            var mask = 1u << (item & 31);
            var covered = 0;
            for (var tile = 0; tile < tileCount; tile++)
            {
                if ((snapshot.tileMasks[tile * snapshot.wordsPerTile + word] & mask) != 0)
                    covered++;
            }

            coverage[item] = covered;
            minTiles = Mathf.Min(minTiles, covered);
            maxTiles = Mathf.Max(maxTiles, covered);
            if (covered == tileCount)
                fullScreenItems++;
        }

        System.Array.Sort(coverage);
        medianTiles = itemCount > 0 ? coverage[itemCount / 2] : 0;
    }

    struct WordDiff
    {
        public int mismatchWords;
        public int mismatchBits;
        public int firstMismatch; // -1 when none inside the used prefix
    }

    static WordDiff DiffWords(uint[] expected, uint[] actual, int usedWords)
    {
        var diff = new WordDiff { firstMismatch = -1 };
        if (expected == null || actual == null)
            return diff;

        var length = Mathf.Min(expected.Length, actual.Length);
        for (var i = 0; i < length; i++)
        {
            if (expected[i] == actual[i])
                continue;

            var xor = expected[i] ^ actual[i];
            diff.mismatchWords++;
            diff.mismatchBits += CountSetBits(xor);
            if (i < usedWords && diff.firstMismatch < 0)
                diff.firstMismatch = i;
        }

        return diff;
    }

    static int CountSetBits(uint value)
    {
        var bits = 0;
        for (; value != 0; value &= value - 1)
            bits++;
        return bits;
    }

    static int CountSetBits(uint[] words, int count)
    {
        var bits = 0;
        for (var i = 0; i < count; i++)
            bits += CountSetBits(words[i]);
        return bits;
    }

    ModePipelines[] ComparisonModes()
    {
        if (forwardPlusPipeline == clusteredDeferredPipeline)
            throw new InvalidOperationException("The Forward+ reference must be independent of clustered deferred.");
        return new[]
        {
            new ModePipelines { name = ClusteredMode, pipeline = clusteredDeferredPipeline },
            new ModePipelines { name = ForwardPlusMode, pipeline = forwardPlusPipeline },
        };
    }

    // Record the CPU slices for the same two pipelines as the image sweep.
    IEnumerator RunCpuBreakdown(string outputRoot, CpuBreakdownReport report)
    {
        SetupArena();
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 0;
        Screen.SetResolution(width, height, false);
        SetLightCount(totalLights);

        var probe = CpuProbeAccess.Create();
        if (probe == null)
            throw new InvalidOperationException("ClusteredDeferredCpuProbe is missing from the URP runtime assembly.");

        var modes = ComparisonModes();

        for (var modeIndex = 0; modeIndex < modes.Length; modeIndex++)
        {
            var mode = modes[modeIndex];
            if (mode.pipeline == null)
                throw new InvalidOperationException("Missing pipeline for " + mode.name);

            yield return SetPipeline(mode.pipeline);
            yield return WaitFrames(warmupFrames);

            probe.SetEnabled(true);
            probe.Reset();
            var frameCpu = new List<double>(cpuBreakdownFrames);
            var timings = new FrameTiming[1];
            for (var frame = 0; frame < cpuBreakdownFrames; frame++)
            {
                FrameTimingManager.CaptureFrameTimings();
                yield return new WaitForEndOfFrame();
                if (FrameTimingManager.GetLatestTimings(1, timings) != 0 && timings[0].cpuFrameTime > 0)
                    frameCpu.Add(timings[0].cpuFrameTime);
            }

            var snapshot = probe.Snapshot(mode.name, Mean(frameCpu));
            report.clusterListAsync = snapshot.clusterListAsync = UniversalRenderPipeline.asset.asyncLightListBuild;
            report.modes.Add(snapshot);
            probe.SetEnabled(false);
            WriteCpuBreakdown(outputRoot, report);
        }

        report.completed = true;
        WriteCpuBreakdown(outputRoot, report);
        Application.Quit(0);
    }

    IEnumerator RunSweep(string outputRoot, PerfReport report)
    {
        SetupArena();
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 0;
        Screen.SetResolution(width, height, false);

        var pipelines = ComparisonModes();

        for (var modeIndex = 0; modeIndex < pipelines.Length; modeIndex++)
        {
            var mode = pipelines[modeIndex];
            if (mode.pipeline == null)
                throw new InvalidOperationException("Missing pipeline for " + mode.name);

            yield return SetPipeline(mode.pipeline);
            yield return WaitFrames(warmupFrames);

            report.clusterListAsync = UniversalRenderPipeline.asset.asyncLightListBuild;
            var modeReport = new ModeReport { name = mode.name, clusterListAsync = report.clusterListAsync, samples = new List<LightCountSample>() };
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
                var screenshotRequested = m_ScreenshotCounts.Contains(lightCount);

                if (m_ClusterAccess != null && m_ClusterAccess.pruneStatsEnabled)
                {
                    m_ClusterAccess.pruneKeptBits = -1;
                    m_ClusterAccess.pruneClearedBits = -1;
                }

                if (m_ClusterAccess != null && m_ClusterAccess.compactStatsEnabled)
                {
                    m_ClusterAccess.compactEntries = -1;
                    m_ClusterAccess.compactCells = -1;
                    m_ClusterAccess.compactOverflowCells = -1;
                    m_ClusterAccess.compactOverflowTiles = -1;
                    m_ClusterAccess.compactScannedCells = -1;
                }

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
                    if (m_TransparentRect.width == 0)
                        m_TransparentRect = ComputeProjectedRect();
                    if (Shader.IsKeywordEnabled("_FORWARD_PLUS") != (mode.name == ForwardPlusMode))
                        throw new InvalidOperationException("Unexpected _FORWARD_PLUS state for " + mode.name);
                    var key = mode.name + "_L" + lightCount;
                    var texture = ScreenCapture.CaptureScreenshotAsTexture();
                    m_Screenshots[key] = texture;
                    var fileName = key + ".png";
                    File.WriteAllBytes(Path.Combine(outputRoot, fileName), texture.EncodeToPNG());
                    sample.screenshotFile = fileName;
                    sample.magentaPixelFraction = MagentaFraction(texture.GetPixels32());
                }

                if (m_ClusterAccess != null && m_ClusterAccess.pruneStatsEnabled &&
                    mode.name == ClusteredMode)
                {
                    sample.pruneKeptBits = m_ClusterAccess.pruneKeptBits;
                    sample.pruneClearedBits = m_ClusterAccess.pruneClearedBits;
                }

                if (m_ClusterAccess != null && m_ClusterAccess.compactStatsEnabled &&
                    mode.name == ClusteredMode)
                {
                    sample.compactEntries = m_ClusterAccess.compactEntries;
                    sample.compactCells = m_ClusterAccess.compactCells;
                    sample.compactOverflowCells = m_ClusterAccess.compactOverflowCells;
                    sample.compactOverflowTiles = m_ClusterAccess.compactOverflowTiles;
                    sample.compactScannedCells = m_ClusterAccess.compactScannedCells;
                }

                modeReport.samples.Add(sample);
                WriteReport(outputRoot, report);
            }
        }

        ComputeDiffs(report.forwardPlusDiffs);
        report.transparentRectX = m_TransparentRect.x;
        report.transparentRectY = m_TransparentRect.y;
        report.transparentRectWidth = m_TransparentRect.width;
        report.transparentRectHeight = m_TransparentRect.height;
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
        yield return SetPipeline(shadowForwardPlusPipeline);
        yield return WaitFrames(warmupFrames);

        yield return RunFeatureCase(outputRoot, report, "MainShadowCascade",
            shadowForwardPlusPipeline, shadowClusteredPipeline, ApplyMainLightShadows);
        yield return RunFeatureCase(outputRoot, report, "ReceiveShadowsOff",
            shadowForwardPlusPipeline, shadowClusteredPipeline, ApplyReceiveShadowsOff);
        yield return RunFeatureCase(outputRoot, report, "MainShadowScreen",
            screenShadowForwardPlusPipeline, screenShadowClusteredPipeline, ApplyMainLightShadows);
        // Last: this case moves six spot lights above the receiver row.
        yield return RunFeatureCase(outputRoot, report, "AdditionalShadowAtlas",
            shadowForwardPlusPipeline, shadowClusteredPipeline, ApplyAdditionalLightShadows);

        yield return RunFeatureCase(outputRoot, report, "LightCookie",
            featureForwardPlusPipeline, featureClusteredPipeline, ApplyLightCookies);
        yield return RunFeatureCase(outputRoot, report, "LightLayers",
            featureForwardPlusPipeline, featureClusteredPipeline, ApplyLightLayers);
        yield return RunFeatureCase(outputRoot, report, "ScreenSpaceAO",
            featureForwardPlusPipeline, featureClusteredPipeline, ApplyDirectAmbientOcclusion);

        WriteFeatureMatrix(outputRoot, report);
    }

    // One feature at a time: reset every shadow switch, toggle only the feature under test,
    // capture forwardPlus and clustered with the feature off and on.
    IEnumerator RunFeatureCase(string outputRoot, FeatureMatrixReport report, string feature,
        UniversalRenderPipelineAsset forwardPlus, UniversalRenderPipelineAsset clustered, Action<bool> applyFeature)
    {
        if (forwardPlus == null || clustered == null || forwardPlus == clustered)
            throw new InvalidOperationException("Expected independent Forward+ and clustered pipelines for " + feature);

        var entry = new FeatureEntry
        {
            feature = feature,
            states = new List<FeatureState>(),
            comparisons = new List<FeatureComparison>(),
        };
        report.entries.Add(entry);

        var shots = new Dictionary<string, Texture2D>();
        yield return CaptureFeatureState(outputRoot, entry, shots, feature, "ForwardPlusOff", forwardPlus, applyFeature, false);
        yield return CaptureFeatureState(outputRoot, entry, shots, feature, "ClusteredOff", clustered, applyFeature, false);
        yield return CaptureFeatureState(outputRoot, entry, shots, feature, "ForwardPlusOn", forwardPlus, applyFeature, true);
        yield return CaptureFeatureState(outputRoot, entry, shots, feature, "ClusteredOn", clustered, applyFeature, true);

        entry.comparisons.Add(CompareFeature("forward_plus_vs_clustered_on",
            shots[feature + "_ForwardPlusOn"], shots[feature + "_ClusteredOn"]));
        entry.comparisons.Add(CompareFeature("forward_plus_vs_clustered_off",
            shots[feature + "_ForwardPlusOff"], shots[feature + "_ClusteredOff"]));
        entry.comparisons.Add(CompareFeature("clustered_feature_effect",
            shots[feature + "_ClusteredOff"], shots[feature + "_ClusteredOn"]));
        entry.comparisons.Add(CompareFeature("forward_plus_feature_effect",
            shots[feature + "_ForwardPlusOff"], shots[feature + "_ForwardPlusOn"]));

        var validStates = true;
        foreach (var state in entry.states)
            validStates &= state.magentaPixelFraction < 0.001f &&
                state.forwardPlusKeyword == state.name.StartsWith(ForwardPlusMode, StringComparison.Ordinal);

        // Liveness: the feature must move at least some pixels beyond the 8/255 noise band.
        entry.featureActive = HasFeatureEffect(entry.comparisons[2]) && HasFeatureEffect(entry.comparisons[3]);
        entry.passed = validStates && entry.featureActive &&
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
            forwardPlusKeyword = Shader.IsKeywordEnabled("_FORWARD_PLUS"),
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
        SetDirectAoStrength(featureForwardPlusPipeline, 0f);
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
        SetDirectAoStrength(featureForwardPlusPipeline, on ? 1f : 0f);
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
            m_ReceiverRenderers[i].sharedMaterial = off && i % 2 == 0 && m_ReceiverMaterials[i] == sphereMaterial
                ? receiveShadowsOffMaterial : m_ReceiverMaterials[i];
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

    static bool HasFeatureEffect(FeatureComparison effect) => effect.pixelsOverThreshold > 0 || effect.meanChannelDiff > 0.05;

    static FeatureComparison CompareFeature(string name, Texture2D reference, Texture2D candidate)
    {
        var diff = Compare(0, name, reference, name, candidate, new RectInt(0, 0, 0, 0));
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

        return report.entries.Count != 7;
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

    // Forward+ ignores the per-object cap; keep the setting equal on both sweep pipelines.
    void ApplyPerObjectLightLimit(int limit)
    {
        var pipelines = new[] { clusteredDeferredPipeline, forwardPlusPipeline, parityAlternateClusteredPipeline };
        for (var i = 0; i < pipelines.Length; i++)
        {
            if (pipelines[i] != null)
                pipelines[i].maxAdditionalLightsCount = limit;
        }
    }

    int[] SortedScreenshotCounts()
    {
        var counts = new List<int>(m_ScreenshotCounts);
        counts.Sort();
        return counts.ToArray();
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

        // Probe fixture: a row of alpha-blended Lit spheres between the camera and the receivers.
        // Measure clustered vs Forward+ inside their projected rect as well as over the frame.
        if (m_TransparentEnabled && m_TransparentMaterial != null)
        {
            for (var i = 0; i < 3; i++)
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "Transparent Receiver " + i;
                sphere.transform.SetParent(root, false);
                sphere.transform.position = new Vector3(-3.8f + i * 3.8f, 4.2f, -8f);
                sphere.transform.localScale = Vector3.one * 3f;
                UnityEngine.Object.Destroy(sphere.GetComponent<Collider>());
                sphere.GetComponent<Renderer>().sharedMaterial = m_TransparentMaterial;
                m_TransparentRenderers.Add(sphere.GetComponent<Renderer>());
            }
        }

        SetLightCount(0);
    }

    void SetLightCount(int count)
    {
        for (var i = 0; i < m_Lights.Count; i++)
            m_Lights[i].enabled = i < count;
    }

    // Union of the transparent group's projected bounds, in screen pixels (origin bottom-left, the
    // same origin GetPixels32 and WorldToScreenPoint use). It is a bounding rect, so it also covers
    // the background between the spheres.
    RectInt ComputeProjectedRect()
    {
        if (m_TransparentRenderers.Count == 0 || arenaCamera == null)
            return new RectInt(0, 0, 0, 0);

        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        for (var r = 0; r < m_TransparentRenderers.Count; r++)
        {
            var bounds = m_TransparentRenderers[r].bounds;
            for (var cornerIndex = 0; cornerIndex < 8; cornerIndex++)
            {
                var offset = new Vector3(
                    (cornerIndex & 1) == 0 ? -bounds.extents.x : bounds.extents.x,
                    (cornerIndex & 2) == 0 ? -bounds.extents.y : bounds.extents.y,
                    (cornerIndex & 4) == 0 ? -bounds.extents.z : bounds.extents.z);
                var screen = arenaCamera.WorldToScreenPoint(bounds.center + offset);
                if (screen.z <= 0f)
                    continue;
                min = Vector2.Min(min, screen);
                max = Vector2.Max(max, screen);
            }
        }

        var xMin = Mathf.Clamp(Mathf.FloorToInt(min.x) - 2, 0, Screen.width);
        var yMin = Mathf.Clamp(Mathf.FloorToInt(min.y) - 2, 0, Screen.height);
        var xMax = Mathf.Clamp(Mathf.CeilToInt(max.x) + 2, 0, Screen.width);
        var yMax = Mathf.Clamp(Mathf.CeilToInt(max.y) + 2, 0, Screen.height);
        if (xMax <= xMin || yMax <= yMin)
            return new RectInt(0, 0, 0, 0);
        return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
    }

    void ComputeDiffs(List<DiffReport> forwardPlusDiffs)
    {
        foreach (var count in SortedScreenshotCounts())
        {
            var reference = m_Screenshots[ForwardPlusMode + "_L" + count];
            var candidate = m_Screenshots[ClusteredMode + "_L" + count];
            forwardPlusDiffs.Add(Compare(count, ForwardPlusMode, reference, ClusteredMode, candidate, m_TransparentRect));
        }
    }

    static DiffReport Compare(int lightCount, string referenceName, Texture2D reference, string candidateName,
        Texture2D candidate, RectInt rect)
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
        var report = new DiffReport
        {
            lightCount = lightCount,
            reference = referenceName,
            candidate = candidateName,
            pixels = length,
            meanChannelDiff = mean,
            meanChannelDiffNormalized = mean / 255.0,
            maxChannelDiff = max,
            pixelsOverThreshold = overThreshold,
            pixelsOverThresholdFraction = length > 0 ? (double)overThreshold / length : 0.0,
            passed = mean <= 2.0 && max <= 24,
        };

        if (rect.width <= 0 || rect.height <= 0 || reference.width != candidate.width || reference.height != candidate.height)
            return report;

        var width = reference.width;
        double rectSum = 0.0;
        var rectMax = 0;
        var rectOver = 0;
        var rectPixels = 0;
        for (var y = rect.y; y < rect.yMax; y++)
        {
            for (var x = rect.x; x < rect.xMax; x++)
            {
                var index = y * width + x;
                if (index >= length)
                    continue;
                var dr = Mathf.Abs(a[index].r - b[index].r);
                var dg = Mathf.Abs(a[index].g - b[index].g);
                var db = Mathf.Abs(a[index].b - b[index].b);
                var pixelMax = Mathf.Max(dr, Mathf.Max(dg, db));
                rectSum += dr + dg + db;
                rectMax = Mathf.Max(rectMax, pixelMax);
                if (pixelMax > 8)
                    rectOver++;
                rectPixels++;
            }
        }

        report.hasTransparentRect = true;
        report.rectX = rect.x;
        report.rectY = rect.y;
        report.rectWidth = rect.width;
        report.rectHeight = rect.height;
        report.rectPixels = rectPixels;
        report.rectMeanChannelDiff = rectPixels > 0 ? rectSum / (rectPixels * 3.0) : 0.0;
        report.rectMaxChannelDiff = rectMax;
        report.rectPixelsOverThreshold = rectOver;
        report.rectPixelsOverThresholdFraction = rectPixels > 0 ? (double)rectOver / rectPixels : 0.0;
        report.rectPassed = report.rectMeanChannelDiff <= 2.0 && rectMax <= 24;
        return report;
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

    static void WriteCpuBreakdown(string outputRoot, CpuBreakdownReport report)
    {
        var json = JsonUtility.ToJson(report, true);
        File.WriteAllText(Path.Combine(outputRoot, "CdCpuBreakdown.json"), json);
    }

    static void WriteParity(string outputRoot, BufferParityReport report)
    {
        File.WriteAllText(Path.Combine(outputRoot, "G1BufferParity.json"), JsonUtility.ToJson(report, true));
    }

    // The probe type is internal to URP; reflection keeps this test asset script independent from
    // package internals (same approach as the GTAO settings above).
    sealed class CpuProbeAccess
    {
        const string TypeName = "UnityEngine.Rendering.Universal.Internal.ClusteredDeferredCpuProbe";
        static readonly string[] k_Slices =
        {
            "preSetup", "setupLights", "lightConstants",
            "resolveMixedLighting", "deferredSetupLights", "precomputeLights",
        };

        readonly Dictionary<string, FieldInfo> m_Fields = new Dictionary<string, FieldInfo>();
        readonly FieldInfo m_Enabled;
        readonly FieldInfo m_Frames;
        readonly MethodInfo m_Reset;

        public static CpuProbeAccess Create()
        {
            var type = typeof(UniversalRenderPipeline).Assembly.GetType(TypeName);
            return type == null ? null : new CpuProbeAccess(type);
        }

        CpuProbeAccess(Type type)
        {
            m_Enabled = type.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
            m_Frames = type.GetField("frames", BindingFlags.Public | BindingFlags.Static);
            m_Reset = type.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static);
            foreach (var slice in k_Slices)
                m_Fields[slice] = type.GetField(slice, BindingFlags.Public | BindingFlags.Static);
        }

        public void SetEnabled(bool value)
        {
            m_Enabled.SetValue(null, value);
        }

        public void Reset()
        {
            m_Reset.Invoke(null, null);
        }

        public CpuModeBreakdown Snapshot(string mode, double frameCpuMeanMs)
        {
            var frames = (int)m_Frames.GetValue(null);
            return new CpuModeBreakdown
            {
                mode = mode,
                frames = frames,
                frameCpuMeanMs = frameCpuMeanMs,
                preSetupMs = PerFrameMs("preSetup", frames),
                setupLightsMs = PerFrameMs("setupLights", frames),
                lightConstantsMs = PerFrameMs("lightConstants", frames),
                resolveMixedLightingMs = PerFrameMs("resolveMixedLighting", frames),
                deferredSetupLightsMs = PerFrameMs("deferredSetupLights", frames),
                precomputeLightsMs = PerFrameMs("precomputeLights", frames),
                otherMs = frames > 0
                    ? Math.Max(0.0, frameCpuMeanMs - PerFrameMs("preSetup", frames) - PerFrameMs("setupLights", frames))
                    : 0.0,
            };
        }

        double PerFrameMs(string slice, int frames)
        {
            if (frames <= 0 || !m_Fields.TryGetValue(slice, out var field) || field == null)
                return 0.0;
            var ticks = (long)field.GetValue(null);
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames;
        }
    }

    // The G1 builder and parity capture are internal to URP; reflection keeps this player runner
    // independent of package internals (same approach as CpuProbeAccess).
    sealed class ClusterListAccess
    {
        const string BuilderTypeName = "UnityEngine.Rendering.Universal.Internal.ClusterListGpuBuilder";
        const string CaptureTypeName = "UnityEngine.Rendering.Universal.Internal.ClusterListParityCapture";
        const string PassTypeName = "UnityEngine.Rendering.Universal.Internal.ClusteredDeferredPass";
        const BindingFlags k_Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        readonly FieldInfo m_ForwardClusterLights;
        readonly FieldInfo m_Prune;
        readonly FieldInfo m_PruneSidePlanes;
        readonly FieldInfo m_PruneStats;
        readonly FieldInfo m_PruneKept;
        readonly FieldInfo m_PruneCleared;
        readonly FieldInfo m_Compact;
        readonly FieldInfo m_CompactStats;
        readonly FieldInfo m_CompactEntries;
        readonly FieldInfo m_CompactCells;
        readonly FieldInfo m_CompactOverflowCells;
        readonly FieldInfo m_CompactOverflowTiles;
        readonly FieldInfo m_CompactScannedCells;
        readonly FieldInfo m_CaptureEnabled;
        readonly FieldInfo m_LastGpu;
        readonly FieldInfo m_ZBins;
        readonly FieldInfo m_TileMasks;
        readonly FieldInfo m_ZBinScale;
        readonly FieldInfo m_ZBinOffset;
        readonly FieldInfo m_BinCount;
        readonly FieldInfo m_WordsPerTile;
        readonly FieldInfo m_ViewCount;
        readonly FieldInfo m_TileWidth;
        readonly FieldInfo m_TileResolution;

        public static ClusterListAccess Create()
        {
            var assembly = typeof(UniversalRenderPipeline).Assembly;
            var builder = assembly.GetType(BuilderTypeName);
            var capture = assembly.GetType(CaptureTypeName);
            var pass = assembly.GetType(PassTypeName);
            return builder == null || capture == null || pass == null ? null : new ClusterListAccess(builder, capture, pass);
        }

        ClusterListAccess(Type builder, Type capture, Type pass)
        {
            m_ForwardClusterLights = pass.GetField("forwardClusterLights", k_Static);
            m_Prune = builder.GetField("pruneByDepth", k_Static);
            m_PruneSidePlanes = builder.GetField("pruneSidePlanes", k_Static);
            m_PruneStats = builder.GetField("pruneStatsEnabled", k_Static);
            m_PruneKept = builder.GetField("lastPruneKeptBits", k_Static);
            m_PruneCleared = builder.GetField("lastPruneClearedBits", k_Static);
            m_Compact = builder.GetField("compactLists", k_Static);
            m_CompactStats = builder.GetField("compactStatsEnabled", k_Static);
            m_CompactEntries = builder.GetField("lastCompactEntries", k_Static);
            m_CompactCells = builder.GetField("lastCompactCells", k_Static);
            m_CompactOverflowCells = builder.GetField("lastCompactOverflowCells", k_Static);
            m_CompactOverflowTiles = builder.GetField("lastCompactOverflowTiles", k_Static);
            m_CompactScannedCells = builder.GetField("lastCompactScannedCells", k_Static);
            m_CaptureEnabled = capture.GetField("enabled", k_Static);
            m_LastGpu = capture.GetField("lastGpu", k_Static);

            var snapshot = m_LastGpu.FieldType;
            if (snapshot != null)
            {
                m_ZBins = snapshot.GetField("zBins");
                m_TileMasks = snapshot.GetField("tileMasks");
                m_ZBinScale = snapshot.GetField("zBinScale");
                m_ZBinOffset = snapshot.GetField("zBinOffset");
                m_BinCount = snapshot.GetField("binCount");
                m_WordsPerTile = snapshot.GetField("wordsPerTile");
                m_ViewCount = snapshot.GetField("viewCount");
                m_TileWidth = snapshot.GetField("tileWidth");
                m_TileResolution = snapshot.GetField("tileResolution");
            }
        }

        public bool forwardClusterLights
        {
            get => (bool)m_ForwardClusterLights.GetValue(null);
            set => m_ForwardClusterLights.SetValue(null, value);
        }

        public bool pruneByDepth
        {
            get => (bool)m_Prune.GetValue(null);
            set => m_Prune.SetValue(null, value);
        }

        public bool pruneStatsEnabled
        {
            get => (bool)m_PruneStats.GetValue(null);
            set => m_PruneStats.SetValue(null, value);
        }

        public bool pruneSidePlanes
        {
            get => (bool)m_PruneSidePlanes.GetValue(null);
            set => m_PruneSidePlanes.SetValue(null, value);
        }

        public int pruneKeptBits
        {
            get => (int)m_PruneKept.GetValue(null);
            set => m_PruneKept.SetValue(null, value);
        }

        public int pruneClearedBits
        {
            get => (int)m_PruneCleared.GetValue(null);
            set => m_PruneCleared.SetValue(null, value);
        }

        public bool compactLists
        {
            get => (bool)m_Compact.GetValue(null);
            set => m_Compact.SetValue(null, value);
        }

        public bool compactStatsEnabled
        {
            get => (bool)m_CompactStats.GetValue(null);
            set => m_CompactStats.SetValue(null, value);
        }

        public int compactEntries
        {
            get => (int)m_CompactEntries.GetValue(null);
            set => m_CompactEntries.SetValue(null, value);
        }

        public int compactCells
        {
            get => (int)m_CompactCells.GetValue(null);
            set => m_CompactCells.SetValue(null, value);
        }

        public int compactOverflowCells
        {
            get => (int)m_CompactOverflowCells.GetValue(null);
            set => m_CompactOverflowCells.SetValue(null, value);
        }

        public int compactOverflowTiles
        {
            get => (int)m_CompactOverflowTiles.GetValue(null);
            set => m_CompactOverflowTiles.SetValue(null, value);
        }

        public int compactScannedCells
        {
            get => (int)m_CompactScannedCells.GetValue(null);
            set => m_CompactScannedCells.SetValue(null, value);
        }

        public bool captureEnabled
        {
            get => (bool)m_CaptureEnabled.GetValue(null);
            set => m_CaptureEnabled.SetValue(null, value);
        }

        public void ClearGpuCapture()
        {
            m_LastGpu.SetValue(null, Activator.CreateInstance(m_LastGpu.FieldType));
        }

        public ClusterWordSnapshot ReadGpu() => Read(m_LastGpu.GetValue(null));


        ClusterWordSnapshot Read(object snapshot)
        {
            if (snapshot == null || m_ZBins == null)
                return null;

            var zBins = (uint[])m_ZBins.GetValue(snapshot);
            if (zBins == null)
                return null;

            return new ClusterWordSnapshot
            {
                zBins = zBins,
                tileMasks = (uint[])m_TileMasks.GetValue(snapshot),
                zBinScale = (float)m_ZBinScale.GetValue(snapshot),
                zBinOffset = (float)m_ZBinOffset.GetValue(snapshot),
                binCount = (int)m_BinCount.GetValue(snapshot),
                wordsPerTile = (int)m_WordsPerTile.GetValue(snapshot),
                viewCount = (int)m_ViewCount.GetValue(snapshot),
                tileWidth = (int)m_TileWidth.GetValue(snapshot),
                tileResolution = (Vector2Int)m_TileResolution.GetValue(snapshot),
            };
        }
    }

    sealed class ClusterWordSnapshot
    {
        public uint[] zBins;
        public uint[] tileMasks;
        public float zBinScale;
        public float zBinOffset;
        public int binCount;
        public int wordsPerTile;
        public int viewCount;
        public int tileWidth;
        public Vector2Int tileResolution;

        // Each bin begins with two packed headers; only the used prefix is what the kernel can index.
        public int ZBinUsedWords => binCount * (2 + wordsPerTile) * viewCount;

        public int TileUsedWords => tileResolution.x * tileResolution.y * wordsPerTile * viewCount;
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
        // G2 diagnostic (--cd-prune-stats): conservative tile bits kept/cleared in the last frame.
        public int pruneKeptBits;
        public int pruneClearedBits;
        // G3 diagnostic (--cd-compact-stats): cell list size/overflow of the last frame.
        public int compactEntries;
        public int compactCells;
        public int compactOverflowCells;
        public int compactOverflowTiles;
        public int compactScannedCells;
    }

    [Serializable]
    public sealed class ModeReport
    {
        public string name;
        public bool clusterListAsync;
        public List<LightCountSample> samples;
    }

    [Serializable]
    public sealed class DiffReport
    {
        public int lightCount;
        public string reference;
        public string candidate;
        public int pixels;
        public double meanChannelDiff;
        public double meanChannelDiffNormalized;
        public int maxChannelDiff;
        public int pixelsOverThreshold;
        public double pixelsOverThresholdFraction;
        public bool passed;
        // Transparent-group bounding rect; zeroed when the fixture is off.
        public bool hasTransparentRect;
        public int rectX;
        public int rectY;
        public int rectWidth;
        public int rectHeight;
        public int rectPixels;
        public double rectMeanChannelDiff;
        public int rectMaxChannelDiff;
        public int rectPixelsOverThreshold;
        public double rectPixelsOverThresholdFraction;
        public bool rectPassed;
    }

    [Serializable]
    public sealed class FeatureState
    {
        public string name;
        public bool forwardPlusKeyword;
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
        public bool supportsAsyncCompute;
        public bool clusterListAsync;
        public bool clusterListPrune;
        public bool clusterListCompact;
        public bool forwardClusterLights;
        public int width;
        public int height;
        public int warmupFrames;
        public int sampleFrames;
        public int[] lightCounts;
        public int screenshotLightCount;
        public int[] screenshotLightCounts;
        public bool transparentFixture;
        public int perObjectLightLimit;
        public bool additionalLightsPixelKeyword;
        public bool additionalLightsVertexKeyword;
        public bool forwardPlusKeyword;
        public bool forwardClusterKeyword;
        public int transparentRectX;
        public int transparentRectY;
        public int transparentRectWidth;
        public int transparentRectHeight;
        public bool captureOnly;
        public bool completed;
        public string error;
        public List<ModeReport> modes;
        public List<DiffReport> forwardPlusDiffs;
    }

    [Serializable]
    public sealed class CpuModeBreakdown
    {
        public string mode;
        public bool clusterListAsync;
        public int frames;
        public double frameCpuMeanMs;
        public double preSetupMs;
        public double setupLightsMs;
        public double lightConstantsMs;
        public double resolveMixedLightingMs;
        public double deferredSetupLightsMs;
        public double precomputeLightsMs;
        public double otherMs;
    }

    [Serializable]
    public sealed class CpuBreakdownReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public int lightCount;
        public int width;
        public int height;
        public int sampleFrames;
        public bool supportsAsyncCompute;
        public bool clusterListAsync;
        public bool completed;
        public string error;
        public List<CpuModeBreakdown> modes;
    }

    [Serializable]
    public sealed class BufferParityReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceType;
        public string graphicsDeviceName;
        public int lightCount;
        public bool supportsAsyncCompute;
        public bool asyncLightListBuildRequested;
        public bool completed;
        public bool passed;
        public string error;
        public bool layoutMatches;
        public int zBinWords;
        public int tileWords;
        public int zBinUsedWords;
        public int tileUsedWords;
        public int binCount;
        public int wordsPerTile;
        public int tileWidth;
        public int tileResolutionX;
        public int tileResolutionY;
        public int asyncSyncZBinMismatchWords;
        public int asyncSyncZBinMismatchBits;
        public int asyncSyncTileMismatchWords;
        public int asyncSyncTileMismatchBits;
        public int asyncSyncFirstMismatch;
        public int gpuTileBits;
        public int itemCount;
        public int fullScreenItems;
        public int minItemTiles;
        public int maxItemTiles;
        public int medianItemTiles;
    }
}
