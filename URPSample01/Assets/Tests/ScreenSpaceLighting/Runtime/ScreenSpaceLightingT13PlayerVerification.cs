using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

// T13 XR verification, running in a standalone player because Mock HMD only produces XR render
// passes while a player renders.
//
// For both stereo modes (multipass and single-pass instanced) the runner captures each eye with the
// screen-space effects off and on, checks that the two eyes are not identical, that turning the
// effects off restores the reference, and inspects the per-eye Screen Space Lighting history:
// resource slices, reset generation, previous view matrices and the previous color pyramids.
//
// Results and PNGs are written to the directory passed with -ssl-t13-out.
public sealed class ScreenSpaceLightingT13PlayerVerification : MonoBehaviour
{
    public UniversalRenderPipelineAsset pipelineAsset;
    public Material metalMaterial;
    public Material glassMaterial;
    public Material transparentReceiverMaterial;
    public Material backdropMaterial;
    public Material floorMaterial;
    public ComputeShader arrayProbeCompute;

    const int WarmupFrames = 12;
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    readonly StringBuilder m_Log = new StringBuilder();
    readonly List<T13XrModeResult> m_Results = new List<T13XrModeResult>();
    string m_OutputDirectory;
    Volume m_Volume;
    VolumeProfile m_Profile;
    ScreenSpaceGlobalIllumination m_Ssgi;
    ScreenSpaceReflection m_Ssr;
    ScreenSpaceRefraction m_Refraction;
    GameObject m_Backdrop;

