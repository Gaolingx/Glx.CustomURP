#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// I2 stripping evidence: records every surviving variant that only produces pixels with hardware ray
// tracing (the GlxDXR hit pass and passes gated by _SCREEN_SPACE_GI_PROBE), the packed ray tracing
// assets, and builds the non-target player from the same fixture scene.
public sealed class I2VariantAudit : IPreprocessShaders, IPostprocessBuildWithReport
{
    const string k_ProbeKeyword = "_SCREEN_SPACE_GI_PROBE";
    static readonly SortedDictionary<string, SortedSet<string>> s_GlxDxrSets = new SortedDictionary<string, SortedSet<string>>();
    static readonly SortedDictionary<string, SortedSet<string>> s_ProbeSets = new SortedDictionary<string, SortedSet<string>>();

    public int callbackOrder => int.MaxValue;

    public static void BuildD3D11Only()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11 });
        var scene = Environment.GetEnvironmentVariable("I2_SCENE");
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
            "Assets/Fixture/I2/I2Pipeline.asset");
        if (pipeline == null)
            throw new InvalidOperationException("I2 fixture pipeline missing");

        // The signed Win64 build assigns the fixture pipeline to every quality level (I2Player.Build);
        // repeat that here so the stripped build compiles the same renderer set instead of losing
        // renderer-only shaders to feature collection.
        var previousPipelines = new RenderPipelineAsset[QualitySettings.names.Length];
        int activeLevel = QualitySettings.GetQualityLevel();
        for (int q = 0; q < previousPipelines.Length; ++q)
        {
            QualitySettings.SetQualityLevel(q, false);
            previousPipelines[q] = QualitySettings.renderPipeline;
            QualitySettings.renderPipeline = pipeline;
        }
        QualitySettings.SetQualityLevel(activeLevel, false);
        var previousDefault = GraphicsSettings.defaultRenderPipeline;
        GraphicsSettings.defaultRenderPipeline = pipeline;
        BuildReport report;
        try
        {
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scene }, target = target, options = BuildOptions.Development,
                locationPathName = Environment.GetEnvironmentVariable("I2_STRIP_EXE")
            });
        }
        finally
        {
            for (int q = 0; q < previousPipelines.Length; ++q)
            {
                QualitySettings.SetQualityLevel(q, false);
                QualitySettings.renderPipeline = previousPipelines[q];
            }
            QualitySettings.SetQualityLevel(activeLevel, false);
            GraphicsSettings.defaultRenderPipeline = previousDefault;
        }

        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("D3D11-only I2 build failed");
        // IPostprocessBuildWithReport.OnPostprocessBuild writes the audit for this build.
    }

    public void OnPostprocessBuild(BuildReport report)
    {
        WriteAudit(report, Environment.GetEnvironmentVariable("I2_RT_AUDIT"));
    }

    public void OnProcessShader(Shader shader, ShaderSnippetData pass, IList<ShaderCompilerData> variants)
    {
        var glxDxr = new SortedSet<string>();
        var probe = new SortedSet<string>();
        foreach (var variant in variants)
        {
            var names = variant.shaderKeywordSet.GetShaderKeywords().Select(keyword => keyword.name)
                .OrderBy(name => name).ToArray();
            if (pass.passName == "GlxDXR")
                glxDxr.Add(string.Join(" ", names));
            if (Array.IndexOf(names, k_ProbeKeyword) >= 0)
                probe.Add(string.Join(" ", names));
        }

        Collect(s_GlxDxrSets, shader.name + "|" + pass.passName, glxDxr);
        Collect(s_ProbeSets, shader.name + "|" + pass.passName, probe);

        if (shader.name != "Universal Render Pipeline/Lit" || pass.passName != "GlxDXR") return;
        var path = Environment.GetEnvironmentVariable("I2_VARIANTS");
        if (string.IsNullOrEmpty(path)) return;
        using var writer = File.AppendText(path);
        writer.WriteLine("pass " + pass.shaderType + " " + variants.Count);
        foreach (var variant in variants)
            writer.WriteLine("set " + string.Join(" ",
                variant.shaderKeywordSet.GetShaderKeywords().Select(keyword => keyword.name).OrderBy(name => name)));
    }

    static void Collect(SortedDictionary<string, SortedSet<string>> target, string key, SortedSet<string> sets)
    {
        if (sets.Count == 0) return;
        if (!target.TryGetValue(key, out var existing))
            target[key] = existing = new SortedSet<string>();
        existing.UnionWith(sets);
    }

    static void WriteAudit(BuildReport report, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var apis = PlayerSettings.GetGraphicsAPIs(report.summary.platform)
            .Select(api => "\"" + api + "\"").ToArray();
        var hits = new SortedSet<string>();
        foreach (var packed in report.packedAssets)
            foreach (var asset in packed.contents)
            {
                var assetPath = asset.sourceAssetPath;
                if (string.IsNullOrEmpty(assetPath)) continue;
                assetPath = assetPath.Replace('\\', '/');
                var extension = Path.GetExtension(assetPath).ToLowerInvariant();
                if (extension == ".raytrace" ||
                    ((extension == ".compute" || extension == ".shader") && assetPath.Contains("/RayTracing/")))
                    hits.Add(assetPath);
            }

        var builder = new StringBuilder();
        builder.Append("{\n");
        builder.Append("  \"platform\": \"").Append(report.summary.platform).Append("\",\n");
        builder.Append("  \"graphicsAPIs\": [").Append(string.Join(", ", apis)).Append("],\n");
        AppendSets(builder, "glxDxr", s_GlxDxrSets, true);
        AppendSets(builder, "probeKeyword", s_ProbeSets, false);
        builder.Append("  \"rtAssetEntries\": [")
            .Append(string.Join(", ", hits.Select(hit => "\"" + hit + "\""))).Append("],\n");
        builder.Append("  \"rtAssetCount\": ").Append(hits.Count).Append('\n');
        builder.Append("}\n");
        File.WriteAllText(path, builder.ToString());
        s_GlxDxrSets.Clear();
        s_ProbeSets.Clear();
    }

    static void AppendSets(StringBuilder builder, string name, SortedDictionary<string, SortedSet<string>> sets, bool includeLists)
    {
        builder.Append("  \"").Append(name).Append("\": [");
        builder.Append(string.Join(",\n", sets.Select(pair =>
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", pair.Value))))
                .Replace("-", string.Empty).ToLowerInvariant();
            var head = "{\"pass\": \"" + pair.Key + "\", \"count\": " + pair.Value.Count +
                       ", \"sha256\": \"" + digest + "\"";
            if (includeLists)
                return head + ", \"sets\": [" +
                       string.Join(", ", pair.Value.Select(set => "\"" + set + "\"")) + "]}";
            var sample = pair.Value.Take(2).Concat(pair.Value.Skip(Math.Max(0, pair.Value.Count - 1)))
                .Select(set => "\"" + set + "\"");
            return head + ", \"sample\": [" + string.Join(", ", sample) + "]}";
        })));
        builder.Append("],\n");
        builder.Append("  \"").Append(name).Append("Sets\": ")
            .Append(sets.Sum(pair => pair.Value.Count)).Append(",\n");
    }
}
#endif
