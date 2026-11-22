using System;
using System.Collections;
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

// 07 acceptance: cameras without a UniversalAdditionalCameraData (Scene View included) get no motion
// vectors from URP, so GTAO's temporal filter blends the previous AO in place and drags it behind a
// moving camera. This harness renders one step per editor tick so Time.frameCount advances between
// poses, then compares the moving frame with a converged reference at the same pose, and with the
// native motion-vector path of an equivalent camera that has the component.
public static class ScreenSpaceLightingEditorMotionVerification
{
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string OutputArgument = "-ssl-editor-motion-output";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 8;
    const int MovingFrames = 4;
    const int ReferenceFrames = 12;
    const float YawStepDegrees = 3f;

    const float ReferenceMeanAbsThreshold = 0.002f;
    const float AcdMeanAbsThreshold = 0.001f;
    const float BaselineMeanAbsThreshold = 0.01f;
    const float PixelDifferenceThreshold = 0.02f;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    enum CameraKind
    {
        NoAdditionalCameraData,
        AdditionalCameraData,
        NoAdditionalCameraDataBaseline,
        StaticNoAdditionalCameraData,
    }

    [Serializable]
    sealed class DiffMetrics
    {
        public bool valid;
        public double meanAbs;
        public float maxAbs;
        public int pixelsOverThreshold;
        public float pixelsOverThresholdFraction;
    }

    [Serializable]
    sealed class FrameCapture
    {
        public int index;
        public int frameCount;
        public float meanAbsMotionX;
        public float meanAbsMotionY;
        public string image;
    }

    [Serializable]
    sealed class CaseResult
    {
        public string name;
        public bool suppressed;
        public bool hasAdditionalCameraData;
        public bool completed;
        public int editorMotionExecutions;
        public int editorMotionDraws;
        public int editorMotionFirstFrameSkips;
        public List<FrameCapture> frames = new List<FrameCapture>();
        public DiffMetrics finalVsReference = new DiffMetrics();
        public DiffMetrics noAcdVsAcd = new DiffMetrics();
        public DiffMetrics fixedVsBaseline = new DiffMetrics();
        public bool passed;
        public List<string> issues = new List<string>();

        [NonSerialized] public List<Color[]> frameSamples = new List<Color[]>();
        [NonSerialized] public Color[] referenceSamples;
    }

    [Serializable]
    sealed class ModeResult
    {
        public string name;
        public string pipelinePath;
        public bool gtaoFound;
        public bool temporalAccumulation;
        public bool fullResolution;
        public List<CaseResult> cases = new List<CaseResult>();
    }

