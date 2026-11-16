using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// P1-3 proxy selection verification in a standalone player. The editor screenshots are replaced by
// GPU frames captured with RenderDoc: the runner triggers one capture per camera pose and probe
// configuration through the in-application API, then records the same glass-region metrics as the
// editor test so the .rdc files can be cross-checked against the PNGs.
public sealed class ScreenSpaceLightingP13PlayerVerification : MonoBehaviour
{
    public UniversalRenderPipelineAsset pipelineAsset;
    public VolumeProfile volumeProfile;
    public Material floorMaterial;
    public Material backdropMaterial;
    public Material glassMaterial;
    public Material unlitMaterial;

    const int RenderWidth = 1280;
    const int RenderHeight = 720;
    const int PatternLayer = 31;
    const int WarmupFrames = 8;
    const float IdenticalThreshold = 1e-6f;

    readonly P13PlayerReport m_Report = new P13PlayerReport();
    readonly StringBuilder m_Log = new StringBuilder();

    Camera m_Camera;
    UniversalAdditionalCameraData m_CameraData;
    Volume m_Volume;
    VolumeProfile m_ProfileInstance;
    ScreenSpaceRefraction m_Refraction;
    GameObject m_Rig;
    GameObject m_Glass;
    GameObject m_Pattern;
    ReflectionProbe m_ProbeA;
    ReflectionProbe m_ProbeB;
    Material m_GlassInstance;
    Material m_PatternInstance;
    Texture2D m_PatternTexture;
    Vector3 m_CameraStart;
    Vector3 m_ViewDirection;
    RenderDocApi m_RenderDoc;
    string m_OutputDirectory;

    void Awake()
    {
        m_OutputDirectory = ResolveOutputPath();
        Directory.CreateDirectory(m_OutputDirectory);
        Screen.SetResolution(RenderWidth, RenderHeight, FullScreenMode.Windowed);

        GraphicsSettings.defaultRenderPipeline = pipelineAsset;
        QualitySettings.renderPipeline = pipelineAsset;

        m_Volume = FindObjectOfType<Volume>();
        if (m_Volume == null)
            throw new InvalidOperationException("The P1-3 player scene has no volume.");
        m_ProfileInstance = ScriptableObject.Instantiate(volumeProfile);
        m_ProfileInstance.name = volumeProfile.name + " (P1-3 Player)";
        foreach (var component in m_ProfileInstance.components)
            component.active = false;
        m_Refraction = m_ProfileInstance.components.OfType<ScreenSpaceRefraction>().First();
        m_Refraction.active = true;
        m_Refraction.enabled.Override(true);
        m_Refraction.intensity.Override(1f);
        m_Refraction.screenFadeDistance.Override(0.1f);
        m_Volume.sharedProfile = m_ProfileInstance;

        DynamicGI.UpdateEnvironment();
    }

    IEnumerator Start()
    {
        m_Report.utcTimestamp = DateTime.UtcNow.ToString("O");
        m_Report.unityVersion = Application.unityVersion;
        m_Report.graphicsDeviceName = SystemInfo.graphicsDeviceName;
        m_Report.graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString();
        m_Report.outputDirectory = m_OutputDirectory;
        m_Report.pipeline = pipelineAsset != null ? pipelineAsset.name : "missing";
        m_Report.poses = new List<P13PlayerPoseResult>();
        m_Report.passed = false;

        try
        {
            yield return RunVerification();
        }
        finally
        {
            WriteOutputs();
        }

        Application.Quit(m_Report.completed && m_Report.passed ? 0 : 1);
    }

