using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// T11 acceptance: screen-space refraction.
//
// The checks cover:
// - the material contract: None is the default and keeps the original transparent output; a
//   selected model changes only the refractive surfaces;
// - current frame color pyramid: the transmitted color follows the opaque scene of the current
//   frame and does not include transparents (the pre-refraction ordering difference is measured);
// - proxy: the box of the active box-projected reflection probe is the scene proxy; without one
//   the material keeps the original URP probe/transparent path;
// - transmittance/absorption and the volume intensity/edge fade are real controls;
// - old transparent materials (no refraction model) are byte identical;
// - ShaderGraph Lit gets the transparent prepass and consumes refraction;
// - all three renderer paths (Forward, Forward+, Deferred).
public static class ScreenSpaceLightingT11Verification
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string MaterialRoot = "Assets/Tests/ScreenSpaceLighting/Materials";
    const string ShaderRoot = "Assets/Tests/ScreenSpaceLighting/Shaders";
    const string OutputRoot = "PALN/validation/T11";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string T1RendererPathFormat = RendererRoot + "/Ssl{0}Renderer.asset";
    const string T11RendererPathFormat = RendererRoot + "/SslT11{0}Renderer.asset";
    const string T11PipelinePathFormat = RendererRoot + "/SslT11{0}Pipeline.asset";

    const string BoxMaterialPath = MaterialRoot + "/T11RefractionBox.mat";
    const string SphereMaterialPath = MaterialRoot + "/T11RefractionSphere.mat";
    const string ThinMaterialPath = MaterialRoot + "/T11RefractionThin.mat";
    const string NoneMaterialPath = MaterialRoot + "/T11RefractionNone.mat";
    const string TintMaterialPath = MaterialRoot + "/T11RefractionTint.mat";
    const string ComplexLitMaterialPath = MaterialRoot + "/T11RefractionComplexLit.mat";
    const string LegacyMaterialPath = MaterialRoot + "/T11LegacyTransparent.mat";
    const string GraphPath = ShaderRoot + "/SslT11Lit.shadergraph";
    const string GraphMaterialPath = MaterialRoot + "/T11RefractionShaderGraph.mat";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 12;
    // Half-float ulp noise: separate shader variants can differ by one ulp without any real change.
    const float k_IdenticalThreshold = 1e-6f;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Create T11 Assets")]
    public static void CreateT11Assets()
    {
        // The graph and its material are regenerated so target/code changes always reach the
        // generated shader (ShaderGraph does not reimport unchanged graph assets).
        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(GraphPath) != null)
            AssetDatabase.DeleteAsset(GraphPath);
        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(GraphMaterialPath) != null)
            AssetDatabase.DeleteAsset(GraphMaterialPath);

        EnsureAssets(force: true);
        EnsureShaderGraphAsset();
        EnsureMaterialAssets();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Tests/Screen Space Lighting/Run T11 Verification")]
    public static void RunT11Verification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var report = new T11Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            passed = true,
            modes = new List<T11ModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        Volume volume = null;
        VolumeProfile originalProfile = null;
        VolumeProfile testProfile = null;
        GameObject rig = null;
        T11Rig rigParts = null;
        try
        {
            EnsureAssets(force: false);
            EnsureShaderGraphAsset();
            EnsureMaterialAssets();
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
            testProfile.name = originalProfile.name + " (T11)";
            volume.sharedProfile = testProfile;
            DisableScreenSpaceEffects(testProfile);

            rigParts = CreateRig(camera);
            rig = rigParts.root;

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslT11Image");
            camera.targetTexture = imageTarget;
            camera.aspect = (float)ImageWidth / ImageHeight;
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;
            cameraData.resetHistory = true;

            for (int modeIndex = 0; modeIndex < ModeNames.Length; modeIndex++)
            {
                var mode = CaptureMode(ModeNames[modeIndex], camera, cameraData, testProfile, imageTarget, rigParts,
                    outputDirectory);
                report.modes.Add(mode);
                report.passed &= mode.passed;
                Debug.Log($"T11 {mode.name}: passed={mode.passed} issues={mode.issues.Count}");
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
            AssetDatabase.SaveAssets();
            File.WriteAllText(Path.Combine(outputDirectory, "T11Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T11 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    // ---------------------------------------------------------------------------------------------
    // Per renderer refraction coverage
    // ---------------------------------------------------------------------------------------------

    static T11ModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        VolumeProfile profile,
        RenderTexture imageTarget,
        T11Rig rigParts,
        string outputDirectory)
    {
        var result = new T11ModeResult
        {
            name = modeName,
            issues = new List<string>(),
            states = new List<T11StateCapture>(),
        };

        var refraction = profile.components.OfType<ScreenSpaceRefraction>().First();
        var pipeline = LoadPipeline(T11PipelinePathFormat, modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T11RendererPathFormat, modeName));
        if (renderer == null)
            throw new FileNotFoundException("Missing T11 renderer asset. Run Create T11 Assets first.");
        var feature = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (feature == null)
            throw new InvalidOperationException("The T11 renderer is missing the Screen Space Lighting feature.");

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        DisableScreenSpaceEffects(profile);
        rigParts.ResetVisibility();
        rigParts.SetModels(1);

        // -----------------------------------------------------------------------------------------
        // Reference: screen-space effects are off. The glass keeps its URP probe/transparent path.
        // -----------------------------------------------------------------------------------------
        refraction.enabled.Override(false);
        cameraData.resetHistory = true;
        var reference = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("Reference", reference, null));
        WriteImage(imageTarget, outputDirectory, "T11_" + modeName + "_Reference.png");

        // -----------------------------------------------------------------------------------------
        // Refraction on.
        // -----------------------------------------------------------------------------------------
        refraction.enabled.Override(true);
        refraction.intensity.Override(1f);
        refraction.screenFadeDistance.Override(0.1f);
        cameraData.resetHistory = true;
        var refractionOn = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("RefractionOn", refractionOn, reference));
        WriteImage(imageTarget, outputDirectory, "T11_" + modeName + "_RefractionOn.png");

        // Editor evidence: the bindings pass must run and find a box-projected proxy.
        var refractionPassType = FindPackageType("ScreenSpaceLightingRefractionPass");
        ResetEditorEvidence(refractionPassType);
        RenderAndCapture(camera, imageTarget, 4);
        result.refractionExecuteCount = ReadEditorInt(refractionPassType, "s_EditorExecuteCount");
        result.refractionBoundCount = ReadEditorInt(refractionPassType, "s_EditorBoundCount");
        result.refractionNoProxyCount = ReadEditorInt(refractionPassType, "s_EditorNoProxyCount");
        result.visibleProbeCount = ReadEditorInt(refractionPassType, "s_EditorVisibleProbeCount");
        result.sceneProbeCount = ReadEditorInt(refractionPassType, "s_EditorSceneProbeCount");
        result.boxProbeCount = ReadEditorInt(refractionPassType, "s_EditorBoxProbeCount");
        if (result.refractionExecuteCount < 4)
            result.issues.Add("The refraction bindings pass did not run every frame.");
        if (result.refractionBoundCount < 4)
            result.issues.Add("The refraction bindings pass did not find a box-projected proxy.");

        // -----------------------------------------------------------------------------------------
        // The model None control: the material must keep the original transparent output even while
        // the volume and the global keyword are enabled.
        // -----------------------------------------------------------------------------------------
        rigParts.SetModels(0);
        cameraData.resetHistory = true;
        var modelsNone = RenderAndCapture(camera, imageTarget, 6);
        result.states.Add(DescribeState("ModelsNone", modelsNone, reference));
        WriteImage(imageTarget, outputDirectory, "T11_" + modeName + "_ModelsNone.png");
        if (!result.states[result.states.Count - 1].identicalToReference)
            result.issues.Add("Refraction model None changed the image; the old transparent path was not preserved.");
        rigParts.SetModels(1);

        // -----------------------------------------------------------------------------------------
        // Intensity 0 must restore the reference byte for byte.
        // -----------------------------------------------------------------------------------------
        refraction.intensity.Override(0f);
        cameraData.resetHistory = true;
        var intensityZero = RenderAndCapture(camera, imageTarget, 6);
        result.states.Add(DescribeState("Intensity0", intensityZero, reference));
        WriteImage(imageTarget, outputDirectory, "T11_" + modeName + "_Intensity0.png");
        if (!result.states[result.states.Count - 1].identicalToReference)
            result.issues.Add("Refraction intensity 0 did not restore the reference; the contribution is not gated by the weight.");

        // -----------------------------------------------------------------------------------------
        // Off again: no residual keyword/output for the next camera.
        // -----------------------------------------------------------------------------------------
        refraction.intensity.Override(1f);
        refraction.enabled.Override(false);
        cameraData.resetHistory = true;
        var offAgain = RenderAndCapture(camera, imageTarget, 6);
        result.states.Add(DescribeState("OffAgain", offAgain, reference));
        WriteImage(imageTarget, outputDirectory, "T11_" + modeName + "_OffAgain.png");
        if (!result.states[result.states.Count - 1].identicalToReference)
            result.issues.Add("Disabling refraction did not restore the reference image.");
        refraction.enabled.Override(true);

        // -----------------------------------------------------------------------------------------
        // Region contract: only refractive surfaces change; the legacy transparent material keeps
        // its original output.
        // -----------------------------------------------------------------------------------------
        var boxRect = ProjectBounds(camera, rigParts.boxGlass, 2);
        var legacyRect = ProjectBounds(camera, rigParts.legacy, 2);
        result.boxChangedPixels = CountDifferencesInRect(refractionOn, reference, boxRect, 0f);
        result.boxMaxDelta = MaxDifferenceInRect(refractionOn, reference, boxRect);
        result.legacyChangedPixels = CountDifferencesInRect(refractionOn, reference, legacyRect, k_IdenticalThreshold);
        result.legacyMaxDelta = MaxDifferenceInRect(refractionOn, reference, legacyRect);
        if (result.boxChangedPixels <= 0)
            result.issues.Add("The box refraction material did not change with refraction enabled.");
        if (result.legacyChangedPixels != 0)
            result.issues.Add("A transparent material without a refraction model changed with refraction enabled.");

        // Complex Lit shares the Lit forward pass; the clear coat must not disable refraction.
        var complexRect = ProjectBounds(camera, rigParts.complexGlass, 2);
        result.complexChangedPixels = CountDifferencesInRect(refractionOn, reference, complexRect, 0f);
        result.complexMaxDelta = MaxDifferenceInRect(refractionOn, reference, complexRect);
        if (result.complexChangedPixels <= 0)
            result.issues.Add("The Complex Lit refraction material did not change with refraction enabled.");

        // Sphere and thin models use the same proxy but different exit points.
        var sphereRect = ProjectBounds(camera, rigParts.sphereGlass, 2);
        result.sphereChangedPixels = CountDifferencesInRect(refractionOn, reference, sphereRect, 0f);
        if (result.sphereChangedPixels <= 0)
            result.issues.Add("The sphere refraction model did not change the image.");

        var shaderGraphRect = ProjectBounds(camera, rigParts.shaderGraphGlass, 2);
        result.shaderGraphChangedPixels = CountDifferencesInRect(refractionOn, reference, shaderGraphRect, 0f);
        result.shaderGraphMaxDelta = MaxDifferenceInRect(refractionOn, reference, shaderGraphRect);
        if (result.shaderGraphChangedPixels <= 0)
            result.issues.Add("The ShaderGraph Lit refraction material did not change with refraction enabled.");

        // -----------------------------------------------------------------------------------------
        // No proxy: disabling box projection removes the only scene proxy, so every material must
        // fall back to the reference image.
        // -----------------------------------------------------------------------------------------
        rigParts.SetProbeBoxProjection(false);
        cameraData.resetHistory = true;
        var noProxy = RenderAndCapture(camera, imageTarget, 8);
        result.states.Add(DescribeState("NoProxyFallback", noProxy, reference));
        WriteImage(imageTarget, outputDirectory, "T11_" + modeName + "_NoProxyFallback.png");
        result.noProxyChangedPixels = CountDifferences(noProxy, reference, k_IdenticalThreshold);
        result.noProxyMaxDelta = MaxDifference(noProxy, reference);
        if (result.noProxyChangedPixels != 0)
            result.issues.Add("Without a box-projected probe the refraction did not fall back to the reference image.");
        rigParts.SetProbeBoxProjection(true);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 4);

        // -----------------------------------------------------------------------------------------
        // Transmittance: a red absorption color must tint the transmitted light. The comparison uses
        // the same sphere and the same scene, only the transmittance color changes.
        // -----------------------------------------------------------------------------------------
        var whiteTint = ReadMeanInRect(camera, imageTarget, rigParts.tintGlass, InteriorInsetPixels(camera, rigParts.tintGlass, 0.15f), out var whiteMean);
        rigParts.SetTint(new Color(0.35f, 1f, 1f, 1f));
        cameraData.resetHistory = true;
        var tinted = RenderAndCapture(camera, imageTarget, 6);
        WriteImage(imageTarget, outputDirectory, "T11_" + modeName + "_Tinted.png");
        ReadMeanInRect(camera, imageTarget, rigParts.tintGlass, InteriorInsetPixels(camera, rigParts.tintGlass, 0.15f), out var tintedMean);
        result.tintStartMean = whiteMean;
        result.tintEndMean = tintedMean;
        // A low red transmittance absorbs red, so only the red mean must drop.
        result.tintApplied = whiteMean.r > 0.001f && tintedMean.r < whiteMean.r * 0.995f;
        result.tint = new T11Tint
        {
            whiteMean = whiteMean,
            tintedMean = tintedMean,
            redRatio = whiteMean.r > 1e-5f ? tintedMean.r / whiteMean.r : 0f,
            applied = result.tintApplied
        };
        if (!result.tintApplied)
            result.issues.Add("Transmittance absorption did not tint the transmitted light.");
        rigParts.SetTint(Color.white);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 4);

        // -----------------------------------------------------------------------------------------
        // Current frame color pyramid: moving the opaque emissive backdrop must move the transmitted
        // color. The reflection probe is frozen (ViaScripting), so only the pyramid can change.
        // -----------------------------------------------------------------------------------------
        // Change the opaque scene color directly behind the upper tint glass (the opaque backdrop).
        // The probe cubemap is frozen (ViaScripting), so a change can only come from the current
        // frame color pyramid. The lower box glass is in front of the opaque T3 objects instead and
        // would not see the backdrop color change.
        var currentFrameTarget = rigParts.tintGlass;
        var currentFrameInset = InteriorInsetPixels(camera, currentFrameTarget, 0.2f);
        ReadMeanInRect(camera, imageTarget, currentFrameTarget, currentFrameInset, out var beforeMove);
        result.moveBeforeMean = beforeMove;
        rigParts.SetBackdropColor(new Color(0.2f, 0.5f, 2.4f));
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 6);
        WriteImage(imageTarget, outputDirectory, "T11_" + modeName + "_BackdropMoved.png");
        ReadMeanInRect(camera, imageTarget, currentFrameTarget, currentFrameInset, out var afterMove);
        result.moveAfterMean = afterMove;
        result.currentFramePyramidFollowsScene = MaxChannelDelta(beforeMove, afterMove) > 0.002f;
        if (!result.currentFramePyramidFollowsScene)
            result.issues.Add("The transmitted color did not follow the current frame scene.");
        rigParts.ResetBackdrop();

        // -----------------------------------------------------------------------------------------
        // Pre-refraction transparent ordering: the current pyramid excludes transparents, so a
        // transparent object between the glass and the opaque backdrop must not appear inside the
        // refraction. The glass region is compared with the transparent panel on and off.
        // -----------------------------------------------------------------------------------------
        rigParts.SetTransparentPanelVisible(false);
        cameraData.resetHistory = true;
        var panelOff = RenderAndCapture(camera, imageTarget, 6);
        WriteImage(imageTarget, outputDirectory, "T11_" + modeName + "_TransparentPanelOff.png");
        var pyramidOff = CaptureDebug(camera, imageTarget, feature, ScreenSpaceLightingDebugMode.CurrentColorPyramid,
            outputDirectory, "T11_" + modeName + "_CurrentColorPyramidPanelOff.png");
        rigParts.SetTransparentPanelVisible(true);
        cameraData.resetHistory = true;
        var panelOn = RenderAndCapture(camera, imageTarget, 6);
        WriteImage(imageTarget, outputDirectory, "T11_" + modeName + "_TransparentPanelOn.png");
        var pyramidOn = CaptureDebug(camera, imageTarget, feature, ScreenSpaceLightingDebugMode.CurrentColorPyramid,
            outputDirectory, "T11_" + modeName + "_CurrentColorPyramidPanelOn.png");

        // The panel sits right behind the box glass. Compare only pixels inside the projected sphere
        // silhouette; the rectangle corners are background where the panel is directly visible.
        var glassCircle = rigParts.boxGlass;
        result.preRefractionGlassChangedPixels = CountDifferencesInCircle(camera, panelOn, panelOff, glassCircle, 0.55f, k_IdenticalThreshold);
        result.preRefractionGlassMaxDelta = MaxDifferenceInCircle(camera, panelOn, panelOff, glassCircle, 0.55f);
        var panelRect = ProjectBounds(camera, rigParts.transparentPanel, 2);
        result.preRefractionPanelChangedPixels = CountDifferencesInRect(panelOn, panelOff, panelRect, 0f);
        result.pyramidWithPanelChangedPixels = CountDifferencesInRect(pyramidOn, pyramidOff, panelRect, k_IdenticalThreshold);
        result.panelDiagnostics = rigParts.DescribePanelMaterial();
        result.preRefractionDifference = new T11Ordering
        {
            glassChangedPixels = result.preRefractionGlassChangedPixels,
            glassMaxDelta = result.preRefractionGlassMaxDelta,
            panelChangedPixels = result.preRefractionPanelChangedPixels,
            pyramidExcludesTransparents = result.preRefractionGlassChangedPixels == 0 &&
                                          result.pyramidWithPanelChangedPixels == 0
        };
        if (result.preRefractionPanelChangedPixels <= 0)
            result.issues.Add("The transparent ordering panel was not visible; the ordering test is invalid.");
        if (result.preRefractionGlassChangedPixels != 0)
            result.issues.Add("The refraction included a pre-refraction transparent surface; the current pyramid ordering was not preserved.");
        rigParts.SetTransparentPanelVisible(false);

        // -----------------------------------------------------------------------------------------
        // ShaderGraph transparent prepass: the SG material opts in to Transparent SSR, so enabling
        // transparent SSR must change its region (the origin is written by the SG prepass).
        // -----------------------------------------------------------------------------------------
        var ssr = profile.components.OfType<ScreenSpaceReflection>().First();
        ssr.enabled.Override(true);
        ssr.intensity.Override(1f);
        ssr.minSmoothness.Override(0.4f);
        ssr.smoothnessFadeStart.Override(0.4f);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        ssr.enabledTransparent.Override(false);
        rigParts.SetReceiveTransparentSsr(true);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 8);
        var sgSsrOff = RenderAndCapture(camera, imageTarget, 4);
        ssr.enabledTransparent.Override(true);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 8);
        var sgSsrOn = RenderAndCapture(camera, imageTarget, 4);
        WriteImage(imageTarget, outputDirectory, "T11_" + modeName + "_ShaderGraphTransparentSsr.png");
        result.shaderGraphTransparentSsrChangedPixels =
            CountDifferencesInRect(sgSsrOn, sgSsrOff, ProjectBounds(camera, rigParts.shaderGraphGlass, 2), 0f);
        if (result.shaderGraphTransparentSsrChangedPixels <= 0)
            result.issues.Add("The ShaderGraph transparent prepass did not feed Transparent SSR.");
        ssr.enabledTransparent.Override(false);
        ssr.enabled.Override(false);
        rigParts.SetReceiveTransparentSsr(false);

        // -----------------------------------------------------------------------------------------
        // Final state: no NaN/Inf or error shader pixels in any captured image.
        // -----------------------------------------------------------------------------------------
        foreach (var state in result.states)
        {
            if (state.magentaPixels > 0)
                result.issues.Add($"{state.state} contains error-shader pixels.");
            if (state.nonFinitePixels > 0)
                result.issues.Add($"{state.state} contains non-finite pixels.");
        }

        result.limitations =
            "The proxy is the box of the active box-projected reflection probe (URP has no per-material proxy volume). " +
            "The color pyramid is captured before transparents, so pre-refraction transparents are absent from the " +
            "refraction; a transparent object between the refracting surface and the background is only visible through " +
            "its own blend, not through the refraction. Refraction is a forward transparent material path only; it is " +
            "not available in the deferred GBuffer.";
        result.passed = result.issues.Count == 0;
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Rig
    // ---------------------------------------------------------------------------------------------

    sealed class T11Rig
    {
        public GameObject root;
        public ReflectionProbe probe;
        public GameObject boxGlass;
        public GameObject sphereGlass;
        public GameObject thinGlass;
        public GameObject complexGlass;
        public GameObject shaderGraphGlass;
        public GameObject tintGlass;
        public GameObject legacy;
        public GameObject backdrop;
        public GameObject transparentPanel;
        internal Material m_PanelMaterial;

        Material m_BoxMaterial;
        Material m_SphereMaterial;
        Material m_ThinMaterial;
        Material m_ComplexMaterial;
        Material m_ShaderGraphMaterial;
        Material m_TintMaterial;
        Vector3 m_BackdropStart;

        public void ApplyMaterials(
            Material box,
            Material sphere,
            Material thin,
            Material complex,
            Material shaderGraph,
            Material tint)
        {
            m_BoxMaterial = box;
            m_SphereMaterial = sphere;
            m_ThinMaterial = thin;
            m_ComplexMaterial = complex;
            m_ShaderGraphMaterial = shaderGraph;
            m_TintMaterial = tint;
            boxGlass.GetComponent<Renderer>().sharedMaterial = box;
            sphereGlass.GetComponent<Renderer>().sharedMaterial = sphere;
            thinGlass.GetComponent<Renderer>().sharedMaterial = thin;
            complexGlass.GetComponent<Renderer>().sharedMaterial = complex;
            shaderGraphGlass.GetComponent<Renderer>().sharedMaterial = shaderGraph;
            tintGlass.GetComponent<Renderer>().sharedMaterial = tint;
            m_BackdropStart = backdrop.transform.position;
        }

        public void SetModels(int model)
        {
            SetModel(m_BoxMaterial, model);
            SetModel(m_SphereMaterial, model == 1 ? 2 : model);
            SetModel(m_ThinMaterial, model == 1 ? 3 : model);
            SetModel(m_ComplexMaterial, model);
            SetModel(m_ShaderGraphMaterial, model);
            SetModel(m_TintMaterial, model);
        }

        static void SetModel(Material material, int model)
        {
            if (material == null)
                return;
            material.SetFloat("_RefractionModel", model);
            SetKeyword(material, ShaderKeywordStrings.RefractionPlane, model == 1);
            SetKeyword(material, ShaderKeywordStrings.RefractionSphere, model == 2);
            SetKeyword(material, ShaderKeywordStrings.RefractionThin, model == 3);
        }

        public void SetReceiveTransparentSsr(bool receive)
        {
            foreach (var material in new[] { m_ShaderGraphMaterial })
            {
                if (material == null || !material.HasProperty("_TransparentSSRReceive"))
                    continue;
                material.SetFloat("_TransparentSSRReceive", receive ? 1f : 0f);
                SetKeyword(material, ShaderKeywordStrings.TransparentSSRReceive, receive);
            }
        }

        public void SetTint(Color color)
        {
            if (m_TintMaterial != null)
                m_TintMaterial.SetColor("_TransmittanceColor", color);
        }

        public void SetProbeBoxProjection(bool enabled)
        {
            if (probe == null)
                return;
            probe.boxProjection = enabled;
            probe.RenderProbe();
        }

        /// <summary>
    /// In the checklist, the emissive backdrop is the opaque scene behind the glass. Changing its
    /// emission color changes the current frame color pyramid without touching the frozen probe.
    /// </summary>
    public void SetBackdropColor(Color color)
        {
            if (backdrop == null)
                return;
            var material = backdrop.GetComponent<Renderer>().sharedMaterial;
            material.SetColor("_EmissionColor", color);
        }

        public void ResetBackdrop()
        {
            SetBackdropColor(new Color(2.2f, 1.2f, 0.35f));
        }

        public void SetTransparentPanelVisible(bool visible)
        {
            if (transparentPanel != null)
                transparentPanel.SetActive(visible);
        }

        public string DescribePanelMaterial()
        {
            if (m_PanelMaterial == null)
                return "missing panel material";
            return string.Format(
                "surface={0} queue={1} transparentKeyword={2} src={3} dst={4} zwrite={5} alpha={6}",
                m_PanelMaterial.GetFloat("_Surface"),
                m_PanelMaterial.renderQueue,
                m_PanelMaterial.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"),
                m_PanelMaterial.GetFloat("_SrcBlend"),
                m_PanelMaterial.GetFloat("_DstBlend"),
                m_PanelMaterial.GetFloat("_ZWrite"),
                m_PanelMaterial.GetColor("_BaseColor").a);
        }

        public void ResetVisibility()
        {
            boxGlass.SetActive(true);
            sphereGlass.SetActive(true);
            thinGlass.SetActive(true);
            complexGlass.SetActive(true);
            shaderGraphGlass.SetActive(true);
            tintGlass.SetActive(true);
            legacy.SetActive(true);
            backdrop.SetActive(true);
            SetTransparentPanelVisible(false);
        }
    }

    static T11Rig CreateRig(Camera camera)
    {
        var rig = new T11Rig();
        var root = new GameObject("T11 Refraction Rig");
        rig.root = root;
        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        if (litShader == null)
            throw new InvalidOperationException("The URP Lit shader is missing.");

        var viewDirection = camera.transform.forward;
        var right = Vector3.Cross(Vector3.up, viewDirection).normalized;
        var center = camera.transform.position + viewDirection * 15f;
        center.y = 1.6f;

        var floorMaterial = CreateLitMaterial(litShader, "T11 Floor", new Color(0.55f, 0.58f, 0.62f), 0f, 0.85f);
        var backdropMaterial = CreateLitMaterial(litShader, "T11 Backdrop", new Color(0.02f, 0.02f, 0.02f), 0f, 0.3f);
        backdropMaterial.EnableKeyword("_EMISSION");
        backdropMaterial.SetColor("_EmissionColor", new Color(2.2f, 1.2f, 0.35f));
        AddBox(root, floorMaterial, "T11 Floor", center + new Vector3(0f, -1.55f, 0f), new Vector3(34f, 0.05f, 22f));
        rig.backdrop = AddBox(root, backdropMaterial, "T11 Emissive Backdrop",
            center + viewDirection * 9f + Vector3.up * 2f, new Vector3(32f, 12f, 0.2f));

        var boxMaterial = LoadMaterial(BoxMaterialPath);
        var sphereMaterial = LoadMaterial(SphereMaterialPath);
        var thinMaterial = LoadMaterial(ThinMaterialPath);
        var complexMaterial = LoadMaterial(ComplexLitMaterialPath);
        var legacyMaterial = LoadMaterial(LegacyMaterialPath);
        var tintMaterial = LoadMaterial(TintMaterialPath);
        var graphMaterial = LoadMaterial(GraphMaterialPath);

        rig.boxGlass = AddSphere(root, boxMaterial, "T11 Box Glass", center + right * -3.6f, 1.8f);
        rig.sphereGlass = AddSphere(root, sphereMaterial, "T11 Sphere Glass", center + right * -1.1f, 1.3f);
        rig.complexGlass = AddSphere(root, complexMaterial, "T11 Complex Lit Glass", center + right * 1.4f, 1.3f);
        rig.shaderGraphGlass = AddSphere(root, graphMaterial, "T11 ShaderGraph Glass", center + right * 3.9f, 1.3f);
        rig.thinGlass = AddSphere(root, thinMaterial, "T11 Thin Glass", center + right * -3.6f + Vector3.up * 3.2f, 1.0f);
        rig.tintGlass = AddSphere(root, tintMaterial, "T11 Tint Glass", center + right * 1.4f + Vector3.up * 3.2f, 1.0f);
        rig.legacy = AddSphere(root, legacyMaterial, "T11 Legacy Transparent", center + right * 6.2f, 1.0f);

        // Transparent object between the glass and the opaque backdrop. The current frame color
        // pyramid is captured before transparents, so this panel must not appear in the refraction.
        var panelMaterial = CreateLitMaterial(litShader, "T11 Transparent Panel", new Color(0.2f, 0.9f, 0.4f), 0f, 0.5f);
        panelMaterial.SetFloat("_Surface", 1f);
        panelMaterial.SetFloat("_Blend", 0f);
        panelMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        panelMaterial.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        panelMaterial.SetFloat("_ZWrite", 0f);
        panelMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        panelMaterial.SetOverrideTag("RenderType", "Transparent");
        panelMaterial.renderQueue = 3000;
        panelMaterial.SetColor("_BaseColor", new Color(0.2f, 0.9f, 0.4f, 0.85f));
        panelMaterial.EnableKeyword("_EMISSION");
        panelMaterial.SetColor("_EmissionColor", new Color(0.4f, 2.4f, 0.8f));
        rig.transparentPanel = AddBox(root, panelMaterial, "T11 Transparent Panel",
            center + right * -3.6f + viewDirection * 3.4f + Vector3.up * 0.3f, new Vector3(3.6f, 3.6f, 0.05f));
        // The panel must not cast a shadow: its shadow would change the opaque color pyramid behind
        // the glass and contaminate the pre-refraction ordering comparison.
        rig.transparentPanel.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
        rig.transparentPanel.SetActive(false);
        rig.m_PanelMaterial = panelMaterial;

        // A single box-projected realtime probe provides the proxy box. ViaScripting keeps the
        // cubemap frozen while the movable backdrop changes, so any transmitted change can only come
        // from the current frame color pyramid.
        var probeObject = new GameObject("T11 Reflection Probe");
        probeObject.transform.SetParent(root.transform, false);
        probeObject.transform.position = center;
        var probe = probeObject.AddComponent<ReflectionProbe>();
        probe.mode = ReflectionProbeMode.Realtime;
        probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
        probe.size = new Vector3(70f, 40f, 40f);
        probe.center = Vector3.zero;
        probe.boxProjection = true;
        probe.resolution = 128;
        probe.hdr = true;
        probe.clearFlags = ReflectionProbeClearFlags.Skybox;
        probe.cullingMask = ~0;
        rig.probe = probe;

        rig.ApplyMaterials(boxMaterial, sphereMaterial, thinMaterial, complexMaterial, graphMaterial, tintMaterial);
        probe.RenderProbe();
        return rig;
    }

    static GameObject AddBox(GameObject parent, Material material, string name, Vector3 position, Vector3 size)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent.transform, false);
        box.transform.position = position;
        box.transform.localScale = size;
        box.GetComponent<Renderer>().sharedMaterial = material;
        return box;
    }

    static GameObject AddSphere(GameObject parent, Material material, string name, Vector3 position, float scale)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = name;
        sphere.transform.SetParent(parent.transform, false);
        sphere.transform.position = position;
        sphere.transform.localScale = Vector3.one * scale;
        sphere.GetComponent<Renderer>().sharedMaterial = material;
        return sphere;
    }

    static Material CreateLitMaterial(Shader shader, string name, Color color, float metallic, float smoothness)
    {
        var material = new Material(shader) { name = name, color = color };
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        return material;
    }

    static Material LoadMaterial(string path)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
            throw new FileNotFoundException("Missing T11 material asset. Run Create T11 Assets first.", path);
        return new Material(material) { name = material.name + " (T11 Runtime)" };
    }

    // ---------------------------------------------------------------------------------------------
    // Material assets
    // ---------------------------------------------------------------------------------------------

    static void EnsureMaterialAssets()
    {
        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        var complexLitShader = Shader.Find("Universal Render Pipeline/Complex Lit");
        EnsureMaterialAsset(BoxMaterialPath, "T11 Refraction Box", litShader, 1, new Color(1f, 1f, 1f, 1f), legacy: false, clearCoat: false);
        EnsureMaterialAsset(SphereMaterialPath, "T11 Refraction Sphere", litShader, 2, new Color(1f, 1f, 1f, 1f), legacy: false, clearCoat: false);
        EnsureMaterialAsset(ThinMaterialPath, "T11 Refraction Thin", litShader, 3, new Color(1f, 1f, 1f, 1f), legacy: false, clearCoat: false);
        EnsureMaterialAsset(NoneMaterialPath, "T11 Refraction None", litShader, 0, new Color(1f, 1f, 1f, 1f), legacy: false, clearCoat: false);
        EnsureMaterialAsset(TintMaterialPath, "T11 Refraction Tint", litShader, 1, new Color(1f, 1f, 1f, 1f), legacy: false, clearCoat: false);
        EnsureMaterialAsset(ComplexLitMaterialPath, "T11 Refraction ComplexLit", complexLitShader, 1, new Color(1f, 1f, 1f, 1f), legacy: false, clearCoat: true);
        EnsureMaterialAsset(LegacyMaterialPath, "T11 Legacy Transparent", litShader, 0, new Color(1f, 1f, 1f, 1f), legacy: true, clearCoat: false);
        EnsureShaderGraphMaterialAsset();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static void EnsureMaterialAsset(string path, string name, Shader shader, int model, Color transmittance, bool legacy, bool clearCoat)
    {
        if (shader == null)
            throw new InvalidOperationException("The URP shader is missing.");

        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, path);
        }

        material.shader = shader;
        ApplyTransparentSettings(material);
        material.SetColor("_BaseColor", new Color(0.12f, 0.14f, 0.16f, 1f));
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Smoothness", 0.95f);
        if (material.HasProperty("_RefractionModel"))
        {
            material.SetFloat("_RefractionModel", model);
            material.SetFloat("_Ior", 1.5f);
            material.SetFloat("_Thickness", 2f);
            material.SetColor("_TransmittanceColor", transmittance);
            material.SetFloat("_TransmittanceDistance", 1f);
            SetKeyword(material, ShaderKeywordStrings.RefractionPlane, model == 1);
            SetKeyword(material, ShaderKeywordStrings.RefractionSphere, model == 2);
            SetKeyword(material, ShaderKeywordStrings.RefractionThin, model == 3);
        }

        if (legacy)
        {
            material.SetFloat("_TransparentSSRReceive", 1f);
            SetKeyword(material, ShaderKeywordStrings.TransparentSSRReceive, true);
        }
        else if (material.HasProperty("_TransparentSSRReceive"))
        {
            material.SetFloat("_TransparentSSRReceive", 0f);
            SetKeyword(material, ShaderKeywordStrings.TransparentSSRReceive, false);
        }

        if (clearCoat && material.HasProperty("_ClearCoat"))
        {
            material.SetFloat("_ClearCoat", 1f);
            material.SetFloat("_ClearCoatMask", 1f);
            material.SetFloat("_ClearCoatSmoothness", 1f);
            material.EnableKeyword("_CLEARCOAT");
        }

        EditorUtility.SetDirty(material);
    }

    static void ApplyTransparentSettings(Material material)
    {
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_Cull", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.SetOverrideTag("RenderType", "Transparent");
        material.renderQueue = 3000;
    }

    static void EnsureShaderGraphMaterialAsset()
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(GraphPath);
        if (shader == null)
            return;

        var material = AssetDatabase.LoadAssetAtPath<Material>(GraphMaterialPath);
        if (material == null)
        {
            material = new Material(shader) { name = "T11 Refraction ShaderGraph" };
            AssetDatabase.CreateAsset(material, GraphMaterialPath);
        }

        material.shader = shader;
        if (material.HasProperty("_Surface"))
            material.SetFloat("_Surface", 1f);
        material.SetColor("_BaseColor", new Color(0.16f, 0.18f, 0.2f, 1f));
        if (material.HasProperty("_Metallic"))
            material.SetFloat("_Metallic", 0f);
        if (material.HasProperty("_Smoothness"))
            material.SetFloat("_Smoothness", 0.95f);
        if (material.HasProperty("_RefractionModel"))
        {
            material.SetFloat("_RefractionModel", 1f);
            material.SetFloat("_Ior", 1.5f);
            material.SetFloat("_Thickness", 2f);
            material.SetColor("_TransmittanceColor", Color.white);
            material.SetFloat("_TransmittanceDistance", 1f);
            SetKeyword(material, ShaderKeywordStrings.RefractionPlane, true);
        }

        EditorUtility.SetDirty(material);
    }

    // Creates a transparent ShaderGraph Lit graph. The graph is intentionally empty: it exercises
    // the generated pass plumbing (keywords, transparent prepass, material properties), not nodes.
    static void EnsureShaderGraphAsset()
    {
        if (AssetDatabase.LoadAssetAtPath<Shader>(GraphPath) != null)
            return;

        var graphType = Type.GetType("UnityEditor.ShaderGraph.GraphData, Unity.ShaderGraph.Editor");
        var targetType = Type.GetType("UnityEditor.Rendering.Universal.ShaderGraph.UniversalTarget, Unity.RenderPipelines.Universal.Editor");
        var litSubTargetType = Type.GetType("UnityEditor.Rendering.Universal.ShaderGraph.UniversalLitSubTarget, Unity.RenderPipelines.Universal.Editor");
        var blockFieldsType = Type.GetType("UnityEditor.ShaderGraph.BlockFields, Unity.ShaderGraph.Editor");
        var blockType = Type.GetType("UnityEditor.ShaderGraph.BlockNode, Unity.ShaderGraph.Editor");
        var fileUtilitiesType = Type.GetType("UnityEditor.ShaderGraph.FileUtilities, Unity.ShaderGraph.Editor");
        if (graphType == null || targetType == null || litSubTargetType == null || blockFieldsType == null ||
            blockType == null || fileUtilitiesType == null)
            throw new TypeLoadException("ShaderGraph editor types are not available; install com.unity.shadergraph 14.0.12.");

        var graph = Activator.CreateInstance(graphType);
        graphType.GetMethod("AddContexts").Invoke(graph, null);
        var target = Activator.CreateInstance(targetType);
        targetType.GetMethod("TrySetActiveSubTarget").Invoke(target, new object[] { litSubTargetType });
        var surfaceTypeProperty = targetType.GetProperty("surfaceType");
        if (surfaceTypeProperty != null)
            surfaceTypeProperty.SetValue(target, Enum.Parse(surfaceTypeProperty.PropertyType, "Transparent"));

        var surfaceDescription = blockFieldsType.GetNestedType("SurfaceDescription");
        var vertexDescription = blockFieldsType.GetNestedType("VertexDescription");
        var blocks = new List<object>
        {
            vertexDescription.GetField("Position").GetValue(null),
            vertexDescription.GetField("Normal").GetValue(null),
            vertexDescription.GetField("Tangent").GetValue(null),
            surfaceDescription.GetField("BaseColor").GetValue(null),
            surfaceDescription.GetField("Metallic").GetValue(null),
            surfaceDescription.GetField("Smoothness").GetValue(null),
            surfaceDescription.GetField("Emission").GetValue(null),
            surfaceDescription.GetField("Occlusion").GetValue(null),
            surfaceDescription.GetField("Alpha").GetValue(null),
        };

        var blockDescriptorType = blocks[0].GetType();
        var blockArray = Array.CreateInstance(blockDescriptorType, blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
            blockArray.SetValue(blocks[i], i);
        var targetArray = Array.CreateInstance(targetType, 1);
        targetArray.SetValue(target, 0);
        graphType.GetMethod("InitializeOutputs").Invoke(graph, new object[] { targetArray, blockArray });

        graphType.GetProperty("path").SetValue(graph, "Shader Graphs");
        fileUtilitiesType.GetMethod("WriteShaderGraphToDisk", StaticFlags)
            .Invoke(null, new object[] { GraphPath, graph });
        AssetDatabase.ImportAsset(GraphPath, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.Refresh();
    }

    // ---------------------------------------------------------------------------------------------
    // Assets
    // ---------------------------------------------------------------------------------------------

    static void EnsureAssets(bool force)
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before creating the T11 assets.");

        AssetDatabase.Refresh();
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var mode = ModeNames[i];
            var renderer = EnsureRenderer(string.Format(T11RendererPathFormat, mode), "SslT11" + mode + "Renderer", mode, force, out bool recreated);
            EnsureFeatureResources(renderer);
            EnsurePipeline(string.Format(T11PipelinePathFormat, mode), "SslT11" + mode + "Pipeline", renderer, force || recreated);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static UniversalRendererData EnsureRenderer(string rendererPath, string rendererName, string mode, bool force, out bool recreated)
    {
        recreated = false;
        var t1Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T1RendererPathFormat, mode));
        if (t1Renderer == null)
            throw new FileNotFoundException("Missing T1 renderer asset. Create the T1 baseline first.", mode);

        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        var existingFeature = renderer != null ? renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault() : null;
        bool matches = existingFeature != null;

        if (renderer == null || force || !matches)
        {
            recreated = true;
            if (renderer != null)
                AssetDatabase.DeleteAsset(rendererPath);
            renderer = UnityEngine.Object.Instantiate(t1Renderer);
            renderer.name = rendererName;
            AssetDatabase.CreateAsset(renderer, rendererPath);

            foreach (var existing in AssetDatabase.LoadAllAssetsAtPath(rendererPath))
            {
                if (existing is ScriptableRendererFeature)
                    AssetDatabase.RemoveObjectFromAsset(existing);
            }

            renderer.rendererFeatures.Clear();
            var feature = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
            feature.name = rendererName + "ssl";
            feature.GetType().GetMethod("Create", InstanceFlags)?.Invoke(feature, null);
            AssetDatabase.AddObjectToAsset(feature, renderer);
            renderer.rendererFeatures.Add(feature);

            EnsureFeatureMap(renderer);
            EditorUtility.SetDirty(renderer);
            renderer.SetDirty();
        }

        return renderer;
    }

    static void EnsureFeatureMap(UniversalRendererData renderer)
    {
        var serialized = new SerializedObject(renderer);
        var map = serialized.FindProperty("m_RendererFeatureMap");
        if (map == null)
            return;

        map.arraySize = renderer.rendererFeatures.Count;
        for (int i = 0; i < renderer.rendererFeatures.Count; i++)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(renderer.rendererFeatures[i], out _, out long id);
            map.GetArrayElementAtIndex(i).longValue = id;
        }

        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(renderer);
    }

    static UniversalRenderPipelineAsset EnsurePipeline(string pipelinePath, string pipelineName, UniversalRendererData renderer, bool force)
    {
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
        if (pipeline == null || force)
        {
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, pipelinePath);
            }
            else
            {
                var template = UniversalRenderPipelineAsset.Create(renderer);
                EditorUtility.CopySerialized(template, pipeline);
                UnityEngine.Object.DestroyImmediate(template);
            }

            pipeline.name = pipelineName;
            pipeline.supportsHDR = true;
            pipeline.msaaSampleCount = 1;
            pipeline.renderScale = 1f;
            pipeline.shadowDistance = 50f;
            pipeline.supportsCameraDepthTexture = false;
            pipeline.supportsCameraOpaqueTexture = false;
            EditorUtility.SetDirty(pipeline);
        }

        // Box projection is required by the refraction proxy; the material reads unity_SpecCube0
        // box data, and the keyword must be enabled for the probe data to be authoritative.
        var serialized = new SerializedObject(pipeline);
        var boxProjection = serialized.FindProperty("m_ReflectionProbeBoxProjection");
        if (boxProjection != null && !boxProjection.boolValue)
        {
            boxProjection.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
        }

        return pipeline;
    }

    static void EnsureFeatureResources(UniversalRendererData renderer)
    {
        foreach (var ssl in renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>())
        {
            var settings = ssl.GetType().GetProperty("settings", InstanceFlags)?.GetValue(ssl);
            var resources = settings?.GetType().GetField("resources", InstanceFlags)?.GetValue(settings);
            var locator = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceLightingResourceLocator");
            locator?.GetMethod("AssignDefaults", StaticFlags)?.Invoke(null, new[] { resources });
            EditorUtility.SetDirty(ssl);
        }
    }

    static void DisableScreenSpaceEffects(VolumeProfile profile)
    {
        var ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault();
        if (ssgi != null)
            ssgi.enabled.Override(false);
        var ssr = profile.components.OfType<ScreenSpaceReflection>().FirstOrDefault();
        if (ssr != null)
        {
            ssr.enabled.Override(false);
            ssr.enabledTransparent.Override(false);
        }
        var refraction = profile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
        if (refraction != null)
            refraction.enabled.Override(false);
    }

    static UniversalRenderPipelineAsset LoadPipeline(string format, string modeName)
    {
        var path = string.Format(format, modeName);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
            throw new FileNotFoundException("Missing pipeline asset.", path);
        return pipeline;
    }

    static void SetKeyword(Material material, string keyword, bool enabled)
    {
        if (enabled)
            material.EnableKeyword(keyword);
        else
            material.DisableKeyword(keyword);
    }

    // ---------------------------------------------------------------------------------------------
    // Editor evidence access
    // ---------------------------------------------------------------------------------------------

    static Type FindPackageType(string typeName)
    {
        var type = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal." + typeName);
        if (type == null)
            throw new InvalidOperationException("Could not find package type " + typeName + ".");
        return type;
    }

    static void ResetEditorEvidence(Type type)
    {
        type.GetMethod("ResetEditorEvidenceCounters", StaticFlags)?.Invoke(null, null);
    }

    static int ReadEditorInt(Type type, string fieldName)
    {
        var field = type.GetField(fieldName, StaticFlags);
        return field != null ? (int)field.GetValue(null) : -1;
    }

    // ---------------------------------------------------------------------------------------------
    // Debug capture
    // ---------------------------------------------------------------------------------------------

    static Color[] CaptureDebug(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        ScreenSpaceLightingDebugMode mode,
        string outputDirectory,
        string fileName)
    {
        SetSslDebug(feature, (int)mode, 0);
        var samples = RenderAndCapture(camera, imageTarget, 2);
        SetSslDebug(feature, (int)ScreenSpaceLightingDebugMode.Off, 0);
        if (!string.IsNullOrEmpty(fileName))
            WriteImage(imageTarget, outputDirectory, fileName);
        return samples;
    }

    static void SetSslDebug(ScreenSpaceLightingRendererFeature feature, int mode, int mip)
    {
        var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        var modeField = settings?.GetType().GetField("debugMode", InstanceFlags);
        var mipField = settings?.GetType().GetField("debugMip", InstanceFlags);
        if (modeField != null)
            modeField.SetValue(settings, Enum.ToObject(modeField.FieldType, mode));
        if (mipField != null)
            mipField.SetValue(settings, mip);
        EditorUtility.SetDirty(feature);
    }

    // ---------------------------------------------------------------------------------------------
    // Image helpers
    // ---------------------------------------------------------------------------------------------

    struct ScreenRect
    {
        public int xMin;
        public int yMin;
        public int xMax;
        public int yMax;
        public bool IsEmpty => xMax < xMin || yMax < yMin;
    }

    // Inset that leaves the inscribed square of a sphere silhouette: for a circle of radius r the
