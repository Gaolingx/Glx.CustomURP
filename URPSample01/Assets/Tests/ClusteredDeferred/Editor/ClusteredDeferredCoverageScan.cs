using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Preflight scan for PLAN/TODO/11-LightCulling (D15/D16).
//
// For every opaque material used by any scene (plus every material asset), report whether the
// shader has a UniversalGBuffer pass and which UniversalMaterialType it declares. Under D16 the
// compute pass reads a material-type mask (0 = skip, 1 = Lit, 2 = SimpleLit); "forwardOnly"
// materials keep rendering through the existing forward-only pass and simply do not receive
// clustered deferred lighting, so a missing GBuffer pass is no longer a rendering hole.
public static class ClusteredDeferredCoverageScan
{
    const string OutputRoot = "PLAN/TODO/11-LightCulling/validation/T6-preflight";
    const string JsonName = "T6Coverage.json";

    static string s_ProjectRoot;

    [MenuItem("Tests/Clustered Deferred/Run Coverage Scan")]
    public static void RunFromMenu()
    {
        Run();
    }

    public static void RunAndExit()
    {
        bool ok;
        try
        {
            ok = Run();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ok = false;
        }

        EditorApplication.Exit(ok ? 0 : 1);
    }

    public static bool Run()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        s_ProjectRoot = projectRoot;
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);

        var report = new CoverageReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            notes = new List<string>
            {
                "A/B multi-light scene does not exist yet (T7 creates it); this scan covers every scene under Assets plus every material asset as the preflight baseline.",
                "clusteredLit/clusteredSimpleLit: shaded by the clustered deferred compute (mask 1/2).",
                "forwardOnly: keeps the original forward-only pass (executed after deferred lighting); mask 0, no double lighting.",
                "skippedByMaterialType: Unlit/ComplexLit; stencil deferred writes MaterialUnlit for both and skips them too.",
            },
            scenes = new List<SceneCoverage>(),
            materials = new List<MaterialCoverage>(),
            issues = new List<string>(),
        };

        var sceneMaterials = new Dictionary<string, Material>(StringComparer.Ordinal);
        var materialScenes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var previousScene = SceneManager.GetActiveScene().path;

        var scenePaths = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        try
        {
            foreach (var scenePath in scenePaths)
            {
                var entry = new SceneCoverage { path = scenePath };
                try
                {
                    EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                    var seen = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
                    {
                        if (renderer == null || renderer.sharedMaterials == null)
                            continue;

                        foreach (var material in renderer.sharedMaterials)
                        {
                            if (material == null)
                                continue;
                            if (!IsOpaque(material))
                            {
                                entry.transparentMaterialCount++;
                                continue;
                            }

                            entry.opaqueRendererCount++;
                            AddMaterial(scenePath, material, seen, entry, sceneMaterials, materialScenes);
                        }
                    }

                    foreach (var terrain in UnityEngine.Object.FindObjectsOfType<Terrain>(true))
                    {
                        var material = terrain != null ? terrain.materialTemplate : null;
                        if (material == null)
                            continue;
                        if (!IsOpaque(material))
                        {
                            entry.transparentMaterialCount++;
                            continue;
                        }

                        entry.opaqueRendererCount++;
                        AddMaterial(scenePath, material, seen, entry, sceneMaterials, materialScenes);
                    }
                }
                catch (Exception exception)
                {
                    report.issues.Add(scenePath + ": " + exception.Message);
                }

                report.scenes.Add(entry);
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(previousScene))
            {
                try
                {
                    EditorSceneManager.OpenScene(previousScene, OpenSceneMode.Single);
                }
                catch (Exception exception)
                {
                    report.issues.Add("restore scene " + previousScene + ": " + exception.Message);
                }
            }
        }

        foreach (var pair in sceneMaterials.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var scenes = materialScenes.TryGetValue(pair.Key, out var list) ? list : new List<string>();
            var coverage = Analyze(pair.Value, pair.Key, scenes, true);
            if (coverage != null)
                report.materials.Add(coverage);
        }

        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || sceneMaterials.ContainsKey(path))
                continue;

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null || !IsOpaque(material))
                continue;

            var coverage = Analyze(material, path, new List<string>(), false);
            if (coverage != null)
                report.materials.Add(coverage);
        }

        report.materials = report.materials
            .OrderBy(entry => entry.verdict, StringComparer.Ordinal)
            .ThenBy(entry => entry.materialPath, StringComparer.Ordinal)
            .ToList();

        report.summary = new CoverageSummary
        {
            sceneCount = report.scenes.Count,
            opaqueMaterialCount = report.materials.Count,
            referencedMaterialCount = report.materials.Count(entry => entry.referencedInScenes),
            clusteredLitCount = report.materials.Count(entry => entry.verdict == "clusteredLit"),
            clusteredSimpleLitCount = report.materials.Count(entry => entry.verdict == "clusteredSimpleLit"),
            clusteredUnknownTypeCount = report.materials.Count(entry => entry.verdict == "clusteredUnknownType"),
            skippedByMaterialTypeCount = report.materials.Count(entry => entry.verdict == "skippedByMaterialType"),
            forwardOnlyCount = report.materials.Count(entry => entry.verdict == "forwardOnly"),
            notURPStyleCount = report.materials.Count(entry => entry.verdict == "notURPStyle"),
            missingShaderCount = report.materials.Count(entry => entry.verdict == "missingShader"),
        };

        if (report.summary.clusteredUnknownTypeCount > 0)
            report.issues.Add("Materials with a UniversalGBuffer pass but no Lit/SimpleLit material type need review.");
        if (report.summary.missingShaderCount > 0)
            report.issues.Add("Materials with a missing shader were found.");

        var outputPath = Path.Combine(outputDirectory, JsonName);
        File.WriteAllText(outputPath, JsonUtility.ToJson(report, true));

        Debug.LogFormat(
            "Clustered deferred coverage scan: scenes={0} opaque={1} clusteredLit={2} clusteredSimpleLit={3} skipped={4} forwardOnly={5} unknownType={6} notURP={7} -> {8}",
            report.summary.sceneCount, report.summary.opaqueMaterialCount,
            report.summary.clusteredLitCount, report.summary.clusteredSimpleLitCount,
            report.summary.skippedByMaterialTypeCount, report.summary.forwardOnlyCount,
            report.summary.clusteredUnknownTypeCount, report.summary.notURPStyleCount, outputPath);

        return report.issues.Count == 0 || report.summary.opaqueMaterialCount > 0;
    }

    static void AddMaterial(string scenePath, Material material, HashSet<string> seen, SceneCoverage entry,
        Dictionary<string, Material> sceneMaterials, Dictionary<string, List<string>> materialScenes)
    {
        var path = AssetDatabase.GetAssetPath(material);
        var key = string.IsNullOrEmpty(path) ? "Embedded:" + material.GetInstanceID() : path;

        if (!sceneMaterials.ContainsKey(key))
            sceneMaterials.Add(key, material);

        if (!materialScenes.TryGetValue(key, out var scenes))
        {
            scenes = new List<string>();
            materialScenes.Add(key, scenes);
        }

        if (!scenes.Contains(scenePath))
            scenes.Add(scenePath);

        if (seen.Add(key))
            entry.opaqueMaterialCount++;
    }

    static bool IsOpaque(Material material)
    {
        return material != null && material.shader != null && material.renderQueue < 2500;
    }

    static MaterialCoverage Analyze(Material material, string path, List<string> scenes, bool referenced)
    {
        if (material == null)
            return null;

        var coverage = new MaterialCoverage
        {
            materialPath = path,
            materialName = material.name,
            renderQueue = material.renderQueue,
            referencedInScenes = referenced,
            scenes = scenes,
            shaderName = material.shader != null ? material.shader.name : string.Empty,
            materialType = string.Empty,
            analysisSource = "passTags",
        };

        var shader = material.shader;
        if (shader == null)
        {
            coverage.verdict = "missingShader";
            return coverage;
        }

        // Batchmode can expose only the active SubShader (often a single placeholder pass) before
        // the shader is compiled. Ask the compiler for messages first, then walk every SubShader.
        try
        {
            ShaderUtil.GetShaderMessageCount(shader);
        }
        catch (ArgumentException)
        {
        }

        var subshaderCount = 1;
        try
        {
            subshaderCount = Math.Max(1, shader.subshaderCount);
        }
        catch (ArgumentException)
        {
        }

        coverage.subshaderCount = subshaderCount;

        var totalPasses = 0;
        for (var subshader = 0; subshader < subshaderCount; subshader++)
        {
            var passCount = 0;
            try
            {
                passCount = shader.GetPassCountInSubshader(subshader);
            }
            catch (ArgumentException)
            {
            }

            totalPasses += passCount;
            for (var pass = 0; pass < passCount; pass++)
            {
                var lightMode = GetPassTag(shader, subshader, pass, "LightMode");
                switch (lightMode)
                {
                    case "UniversalGBuffer":
                        coverage.hasGBuffer = true;
                        break;
                    case "UniversalForwardOnly":
                        coverage.hasForwardOnly = true;
                        break;
                    case "UniversalForward":
                        coverage.hasUniversalForward = true;
                        break;
                    case "SRPDefaultUnlit":
                    case "LightweightForward":
                        coverage.hasSRPDefaultUnlit = true;
                        break;
                }

                if (string.IsNullOrEmpty(coverage.materialType))
                {
                    var materialType = GetPassTag(shader, subshader, pass, "UniversalMaterialType");
                    if (!string.IsNullOrEmpty(materialType))
                        coverage.materialType = materialType;
                }
            }
        }

        var foundPassTags = coverage.hasGBuffer || coverage.hasForwardOnly || coverage.hasUniversalForward ||
                            coverage.hasSRPDefaultUnlit || !string.IsNullOrEmpty(coverage.materialType);
        if (!foundPassTags)
            ApplySourceFallback(coverage, shader);

        coverage.shaderPassCount = totalPasses;
        coverage.verdict = Classify(coverage);
        return coverage;
    }

    static string GetPassTag(Shader shader, int subshader, int pass, string tagName)
    {
        try
        {
            return shader.FindPassTagValue(subshader, pass, new ShaderTagId(tagName)).name;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    static void ApplySourceFallback(MaterialCoverage coverage, Shader shader)
    {
        var shaderPath = AssetDatabase.GetAssetPath(shader);
        if (string.IsNullOrEmpty(shaderPath) && shader.name.StartsWith("Shader Graphs/", StringComparison.Ordinal))
        {
            var graphName = shader.name.Substring("Shader Graphs/".Length);
            shaderPath = AssetDatabase.FindAssets(graphName, new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(path => path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrEmpty(shaderPath))
            return;

        var fullPath = Path.IsPathRooted(shaderPath) ? shaderPath : Path.Combine(s_ProjectRoot, shaderPath);
        if (!File.Exists(fullPath))
            return;

        var text = File.ReadAllText(fullPath);

        if (shaderPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
        {
            coverage.analysisSource = "graphJson";
            if (text.Contains("UniversalSimpleLitSubTarget"))
                coverage.materialType = "SimpleLit";
            else if (text.Contains("UniversalComplexLitSubTarget"))
                coverage.materialType = "ComplexLit";
            else if (text.Contains("UniversalUnlitSubTarget"))
                coverage.materialType = "Unlit";
            else if (text.Contains("UniversalLitSubTarget"))
                coverage.materialType = "Lit";

            // Opaque Shader Graph Lit/SimpleLit targets generate a UniversalGBuffer pass; the
            // Unlit target does not.
            coverage.hasGBuffer = coverage.materialType == "Lit" || coverage.materialType == "SimpleLit" ||
                                  coverage.materialType == "ComplexLit";
            return;
        }

        if (shaderPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
        {
            coverage.analysisSource = "shaderSource";
            coverage.hasGBuffer = text.Contains("\"UniversalGBuffer\"");
            coverage.hasForwardOnly = text.Contains("\"UniversalForwardOnly\"");
            coverage.hasUniversalForward = text.Contains("\"UniversalForward\"");
            coverage.hasSRPDefaultUnlit = text.Contains("\"SRPDefaultUnlit\"") || !text.Contains("\"LightMode\"");

            if (text.Contains("\"UniversalMaterialType\" = \"SimpleLit\""))
                coverage.materialType = "SimpleLit";
            else if (text.Contains("\"UniversalMaterialType\" = \"ComplexLit\""))
                coverage.materialType = "ComplexLit";
            else if (text.Contains("\"UniversalMaterialType\" = \"Unlit\""))
                coverage.materialType = "Unlit";
            else if (text.Contains("\"UniversalMaterialType\" = \"Lit\""))
                coverage.materialType = "Lit";
        }
    }

    static string Classify(MaterialCoverage coverage)
    {
        // Stencil deferred writes MaterialUnlit for Unlit and ComplexLit (GBufferPass.cs:58-61),
        // so both are excluded from direct lighting in the stencil path as well.
        if (coverage.materialType == "Unlit" || coverage.materialType == "ComplexLit")
            return "skippedByMaterialType";

        if (coverage.hasGBuffer)
        {
            if (coverage.materialType == "SimpleLit")
                return "clusteredSimpleLit";
            if (coverage.materialType == "Lit")
                return "clusteredLit";
            return "clusteredUnknownType";
        }

        if (coverage.hasUniversalForward || coverage.hasForwardOnly || coverage.hasSRPDefaultUnlit)
            return "forwardOnly";

        return "notURPStyle";
    }

    [Serializable]
    public class CoverageReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceType;
        public List<string> notes;
        public List<SceneCoverage> scenes;
        public List<MaterialCoverage> materials;
        public CoverageSummary summary;
        public List<string> issues;
    }

    [Serializable]
    public class SceneCoverage
    {
        public string path;
        public int opaqueRendererCount;
        public int opaqueMaterialCount;
        public int transparentMaterialCount;
    }

    [Serializable]
    public class MaterialCoverage
    {
        public string materialPath;
        public string materialName;
        public string shaderName;
        public string materialType;
        public string analysisSource;
        public int renderQueue;
        public int shaderPassCount;
        public int subshaderCount;
        public bool hasGBuffer;
        public bool hasForwardOnly;
        public bool hasSRPDefaultUnlit;
        public bool hasUniversalForward;
        public bool referencedInScenes;
        public string verdict;
        public List<string> scenes;
    }

    [Serializable]
    public class CoverageSummary
    {
        public int sceneCount;
        public int opaqueMaterialCount;
        public int referencedMaterialCount;
        public int clusteredLitCount;
        public int clusteredSimpleLitCount;
        public int clusteredUnknownTypeCount;
        public int skippedByMaterialTypeCount;
        public int forwardOnlyCount;
        public int notURPStyleCount;
        public int missingShaderCount;
    }
}