    IEnumerator RunVerification()
    {
        BuildRig();
        m_RenderDoc = RenderDocApi.Load(m_OutputDirectory, m_Log);
        m_Report.renderDocAvailable = m_RenderDoc != null;
        m_Report.renderDocVersion = m_RenderDoc != null ? m_RenderDoc.Version : "unavailable";
        m_Log.AppendLine("renderDoc=" + m_Report.renderDocVersion);

        // Let URP allocate its history and the refraction pass build the proxy table.
        yield return WaitFrames(12);

        m_Report.renderWidth = Screen.width;
        m_Report.renderHeight = Screen.height;
        m_Report.glassCenter = m_Glass.transform.position;
        m_Report.probeACenter = m_ProbeA.transform.position + m_ProbeA.center;
        m_Report.probeASize = m_ProbeA.size;
        m_Report.probeBCenter = m_ProbeB.transform.position + m_ProbeB.center;
        m_Report.probeBSize = m_ProbeB.size;

        var poses = new[]
        {
            new PoseCase("CameraAtA", m_CameraStart),
            new PoseCase("CameraMovedToB", m_CameraStart + m_ViewDirection * 6f),
        };
        var passed = true;
        foreach (var pose in poses)
        {
            var poseResult = new P13PlayerPoseResult
            {
                name = pose.name,
                cameraPosition = pose.position,
                cases = new List<P13PlayerCaseResult>(),
                passed = true,
                issues = new List<string>(),
            };
            yield return RunPose(poseResult, pose.position);
            m_Report.poses.Add(poseResult);
            passed &= poseResult.passed;
            m_Log.AppendLine($"pose {poseResult.name}: passed={poseResult.passed} issues={poseResult.issues.Count}");
        }

        m_Report.passed = passed;
        m_Report.completed = true;
        m_Refraction.enabled.Override(false);
    }

    IEnumerator RunPose(P13PlayerPoseResult pose, Vector3 cameraPosition)
    {
        m_Camera.transform.position = cameraPosition;
        m_CameraData.resetHistory = true;

        var configs = new[] { ProbeConfig.Both, ProbeConfig.ProbeAOnly, ProbeConfig.ProbeBOnly };
        var samples = new Dictionary<ProbeConfig, Color[]>();
        var metrics = new Dictionary<ProbeConfig, P13PlayerCaseResult>();

        foreach (var config in configs)
        {
            var caseResult = new P13PlayerCaseResult { name = config.ToString(), pose = pose.name };
            SetProbeConfig(config);
            m_CameraData.resetHistory = true;
            yield return WaitFrames(WarmupFrames);

            if (m_RenderDoc != null)
                yield return CaptureWithRenderDoc(caseResult, pose.name + "_" + config);

            yield return WaitFrames(1);
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                ReadCase(texture, caseResult, out var pixelData);
                samples[config] = pixelData;
                var fileName = $"P13Player_{pose.name}_{config}.png";
                File.WriteAllBytes(Path.Combine(m_OutputDirectory, fileName), texture.EncodeToPNG());
                caseResult.screenshot = fileName;
            }
            finally
            {
                Destroy(texture);
            }

            metrics[config] = caseResult;
            pose.cases.Add(caseResult);
        }