    IEnumerator Start()
    {
        m_OutputDirectory = ResolveOutputPath();
        Directory.CreateDirectory(m_OutputDirectory);
        m_Log.AppendLine("T13 XR player verification");
        m_Log.AppendLine("graphicsDeviceType=" + SystemInfo.graphicsDeviceType);
        m_Log.AppendLine("device=" + SystemInfo.graphicsDeviceName);
        m_Log.AppendLine("foveatedRenderingCaps=" + SystemInfo.foveatedRenderingCaps);
        m_Log.AppendLine("xrAvailable=" + ScreenSpaceLightingT13XrEnvironment.xrPackageAvailable);

        try
        {
            CreateRig();
        }
        catch (Exception exception)
        {
            m_Log.AppendLine("rig exception=" + exception);
            Finish(1);
            yield break;
        }

        yield return null;

        m_Log.AppendLine("arrayProbe=" + RunArrayProbe());

        foreach (var mode in ResolveModes())
        {
            var result = new T13XrModeResult
            {
                mode = mode.ToString(),
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                foveatedRenderingCaps = SystemInfo.foveatedRenderingCaps.ToString(),
                cases = new List<T13XrCaseResult>(),
                passed = true,
                issues = new List<string>(),
            };

            string startMessage = string.Empty;
            bool started = false;
            try
            {
                ScreenSpaceLightingT13XrEnvironment.Stop();
                started = ScreenSpaceLightingT13XrEnvironment.Start(mode, out startMessage);
            }
            catch (Exception exception)
            {
                startMessage = exception.ToString();
            }

            result.started = started;
            result.startMessage = startMessage;
            if (!started)
            {
                result.passed = false;
                result.issues.Add("XR display did not start: " + startMessage);
                m_Results.Add(result);
                continue;
            }

            SetEffects(false, false, false, false);
            yield return null;

            // Reference.
            EyePair reference = null;
            yield return CaptureRoutine("Reference", result, mode, pair => reference = pair);

            // All on.
            SetEffects(true, true, true, true);
            EyePair allOn = null;
            yield return CaptureRoutine("AllOn", result, mode, pair => allOn = pair);
            result.historyEntries = CountHistoryEntries();
            result.historySlices = ReadHistoryInt("slices");
            result.historyDimension = ReadHistoryString("dimension");
            result.resetCount = ReadHistoryInt("resetCount");
            result.depthPyramidSliceCount = ReadDepthPyramidSlices();
            result.previousViewProjViews = CountPreviousViewProjViews();
            result.previousColorPyramidSliceHashes = ReadPreviousColorPyramidSliceHashes();
            result.ssgiProduced = ReadPassProduced("m_SsgiPass");
            result.ssrProduced = ReadPassProduced("m_SsrPass");
            result.transparentSsrProduced = ReadPassProduced("m_TransparentSsrPass");
            ReadCameraAttachmentDimensions(result);
            ReadEffectTextureStats(result);
            result.ssgiHitXMean = ReadHistorySliceMeans("ssgiHitPoint", 0);
            result.ssrHitXMean = ReadHistorySliceMeans("ssrHitPoint", 0);
            result.gtaoMean = ReadGtaoSliceMeans();
            result.colorPyramidMean = ReadHistorySliceMeans("currentColorPyramid", 0);
            ReadDispatchCounters(result);
            ReadViewProjSummary(result);
            yield return null;

            // Off again restores the reference.
            SetEffects(false, false, false, false);
            EyePair offAgain = null;
            yield return CaptureRoutine("AllOffAgain", result, mode, pair => offAgain = pair);
            result.referenceRestored = ImageEquals(reference.left, offAgain.left) && ImageEquals(reference.right, offAgain.right);
            if (!result.referenceRestored)
                result.issues.Add("Turning every effect off did not restore the reference image.");
            yield return null;

            // Per-eye separation in the reference and effect images.
            result.referenceEyeDifference = CountDifferences(reference.left, reference.right);
            result.effectEyeDifference = CountDifferences(allOn.left, allOn.right);
            result.effectsChangedLeft = CountDifferences(reference.left, allOn.left);
            result.effectsChangedRight = CountDifferences(reference.right, allOn.right);
            result.nonFinitePixels = CountNonFinite(allOn.left) + CountNonFinite(allOn.right);

            if (result.referenceEyeDifference <= 0)
                result.issues.Add("The two eyes rendered identical reference images.");
            if (result.effectsChangedLeft <= 0 || result.effectsChangedRight <= 0)
                result.issues.Add("Screen-space effects were not visible in both eyes.");
            if (result.nonFinitePixels != 0)
                result.issues.Add("The all-on image contains non-finite pixels.");
            if (result.historyEntries <= 0)
                result.issues.Add("No Screen Space Lighting history entry was found.");
            if (mode == ScreenSpaceLightingT13XrEnvironment.StereoMode.SinglePassInstanced)
            {
                if (result.historySlices != 2 || result.historyDimension != "Tex2DArray")
                    result.issues.Add("Single-pass history is not a two-slice texture array.");
                if (result.depthPyramidSliceCount != 2)
                    result.issues.Add("Single-pass depth pyramid does not have two slices.");
                if (result.previousColorPyramidSliceHashes.Length != 2 ||
                    result.previousColorPyramidSliceHashes[0] == result.previousColorPyramidSliceHashes[1])
                    result.issues.Add("Single-pass previous color pyramids are identical across eyes.");
            }
            else
            {
                if (result.historyEntries != 2 || result.historySlices != 1)
                    result.issues.Add("Multipass history is not one entry per eye.");
                if (result.previousViewProjViews < 2)
                    result.issues.Add("Multipass did not record a previous matrix for both eyes.");
            }

            // Per-module attribution, so a single effect cannot hide behind another.
            SetEffects(true, false, false, false);
            EyePair ssgi = null;
            yield return CaptureRoutine("SsgiOnly", result, mode, pair => ssgi = pair);
            result.ssgiChangedLeft = CountDifferences(reference.left, ssgi.left);
            result.ssgiChangedRight = CountDifferences(reference.right, ssgi.right);
            yield return null;

            SetEffects(false, true, false, false);
            EyePair ssr = null;
            yield return CaptureRoutine("SsrOnly", result, mode, pair => ssr = pair);
            result.ssrChangedLeft = CountDifferences(reference.left, ssr.left);
            result.ssrChangedRight = CountDifferences(reference.right, ssr.right);
            yield return null;

            SetEffects(false, false, true, false);
            EyePair refraction = null;
            yield return CaptureRoutine("RefractionOnly", result, mode, pair => refraction = pair);
            result.refractionChangedLeft = CountDifferences(reference.left, refraction.left);
            result.refractionChangedRight = CountDifferences(reference.right, refraction.right);
            yield return null;

            SetEffects(false, false, false, true);
            EyePair gtao = null;
            yield return CaptureRoutine("GtaoOnly", result, mode, pair => gtao = pair);
            result.gtaoChangedLeft = CountDifferences(reference.left, gtao.left);
            result.gtaoChangedRight = CountDifferences(reference.right, gtao.right);
            yield return null;

            if (result.ssgiChangedLeft <= 0 || result.ssgiChangedRight <= 0)
                result.issues.Add("SSGI was not visible in both eyes.");
            if (result.ssrChangedLeft <= 0 || result.ssrChangedRight <= 0)
                result.issues.Add("SSR was not visible in both eyes.");
            if (result.refractionChangedLeft <= 0 || result.refractionChangedRight <= 0)
                result.issues.Add("Refraction was not visible in both eyes.");

            SetEffects(false, false, false, false);
            yield return null;

            result.passed = result.issues.Count == 0;
            m_Results.Add(result);
            m_Log.AppendLine($"{mode}: passed={result.passed} issues={result.issues.Count} " +
                             $"eyes={result.referenceEyeDifference}/{result.effectEyeDifference} " +
                             $"changed={result.effectsChangedLeft}/{result.effectsChangedRight} " +
                             $"entries={result.historyEntries} slices={result.historySlices} dim={result.historyDimension}");
            foreach (var issue in result.issues)
                m_Log.AppendLine("  issue: " + issue);
        }

        ScreenSpaceLightingT13XrEnvironment.Stop();
        Finish(m_Results.All(r => r.passed) ? 0 : 1);
    }

