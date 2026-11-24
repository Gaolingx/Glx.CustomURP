// Scratch Player build ONLY: retain precisely the two fixture hit-pass keyword sets.
// No production shader stripping policy is modified or tested here.
using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

public sealed class T2BeforeStrip : IPreprocessShaders
{
    internal static ShaderCompilerData[] incoming;
    internal static bool Match(Shader shader, ShaderSnippetData pass) =>
        shader.name == "Hidden/T2/IntersectionFixture" && pass.passName == "GlxDXR";
    public int callbackOrder => int.MinValue;
    public void OnProcessShader(Shader shader, ShaderSnippetData pass, IList<ShaderCompilerData> data)
    {
        if (!Match(shader,pass)) return;
        incoming=new ShaderCompilerData[data.Count]; data.CopyTo(incoming,0);
    }
}
public sealed class T2AfterStrip : IPreprocessShaders
{
    public int callbackOrder => int.MaxValue;
    public void OnProcessShader(Shader shader, ShaderSnippetData pass, IList<ShaderCompilerData> data)
    {
        if (!T2BeforeStrip.Match(shader,pass)) return;
        if(data.Count==0) foreach(var variant in T2BeforeStrip.incoming) data.Add(variant);
        T2BeforeStrip.incoming=null;
    }
}