        var both = metrics[ProbeConfig.Both];
        var aOnly = metrics[ProbeConfig.ProbeAOnly];
        var bOnly = metrics[ProbeConfig.ProbeBOnly];
        var allowedB = Mathf.Max(4, Mathf.CeilToInt(both.glassCirclePixelCount * 0.03f));
        pose.circlePixelCount = both.glassCirclePixelCount;
        pose.bothToBMeanDelta = MaxChannelDelta(both.mean, bOnly.mean);
        pose.bothToAMeanDelta = MaxChannelDelta(both.mean, aOnly.mean);
        pose.bothToBChangedPixels = CountDifferencesInCircle(
            samples[ProbeConfig.Both], samples[ProbeConfig.ProbeBOnly], m_Report.renderWidth,
            both.glassCircleCenterX, both.glassCircleCenterY, both.glassCircleRadius, 1f / 255f);
        pose.bothToAChangedPixels = CountDifferencesInCircle(
            samples[ProbeConfig.Both], samples[ProbeConfig.ProbeAOnly], m_Report.renderWidth,
            both.glassCircleCenterX, both.glassCircleCenterY, both.glassCircleRadius, 1f / 255f);
        pose.bothToBMaxDelta = MaxDifferenceInCircle(
            samples[ProbeConfig.Both], samples[ProbeConfig.ProbeBOnly], m_Report.renderWidth,
            both.glassCircleCenterX, both.glassCircleCenterY, both.glassCircleRadius);
        pose.bothToAMaxDelta = MaxDifferenceInCircle(
            samples[ProbeConfig.Both], samples[ProbeConfig.ProbeAOnly], m_Report.renderWidth,
            both.glassCircleCenterX, both.glassCircleCenterY, both.glassCircleRadius);
        pose.nonFinitePixels = both.nonFinitePixels;
        // The inner circle excludes the Fresnel rim, where URP's per-renderer reflection probe
        // (not the refraction proxy table) provides the cubemap fallback.
        pose.passed = pose.nonFinitePixels == 0 &&
                      pose.bothToBMeanDelta <= 0.005f &&
                      pose.bothToAMeanDelta >= 0.01f &&
                      pose.bothToBChangedPixels <= allowedB &&
                      pose.bothToAChangedPixels > pose.circlePixelCount / 2;
        if (pose.nonFinitePixels != 0)
            pose.issues.Add("The glass region contains non-finite pixels.");
        if (pose.bothToBMeanDelta > 0.005f)
            pose.issues.Add("The both-probe frame does not match the probe-B-only frame.");
        if (pose.bothToAMeanDelta < 0.01f)
            pose.issues.Add("The both-probe frame matches the probe-A-only frame.");
        if (pose.bothToBChangedPixels > allowedB)
            pose.issues.Add("The both-probe frame differs from probe B in more than the allowed pixels.");
        if (pose.bothToAChangedPixels <= pose.circlePixelCount / 2)
            pose.issues.Add("The both-probe frame does not differ from probe A in most of the glass.");
        m_Camera.transform.position = m_CameraStart;
    }

    IEnumerator CaptureWithRenderDoc(P13PlayerCaseResult result, string captureName)
    {
        m_RenderDoc.SetCaptureFilePathTemplate(Path.Combine(m_OutputDirectory, "P13_" + captureName));
        var before = m_RenderDoc.GetNumCaptures();
        m_RenderDoc.TriggerCapture();

        // RenderDoc ends the capture at the next present, which can be a few frames away.
        for (var i = 0; i < 10 && m_RenderDoc.GetNumCaptures() <= before; i++)
            yield return new WaitForEndOfFrame();

        var after = m_RenderDoc.GetNumCaptures();
        result.captureTriggered = true;
        result.captureCountBefore = (int)before;
        result.captureCountAfter = (int)after;
        result.captureSucceeded = after > before;
        result.capture = result.captureSucceeded ? m_RenderDoc.GetCapturePath(after - 1) : string.Empty;
        m_Log.AppendLine($"capture {captureName}: succeeded={result.captureSucceeded} path={result.capture}");
    }

    void BuildRig()
    {
        m_Camera = Camera.main;
        if (m_Camera == null)
            m_Camera = FindObjectOfType<Camera>();
        if (m_Camera == null)
            throw new InvalidOperationException("The P1-3 player scene has no camera.");
        m_CameraData = m_Camera.GetComponent<UniversalAdditionalCameraData>();
        if (m_CameraData == null)
            m_CameraData = m_Camera.gameObject.AddComponent<UniversalAdditionalCameraData>();

        m_CameraStart = new Vector3(0f, 1.6f, -15f);
        m_Camera.transform.position = m_CameraStart;
        m_Camera.transform.rotation = Quaternion.identity;
        m_Camera.fieldOfView = 60f;
        m_Camera.nearClipPlane = 0.1f;
        m_Camera.farClipPlane = 100f;
        m_Camera.allowHDR = true;
        m_Camera.allowMSAA = false;
        m_Camera.clearFlags = CameraClearFlags.Skybox;
        m_Camera.cullingMask |= 1 << PatternLayer;
        m_CameraData.renderPostProcessing = false;
        m_CameraData.antialiasing = AntialiasingMode.None;

        m_ViewDirection = m_Camera.transform.forward;
        var center = m_CameraStart + m_ViewDirection * 15f;
        center.y = 1.6f;

        m_Rig = new GameObject("P1-3 Refraction Rig");
        AddBox(m_Rig.transform, floorMaterial, "P1-3 Floor", center + new Vector3(0f, -1.55f, 0f),
            new Vector3(34f, 0.05f, 22f));
        AddBox(m_Rig.transform, backdropMaterial, "P1-3 Emissive Backdrop",
            center + m_ViewDirection * 9f + Vector3.up * 2f, new Vector3(32f, 12f, 0.2f));

        m_GlassInstance = new Material(glassMaterial) { name = glassMaterial.name + " (P1-3 Player)" };
        m_GlassInstance.SetFloat("_Ior", 1f);
        m_GlassInstance.SetFloat("_Metallic", 0f);
        m_GlassInstance.SetFloat("_Smoothness", 1f);
        m_GlassInstance.SetFloat("_Thickness", 1f);
        m_GlassInstance.SetFloat("_TransmittanceDistance", 1f);
        m_GlassInstance.SetColor("_TransmittanceColor", Color.white);
        m_GlassInstance.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0f));
        m_GlassInstance.SetFloat("_RefractionModel", 1f);
        SetKeyword(m_GlassInstance, ShaderKeywordStrings.RefractionPlane, true);
        SetKeyword(m_GlassInstance, ShaderKeywordStrings.RefractionSphere, false);
        SetKeyword(m_GlassInstance, ShaderKeywordStrings.RefractionThin, false);
        m_Glass = AddSphere(m_Rig.transform, m_GlassInstance, "P1-3 Glass Sphere", center, 1.8f);

        m_PatternTexture = CreateStripeTexture();
        m_PatternInstance = new Material(unlitMaterial) { name = unlitMaterial.name + " (P1-3 Player)" };
        m_PatternInstance.SetTexture("_BaseMap", m_PatternTexture);
        m_Pattern = AddBox(m_Rig.transform, m_PatternInstance, "P1-3 Pattern Panel",
            center + m_ViewDirection * 6f, new Vector3(16f, 10f, 0.2f));
        m_Pattern.layer = PatternLayer;
        m_Pattern.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;

        var probeBObject = new GameObject("P1-3 Probe B");
        probeBObject.transform.SetParent(m_Rig.transform, false);
        probeBObject.transform.position = center;
        m_ProbeB = CreateProbe(probeBObject, new Vector3(8f, 8f, 8f), 1);

        var probeAObject = new GameObject("P1-3 Probe A");
        probeAObject.transform.SetParent(m_Rig.transform, false);
        probeAObject.transform.position = center;
        m_ProbeA = CreateProbe(probeAObject, new Vector3(4f, 4f, 4f), 0);
        m_ProbeA.center = m_CameraStart - center;

        // Like the editor rig, the probe cubemaps are frozen while refraction is off; only the
        // proxy boxes differ between the two probes.
        m_Refraction.enabled.Override(false);
        m_ProbeA.RenderProbe();
        m_ProbeB.RenderProbe();
        m_Refraction.enabled.Override(true);
    }

    ReflectionProbe CreateProbe(GameObject probeObject, Vector3 size, int importance)
    {
        var probe = probeObject.AddComponent<ReflectionProbe>();
        probe.mode = ReflectionProbeMode.Realtime;
        probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
        probe.size = size;
        probe.center = Vector3.zero;
        probe.boxProjection = true;
        probe.importance = importance;
        probe.resolution = 128;
        probe.hdr = true;
        probe.clearFlags = ReflectionProbeClearFlags.Skybox;
        probe.cullingMask = ~(1 << PatternLayer);
        return probe;
    }

    void SetProbeConfig(ProbeConfig config)
    {
        m_ProbeA.enabled = config != ProbeConfig.ProbeBOnly;
        m_ProbeB.enabled = config != ProbeConfig.ProbeAOnly;
    }

    void ReadCase(Texture2D texture, P13PlayerCaseResult result, out Color[] pixels)
    {
        pixels = texture.GetPixels();
        var center = ToPixel(m_Camera, m_Glass.transform.position, texture.width, texture.height);
        var radius = GlassCircleRadius(m_Camera, m_Glass, texture.width, texture.height);
        result.glassCircleCenterX = center.x;
        result.glassCircleCenterY = center.y;
        result.glassCircleRenderDocX = center.x;
        result.glassCircleRenderDocY = texture.height - 1 - center.y;
        result.glassCircleRadius = radius;
        result.glassCirclePixelCount = CountCirclePixels(center.x, center.y, radius, texture.width, texture.height);

        var rect = ProjectBounds(m_Camera, m_Glass, texture.width, texture.height, 0);
        int inset = Mathf.Max(0, Mathf.RoundToInt(
            Mathf.Min(rect.xMax - rect.xMin, rect.yMax - rect.yMin) * 0.25f));
        rect.xMin = Mathf.Clamp(rect.xMin + inset, 0, texture.width - 1);
        rect.yMin = Mathf.Clamp(rect.yMin + inset, 0, texture.height - 1);
        rect.xMax = Mathf.Clamp(rect.xMax - inset, 0, texture.width - 1);
        rect.yMax = Mathf.Clamp(rect.yMax - inset, 0, texture.height - 1);

        result.mean = MeanInRect(pixels, texture.width, rect);
        result.nonFinitePixels = CountNonFinite(pixels);
        result.meanLuminance = Luminance(result.mean);
    }

    static Texture2D CreateStripeTexture()
    {
        var texture = new Texture2D(8, 1, TextureFormat.RGBA32, false, false)
        {
            name = "P1-3 Player Stripes",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };
        texture.SetPixels32(new[]
        {
            new Color32(255, 0, 0, 255),
            new Color32(0, 255, 0, 255),
            new Color32(0, 0, 255, 255),
            new Color32(255, 255, 0, 255),
            new Color32(255, 0, 255, 255),
            new Color32(0, 255, 255, 255),
            new Color32(255, 255, 255, 255),
            new Color32(0, 0, 0, 255),
        });
        texture.Apply(false, false);
        return texture;
    }

    static void SetKeyword(Material material, string keyword, bool enabled)
    {
        if (enabled)
            material.EnableKeyword(keyword);
        else
            material.DisableKeyword(keyword);
    }

    static GameObject AddBox(Transform parent, Material material, string name, Vector3 position, Vector3 scale)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent, false);
        box.transform.position = position;
        box.transform.localScale = scale;
        box.GetComponent<Renderer>().sharedMaterial = material;
        return box;
    }

    static GameObject AddSphere(Transform parent, Material material, string name, Vector3 position, float scale)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = name;
        sphere.transform.SetParent(parent, false);
        sphere.transform.position = position;
        sphere.transform.localScale = Vector3.one * scale;
        sphere.GetComponent<Renderer>().sharedMaterial = material;
        return sphere;
    }

    IEnumerator WaitFrames(int frames)
    {
        for (var i = 0; i < frames; i++)
            yield return new WaitForEndOfFrame();
    }

    void WriteOutputs()
    {
        File.WriteAllText(Path.Combine(m_OutputDirectory, "P13PlayerReport.json"),
            JsonUtility.ToJson(m_Report, true));
        m_Log.Insert(0, $"completed={m_Report.completed} passed={m_Report.passed}\n");
        File.WriteAllText(Path.Combine(m_OutputDirectory, "P13Player.log"), m_Log.ToString());
        if (!string.IsNullOrEmpty(m_Report.error))
            Debug.LogError(m_Report.error);
    }

    static string ResolveOutputPath()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-ssl-p13-out")
                return args[i + 1];
        }

        return Path.Combine(Application.persistentDataPath, "P1-3Player");
    }

    struct ScreenRect
    {
        public int xMin;
        public int yMin;
        public int xMax;
        public int yMax;
    }

    static ScreenRect ProjectBounds(Camera camera, GameObject gameObject, int width, int height, int inset)
    {
        var renderer = gameObject.GetComponent<Renderer>();
        var bounds = renderer != null ? renderer.bounds : new Bounds(gameObject.transform.position, Vector3.one);
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? bounds.min.x : bounds.max.x,
                (i & 2) == 0 ? bounds.min.y : bounds.max.y,
                (i & 4) == 0 ? bounds.min.z : bounds.max.z);
            var viewport = camera.WorldToViewportPoint(corner);
            min = Vector3.Min(min, viewport);
            max = Vector3.Max(max, viewport);
        }

        return new ScreenRect
        {
            xMin = Mathf.Clamp(Mathf.RoundToInt(min.x * width) + inset, 0, width - 1),
            yMin = Mathf.Clamp(Mathf.RoundToInt(min.y * height) + inset, 0, height - 1),
            xMax = Mathf.Clamp(Mathf.RoundToInt(max.x * width) - inset, 0, width - 1),
            yMax = Mathf.Clamp(Mathf.RoundToInt(max.y * height) - inset, 0, height - 1),
        };
    }

    static Vector2Int ToPixel(Camera camera, Vector3 worldPosition, int width, int height)
    {
        var viewport = camera.WorldToViewportPoint(worldPosition);
        return new Vector2Int(
            Mathf.Clamp(Mathf.RoundToInt(viewport.x * width), 0, width - 1),
            Mathf.Clamp(Mathf.RoundToInt(viewport.y * height), 0, height - 1));
    }

    static float GlassCircleRadius(Camera camera, GameObject gameObject, int width, int height)
    {
        var rect = ProjectBounds(camera, gameObject, width, height, 0);
        return 0.5f * Mathf.Min(rect.xMax - rect.xMin, rect.yMax - rect.yMin) * 0.5f;
    }

    static Color MeanInRect(Color[] pixels, int width, ScreenRect rect)
    {
        if (rect.xMax < rect.xMin || rect.yMax < rect.yMin)
            return Color.black;
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var y = rect.yMin; y <= rect.yMax; y++)
        {
            for (var x = rect.xMin; x <= rect.xMax; x++)
            {
                var c = pixels[y * width + x];
                r += c.r;
                g += c.g;
                b += c.b;
                count++;
            }
        }

        return count > 0
            ? new Color((float)(r / count), (float)(g / count), (float)(b / count), 1f)
            : Color.black;
    }

    static int CountDifferencesInCircle(Color[] a, Color[] b, int width, int centerX, int centerY, float radius, float threshold)
    {
        if (a == null || b == null || a.Length != b.Length)
            return -1;
        var height = a.Length / Mathf.Max(1, width);
        var count = 0;
        var r = Mathf.CeilToInt(radius);
        for (var y = Mathf.Max(0, centerY - r); y <= Mathf.Min(height - 1, centerY + r); y++)
        {
            for (var x = Mathf.Max(0, centerX - r); x <= Mathf.Min(width - 1, centerX + r); x++)
            {
                if ((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) > radius * radius)
                    continue;
                if (MaxChannelDelta(a[y * width + x], b[y * width + x]) > threshold)
                    count++;
            }
        }

        return count;
    }

    static int CountCirclePixels(int centerX, int centerY, float radius, int width, int height)
    {
        var count = 0;
        var r = Mathf.CeilToInt(radius);
        for (var y = Mathf.Max(0, centerY - r); y <= Mathf.Min(height - 1, centerY + r); y++)
        {
            for (var x = Mathf.Max(0, centerX - r); x <= Mathf.Min(width - 1, centerX + r); x++)
            {
                if ((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) <= radius * radius)
                    count++;
            }
        }

        return count;
    }

    static float MaxDifferenceInCircle(Color[] a, Color[] b, int width, int centerX, int centerY, float radius)
    {
        if (a == null || b == null || a.Length != b.Length)
            return float.PositiveInfinity;
        var height = a.Length / Mathf.Max(1, width);
        var max = 0f;
        var r = Mathf.CeilToInt(radius);
        for (var y = Mathf.Max(0, centerY - r); y <= Mathf.Min(height - 1, centerY + r); y++)
        {
            for (var x = Mathf.Max(0, centerX - r); x <= Mathf.Min(width - 1, centerX + r); x++)
            {
                if ((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) > radius * radius)
                    continue;
                max = Mathf.Max(max, MaxChannelDelta(a[y * width + x], b[y * width + x]));
            }
        }

        return max;
    }

    static int CountNonFinite(Color[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            if (!IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b) || !IsFinite(c.a))
                count++;
        }

        return count;
    }

    static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    static float MaxChannelDelta(Color a, Color b)
    {
        return Mathf.Max(
            Mathf.Abs(a.r - b.r),
            Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
    }

    static float Luminance(Color color)
    {
        return color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;
    }

    readonly struct PoseCase
    {
        public readonly string name;
        public readonly Vector3 position;

        public PoseCase(string name, Vector3 position)
        {
            this.name = name;
            this.position = position;
        }
    }

    enum ProbeConfig
    {
        Both,
        ProbeAOnly,
        ProbeBOnly,
    }

    sealed class RenderDocApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void SetCaptureFilePathTemplateDelegate(
            [MarshalAs(UnmanagedType.LPStr)] string pathTemplate);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint GetNumCapturesDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint GetCaptureDelegate(uint index, byte[] filename, ref uint pathLength, out ulong timestamp);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void TriggerCaptureDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GetApiDelegate(uint version, out IntPtr apiPointers);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [StructLayout(LayoutKind.Sequential)]
        struct ApiTable
        {
            public IntPtr GetAPIVersion;
            public IntPtr SetCaptureOptionU32;
            public IntPtr SetCaptureOptionF32;
            public IntPtr GetCaptureOptionU32;
            public IntPtr GetCaptureOptionF32;
            public IntPtr SetFocusToggleKeys;
            public IntPtr SetCaptureKeys;
            public IntPtr GetOverlayBits;
            public IntPtr MaskOverlayBits;
            public IntPtr Shutdown;
            public IntPtr UnloadCrashHandler;
            public IntPtr SetCaptureFilePathTemplate;
            public IntPtr GetCaptureFilePathTemplate;
            public IntPtr GetNumCaptures;
            public IntPtr GetCapture;
            public IntPtr TriggerCapture;
            public IntPtr IsTargetControlConnected;
            public IntPtr LaunchReplayUI;
            public IntPtr SetActiveWindow;
            public IntPtr StartFrameCapture;
            public IntPtr IsFrameCapturing;
            public IntPtr EndFrameCapture;
        }

        const uint ApiVersion = 10600;

        SetCaptureFilePathTemplateDelegate m_SetTemplate;
        GetNumCapturesDelegate m_GetNumCaptures;
        GetCaptureDelegate m_GetCapture;
        TriggerCaptureDelegate m_TriggerCapture;

        public string Version { get; private set; } = "unavailable";

        public static RenderDocApi Load(string outputDirectory, StringBuilder log)
        {
            var module = GetModuleHandle("renderdoc.dll");
            if (module == IntPtr.Zero)
            {
                log.AppendLine("renderdoc.dll not loaded; run the player through renderdoccmd capture.");
                return null;
            }

            var getApiAddress = GetProcAddress(module, "RENDERDOC_GetAPI");
            if (getApiAddress == IntPtr.Zero)
                throw new InvalidOperationException("renderdoc.dll has no RENDERDOC_GetAPI export.");
            var getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(getApiAddress);
            if (getApi(ApiVersion, out var apiPointers) == 0 || apiPointers == IntPtr.Zero)
                throw new InvalidOperationException("RENDERDOC_GetAPI rejected version 1.6.0.");

            var table = Marshal.PtrToStructure<ApiTable>(apiPointers);
            var api = new RenderDocApi
            {
                m_SetTemplate = Marshal.GetDelegateForFunctionPointer<SetCaptureFilePathTemplateDelegate>(
                    table.SetCaptureFilePathTemplate),
                m_GetNumCaptures = Marshal.GetDelegateForFunctionPointer<GetNumCapturesDelegate>(
                    table.GetNumCaptures),
                m_GetCapture = Marshal.GetDelegateForFunctionPointer<GetCaptureDelegate>(
                    table.GetCapture),
                m_TriggerCapture = Marshal.GetDelegateForFunctionPointer<TriggerCaptureDelegate>(
                    table.TriggerCapture),
            };
            var version = Marshal.GetDelegateForFunctionPointer<GetVersionDelegate>(table.GetAPIVersion);
            version(out var major, out var minor, out var patch);
            api.Version = $"{major}.{minor}.{patch}";
            log.AppendLine("renderdoc api " + api.Version + " output=" + outputDirectory);
            return api;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void GetVersionDelegate(out int major, out int minor, out int patch);

        public void SetCaptureFilePathTemplate(string pathTemplate)
        {
            m_SetTemplate(pathTemplate.Replace('\\', '/'));
        }

        public uint GetNumCaptures()
        {
            return m_GetNumCaptures();
        }

        public void TriggerCapture()
        {
            m_TriggerCapture();
        }

        public string GetCapturePath(uint index)
        {
            var buffer = new byte[1024];
            uint length = (uint)buffer.Length;
            if (m_GetCapture(index, buffer, ref length, out _) == 0)
                return string.Empty;
            var count = (int)Math.Min(length, (uint)buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, count).TrimEnd('\0');
        }
    }
}