    // ---------------------------------------------------------------------------------------------
    // Rig
    // ---------------------------------------------------------------------------------------------

    void CreateRig()
    {
        if (pipelineAsset != null)
        {
            GraphicsSettings.defaultRenderPipeline = pipelineAsset;
            QualitySettings.renderPipeline = pipelineAsset;
        }

        var camera = gameObject.AddComponent<Camera>();
        camera.transform.position = new Vector3(0f, 1.6f, -6f);
        camera.transform.LookAt(new Vector3(0f, 1.2f, 0f));
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.05f, 0.06f, 0.08f);
        camera.tag = "MainCamera";
        var cameraData = camera.GetUniversalAdditionalCameraData();
        cameraData.renderPostProcessing = false;
        cameraData.antialiasing = AntialiasingMode.None;

        var center = new Vector3(0f, 1.2f, 0f);
        AddBox(floorMaterial, "T13 Floor", center + new Vector3(0f, -1.6f, 0f), new Vector3(30f, 0.05f, 20f));
        m_Backdrop = AddBox(backdropMaterial, "T13 Backdrop", center + new Vector3(0f, 1.2f, 8f), new Vector3(26f, 9f, 0.2f));
        AddSphere(metalMaterial, "T13 Metal Sphere", center + new Vector3(2.4f, -0.2f, 1.5f), 1.6f);
        AddSphere(glassMaterial, "T13 Glass", center + new Vector3(-2.4f, -0.3f, 1f), 1.3f);
        AddSphere(transparentReceiverMaterial, "T13 Transparent Receiver", center + new Vector3(0.2f, -0.4f, 2.2f), 1.2f);

        var lightObject = new GameObject("T13 Light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.6f;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var probeObject = new GameObject("T13 Reflection Probe");
        probeObject.transform.position = center;
        var probe = probeObject.AddComponent<ReflectionProbe>();
        probe.mode = ReflectionProbeMode.Realtime;
        probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
        probe.size = new Vector3(40f, 20f, 30f);
        probe.boxProjection = true;
        probe.resolution = 64;
        probe.hdr = true;
        probe.RenderProbe();

        var volumeObject = new GameObject("T13 Volume");
        m_Volume = volumeObject.AddComponent<Volume>();
        m_Volume.isGlobal = true;
        m_Volume.priority = 10f;
        m_Profile = ScriptableObject.CreateInstance<VolumeProfile>();
        m_Profile.name = "T13 XR Profile";
        m_Ssgi = m_Profile.Add<ScreenSpaceGlobalIllumination>(true);
        m_Ssr = m_Profile.Add<ScreenSpaceReflection>(true);
        m_Refraction = m_Profile.Add<ScreenSpaceRefraction>(true);
        m_Volume.sharedProfile = m_Profile;
    }

