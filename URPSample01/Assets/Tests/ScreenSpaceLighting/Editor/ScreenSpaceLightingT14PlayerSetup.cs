using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// T14 release regression: builds the standalone player without Development/IncludeTestAssemblies
// and records which screen-space resources survived shader stripping. The player itself runs the
// T13 XR verification scene, so the same stereo image checks double as the release-build evidence.
public static class ScreenSpaceLightingT14PlayerSetup
{
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT13XrPlayer.unity";
    const string StrippingScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT14Stripping.unity";
    const string PipelinePath = "Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardPipeline.asset";
    const string OutputRoot = "PALN/validation/T14";
    const string PlayerFolder = "Player";
    const string ExecutableName = "SslT14Player.exe";

    // The player scene covers Lit, textured Lit and the transparent materials. The stripping probe
    // scene adds the material paths the T13 scene never references (ComplexLit, SimpleLit, ShaderGraph)
    // so the release build proves those variants survive alongside the runtime scene.
    static readonly string[] MaterialPaths =
    {
        "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionComplexLit.mat",
        "Assets/Tests/ScreenSpaceLighting/Materials/T10TransparentComplexLit.mat",
        "Assets/Tests/ScreenSpaceLighting/Materials/T10TransparentSimpleLit.mat",
        "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionShaderGraph.mat",
        "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionSphere.mat",
        "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionThin.mat",
        "Assets/Tests/ScreenSpaceLighting/Materials/T11LegacyTransparent.mat",
    };

    static readonly string[] ShaderGraphPaths =
    {
        "Assets/Tests/ScreenSpaceLighting/Shaders/SslT9Lit.shadergraph",
        "Assets/Tests/ScreenSpaceLighting/Shaders/SslT9ComplexLit.shadergraph",
    };

    static readonly string[] RequiredResourceSuffixes =
    {
        "/ScreenSpaceCommon/DepthPyramid.compute",
        "/ScreenSpaceLighting/ColorPyramid.shader",
        "/ScreenSpaceLighting/ScreenSpaceLightingDebug.shader",
        "/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute",
        "/ScreenSpaceLighting/TemporalFilter.compute",
        "/ScreenSpaceLighting/DiffuseDenoiser.compute",
        "/ScreenSpaceCommon/BilateralUpsample.compute",
        "/ScreenSpaceLighting/ScreenSpaceReflections.compute",
        "/GroundTruthAmbientOcclusion/GTAO.compute",
        "/GroundTruthAmbientOcclusion/GTAOSpatialDenoise.compute",
        "/GroundTruthAmbientOcclusion/GTAOTemporalDenoise.compute",
        "/GroundTruthAmbientOcclusion/GTAOCopyHistory.compute",
        "/GroundTruthAmbientOcclusion/GTAOBlurAndUpsample.compute",
        "/GroundTruthAmbientOcclusion/GTAODebug.shader",
        "/Shaders/Utils/CopyDepth.shader",
        "/Shaders/Lit.shader",
        "/Shaders/ComplexLit.shader",
        "/Shaders/SimpleLit.shader",
        "/Shaders/SslT9Lit.shadergraph",
        "/Shaders/SslT9ComplexLit.shadergraph",
        "/Shaders/SslT11Lit.shadergraph",
        "/Shaders/T13XrArrayProbe.compute",
    };