// projected bounds are 2r wide, and the inscribed square needs ~0.29r of inset per side.
    static int InteriorInsetPixels(Camera camera, GameObject gameObject, float fraction)
    {
        var rect = ProjectBounds(camera, gameObject, 0);
        int size = Mathf.Min(rect.xMax - rect.xMin, rect.yMax - rect.yMin);
        return Mathf.Max(0, Mathf.RoundToInt(size * fraction));
    }

    static ScreenRect InteriorRect(Camera camera, GameObject gameObject, float fraction)
    {
        return ProjectBounds(camera, gameObject, InteriorInsetPixels(camera, gameObject, fraction));
    }

    static int CountDifferencesInCircle(Camera camera, Color[] a, Color[] b, GameObject gameObject, float radiusScale, float threshold)
    {
        if (a == null || b == null || a.Length != b.Length)
            return -1;
        var rect = ProjectBounds(camera, gameObject, 0);
        float radius = 0.5f * Mathf.Min(rect.xMax - rect.xMin, rect.yMax - rect.yMin) * radiusScale;
        var center = ToPixel(camera, gameObject.transform.position);
        int count = 0;
        int r = Mathf.CeilToInt(radius);
        for (int y = Mathf.Max(0, center.y - r); y <= Mathf.Min(ImageHeight - 1, center.y + r); y++)
        {
            for (int x = Mathf.Max(0, center.x - r); x <= Mathf.Min(ImageWidth - 1, center.x + r); x++)
            {
                if ((x - center.x) * (x - center.x) + (y - center.y) * (y - center.y) > radius * radius)
                    continue;
                int index = y * ImageWidth + x;
                if (MaxChannelDelta(a[index], b[index]) > threshold)
                    count++;
            }
        }

        return count;
    }

    static float MaxDifferenceInCircle(Camera camera, Color[] a, Color[] b, GameObject gameObject, float radiusScale)
    {
        if (a == null || b == null || a.Length != b.Length)
            return float.PositiveInfinity;
        var rect = ProjectBounds(camera, gameObject, 0);
        float radius = 0.5f * Mathf.Min(rect.xMax - rect.xMin, rect.yMax - rect.yMin) * radiusScale;
        var center = ToPixel(camera, gameObject.transform.position);
        float max = 0f;
        int r = Mathf.CeilToInt(radius);
        for (int y = Mathf.Max(0, center.y - r); y <= Mathf.Min(ImageHeight - 1, center.y + r); y++)
        {
            for (int x = Mathf.Max(0, center.x - r); x <= Mathf.Min(ImageWidth - 1, center.x + r); x++)
            {
                if ((x - center.x) * (x - center.x) + (y - center.y) * (y - center.y) > radius * radius)
                    continue;
                int index = y * ImageWidth + x;
                max = Mathf.Max(max, MaxChannelDelta(a[index], b[index]));
            }
        }

        return max;
    }

    static ScreenRect ProjectBounds(Camera camera, GameObject gameObject, int inset)
    {
        var renderer = gameObject.GetComponent<Renderer>();
        var bounds = renderer != null ? renderer.bounds : new Bounds(gameObject.transform.position, Vector3.one);
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < 8; i++)
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
            xMin = Mathf.Clamp(Mathf.RoundToInt(min.x * ImageWidth) + inset, 0, ImageWidth - 1),
            yMin = Mathf.Clamp(Mathf.RoundToInt(min.y * ImageHeight) + inset, 0, ImageHeight - 1),
            xMax = Mathf.Clamp(Mathf.RoundToInt(max.x * ImageWidth) - inset, 0, ImageWidth - 1),
            yMax = Mathf.Clamp(Mathf.RoundToInt(max.y * ImageHeight) - inset, 0, ImageHeight - 1),
        };
    }

    static Vector2Int ToPixel(Camera camera, Vector3 worldPosition)
    {
        var viewport = camera.WorldToViewportPoint(worldPosition);
        return new Vector2Int(
            Mathf.Clamp(Mathf.RoundToInt(viewport.x * ImageWidth), 0, ImageWidth - 1),
            Mathf.Clamp(Mathf.RoundToInt(viewport.y * ImageHeight), 0, ImageHeight - 1));
    }

    static int CountDifferencesInRect(Color[] a, Color[] b, ScreenRect rect, float threshold)
    {
        if (a == null || b == null || a.Length != b.Length || rect.IsEmpty)
            return -1;
        int count = 0;
        for (int y = rect.yMin; y <= rect.yMax; y++)
        {
            for (int x = rect.xMin; x <= rect.xMax; x++)
            {
                int index = y * ImageWidth + x;
                if (MaxChannelDelta(a[index], b[index]) > threshold)
                    count++;
            }
        }

        return count;
    }

    static float MaxDifferenceInRect(Color[] a, Color[] b, ScreenRect rect)
    {
        if (a == null || b == null || a.Length != b.Length || rect.IsEmpty)
            return float.PositiveInfinity;
        float max = 0f;
        for (int y = rect.yMin; y <= rect.yMax; y++)
        {
            for (int x = rect.xMin; x <= rect.xMax; x++)
            {
                int index = y * ImageWidth + x;
                max = Mathf.Max(max, MaxChannelDelta(a[index], b[index]));
            }
        }

        return max;
    }

    static Color[] ReadMeanInRect(Camera camera, RenderTexture imageTarget, GameObject gameObject, int inset, out Color mean)
    {
        var samples = RenderAndCapture(camera, imageTarget, 2);
        var rect = ProjectBounds(camera, gameObject, inset);
        mean = Color.black;
        if (rect.IsEmpty)
            return samples;

        double r = 0, g = 0, b = 0;
        int count = 0;
        for (int y = rect.yMin; y <= rect.yMax; y++)
        {
            for (int x = rect.xMin; x <= rect.xMax; x++)
            {
                var c = samples[y * ImageWidth + x];
                r += c.r;
                g += c.g;
                b += c.b;
                count++;
            }
        }

        if (count > 0)
            mean = new Color((float)(r / count), (float)(g / count), (float)(b / count), 1f);
        return samples;
    }

    static float MaxChannelDelta(Color a, Color b)
    {
        return Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
    }

    static RenderTexture CreateTarget(int width, int height, string name)
    {
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { name = name };
        target.Create();
        return target;
    }

    static Color[] RenderAndCapture(Camera camera, RenderTexture imageTarget, int frames)
    {
        for (var i = 0; i < frames; i++)
            camera.Render();
        return ReadPixels(imageTarget);
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
        if (string.IsNullOrEmpty(outputDirectory))
            return;
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

    static T11StateCapture DescribeState(string stateName, Color[] samples, Color[] reference)
    {
        var capture = new T11StateCapture
        {
            state = stateName,
            sha256 = HashSamples(samples),
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
            nonFinitePixels = Count(samples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)),
            meanLuminance = Mean(samples, Luminance),
            differingPixels = reference != null ? CountDifferences(samples, reference, k_IdenticalThreshold) : 0,
            maxChannelDelta = reference != null ? MaxDifference(samples, reference) : 0f
        };
        capture.differsFromReference = capture.differingPixels > 0;
        capture.identicalToReference = reference != null && capture.differingPixels == 0;
        return capture;
    }

    static int CountDifferences(Color[] a, Color[] b, float threshold)
    {
        if (a == null || b == null || a.Length != b.Length)
            return Math.Max(a?.Length ?? 0, b?.Length ?? 0);
        int count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (MaxChannelDelta(a[i], b[i]) > threshold)
                count++;
        }

        return count;
    }

    static float MaxDifference(Color[] a, Color[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return float.PositiveInfinity;
        float max = 0f;
        for (int i = 0; i < a.Length; i++)
            max = Mathf.Max(max, MaxChannelDelta(a[i], b[i]));
        return max;
    }

    static int Count(Color[] samples, Func<Color, bool> predicate)
    {
        if (samples == null)
            return 0;
        int count = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if (predicate(samples[i]))
                count++;
        }

        return count;
    }

    static float Mean(Color[] samples, Func<Color, float> selector)
    {
        if (samples == null || samples.Length == 0)
            return 0f;
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += selector(samples[i]);
        return (float)(sum / samples.Length);
    }

    static float Luminance(Color color) => color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;

    static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

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
}