    GameObject AddBox(Material material, string name, Vector3 position, Vector3 size)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.position = position;
        box.transform.localScale = size;
        box.GetComponent<Renderer>().sharedMaterial = material;
        return box;
    }

    GameObject AddSphere(Material material, string name, Vector3 position, float scale)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = name;
        sphere.transform.position = position;
        sphere.transform.localScale = Vector3.one * scale;
        sphere.GetComponent<Renderer>().sharedMaterial = material;
        return sphere;
    }

    void SetEffects(bool ssgi, bool ssr, bool refraction, bool gtao)
    {
        m_Ssgi.enabled.Override(ssgi);
        m_Ssr.enabled.Override(ssr);
        m_Ssr.enabledTransparent.Override(ssr);
        m_Refraction.enabled.Override(refraction);
        SetGtaoIntensity(gtao ? 1f : 0f);
    }

    // ---------------------------------------------------------------------------------------------
    // Capture and comparison
    // ---------------------------------------------------------------------------------------------

    sealed class EyePair
    {
        public Color[] left;
        public Color[] right;
        public int width;
        public int height;
    }

    IEnumerator CaptureRoutine(
        string caseName,
        T13XrModeResult result,
        ScreenSpaceLightingT13XrEnvironment.StereoMode mode,
        Action<EyePair> store)
    {
        for (int frame = 0; frame < WarmupFrames; frame++)
        {
            yield return null;
        }

        var display = ScreenSpaceLightingT13XrEnvironment.GetDisplay();
        if (display == null)
        {
            store(new EyePair { left = Array.Empty<Color>(), right = Array.Empty<Color>() });
            yield break;
        }

        yield return new WaitForEndOfFrame();

        var pair = new EyePair();
        if (mode == ScreenSpaceLightingT13XrEnvironment.StereoMode.MultiPass)
        {
            var left = ScreenSpaceLightingT13XrEnvironment.GetEyeTexture(0);
            var right = ScreenSpaceLightingT13XrEnvironment.GetEyeTexture(1);
            pair.left = ReadTexture(left);
            pair.right = ReadTexture(right);
            pair.width = left != null ? left.width : 0;
            pair.height = left != null ? left.height : 0;
        }
        else
        {
            var array = ScreenSpaceLightingT13XrEnvironment.GetEyeTexture(0);
            pair.left = ReadArraySlice(array, 0);
            pair.right = ReadArraySlice(array, 1);
            pair.width = array != null ? array.width : 0;
            pair.height = array != null ? array.height : 0;
        }

        var caseResult = new T13XrCaseResult
        {
            name = caseName,
            leftHash = HashSamples(pair.left),
            rightHash = HashSamples(pair.right),
            leftMeanLuminance = MeanLuminance(pair.left),
            rightMeanLuminance = MeanLuminance(pair.right),
            leftNonFinite = CountNonFinite(pair.left),
            rightNonFinite = CountNonFinite(pair.right),
        };
        result.cases.Add(caseResult);

        WriteImage(pair, pair.left, caseName + "_Left.png");
        WriteImage(pair, pair.right, caseName + "_Right.png");
        store(pair);
    }

    static Color[] ReadTexture(RenderTexture texture)
    {
        if (texture == null)
            return Array.Empty<Color>();

        var previous = RenderTexture.active;
        RenderTexture.active = texture;
        var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
        try
        {
            copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
            copy.Apply(false, false);
            return copy.GetPixels();
        }
        finally
        {
            RenderTexture.active = previous;
            UnityEngine.Object.Destroy(copy);
        }
    }

    // A texture array slice is read by binding it as the active render target. CopyTexture is not used