    [MenuItem("Tests/Screen Space Lighting/Build T14 Release Player")]
    public static void BuildReleasePlayer()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot, PlayerFolder);
        Directory.CreateDirectory(outputDirectory);
        var executablePath = Path.Combine(outputDirectory, ExecutableName);

        ScreenSpaceLightingT13XrEnvironment.EnsureAssets();
        EnsureStrippingScene();

        var previousApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        var buildApis = new[]
        {
            GraphicsDeviceType.Direct3D11,
            GraphicsDeviceType.Direct3D12,
            GraphicsDeviceType.Vulkan,
        };
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, buildApis);

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
        BuildReport report;
        try
        {
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath, StrippingScenePath },
                locationPathName = executablePath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            });
        }
        finally
        {
            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, previousApis);
        }

        var summary = report.summary;
        var packedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var packed in report.packedAssets)
        {
            foreach (var entry in packed.contents)
            {
                var path = entry.sourceAssetPath;
                if (!string.IsNullOrEmpty(path))
                    packedPaths.Add(path.Replace('\\', '/'));
            }
        }

        var resources = new List<T14PackedResource>();
        foreach (var suffix in RequiredResourceSuffixes)
        {
            var match = packedPaths.FirstOrDefault(path => path.EndsWith(suffix, StringComparison.Ordinal));
            resources.Add(new T14PackedResource
            {
                suffix = suffix,
                present = match != null,
                packedPath = match,
            });
        }

        var result = new T14PlayerBuildSummary
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            result = summary.result.ToString(),
            totalErrors = summary.totalErrors,
            totalWarnings = summary.totalWarnings,
            totalSizeBytes = (long)summary.totalSize,
            totalTimeSeconds = summary.totalTime.TotalSeconds,
            executablePath = executablePath,
            buildOptions = "None (release: no Development, no IncludeTestAssemblies)",
            graphicsApis = buildApis.Select(api => api.ToString()).ToArray(),
            managedStrippingLevel = PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone).ToString(),
            stripEngineCode = PlayerSettings.stripEngineCode,
            packedAssetCount = packedPaths.Count,
            requiredResources = resources.ToArray(),
            allRequiredResourcesPacked = resources.All(resource => resource.present),
        };

        var summaryPath = Path.Combine(projectRoot, OutputRoot, "T14PlayerBuildSummary.json");
        File.WriteAllText(summaryPath, JsonUtility.ToJson(result, true));

        Debug.Log($"T14 release player build: {summary.result} errors={summary.totalErrors} " +
                  $"packed={result.packedAssetCount} allRequiredResourcesPacked={result.allRequiredResourcesPacked} path={executablePath}");
        EditorApplication.Exit(
            summary.result == BuildResult.Succeeded && summary.totalErrors == 0 && result.allRequiredResourcesPacked ? 0 : 1);
    }

    // Keeps the material paths that the runtime scene does not reference in the build so the
    // stripping audit can prove the Lit/ComplexLit/ShaderGraph variants survive a release build.
    static Material EnsureShaderGraphMaterial(string graphPath)
    {
        var materialPath = "Assets/Tests/ScreenSpaceLighting/Materials/" +
                           Path.GetFileNameWithoutExtension(graphPath) + "T14.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material != null)
            return material;

        var shader = AssetDatabase.LoadAllAssetsAtPath(graphPath).OfType<Shader>().FirstOrDefault();
        if (shader == null)
            throw new FileNotFoundException("Missing ShaderGraph shader sub-asset.", graphPath);

        Directory.CreateDirectory(Path.GetDirectoryName(materialPath));
        material = new Material(shader) { name = Path.GetFileNameWithoutExtension(graphPath) + " T14" };
        AssetDatabase.CreateAsset(material, materialPath);
        return material;
    }

    static void EnsureStrippingScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var root = new GameObject("T14 Stripping Probe");
        var materials = new List<Material>();
        foreach (var path in MaterialPaths)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
                throw new FileNotFoundException("Missing T14 stripping material.", path);
            materials.Add(material);
        }

        // The T9 ShaderGraph variants are only created at runtime by the T9 verification, so the
        // release build has no material asset referencing them. Create one per graph.
        foreach (var graphPath in ShaderGraphPaths)
            materials.Add(EnsureShaderGraphMaterial(graphPath));

        for (int i = 0; i < materials.Count; i++)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = materials[i].name;
            sphere.transform.SetParent(root.transform, false);
            sphere.transform.position = new Vector3(i * 2f, 0f, 0f);
            sphere.GetComponent<Renderer>().sharedMaterial = materials[i];
        }

        var lightObject = new GameObject("T14 Stripping Directional Light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1f;

        var cameraObject = new GameObject("T14 Stripping Camera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.transform.position = new Vector3(0f, 1f, -12f);
        camera.transform.LookAt(Vector3.zero);

        Directory.CreateDirectory(Path.GetDirectoryName(StrippingScenePath));
        EditorSceneManager.SaveScene(scene, StrippingScenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }
}

[Serializable]
public sealed class T14PlayerBuildSummary
{
    public string utcTimestamp;
    public string unityVersion;
    public string result;
    public int totalErrors;
    public int totalWarnings;
    public long totalSizeBytes;
    public double totalTimeSeconds;
    public string executablePath;
    public string buildOptions;
    public string[] graphicsApis;
    public string managedStrippingLevel;
    public bool stripEngineCode;
    public int packedAssetCount;
    public T14PackedResource[] requiredResources;
    public bool allRequiredResourcesPacked;
}

[Serializable]
public sealed class T14PackedResource
{
    public string suffix;
    public bool present;
    public string packedPath;
}