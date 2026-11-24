using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

public sealed class T3bVariantAudit : IPreprocessShaders
{
    public int callbackOrder => int.MaxValue;

    public static void BuildD3D11Only()
    {
        const UnityEditor.BuildTarget target = UnityEditor.BuildTarget.StandaloneWindows64;
        UnityEditor.PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        UnityEditor.PlayerSettings.SetGraphicsAPIs(target, new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 });
        var scene = Environment.GetEnvironmentVariable("T3B_SCENE");
        var report = UnityEditor.BuildPipeline.BuildPlayer(new UnityEditor.BuildPlayerOptions {
            scenes = new[] { scene }, target = target, options = UnityEditor.BuildOptions.Development,
            locationPathName = Environment.GetEnvironmentVariable("T3B_EXE") });
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            throw new InvalidOperationException("D3D11-only build failed");
    }

    public void OnProcessShader(Shader shader, ShaderSnippetData pass, IList<ShaderCompilerData> variants)
    {
        if (shader.name != "Universal Render Pipeline/Lit" || pass.passName != "GlxDXR") return;
        var path = Environment.GetEnvironmentVariable("T3B_VARIANTS");
        if (string.IsNullOrEmpty(path)) return;
        using (var f = File.AppendText(path))
        {
            f.WriteLine("pass " + pass.shaderType + " " + variants.Count);
            foreach (var v in variants)
                f.WriteLine("set " + string.Join(" ", v.shaderKeywordSet.GetShaderKeywords().Select(k => k.name).OrderBy(n => n)));
        }
    }
}