// because it rejects some floating point array formats on D3D11 and silently leaves the copy empty.
    static Color[] ReadArraySlice(RenderTexture array, int slice)
    {
        if (array == null)
            return Array.Empty<Color>();

        var previous = RenderTexture.active;
        var copy = new Texture2D(array.width, array.height, TextureFormat.RGBAFloat, false, true);
        try
        {
            Graphics.SetRenderTarget(array, 0, CubemapFace.Unknown, slice);
            copy.ReadPixels(new Rect(0, 0, array.width, array.height), 0, 0, false);
            copy.Apply(false, false);
            return copy.GetPixels();
        }
        finally
        {
            RenderTexture.active = previous;
            UnityEngine.Object.Destroy(copy);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // History inspection
    // ---------------------------------------------------------------------------------------------

    object GetSslHistory()
    {
        var feature = Resources.FindObjectsOfTypeAll<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        var store = feature?.GetType().GetField("m_HistoryStore", InstanceFlags)?.GetValue(feature);
        var entries = store?.GetType().GetField("m_Entries", InstanceFlags)?.GetValue(store) as System.Collections.IDictionary;
        if (entries == null || entries.Count == 0)
            return null;

        foreach (System.Collections.DictionaryEntry entry in entries)
        {
            var value = entry.Value;
            var history = value?.GetType().GetField("history", InstanceFlags)?.GetValue(value);
            if (history != null)
                return history;
        }

        return null;
    }

    int CountHistoryEntries()
    {
        var feature = Resources.FindObjectsOfTypeAll<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        var store = feature?.GetType().GetField("m_HistoryStore", InstanceFlags)?.GetValue(feature);
        var entries = store?.GetType().GetField("m_Entries", InstanceFlags)?.GetValue(store) as System.Collections.IDictionary;
        return entries?.Count ?? -1;
    }

    int ReadHistoryInt(string property)
    {
        var history = GetSslHistory();
        if (history == null)
            return -1;

        var info = history.GetType().GetProperty(property, InstanceFlags);
        return info != null ? Convert.ToInt32(info.GetValue(history)) : -1;
    }

    string ReadHistoryString(string property)
    {
        var history = GetSslHistory();
        if (history == null)
            return string.Empty;

        var info = history.GetType().GetProperty(property, InstanceFlags);
        return info != null ? info.GetValue(history).ToString() : string.Empty;
    }

    int ReadDepthPyramidSlices()
    {
        var history = GetSslHistory();
        var handle = history?.GetType().GetProperty("depthPyramid", InstanceFlags)?.GetValue(history) as RTHandle;
        return handle?.rt != null ? handle.rt.volumeDepth : -1;
    }

    int ReadPreviousViewProjCount()
    {
        var history = GetSslHistory();
        var flags = history?.GetType().GetField("m_HasPreviousViewProj", InstanceFlags)?.GetValue(history) as bool[];
        return flags?.Count(f => f) ?? -1;
    }

    // Multipass renders one eye per camera pass, so each history entry records only view 0; the
    // total across entries is what matters.
    int CountPreviousViewProjViews()
    {
        var feature = Resources.FindObjectsOfTypeAll<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        var store = feature?.GetType().GetField("m_HistoryStore", InstanceFlags)?.GetValue(feature);
        var entries = store?.GetType().GetField("m_Entries", InstanceFlags)?.GetValue(store) as System.Collections.IDictionary;
        if (entries == null)
            return -1;

        int count = 0;
        foreach (System.Collections.DictionaryEntry entry in entries)
        {
            var history = entry.Value?.GetType().GetField("history", InstanceFlags)?.GetValue(entry.Value);
            if (history?.GetType().GetField("m_HasPreviousViewProj", InstanceFlags)?.GetValue(history) is bool[] flags)
                count += flags.Count(f => f);
        }

        return count;
    }

    static bool ReadPassProduced(string passFieldName)
    {
        var feature = Resources.FindObjectsOfTypeAll<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        var pass = feature?.GetType().GetField(passFieldName, InstanceFlags)?.GetValue(feature);
        var produced = pass?.GetType().GetProperty("producedOutput", InstanceFlags)?.GetValue(pass);
        return produced is bool value && value;
    }

    void ReadCameraAttachmentDimensions(T13XrModeResult result)
    {
        var camera = GetComponent<Camera>();
        var cameraData = camera != null ? camera.GetUniversalAdditionalCameraData() : null;
        var renderer = cameraData?.scriptableRenderer as UniversalRenderer;
        if (renderer == null)
            return;

        var normalsHandle = renderer.GetType().GetProperty("cameraNormalsTextureHandle", InstanceFlags)?.GetValue(renderer) as RTHandle;
        var depthHandle = renderer.GetType().GetField("m_DepthTexture", InstanceFlags)?.GetValue(renderer) as RTHandle;
        result.normalsDimension = normalsHandle?.rt != null ? normalsHandle.rt.dimension.ToString() : "null";
        result.depthDimension = depthHandle?.rt != null ? depthHandle.rt.dimension.ToString() : "null";
    }

    // Reads the private SSGI/SSR result so a material-side keyword problem can be told apart from an
    // empty compute result.
    void ReadEffectTextureStats(T13XrModeResult result)
    {
        var ssgiFinal = ReadPassHandle("m_SsgiPass", "finalTexture");
        var ssrOutput = ReadPassHandle("m_SsrPass", "outputTexture");
        result.ssgiFinalAlphaMean = ReadSliceAlphaMeans(ssgiFinal);
        result.ssrWeightMean = ReadSliceAlphaMeans(ssrOutput);
    }

    static RTHandle ReadPassHandle(string passFieldName, string propertyName)
    {
        var feature = Resources.FindObjectsOfTypeAll<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        var pass = feature?.GetType().GetField(passFieldName, InstanceFlags)?.GetValue(feature);
        return pass?.GetType().GetProperty(propertyName, InstanceFlags)?.GetValue(pass) as RTHandle;
    }

    static float[] ReadSliceAlphaMeans(RTHandle handle)
    {
        return ReadSliceChannelMeans(handle, 3);
    }

    static float[] ReadSliceChannelMeans(RTHandle handle, int channel)
    {
        if (handle?.rt == null)
            return Array.Empty<float>();

        int slices = Mathf.Max(1, handle.rt.volumeDepth);
        var means = new float[slices];
        for (int slice = 0; slice < slices; slice++)
        {
            var samples = handle.rt.dimension == TextureDimension.Tex2DArray
                ? ReadArraySlice(handle.rt, slice)
                : ReadTexture(handle.rt);
            means[slice] = MeanChannel(samples, channel);
        }

        return means;
    }

    static float MeanChannel(Color[] samples, int channel)
    {
        if (samples == null || samples.Length == 0)
            return 0f;

        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += channel == 0 ? samples[i].r : channel == 1 ? samples[i].g : channel == 2 ? samples[i].b : samples[i].a;
        return (float)(sum / samples.Length);
    }

    float[] ReadHistorySliceMeans(string propertyName, int channel)
    {
        var history = GetSslHistory();
        var handle = history?.GetType().GetProperty(propertyName, InstanceFlags)?.GetValue(history) as RTHandle;
        if (handle?.rt == null)
            return Array.Empty<float>();

        int slices = Mathf.Max(1, handle.rt.volumeDepth);
        var means = new float[slices];
        for (int slice = 0; slice < slices; slice++)
        {
            var samples = handle.rt.dimension == TextureDimension.Tex2DArray
                ? ReadArraySlice(handle.rt, slice)
                : ReadTexture(handle.rt);
            double sum = 0;
            for (int i = 0; i < samples.Length; i++)
                sum += channel == 0 ? samples[i].r : samples[i].g;
            means[slice] = samples.Length > 0 ? (float)(sum / samples.Length) : 0f;
        }

        return means;
    }

    static float[] ReadGtaoSliceMeans()
    {
        var feature = Resources.FindObjectsOfTypeAll<GroundTruthAmbientOcclusionRendererFeature>().FirstOrDefault();
        var pass = feature?.GetType().GetField("pass", InstanceFlags)?.GetValue(feature);
        var history = pass?.GetType().GetField("m_History", InstanceFlags)?.GetValue(pass);
        var handle = history?.GetType().GetProperty("occlusionTexture", InstanceFlags)?.GetValue(history) as RTHandle;
        if (handle?.rt == null)
            return Array.Empty<float>();

        int slices = Mathf.Max(1, handle.rt.volumeDepth);
        var means = new float[slices];
        for (int slice = 0; slice < slices; slice++)
        {
            var samples = handle.rt.dimension == TextureDimension.Tex2DArray
                ? ReadArraySlice(handle.rt, slice)
                : ReadTexture(handle.rt);
            double sum = 0;
            for (int i = 0; i < samples.Length; i++)
                sum += samples[i].r;
            means[slice] = samples.Length > 0 ? (float)(sum / samples.Length) : 0f;
        }

        return means;
    }

    void ReadViewProjSummary(T13XrModeResult result)
    {
        var camera = GetComponent<Camera>();
        var cameraData = camera != null ? camera.GetUniversalAdditionalCameraData() : null;
        if (cameraData == null)
            return;

        var renderer = cameraData.scriptableRenderer;
        // The XR pass matrices are not public, so log the mono values as a sanity check and rely on
        // the hit-point statistics for the XR path.
        var view = camera.worldToCameraMatrix;
        var proj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
        var viewProj = proj * view;
        result.monoViewProjSummary = $"{viewProj.m00:F3},{viewProj.m03:F3},{viewProj.m23:F3}";
        result.monoCameraPosition = camera.transform.position.ToString("F3");
    }

    void ReadDispatchCounters(T13XrModeResult result)
    {
        var ssgiType = FindPackageType("ScreenSpaceLightingSsgiPass");
        var ssrType = FindPackageType("ScreenSpaceLightingSsrPass");
        result.ssgiTraceDispatches = ReadStaticInt(ssgiType, "s_TraceDispatchCount");
        result.ssgiReprojectDispatches = ReadStaticInt(ssgiType, "s_ReprojectDispatchCount");
        result.ssgiDispatchViewCount = ReadStaticInt(ssgiType, "s_LastDispatchViewCount");
        result.ssgiDispatchTextureArray = ReadStaticInt(ssgiType, "s_LastDispatchTextureArray");
        result.ssrTraceDispatches = ReadStaticInt(ssrType, "s_TraceDispatchCount");
        result.ssrReprojectDispatches = ReadStaticInt(ssrType, "s_ReprojectDispatchCount");
        result.ssrDispatchViewCount = ReadStaticInt(ssrType, "s_LastDispatchViewCount");
        result.ssrDispatchTextureArray = ReadStaticInt(ssrType, "s_LastDispatchTextureArray");
    }

    static Type FindPackageType(string typeName)
    {
        return typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal." + typeName);
    }

    static int ReadStaticInt(Type type, string fieldName)
    {
        var field = type?.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return field != null ? Convert.ToInt32(field.GetValue(null)) : -1;
    }

    string[] ReadPreviousColorPyramidSliceHashes()
    {
        var history = GetSslHistory();
        if (history == null)
            return Array.Empty<string>();

        var handle = history.GetType().GetProperty("readColorPyramid", InstanceFlags)?.GetValue(history) as RTHandle;
        if (handle?.rt == null || handle.rt.volumeDepth < 2)
            return Array.Empty<string>();

        var hashes = new string[handle.rt.volumeDepth];
        for (int slice = 0; slice < hashes.Length; slice++)
            hashes[slice] = HashSamples(ReadArraySlice(handle.rt, slice));
        return hashes;
    }

    static void SetGtaoIntensity(float value)
    {
        var feature = Resources.FindObjectsOfTypeAll<GroundTruthAmbientOcclusionRendererFeature>().FirstOrDefault();
        var settings = feature?.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        var field = settings?.GetType().GetField("intensity", InstanceFlags);
        if (field != null)
            field.SetValue(settings, value);
    }

    // ---------------------------------------------------------------------------------------------
    // Image helpers
    // ---------------------------------------------------------------------------------------------

    static int CountDifferences(Color[] a, Color[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return Math.Max(a?.Length ?? 0, b?.Length ?? 0);

        int count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float delta = Mathf.Max(
                Mathf.Abs(a[i].r - b[i].r),
                Mathf.Max(Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b)));
            if (delta > 1e-6f)
                count++;
        }

        return count;
    }

    static bool ImageEquals(Color[] a, Color[] b)
    {
        return a != null && b != null && a.Length == b.Length && CountDifferences(a, b) == 0;
    }

    static int CountNonFinite(Color[] samples)
    {
        if (samples == null)
            return 0;

        int count = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            var c = samples[i];
            if (float.IsNaN(c.r) || float.IsInfinity(c.r) ||
                float.IsNaN(c.g) || float.IsInfinity(c.g) ||
                float.IsNaN(c.b) || float.IsInfinity(c.b))
                count++;
        }

        return count;
    }

    static float MeanLuminance(Color[] samples)
    {
        if (samples == null || samples.Length == 0)
            return 0f;

        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i].r * 0.2126 + samples[i].g * 0.7152 + samples[i].b * 0.0722;
        return (float)(sum / samples.Length);
    }

    static string HashSamples(Color[] samples)
    {
        if (samples == null)
            return string.Empty;

        using (var sha = SHA256.Create())
        {
            var bytes = new byte[samples.Length * 16];
            for (int i = 0; i < samples.Length; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(samples[i].r), 0, bytes, i * 16, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(samples[i].g), 0, bytes, i * 16 + 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(samples[i].b), 0, bytes, i * 16 + 8, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(samples[i].a), 0, bytes, i * 16 + 12, 4);
            }

            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
        }
    }

    void WriteImage(EyePair pair, Color[] samples, string fileName)
    {
        if (samples == null || samples.Length == 0 || pair.width <= 0 || pair.height <= 0)
            return;

        int width = pair.width;
        int height = pair.height;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        try
        {
            var pixels = new Color32[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                pixels[i] = new Color32(
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(samples[i].r) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(samples[i].g) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(samples[i].b) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(samples[i].a) * 255f));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(Path.Combine(m_OutputDirectory, fileName), texture.EncodeToPNG());
        }
        finally
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Output
    // ---------------------------------------------------------------------------------------------

    string RunArrayProbe()
    {
        if (arrayProbeCompute == null)
            return "compute asset missing";

        var descriptor = new RenderTextureDescriptor(8, 8, RenderTextureFormat.ARGB32, 0)
        {
            dimension = TextureDimension.Tex2DArray,
            volumeDepth = 2,
            msaaSamples = 1,
        };
        var target = new RenderTexture(descriptor) { enableRandomWrite = true, useMipMap = false };
        target.Create();
        try
        {
            var kernel = arrayProbeCompute.FindKernel("Write");
            arrayProbeCompute.EnableKeyword("T13_XR_ARRAY");
            arrayProbeCompute.SetTexture(kernel, "_T13ProbeDstArray", target);
            var cmd = new CommandBuffer { name = "T13 array probe" };
            cmd.DispatchCompute(arrayProbeCompute, kernel, 1, 1, 2);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            var slice0 = ReadArraySlice(target, 0);
            var slice1 = ReadArraySlice(target, 1);
            return $"slice0={(slice0.Length > 0 ? slice0[0].ToString() : "empty")} " +
                   $"slice1={(slice1.Length > 0 ? slice1[0].ToString() : "empty")}";
        }
        catch (Exception exception)
        {
            return "exception=" + exception.Message;
        }
        finally
        {
            target.Release();
            UnityEngine.Object.Destroy(target);
        }
    }

    static string ResolveOutputPath()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-ssl-t13-out")
                return args[i + 1];
        }

        return Path.Combine(Application.persistentDataPath, "T13");
    }

    // -ssl-t13-mode makes a run cover one stereo mode so mode transitions cannot mask a failure.
    static ScreenSpaceLightingT13XrEnvironment.StereoMode[] ResolveModes()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "-ssl-t13-mode")
                continue;

            if (args[i + 1].IndexOf("single", StringComparison.OrdinalIgnoreCase) >= 0)
                return new[] { ScreenSpaceLightingT13XrEnvironment.StereoMode.SinglePassInstanced };
            if (args[i + 1].IndexOf("multi", StringComparison.OrdinalIgnoreCase) >= 0)
                return new[] { ScreenSpaceLightingT13XrEnvironment.StereoMode.MultiPass };
        }

        return new[]
        {
            ScreenSpaceLightingT13XrEnvironment.StereoMode.MultiPass,
            ScreenSpaceLightingT13XrEnvironment.StereoMode.SinglePassInstanced,
        };
    }

    void Finish(int exitCode)
    {
        var report = new T13XrPlayerReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            foveatedRenderingCaps = SystemInfo.foveatedRenderingCaps.ToString(),
            modes = m_Results,
        };
        File.WriteAllText(Path.Combine(m_OutputDirectory, "T13XrPlayerVerification.json"), JsonUtility.ToJson(report, true));
        File.WriteAllText(Path.Combine(m_OutputDirectory, "T13XrPlayerVerification.log.txt"), m_Log.ToString());
        Debug.Log(m_Log.ToString());
        Application.Quit(exitCode);
    }
}

