#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

// I2 stripping evidence: records the retained Lit GlxDXR keyword sets for both the normal Win64
// build and a D3D11-only build, and builds the non-target player from the same fixture scene.
public sealed class I2VariantAudit : IPreprocessShaders
{
    public int callbackOrder => int.MaxValue;

    public static void BuildD3D11Only()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11 });
        var scene = Environment.GetEnvironmentVariable("I2_SCENE");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scene }, target = target, options = BuildOptions.Development,
            locationPathName = Environment.GetEnvironmentVariable("I2_STRIP_EXE")
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("D3D11-only I2 build failed");
    }

    public void OnProcessShader(Shader shader, ShaderSnippetData pass, IList<ShaderCompilerData> variants)
    {
        if (shader.name != "Universal Render Pipeline/Lit" || pass.passName != "GlxDXR") return;
        var path = Environment.GetEnvironmentVariable("I2_VARIANTS");
        if (string.IsNullOrEmpty(path)) return;
        using var writer = File.AppendText(path);
        writer.WriteLine("pass " + pass.shaderType + " " + variants.Count);
        foreach (var variant in variants)
            writer.WriteLine("set " + string.Join(" ",
                variant.shaderKeywordSet.GetShaderKeywords().Select(keyword => keyword.name).OrderBy(name => name)));
    }
}
#endif