    [Serializable]
    sealed class Report
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public string scenePath;
        public bool completed;
        public bool passed;
        public string error;
        public List<ModeResult> modes = new List<ModeResult>();
    }

    static Report s_Report;
    static IEnumerator s_Routine;
    static string s_OutputDirectory;
    static RenderTexture s_ImageTarget;
    static Camera s_SourceCamera;
    static GameObject s_VolumeObject;
    static bool s_SourceCameraWasEnabled;
    static bool s_VolumeWasActive;
    static RenderPipelineAsset s_PreviousPipeline;
    static RenderPipelineAsset s_PreviousQualityPipeline;
    static string s_PreviousScenePath;
    static bool s_PreviousSuppression;
    static readonly List<(GroundTruthAmbientOcclusionRendererFeature feature, int mode, int mip)> s_DebugRestores =
        new List<(GroundTruthAmbientOcclusionRendererFeature, int, int)>();
    static Vector3 s_StartPosition;
    static Quaternion s_StartRotation;

    [MenuItem("Tests/Screen Space Lighting/Run 07 Editor Motion Verification")]
    public static void RunFromMenu()
    {
        Begin();
    }

    // -executeMethod entry point. The editor keeps ticking after this method returns; the routine
    // renders one step per EditorApplication.update and exits the editor when it finishes.
    public static void Run()
    {
        Begin();
    }

    static void Begin()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        s_OutputDirectory = GetCommandLineValue(OutputArgument)
            ?? Path.Combine(projectRoot, "PLAN/Done/07-EditorMVPass/validation/EditorMotion");
        Directory.CreateDirectory(s_OutputDirectory);

        s_Report = new Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            scenePath = ScenePath,
        };

        try
        {
            Prepare();
            s_Routine = RunModes().GetEnumerator();
            EditorApplication.update += Tick;
        }
        catch (Exception exception)
        {
            Finish(false, exception.ToString());
        }
    }

    static void Tick()
    {
        if (s_Routine == null)
            return;

        try
        {
            if (s_Routine.MoveNext())
                return;

            s_Routine = null;
            EditorApplication.update -= Tick;
            Finish(true, null);
        }
        catch (Exception exception)
        {
            s_Routine = null;
            EditorApplication.update -= Tick;
            Finish(false, exception.ToString());
        }
    }

    static void Prepare()
    {
        s_PreviousPipeline = GraphicsSettings.defaultRenderPipeline;
        s_PreviousQualityPipeline = QualitySettings.renderPipeline;
        s_PreviousScenePath = SceneManager.GetActiveScene().path;

        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        DynamicGI.UpdateEnvironment();

        s_SourceCamera = UnityEngine.Object.FindObjectOfType<Camera>();
        if (s_SourceCamera == null)
            throw new InvalidOperationException("The verification scene has no camera.");

        var runner = UnityEngine.Object.FindObjectOfType<ScreenSpaceLightingBaselineRunner>();
        if (runner != null)
            runner.enabled = false;

        s_VolumeObject = GameObject.Find("Baseline Volume");
        if (s_VolumeObject == null || s_VolumeObject.GetComponent<Volume>() == null)
            throw new InvalidOperationException("The verification scene has no Baseline Volume.");

        s_StartPosition = s_SourceCamera.transform.position;
        s_StartRotation = s_SourceCamera.transform.rotation;
        s_SourceCameraWasEnabled = s_SourceCamera.enabled;
        s_SourceCamera.enabled = false;
        s_VolumeWasActive = s_VolumeObject.activeSelf;
        s_VolumeObject.SetActive(false);

        s_ImageTarget = new RenderTexture(
            ImageWidth,
            ImageHeight,
            24,
            RenderTextureFormat.ARGBFloat,
            RenderTextureReadWrite.Linear)
        {
            name = "EditorMotionTarget"
        };
        s_ImageTarget.Create();

        s_PreviousSuppression = GetSuppression();
        SetSuppression(false);
        s_DebugRestores.Clear();
    }

    static IEnumerable RunModes()
    {
        foreach (var mode in ModeNames)
        {
            var pipelinePath = string.Format("{0}/SslT5{1}Pipeline.asset", RendererRoot, mode);
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            if (pipeline == null)
                throw new FileNotFoundException("Missing T5 pipeline asset.", pipelinePath);

            var feature = FindGtaoFeature(pipeline);
            if (feature == null)
                throw new InvalidOperationException("The pipeline's renderer has no GTAO feature: " + pipelinePath);

            var modeResult = new ModeResult
            {
                name = mode,
                pipelinePath = pipelinePath,
                gtaoFound = true,
                temporalAccumulation = ReadBool(GetSettings(feature), "temporalAccumulation"),
                fullResolution = ReadBool(GetSettings(feature), "fullResolution"),
            };
            s_Report.modes.Add(modeResult);

            var settings = GetSettings(feature);
            s_DebugRestores.Add((feature, ReadInt(settings, "debugMode"), ReadInt(settings, "debugMip")));

            if (!modeResult.temporalAccumulation)
            {
                modeResult.cases.Add(FailedCase("Configuration", "GTAO temporal accumulation must be enabled."));
                continue;
            }

            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            SetDebugMode(feature, 1, 0); // AmbientOcclusion

            var noAcd = new CaseResult();
            foreach (var ignored in RunCase(mode, feature, CameraKind.NoAdditionalCameraData, noAcd))
                yield return null;

            var acd = new CaseResult();
            foreach (var ignored in RunCase(mode, feature, CameraKind.AdditionalCameraData, acd))
                yield return null;

            var baseline = new CaseResult();
            foreach (var ignored in RunCase(mode, feature, CameraKind.NoAdditionalCameraDataBaseline, baseline))
                yield return null;

            var staticCase = new CaseResult();
            foreach (var ignored in RunCase(mode, feature, CameraKind.StaticNoAdditionalCameraData, staticCase))
                yield return null;

            CompareNoAcdWithNativeMotionPath(noAcd, acd);
            CompareFixedWithBaseline(noAcd, baseline);
            modeResult.cases.Add(noAcd);
            modeResult.cases.Add(acd);
            modeResult.cases.Add(baseline);
            modeResult.cases.Add(staticCase);
            foreach (var caseResult in modeResult.cases)
                EvaluateCase(caseResult);

            EditorUtility.SetDirty(feature);
            AssetDatabase.SaveAssets();
        }
    }

    static IEnumerable RunCase(
        string mode,
        GroundTruthAmbientOcclusionRendererFeature feature,
        CameraKind kind,
        CaseResult result)
    {
        result.name = kind.ToString();
        result.suppressed = kind == CameraKind.NoAdditionalCameraDataBaseline;
        result.hasAdditionalCameraData = kind == CameraKind.AdditionalCameraData;
        SetSuppression(result.suppressed);
        ResetEditorMotionCounters();

        var moving = kind != CameraKind.StaticNoAdditionalCameraData;
        var camera = CreateCamera(result.hasAdditionalCameraData, mode + "_" + kind);
        try
        {
            // One render per Time.frameCount: URP's native motion data is throttled by the frame counter,
            // so two renders in one frame would reuse the first render's view-projection and break the
            // native reference. The editor can tick several times per frame, hence the explicit wait.
            for (var warmup = 0; warmup < WarmupFrames; warmup++)
            {
                var renderedFrame = Time.frameCount;
                camera.Render();
                foreach (var ignored in WaitForFrameAdvance(renderedFrame))
                    yield return ignored;
            }

            for (var frame = 0; frame < MovingFrames; frame++)
            {
                if (moving)
                {
                    camera.transform.rotation = s_StartRotation *
                        Quaternion.Euler(0f, YawStepDegrees * (frame + 1), 0f);
                }

                var renderedFrame = Time.frameCount;
                camera.Render();
                var samples = ReadPixels(s_ImageTarget);
                var capture = new FrameCapture
                {
                    index = frame,
                    frameCount = Time.frameCount,
                    image = string.Format("{0}_{1}_frame{2}.png", mode, result.name, frame),
                };
                if (TryReadMotion(out var motionX, out var motionY))
                {
                    capture.meanAbsMotionX = motionX;
                    capture.meanAbsMotionY = motionY;
                }

                result.frames.Add(capture);
                result.frameSamples.Add(samples);
                WriteImage(samples, capture.image);
                foreach (var ignored in WaitForFrameAdvance(renderedFrame))
                    yield return ignored;
            }

            // Same pose, fresh camera and history: temporal accumulation converges without ghosting.
            var reference = CreateCamera(result.hasAdditionalCameraData, mode + "_" + result.name + "_Reference");
            try
            {
                for (var frame = 0; frame < ReferenceFrames; frame++)
                {
                    var renderedFrame = Time.frameCount;
                    reference.Render();
                    foreach (var ignored in WaitForFrameAdvance(renderedFrame))
                        yield return ignored;
                }

                result.referenceSamples = ReadPixels(s_ImageTarget);
                WriteImage(result.referenceSamples, string.Format("{0}_{1}_reference.png", mode, result.name));
                result.finalVsReference = Compare(result.frameSamples.LastOrDefault(), result.referenceSamples);
            }
            finally
            {
                DestroyCamera(reference);
            }

            result.editorMotionExecutions = ReadEditorMotionCounter("s_EditorExecuteCount");
            result.editorMotionDraws = ReadEditorMotionCounter("s_EditorDrawCount");
            result.editorMotionFirstFrameSkips = ReadEditorMotionCounter("s_EditorFirstFrameSkipCount");
            result.completed = true;
        }
        finally
        {
            DestroyCamera(camera);
            SetSuppression(false);
        }
    }

    static IEnumerable WaitForFrameAdvance(int renderedFrame)
{
    // The batch-mode editor can tick updates without advancing the player-loop clock, and URP's
    // native motion data only refreshes when Time.frameCount changes. Ask for a player-loop update
    // and fail instead of hanging if the clock still does not move.
    EditorApplication.QueuePlayerLoopUpdate();
    var ticks = 0;
    while (Time.frameCount == renderedFrame)
    {
        if (++ticks > 600)
            throw new TimeoutException("Time.frameCount did not advance; the editor is not running player-loop updates.");
        yield return null;
        EditorApplication.QueuePlayerLoopUpdate();
    }
}