[Serializable]
public sealed class T13XrPlayerReport
{
    public string utcTimestamp;
    public string unityVersion;
    public string graphicsDeviceType;
    public string graphicsDeviceName;
    public string foveatedRenderingCaps;
    public List<T13XrModeResult> modes;
}

[Serializable]
public sealed class T13XrModeResult
{
    public string mode;
    public string graphicsDeviceType;
    public string foveatedRenderingCaps;
    public bool started;
    public string startMessage;
    public bool passed;
    public List<string> issues;
    public List<T13XrCaseResult> cases;
    public int historyEntries;
    public int historySlices;
    public string historyDimension;
    public int resetCount;
    public int depthPyramidSliceCount;
    public int previousViewProjViews;
    public string[] previousColorPyramidSliceHashes;
    public bool ssgiProduced;
    public bool ssrProduced;
    public bool transparentSsrProduced;
    public string normalsDimension;
    public string depthDimension;
    public float[] ssgiFinalAlphaMean;
    public float[] ssrWeightMean;
    public float[] ssgiHitXMean;
    public float[] ssrHitXMean;
    public float[] gtaoMean;
    public float[] colorPyramidMean;
    public string monoViewProjSummary;
    public string monoCameraPosition;
    public int ssgiTraceDispatches;
    public int ssgiReprojectDispatches;
    public int ssgiDispatchViewCount;
    public int ssgiDispatchTextureArray;
    public int ssrTraceDispatches;
    public int ssrReprojectDispatches;
    public int ssrDispatchViewCount;
    public int ssrDispatchTextureArray;
    public bool referenceRestored;
    public int referenceEyeDifference;
    public int effectEyeDifference;
    public int effectsChangedLeft;
    public int effectsChangedRight;
    public int ssgiChangedLeft;
    public int ssgiChangedRight;
    public int ssrChangedLeft;
    public int ssrChangedRight;
    public int refractionChangedLeft;
    public int refractionChangedRight;
    public int gtaoChangedLeft;
    public int gtaoChangedRight;
    public int nonFinitePixels;
}

[Serializable]
public sealed class T13XrCaseResult
{
    public string name;
    public string leftHash;
    public string rightHash;
    public float leftMeanLuminance;
    public float rightMeanLuminance;
    public int leftNonFinite;
    public int rightNonFinite;
}