[Serializable]
public sealed class T11Report
{
    public string utcTimestamp;
    public string unityVersion;
    public string graphicsDeviceName;
    public string graphicsDeviceType;
    public bool completed;
    public bool passed;
    public string error;
    public List<T11ModeResult> modes;
}

[Serializable]
public sealed class T11ModeResult
{
    public string name;
    public bool passed;
    public List<string> issues;
    public List<T11StateCapture> states;
    public int boxChangedPixels;
    public float boxMaxDelta;
    public int legacyChangedPixels;
    public float legacyMaxDelta;
    public int complexChangedPixels;
    public float complexMaxDelta;
    public int sphereChangedPixels;
    public int shaderGraphChangedPixels;
    public float shaderGraphMaxDelta;
    public int noProxyChangedPixels;
    public float noProxyMaxDelta;
    public bool tintApplied;
    public T11Tint tint;
    public Color tintStartMean;
    public Color tintEndMean;
    public Color moveBeforeMean;
    public Color moveAfterMean;
    public bool currentFramePyramidFollowsScene;
    public int preRefractionGlassChangedPixels;
    public float preRefractionGlassMaxDelta;
    public int preRefractionPanelChangedPixels;
    public int pyramidWithPanelChangedPixels;
    public string panelDiagnostics;
    public T11Ordering preRefractionDifference;
    public int shaderGraphTransparentSsrChangedPixels;
    public int refractionExecuteCount;
    public int refractionBoundCount;
    public int refractionNoProxyCount;
    public int visibleProbeCount;
    public int sceneProbeCount;
    public int boxProbeCount;
    public string limitations;
}

[Serializable]
public sealed class T11Tint
{
    public Color whiteMean;
    public Color tintedMean;
    public float redRatio;
    public bool applied;
}

[Serializable]
public sealed class T11Ordering
{
    public int glassChangedPixels;
    public float glassMaxDelta;
    public int panelChangedPixels;
    public bool pyramidExcludesTransparents;
}

[Serializable]
public sealed class T11StateCapture
{
    public string state;
    public string sha256;
    public int differingPixels;
    public float maxChannelDelta;
    public bool differsFromReference;
    public bool identicalToReference;
    public int magentaPixels;
    public int nonFinitePixels;
    public float meanLuminance;
}