[Serializable]
public sealed class P13PlayerReport
{
    public string utcTimestamp;
    public string unityVersion;
    public string graphicsDeviceName;
    public string graphicsDeviceType;
    public string outputDirectory;
    public string pipeline;
    public bool renderDocAvailable;
    public string renderDocVersion;
    public int renderWidth;
    public int renderHeight;
    public Vector3 glassCenter;
    public Vector3 probeACenter;
    public Vector3 probeASize;
    public Vector3 probeBCenter;
    public Vector3 probeBSize;
    public bool passed;
    public bool completed;
    public string error;
    public List<P13PlayerPoseResult> poses;
}

[Serializable]
public sealed class P13PlayerPoseResult
{
    public string name;
    public Vector3 cameraPosition;
    public List<P13PlayerCaseResult> cases;
    public int circlePixelCount;
    public float bothToBMeanDelta;
    public float bothToAMeanDelta;
    public int bothToBChangedPixels;
    public int bothToAChangedPixels;
    public float bothToBMaxDelta;
    public float bothToAMaxDelta;
    public int nonFinitePixels;
    public bool passed;
    public List<string> issues;
}

[Serializable]
public sealed class P13PlayerCaseResult
{
    public string name;
    public string pose;
    public bool captureTriggered;
    public bool captureSucceeded;
    public int captureCountBefore;
    public int captureCountAfter;
    public string capture;
    public string screenshot;
    public int glassCircleCenterX;
    public int glassCircleCenterY;
    public int glassCircleRenderDocX;
    public int glassCircleRenderDocY;
    public float glassCircleRadius;
    public int glassCirclePixelCount;
    public Color mean;
    public float meanLuminance;
    public int nonFinitePixels;
}