static void EvaluateCase(CaseResult result)
    {
        if (!result.finalVsReference.valid)
            result.issues.Add("The final frame and the converged reference could not be compared.");

        if (result.name == CameraKind.NoAdditionalCameraDataBaseline.ToString())
        {
            // The A/B gate: without the editor motion pass the same pose sequence must produce a
            // clearly different image than the fixed path.
            if (result.fixedVsBaseline.meanAbs < BaselineMeanAbsThreshold &&
                result.fixedVsBaseline.pixelsOverThresholdFraction <= 0.01f)
            {
                result.issues.Add("The suppressed baseline did not ghost: it must differ from the fixed path.");
            }
        }
        else if (result.name == CameraKind.StaticNoAdditionalCameraData.ToString())
        {
            // finalVsReference is exact for a still camera, so it directly proves the history is stable.
            if (!result.finalVsReference.valid || result.finalVsReference.meanAbs > ReferenceMeanAbsThreshold)
                result.issues.Add("Static camera differs from its converged reference.");
            if (result.finalVsReference.pixelsOverThresholdFraction > 0.001f)
                result.issues.Add("Static camera accumulates visible differences over time.");
        }
        else
        {
            // A fresh converged reference is not a ghosting discriminator: temporal accumulation lags
            // the instantaneous AO by its feedback window, and URP's native motion path shows the
            // same lag. The verdict is equality with the native path instead (bit-exact target).
            if (!result.noAcdVsAcd.valid ||
                result.noAcdVsAcd.meanAbs > AcdMeanAbsThreshold ||
                result.noAcdVsAcd.pixelsOverThreshold > 0)
            {
                result.issues.Add("The editor motion path does not match URP's native camera motion vectors.");
            }
        }

        if (!result.hasAdditionalCameraData && !result.suppressed && result.editorMotionDraws <= 0)
            result.issues.Add("The editor motion pass never drew for a camera without camera data.");
        if (result.hasAdditionalCameraData && result.editorMotionDraws != 0)
            result.issues.Add("The editor motion pass drew for a camera URP already handles.");
        if (result.suppressed && result.editorMotionDraws != 0)
            result.issues.Add("The editor motion pass ran although the test hook suppressed it.");
        if (!result.hasAdditionalCameraData && !result.suppressed && result.editorMotionFirstFrameSkips <= 0)
            result.issues.Add("The editor motion pass never recorded a first pose before drawing.");

        if (result.name == CameraKind.StaticNoAdditionalCameraData.ToString())
        {
            for (var i = 0; i < result.frames.Count; i++)
            {
                if (Mathf.Abs(result.frames[i].meanAbsMotionX) > 0.0005f ||
                    Mathf.Abs(result.frames[i].meanAbsMotionY) > 0.0005f)
                {
                    result.issues.Add("A static camera produced non-zero motion vectors.");
                    break;
                }
            }
        }
        else if (!result.suppressed)
        {
            var hasMotion = result.frames.Any(frame =>
                Mathf.Abs(frame.meanAbsMotionX) > 0.0005f || Mathf.Abs(frame.meanAbsMotionY) > 0.0005f);
            if (!hasMotion)
                result.issues.Add("The moving camera produced no motion vectors.");
        }

        result.passed = result.issues.Count == 0;
    }

    static void CompareNoAcdWithNativeMotionPath(CaseResult noAcd, CaseResult acd)
    {
        if (noAcd.frameSamples.Count != acd.frameSamples.Count)
        {
            noAcd.issues.Add("The native motion path rendered a different number of frames.");
            noAcd.passed = false;
            return;
        }

        var worst = new DiffMetrics { valid = true };
        for (var index = 0; index < noAcd.frameSamples.Count; index++)
        {
            var diff = Compare(noAcd.frameSamples[index], acd.frameSamples[index]);
            worst.meanAbs = Math.Max(worst.meanAbs, diff.meanAbs);
            worst.maxAbs = Mathf.Max(worst.maxAbs, diff.maxAbs);
            worst.pixelsOverThreshold = Math.Max(worst.pixelsOverThreshold, diff.pixelsOverThreshold);
            worst.pixelsOverThresholdFraction = Mathf.Max(worst.pixelsOverThresholdFraction, diff.pixelsOverThresholdFraction);
        }

        noAcd.noAcdVsAcd = worst;
        acd.noAcdVsAcd = worst;
    }

    static void CompareFixedWithBaseline(CaseResult fixedCase, CaseResult baseline)
    {
        if (fixedCase.frameSamples.Count != baseline.frameSamples.Count)
        {
            baseline.issues.Add("The suppressed baseline rendered a different number of frames.");
            baseline.passed = false;
            return;
        }

        var worst = new DiffMetrics { valid = true };
        for (var index = 0; index < fixedCase.frameSamples.Count; index++)
        {
            var diff = Compare(fixedCase.frameSamples[index], baseline.frameSamples[index]);
            worst.meanAbs = Math.Max(worst.meanAbs, diff.meanAbs);
            worst.maxAbs = Mathf.Max(worst.maxAbs, diff.maxAbs);
            worst.pixelsOverThreshold = Math.Max(worst.pixelsOverThreshold, diff.pixelsOverThreshold);
            worst.pixelsOverThresholdFraction = Mathf.Max(worst.pixelsOverThresholdFraction, diff.pixelsOverThresholdFraction);
        }

        baseline.fixedVsBaseline = worst;
    }

    static Camera CreateCamera(bool withAdditionalCameraData, string name)
    {
        var gameObject = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
        var camera = gameObject.AddComponent<Camera>();
        camera.CopyFrom(s_SourceCamera);
        camera.transform.SetPositionAndRotation(s_StartPosition, s_StartRotation);
        camera.targetTexture = s_ImageTarget;
        camera.aspect = (float)ImageWidth / ImageHeight;
        // Render only through the explicit Camera.Render calls below. An enabled camera is also
        // rendered by the editor's player loop, which would add same-pose frames between poses and
        // invalidate both the motion vectors and the convergence comparison.
        camera.enabled = false;
        if (withAdditionalCameraData)
        {
            var cameraData = gameObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;
            cameraData.resetHistory = true;
        }

        return camera;
    }

    static void DestroyCamera(Camera camera)
    {
        if (camera != null)
            UnityEngine.Object.DestroyImmediate(camera.gameObject);
    }

    static void Finish(bool completed, string error)
    {
        s_Report.completed = completed;
        s_Report.error = error;

        try
        {
            SetSuppression(s_PreviousSuppression);
            foreach (var restore in s_DebugRestores)
            {
                if (restore.feature == null)
                    continue;
                SetDebugMode(restore.feature, restore.mode, restore.mip);
                EditorUtility.SetDirty(restore.feature);
            }
            AssetDatabase.SaveAssets();

            if (s_SourceCamera != null)
                s_SourceCamera.enabled = s_SourceCameraWasEnabled;
            if (s_VolumeObject != null)
                s_VolumeObject.SetActive(s_VolumeWasActive);

            if (s_ImageTarget != null)
            {
                s_ImageTarget.Release();
                UnityEngine.Object.DestroyImmediate(s_ImageTarget);
            }

            GraphicsSettings.defaultRenderPipeline = s_PreviousPipeline;
            QualitySettings.renderPipeline = s_PreviousQualityPipeline;
            if (!string.IsNullOrEmpty(s_PreviousScenePath))
                EditorSceneManager.OpenScene(s_PreviousScenePath, OpenSceneMode.Single);
        }
        catch (Exception exception)
        {
            s_Report.error = (s_Report.error == null ? string.Empty : s_Report.error + "\n") + exception;
        }

        if (error != null)
        {
            s_Report.passed = false;
        }
        else
        {
            s_Report.passed = s_Report.modes.Count > 0 &&
                s_Report.modes.All(mode => mode.cases.Count > 0 && mode.cases.All(caseResult => caseResult.passed));
        }

        WriteReports();
        Debug.Log($"07 editor motion verification finished: completed={s_Report.completed} passed={s_Report.passed}");
        if (Application.isBatchMode)
            EditorApplication.Exit(s_Report.completed && s_Report.passed ? 0 : 1);
    }

    static void WriteReports()
    {
        File.WriteAllText(Path.Combine(s_OutputDirectory, "Report.json"), JsonUtility.ToJson(s_Report, true));

        var lines = new List<string> { "{" };
        lines.Add($"  \"completed\": {s_Report.completed.ToString().ToLowerInvariant()},");
        lines.Add($"  \"passed\": {s_Report.passed.ToString().ToLowerInvariant()},");
        lines.Add("  \"modes\": [");
        for (var modeIndex = 0; modeIndex < s_Report.modes.Count; modeIndex++)
        {
            var mode = s_Report.modes[modeIndex];
            lines.Add("    {");
            lines.Add($"      \"name\": \"{mode.name}\",");
            lines.Add("      \"cases\": [");
            for (var caseIndex = 0; caseIndex < mode.cases.Count; caseIndex++)
            {
                var item = mode.cases[caseIndex];
                lines.Add("        {");
                lines.Add($"          \"name\": \"{item.name}\", \"passed\": {item.passed.ToString().ToLowerInvariant()},");
                lines.Add($"          \"meanAbsVsReference\": {item.finalVsReference.meanAbs.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},");
                lines.Add($"          \"maxAbsVsReference\": {item.finalVsReference.maxAbs.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},");
                lines.Add($"          \"pixelsOver02\": {item.finalVsReference.pixelsOverThreshold},");
                lines.Add($"          \"meanAbsVsAcd\": {item.noAcdVsAcd.meanAbs.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},");
                lines.Add($"          \"meanAbsVsBaseline\": {item.fixedVsBaseline.meanAbs.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},");
                lines.Add($"          \"pixelsOver02VsAcd\": {item.noAcdVsAcd.pixelsOverThreshold},");
                lines.Add($"          \"pixelsOver02VsBaseline\": {item.fixedVsBaseline.pixelsOverThreshold},");
                lines.Add($"          \"motionMeanAbsX\": {item.frames.DefaultIfEmpty(new FrameCapture()).Max(frame => (double)frame.meanAbsMotionX).ToString("R", System.Globalization.CultureInfo.InvariantCulture)},");
                lines.Add($"          \"motionMeanAbsY\": {item.frames.DefaultIfEmpty(new FrameCapture()).Max(frame => (double)frame.meanAbsMotionY).ToString("R", System.Globalization.CultureInfo.InvariantCulture)},");
                lines.Add($"          \"editorMotionDraws\": {item.editorMotionDraws},");
                lines.Add($"          \"editorMotionFirstFrameSkips\": {item.editorMotionFirstFrameSkips}");
                lines.Add(caseIndex + 1 == mode.cases.Count ? "        }" : "        },");
            }
            lines.Add("      ]");
            lines.Add(modeIndex + 1 == s_Report.modes.Count ? "    }" : "    },");
        }
        lines.Add("  ]");
        lines.Add("}");
        File.WriteAllLines(Path.Combine(s_OutputDirectory, "Metrics.json"), lines);
    }

    static CaseResult FailedCase(string name, string issue)
    {
        var result = new CaseResult { name = name, completed = false };
        result.issues.Add(issue);
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Capture helpers
    // ---------------------------------------------------------------------------------------------

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

    static void WriteImage(Color[] samples, string fileName)
    {
        var texture = new Texture2D(ImageWidth, ImageHeight, TextureFormat.RGBA32, false, false);
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
            File.WriteAllBytes(Path.Combine(s_OutputDirectory, fileName), texture.EncodeToPNG());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    static DiffMetrics Compare(Color[] left, Color[] right)
    {
        var metrics = new DiffMetrics();
        if (left == null || right == null || left.Length != right.Length)
            return metrics;

        double sum = 0.0;
        var max = 0f;
        var over = 0;
        for (var i = 0; i < left.Length; i++)
        {
            var difference = Mathf.Max(
                Mathf.Max(Mathf.Abs(left[i].r - right[i].r), Mathf.Abs(left[i].g - right[i].g)),
                Mathf.Max(Mathf.Abs(left[i].b - right[i].b), Mathf.Abs(left[i].a - right[i].a)));
            sum += difference;
            max = Mathf.Max(max, difference);
            if (difference > PixelDifferenceThreshold)
                over++;
        }

        metrics.valid = true;
        metrics.meanAbs = sum / left.Length;
        metrics.maxAbs = max;
        metrics.pixelsOverThreshold = over;
        metrics.pixelsOverThresholdFraction = over / (float)left.Length;
        return metrics;
    }

    static bool TryReadMotion(out float meanX, out float meanY)
    {
        meanX = 0f;
        meanY = 0f;
        var texture = Shader.GetGlobalTexture(Shader.PropertyToID("_MotionVectorTexture"));
        if (texture == null)
            return false;

        var temporary = RenderTexture.GetTemporary(
            texture.width,
            texture.height,
            0,
            RenderTextureFormat.ARGBFloat,
            RenderTextureReadWrite.Linear);
        try
        {
            Graphics.Blit(texture, temporary);
            var pixels = ReadPixels(temporary);
            double sumX = 0.0;
            double sumY = 0.0;
            for (var i = 0; i < pixels.Length; i++)
            {
                sumX += Mathf.Abs(pixels[i].r);
                sumY += Mathf.Abs(pixels[i].g);
            }

            meanX = (float)(sumX / pixels.Length);
            meanY = (float)(sumY / pixels.Length);
            return true;
        }
        finally
        {
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Plugin reflection helpers
    // ---------------------------------------------------------------------------------------------

    static GroundTruthAmbientOcclusionRendererFeature FindGtaoFeature(UniversalRenderPipelineAsset pipeline)
    {
        var path = AssetDatabase.GetAssetPath(pipeline);
        var rendererPath = path.Replace("Pipeline.asset", "Renderer.asset");
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        return renderer?.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>().FirstOrDefault();
    }

    static object GetSettings(GroundTruthAmbientOcclusionRendererFeature feature)
    {
        return feature?.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
    }

    static void SetDebugMode(GroundTruthAmbientOcclusionRendererFeature feature, int mode, int mip)
    {
        var settings = GetSettings(feature);
        var modeField = settings?.GetType().GetField("debugMode", InstanceFlags);
        var mipField = settings?.GetType().GetField("debugMip", InstanceFlags);
        modeField?.SetValue(settings, Enum.ToObject(modeField.FieldType, mode));
        mipField?.SetValue(settings, mip);
        EditorUtility.SetDirty(feature);
    }

    static int ReadInt(object instance, string name)
    {
        if (instance == null)
            return 0;
        var property = instance.GetType().GetProperty(name, InstanceFlags);
        if (property != null)
            return (int)property.GetValue(instance);
        var field = instance.GetType().GetField(name, InstanceFlags);
        return field == null ? 0 : (int)field.GetValue(instance);
    }

    static bool ReadBool(object instance, string name)
    {
        if (instance == null)
            return false;
        var field = instance.GetType().GetField(name, InstanceFlags);
        return field != null && (bool)field.GetValue(instance);
    }

    static int ReadEditorMotionCounter(string fieldName)
    {
        var type = typeof(GroundTruthAmbientOcclusionRendererFeature).Assembly
            .GetType("UnityEngine.Rendering.Universal.ScreenSpaceEditorCameraMotionPass");
        return type == null ? -1 : (int)type.GetField(fieldName, StaticFlags).GetValue(null);
    }

    static void ResetEditorMotionCounters()
    {
        var type = typeof(GroundTruthAmbientOcclusionRendererFeature).Assembly
            .GetType("UnityEngine.Rendering.Universal.ScreenSpaceEditorCameraMotionPass");
        type?.GetMethod("ResetEditorCounters", StaticFlags)?.Invoke(null, null);
    }

    static bool GetSuppression()
    {
        var type = typeof(GroundTruthAmbientOcclusionRendererFeature).Assembly
            .GetType("UnityEngine.Rendering.Universal.ScreenSpaceEditorCameraMotionPass");
        var field = type?.GetField("s_EditorSuppressEditorMotionPass", StaticFlags);
        return field != null && (bool)field.GetValue(null);
    }

    static void SetSuppression(bool value)
    {
        var type = typeof(GroundTruthAmbientOcclusionRendererFeature).Assembly
            .GetType("UnityEngine.Rendering.Universal.ScreenSpaceEditorCameraMotionPass");
        var field = type?.GetField("s_EditorSuppressEditorMotionPass", StaticFlags);
        field?.SetValue(null, value);
    }

    static string GetCommandLineValue(string name)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var i = 0; i + 1 < arguments.Length; i++)
        {
            if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                return arguments[i + 1];
        }

        return null;
    }
}