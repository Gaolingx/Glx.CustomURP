using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// T2 acceptance: proves the Core early motion/depth schedule and the depth-normals smoothness
// encoding in all three renderer modes without any GTAO/SSL implementation present.
public static class ScreenSpaceLightingT2Verification
{
    const string Root = "Assets/Tests/ScreenSpaceLighting";
    const string T1RendererRoot = Root + "/Renderers";
    const string T2RendererRoot = Root + "/Renderers";
    const string MaterialRoot = Root + "/Materials";
    const string OutputRoot = "PLAN/Done/01-ScreenSpaceLighting/validation/T2";

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };

    [MenuItem("Tests/Screen Space Lighting/Create T2 Probe Assets")]
    public static void CreateT2Assets()
    {
        EnsureAssets(force: true);
    }

    [MenuItem("Tests/Screen Space Lighting/Run T2 Verification")]
    public static void RunT2Verification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var report = new T2Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            modes = new List<T2ModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        GameObject unlitObject = null;
        Material unlitMaterial = null;
        GameObject volumeObject = null;
        RenderTexture probeTarget = null;
        RenderTexture imageTarget = null;
        try
        {
            EnsureAssets(force: false);
            EditorSceneManager.OpenScene(ScreenSpaceLightingBaselineSetup.ScenePath, OpenSceneMode.Single);

            var camera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (camera == null)
                throw new InvalidOperationException("The T2 baseline scene has no camera.");
            var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData == null)
                throw new InvalidOperationException("The T2 baseline camera has no UniversalAdditionalCameraData.");
            var runner = UnityEngine.Object.FindObjectOfType<ScreenSpaceLightingBaselineRunner>();
            if (runner != null)
                runner.enabled = false;

            var probeMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/SslProbe.mat");
            if (probeMaterial == null)
                throw new FileNotFoundException("Missing T2 probe material.");

            probeTarget = new RenderTexture(320, 180, 0, GraphicsFormat.R32G32B32A32_SFloat) { name = "SslT2Probe" };
            probeTarget.Create();
            imageTarget = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { name = "SslT2Image" };
            imageTarget.Create();
            ScreenSpaceLightingPassProbeFeature.probeTarget = probeTarget;
            camera.targetTexture = imageTarget;
            camera.aspect = 1280f / 720f;

            unlitObject = CreateUnlitProbeObject(out unlitMaterial);
            volumeObject = CreateMotionBlurVolume();
            var cameraTransform = camera.transform;
            var cameraPosition = cameraTransform.position;
            var cameraRotation = cameraTransform.rotation;

            for (var modeIndex = 0; modeIndex < ModeNames.Length; modeIndex++)
            {
                var mode = CaptureMode(
                    ModeNames[modeIndex],
                    modeIndex,
                    camera,
                    cameraData,
                    cameraTransform,
                    cameraPosition,
                    cameraRotation,
                    probeMaterial,
                    probeTarget,
                    imageTarget,
                    volumeObject);
                report.modes.Add(mode);
                report.passed &= mode.passed;
                Debug.Log($"T2 {mode.name}: passed={mode.passed} staticMotionMax={mode.staticMotionMax:G4} dynamicMotionMax={mode.dynamicMotionMax:G4}");
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
            ScreenSpaceLightingPassProbeFeature.probeTarget = null;
            if (unlitObject != null)
                UnityEngine.Object.DestroyImmediate(unlitObject);
            if (unlitMaterial != null)
                UnityEngine.Object.DestroyImmediate(unlitMaterial);
            if (volumeObject != null)
                UnityEngine.Object.DestroyImmediate(volumeObject);
            if (probeTarget != null)
            {
                probeTarget.Release();
                UnityEngine.Object.DestroyImmediate(probeTarget);
            }
            if (imageTarget != null)
            {
                imageTarget.Release();
                UnityEngine.Object.DestroyImmediate(imageTarget);
            }
            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            File.WriteAllText(Path.Combine(outputDirectory, "T2Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T2 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static T2ModeResult CaptureMode(
        string modeName,
        int modeIndex,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        Transform cameraTransform,
        Vector3 cameraPosition,
        Quaternion cameraRotation,
        Material probeMaterial,
        RenderTexture probeTarget,
        RenderTexture imageTarget,
        GameObject volumeObject)
    {
        var result = new T2ModeResult { name = modeName, issues = new List<string>() };
        var rendererPath = T2RendererRoot + "/SslT2" + modeName + "Renderer.asset";
        var pipelinePath = T2RendererRoot + "/SslT2" + modeName + "Pipeline.asset";
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
        if (renderer == null || pipeline == null)
            throw new FileNotFoundException("Missing T2 renderer or pipeline asset for " + modeName + ".");
        var feature = renderer.rendererFeatures.Count > 0 ? renderer.rendererFeatures[0] as ScreenSpaceLightingPassProbeFeature : null;
        if (feature == null)
            throw new InvalidOperationException("Missing T2 probe feature on " + rendererPath + ".");

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        cameraData.renderPostProcessing = false;
        cameraData.antialiasing = AntialiasingMode.None;
        cameraData.renderShadows = true;
        camera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
        feature.SetActive(true);

        var dynamicObject = GameObject.Find("Material Sphere 1_1");
        if (dynamicObject == null)
            throw new InvalidOperationException("Missing the T2 dynamic probe object.");
        var dynamicStart = dynamicObject.transform.position;
        var dynamicProbeCount = 0;
        var executeCountBefore = ScreenSpaceLightingPassProbeFeature.executeCount;

        // Static camera and objects: motion must be zero and all inputs must be readable at 203.
        for (var i = 0; i < 8; i++)
            camera.Render();
        probeMaterial.SetFloat("_ProbeMode", 0f);
        camera.Render();
        var probe = ReadProbe(probeTarget);
        WriteProbePng(probeTarget, "T2ProbeDepth_" + modeName + ".png");
        result.probeExecutions = ScreenSpaceLightingPassProbeFeature.executeCount - executeCountBefore;
        result.staticMotionMax = MaxAbsMotion(probe);
        result.depthGeometryFraction = Fraction(probe, c => c.r < 0.999f);
        result.minDepth01 = MinDepth(probe);
        result.alphaMin = Min(probe, c => c.a);
        result.alphaMax = Max(probe, c => c.a);
        result.alphaMean = Mean(probe, c => c.a);
        result.alphaUnsupportedCount = Count(probe, c => c.a <= 1e-4f);
        result.alphaSmoothnessZeroCount = Count(probe, c => Mathf.Abs(c.a - 0.5f) <= 0.005f);
        result.alphaHighCount = Count(probe, c => c.a >= 0.9f && c.a <= 1.0f);
        result.alphaReservedGapCount = Count(probe, c => c.a > 1e-4f && c.a < 0.495f);

        probeMaterial.SetFloat("_ProbeMode", 1f);
        camera.Render();
        var normalProbe = ReadProbe(probeTarget);
        result.normalsPresentFraction = Fraction(normalProbe, c => c.a > 0.5f);
        result.decodedNormalLengthMean = Mean(normalProbe, c =>
        {
            if (c.a <= 0.5f)
                return 0f;
            var x = c.r * 2f - 1f;
            var y = c.g * 2f - 1f;
            var z = c.b * 2f - 1f;
            return Mathf.Sqrt(x * x + y * y + z * z);
        });

        probeMaterial.SetFloat("_ProbeMode", 2f);
        camera.Render();
        var constantProbe = ReadProbe(probeTarget);
        result.constantAlphaMin = Min(constantProbe, c => c.a);
        result.constantAlphaMax = Max(constantProbe, c => c.a);

        probeMaterial.SetFloat("_ProbeMode", 3f);
        camera.Render();
        WriteProbePng(probeTarget, "T2ProbeRawNormal_" + modeName + ".png");
        var rawProbe = ReadProbe(probeTarget);
        result.rawNormalRedMean = Mean(rawProbe, c => c.r);
        result.rawNormalGreenMean = Mean(rawProbe, c => c.g);
        result.rawNormalBlueMean = Mean(rawProbe, c => c.b);
        result.rawNormalAlphaMean = Mean(rawProbe, c => c.a);
        result.rawNormalAlphaMin = Min(rawProbe, c => c.a);
        result.rawNormalAlphaMax = Max(rawProbe, c => c.a);
        // Unity's error shader paints (1, 0, 1); catch a DepthNormals variant that failed to compile.
        result.rawNormalMagentaCount = Count(rawProbe, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f);

        // One-frame object jump: motion at 203 must describe the current frame, not the previous one.
        probeMaterial.SetFloat("_ProbeMode", 0f);
        dynamicObject.transform.position = dynamicStart + new Vector3(0.35f, 0f, 0f);
        camera.Render();
        var dynamicProbe = ReadProbe(probeTarget);
        result.dynamicMotionMax = MaxAbsMotion(dynamicProbe);
        dynamicProbeCount = Count(dynamicProbe, c => Mathf.Abs(c.g) + Mathf.Abs(c.b) > 1e-4f);
        result.dynamicMotionPixelCount = dynamicProbeCount;
        dynamicObject.transform.position = dynamicStart;
        camera.Render();
        camera.Render();

        // TAA equivalence: with an early consumer the last frame must match the default schedule.
        cameraData.renderPostProcessing = true;
        cameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
        for (var i = 0; i < 20; i++)
            camera.Render();
        result.taaEarlyHash = ReadImageHash(imageTarget, out var taaSamples);
        feature.SetActive(false);
        for (var i = 0; i < 20; i++)
            camera.Render();
        result.taaDefaultHash = ReadImageHash(imageTarget, out var taaSamplesDefault);
        result.taaMeanAbsDiff = MeanAbsDiff(taaSamples, taaSamplesDefault);

        // Motion blur equivalence with continuous camera rotation. Both sequences must start from the
        // same pose, otherwise the last frame of each would be captured at a different heading.
        feature.SetActive(true);
        cameraData.antialiasing = AntialiasingMode.None;
        EnableMotionBlur(volumeObject, true);
        camera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
        for (var i = 0; i < 20; i++)
        {
            camera.transform.Rotate(0f, 0.4f, 0f, Space.World);
            camera.Render();
        }
        result.motionBlurEarlyHash = ReadImageHash(imageTarget, out var blurSamples);
        feature.SetActive(false);
        camera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
        for (var i = 0; i < 20; i++)
        {
            camera.transform.Rotate(0f, 0.4f, 0f, Space.World);
            camera.Render();
        }
        result.motionBlurDefaultHash = ReadImageHash(imageTarget, out var blurSamplesDefault);
        result.motionBlurMeanAbsDiff = MeanAbsDiff(blurSamples, blurSamplesDefault);
        EnableMotionBlur(volumeObject, false);

        cameraData.renderPostProcessing = false;
        cameraData.antialiasing = AntialiasingMode.None;
        camera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);

        Evaluate(result);
        return result;
    }

    static void Evaluate(T2ModeResult result)
    {
        if (result.probeExecutions <= 0)
            result.issues.Add("probe pass did not execute");
        if (result.staticMotionMax > 1e-4f)
            result.issues.Add($"static motion is not zero ({result.staticMotionMax:G4})");
        if (result.dynamicMotionMax <= 1e-3f)
            result.issues.Add($"dynamic motion is not visible at the probe event ({result.dynamicMotionMax:G4})");
        if (result.dynamicMotionPixelCount <= 0)
            result.issues.Add("no pixel carries dynamic motion");
        if (result.depthGeometryFraction <= 0.01f)
            result.issues.Add("camera depth texture has no geometry at the probe event");
        if (result.minDepth01 >= 0.999f)
            result.issues.Add("camera depth texture is far depth at the probe event");
        if (result.normalsPresentFraction <= 0.01f)
            result.issues.Add("camera normals texture is empty at the probe event");
        if (result.rawNormalMagentaCount > 0)
            result.issues.Add($"normals texture contains the error-shader magenta ({result.rawNormalMagentaCount} pixels)");
        if (result.alphaSmoothnessZeroCount <= 0)
            result.issues.Add("no pixel encodes smoothness zero (expected alpha 0.5)");
        if (result.alphaUnsupportedCount <= 0)
            result.issues.Add("no unsupported (alpha 0) pixel");
        if (result.alphaHighCount <= 0)
            result.issues.Add("no high-smoothness pixel");
        if (result.alphaReservedGapCount > 0)
            result.issues.Add($"alpha uses the reserved 0-0.5 gap ({result.alphaReservedGapCount} pixels)");
        if (result.taaMeanAbsDiff > 0.01f)
            result.issues.Add($"TAA output differs with early motion ({result.taaMeanAbsDiff:G4})");
        if (result.motionBlurMeanAbsDiff > 0.01f)
            result.issues.Add($"motion blur output differs with early motion ({result.motionBlurMeanAbsDiff:G4})");
        result.passed = result.issues.Count == 0;
    }

    static void EnsureAssets(bool force)
    {
        var probeMaterial = GetOrCreateProbeMaterial();
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var mode = ModeNames[i];
            var t1RendererPath = T1RendererRoot + "/Ssl" + mode + "Renderer.asset";
            var rendererPath = T2RendererRoot + "/SslT2" + mode + "Renderer.asset";
            var pipelinePath = T2RendererRoot + "/SslT2" + mode + "Pipeline.asset";
            var t1Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(t1RendererPath);
            if (t1Renderer == null)
                throw new FileNotFoundException("Missing T1 renderer asset. Create the T1 baseline first.", t1RendererPath);

            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (renderer == null || force)
            {
                if (renderer != null)
                    AssetDatabase.DeleteAsset(rendererPath);
                renderer = UnityEngine.Object.Instantiate(t1Renderer);
                renderer.name = "SslT2" + mode + "Renderer";
                AssetDatabase.CreateAsset(renderer, rendererPath);
            }

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            if (pipeline == null || force)
            {
                var template = UniversalRenderPipelineAsset.Create(renderer);
                if (pipeline == null)
                {
                    pipeline = template;
                    AssetDatabase.CreateAsset(pipeline, pipelinePath);
                }
                else
                {
                    EditorUtility.CopySerialized(template, pipeline);
                    UnityEngine.Object.DestroyImmediate(template);
                }
                // CopySerialized overwrites the name with the template's empty one when re-creating.
                pipeline.name = "SslT2" + mode + "Pipeline";
                pipeline.supportsHDR = true;
                pipeline.msaaSampleCount = 1;
                pipeline.renderScale = 1f;
                pipeline.shadowDistance = 50f;
                pipeline.supportsCameraDepthTexture = false;
                pipeline.supportsCameraOpaqueTexture = false;
                EditorUtility.SetDirty(pipeline);
            }

            var feature = renderer.rendererFeatures.Count > 0 ? renderer.rendererFeatures[0] as ScreenSpaceLightingPassProbeFeature : null;
            if (feature == null)
            {
                foreach (var existing in AssetDatabase.LoadAllAssetsAtPath(rendererPath))
                {
                    if (existing is ScreenSpaceLightingPassProbeFeature)
                        AssetDatabase.RemoveObjectFromAsset(existing);
                }
                feature = ScriptableObject.CreateInstance<ScreenSpaceLightingPassProbeFeature>();
                feature.name = "ScreenSpaceLighting Pass Probe";
                feature.probeEvent = (RenderPassEvent)203;
                feature.probeMaterial = probeMaterial;
                AssetDatabase.AddObjectToAsset(feature, renderer);
                renderer.rendererFeatures.Clear();
                renderer.rendererFeatures.Add(feature);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
                var serialized = new SerializedObject(renderer);
                var map = serialized.FindProperty("m_RendererFeatureMap");
                if (map != null)
                {
                    map.arraySize = 1;
                    map.GetArrayElementAtIndex(0).longValue = localId;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(renderer);
            }
            else
            {
                feature.probeMaterial = probeMaterial;
                feature.probeEvent = (RenderPassEvent)203;
                EditorUtility.SetDirty(feature);
            }
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static Material GetOrCreateProbeMaterial()
    {
        var path = MaterialRoot + "/SslProbe.mat";
        var shader = Shader.Find("Hidden/ScreenSpaceLighting/Probe");
        if (shader == null)
            throw new InvalidOperationException("The T2 probe shader was not imported.");
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        else
        {
            material.shader = shader;
        }
        return material;
    }

    static GameObject CreateUnlitProbeObject(out Material material)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "T2 Unlit Probe";
        sphere.transform.position = new Vector3(-3.5f, 0.65f, 4.8f);
        material = new Material(Shader.Find("Universal Render Pipeline/Unlit"))
        {
            name = "T2 Unlit Probe Material",
            hideFlags = HideFlags.HideAndDontSave
        };
        material.SetColor("_BaseColor", new Color(0.1f, 0.9f, 0.3f));
        sphere.GetComponent<Renderer>().sharedMaterial = material;
        return sphere;
    }

    static GameObject CreateMotionBlurVolume()
    {
        var volumeObject = new GameObject("T2 Motion Blur Volume");
        var volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 100f;
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        var motionBlur = profile.Add<MotionBlur>();
        motionBlur.active = true;
        motionBlur.mode.Override(MotionBlurMode.CameraOnly);
        motionBlur.intensity.Override(1f);
        motionBlur.quality.Override(MotionBlurQuality.High);
        motionBlur.clamp.Override(0.1f);
        volume.sharedProfile = profile;
        volume.enabled = false;
        return volumeObject;
    }

    static void EnableMotionBlur(GameObject volumeObject, bool enabled)
    {
        volumeObject.SetActive(enabled);
        var volume = volumeObject.GetComponent<Volume>();
        if (volume != null)
            volume.enabled = enabled;
    }

    static void WriteProbePng(RenderTexture target, string fileName)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
        try
        {
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            texture.Apply(false, false);
            RenderTexture.active = previous;
            File.WriteAllBytes(Path.Combine(projectRoot, OutputRoot, fileName), texture.EncodeToPNG());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    static Color[] ReadProbe(RenderTexture target)
    {        var texture = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
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

    static float MaxAbsMotion(Color[] probe)
    {
        var max = 0f;
        foreach (var pixel in probe)
            max = Mathf.Max(max, Mathf.Abs(pixel.g), Mathf.Abs(pixel.b));
        return max;
    }

    static float MinDepth(Color[] probe)
    {
        var min = 1f;
        foreach (var pixel in probe)
        {
            if (pixel.r > 0f && pixel.r < min)
                min = pixel.r;
        }
        return min;
    }

    static float Min(Color[] probe, Func<Color, float> selector)
    {
        var min = float.PositiveInfinity;
        foreach (var pixel in probe)
            min = Mathf.Min(min, selector(pixel));
        return probe.Length == 0 ? 0f : min;
    }

    static float Max(Color[] probe, Func<Color, float> selector)
    {
        var max = float.NegativeInfinity;
        foreach (var pixel in probe)
            max = Mathf.Max(max, selector(pixel));
        return probe.Length == 0 ? 0f : max;
    }

    static float Mean(Color[] probe, Func<Color, float> selector)
    {
        double sum = 0.0;
        foreach (var pixel in probe)
            sum += selector(pixel);
        return probe.Length == 0 ? 0f : (float)(sum / probe.Length);
    }

    static float Fraction(Color[] probe, Func<Color, bool> predicate)
    {
        var count = 0;
        foreach (var pixel in probe)
        {
            if (predicate(pixel))
                count++;
        }
        return probe.Length == 0 ? 0f : (float)count / probe.Length;
    }

    static int Count(Color[] probe, Func<Color, bool> predicate)
    {
        var count = 0;
        foreach (var pixel in probe)
        {
            if (predicate(pixel))
                count++;
        }
        return count;
    }

    static string ReadImageHash(RenderTexture target, out Color[] samples)
    {
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
        try
        {
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            texture.Apply(false, false);
            RenderTexture.active = previous;
            samples = texture.GetPixels();
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(texture.GetRawTextureData())).Replace("-", string.Empty).ToLowerInvariant();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    static float MeanAbsDiff(Color[] first, Color[] second)
    {
        if (first == null || second == null || first.Length != second.Length)
            return float.PositiveInfinity;
        double sum = 0.0;
        for (var i = 0; i < first.Length; i++)
        {
            sum += Math.Abs(first[i].r - second[i].r);
            sum += Math.Abs(first[i].g - second[i].g);
            sum += Math.Abs(first[i].b - second[i].b);
        }
        return (float)(sum / (first.Length * 3.0));
    }

    [Serializable]
    sealed class T2Report
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed = true;
        public string error;
        public List<T2ModeResult> modes;
    }

    [Serializable]
    sealed class T2ModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public int probeExecutions;
        public float staticMotionMax;
        public float dynamicMotionMax;
        public int dynamicMotionPixelCount;
        public float depthGeometryFraction;
        public float minDepth01;
        public float normalsPresentFraction;
        public int alphaUnsupportedCount;
        public int alphaSmoothnessZeroCount;
        public int alphaHighCount;
        public int alphaReservedGapCount;
        public float alphaMin;
        public float alphaMax;
        public float alphaMean;
        public float decodedNormalLengthMean;
        public float constantAlphaMin;
        public float constantAlphaMax;
        public float rawNormalRedMean;
        public float rawNormalGreenMean;
        public float rawNormalBlueMean;
        public float rawNormalAlphaMean;
        public float rawNormalAlphaMin;
        public float rawNormalAlphaMax;
        public int rawNormalMagentaCount;
        public string taaEarlyHash;
        public string taaDefaultHash;
        public float taaMeanAbsDiff;
        public string motionBlurEarlyHash;
        public string motionBlurDefaultHash;
        public float motionBlurMeanAbsDiff;
    }
}
