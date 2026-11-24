using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

// Records the retained Lit GlxDXR keyword sets of this build. R4's contract is the B0 cap of
// 16 production hit variants (normal map, metallic/gloss map, emission, alpha test).
public sealed class R4VariantAudit : IPreprocessShaders
{
    public int callbackOrder => int.MaxValue;

    public void OnProcessShader(Shader shader, ShaderSnippetData pass, IList<ShaderCompilerData> variants)
    {
        if (shader.name != "Universal Render Pipeline/Lit" || pass.passName != "GlxDXR") return;
        var path = Environment.GetEnvironmentVariable("R4_VARIANTS");
        if (string.IsNullOrEmpty(path)) return;
        using (var file = File.AppendText(path))
        {
            file.WriteLine("pass " + pass.shaderType + " " + variants.Count);
            foreach (var variant in variants)
                file.WriteLine("set " + string.Join(" ", variant.shaderKeywordSet.GetShaderKeywords().Select(k => k.name).OrderBy(n => n)));
        }
    }
}
