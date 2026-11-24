using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class ContactShadowShaderCompileTests
    {
        const BuildTarget Win = BuildTarget.StandaloneWindows64;
        const ShaderCompilerPlatform D3D = ShaderCompilerPlatform.D3D;
        const ShaderCompilerPlatform ES3 = ShaderCompilerPlatform.GLES3x;
        const ShaderCompilerPlatform ES2 = ShaderCompilerPlatform.GLES20;
        const string Lit = "Lit.shader", Forward = "ForwardLit";
        const string Lights = "_MAIN_LIGHT_SHADOWS _ADDITIONAL_LIGHTS _ADDITIONAL_LIGHT_SHADOWS";
        const string Transparent = "_SURFACE_TYPE_TRANSPARENT", NoReceive = "_RECEIVE_SHADOWS_OFF";
        static string ShaderPath(string file) => UniversalRenderPipelineAsset.packagePath + "/Shaders/" + file;

        // Use the real compiler platform, not a D3D compilation with SHADER_API_GLES defined.
        // Cross-compiling a WebGL shader does not require building a WebGL player.
        [TestCase(Lit, Forward, D3D, Win, "", true)]
        [TestCase(Lit, Forward, D3D, Win, Lights, true)]
        [TestCase(Lit, Forward, D3D, Win, Lights + " _FORWARD_PLUS", true)]
        [TestCase(Lit, Forward, D3D, Win, Lights + " " + Transparent, false)]
        [TestCase(Lit, Forward, D3D, Win, Lights + " " + NoReceive, false)]
        [TestCase(Lit, Forward, D3D, Win, Lights + " " + Transparent + " " + NoReceive, false)]
        [TestCase("SimpleLit.shader", Forward, D3D, Win, Lights, true)]
        [TestCase(Lit, "GBuffer", D3D, Win, NoReceive, false)]
        [TestCase(Lit, Forward, ES3, Win, Lights, false)]
        [TestCase(Lit, Forward, ES3, Win, Lights + " " + Transparent, false)]
        [TestCase(Lit, Forward, ES3, Win, Lights + " " + NoReceive, false)]
        [TestCase(Lit, Forward, ES2, Win, "", false)]
        [TestCase(Lit, Forward, ES2, Win, "_ADDITIONAL_LIGHTS", false)]
        [TestCase(Lit, Forward, ES3, BuildTarget.WebGL, Lights, false)]
        [TestCase(Lit, Forward, ES2, BuildTarget.WebGL, "_ADDITIONAL_LIGHTS", false)]
        public void LightingVariantCompilesWithExpectedContactTexture(string file, string passName,
            ShaderCompilerPlatform platform, BuildTarget target, string keywords, bool expectTexture)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath(file));
            Assert.IsNotNull(shader, ShaderPath(file));
            var subshader = ShaderUtil.GetShaderData(shader).GetSubshader(0);
            var pass = Enumerable.Range(0, subshader.PassCount).Select(subshader.GetPass)
                .Single(p => string.Equals(p.Name, passName, StringComparison.OrdinalIgnoreCase));
            var keys = keywords.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // Installed 2022.3 ShaderData.Pass.CompileVariant docs: GLES returns a linked
            // vertex+fragment program for Vertex. Asking for Fragment can succeed with no bytes.
            ShaderData.VariantCompileInfo Compile(ShaderType stage)
            {
                var result = pass.CompileVariant(stage, keys, platform, target);
                string label = $"{file}/{passName} {platform}/{target} {stage} [{keywords}]";
                string errors = Errors(result.Messages);
                Assert.IsTrue(result.Success, label + "\n" + errors);
                Assert.IsEmpty(errors, label);
                Assert.IsNotEmpty(result.ShaderData, label + ": compiler returned no program");
                return result;
            }
            var program = Compile(ShaderType.Vertex);
            if (platform == D3D) program = Compile(ShaderType.Fragment);
            var bindings = program.TextureBindings.Select(binding => binding.Name).ToArray();
            Assert.AreEqual(expectTexture, bindings.Contains("_ContactShadowTexture"),
                $"Compiled {file}/{passName} {platform}/{target} [{keywords}] texture bindings: {string.Join(", ", bindings)}");
        }

        [TestCase("Utils/ContactShadows/ContactShadows.compute", "KContactShadow", "")]
        [TestCase("Utils/ScreenSpaceCommon/DepthPyramid.compute", "KDepthPyramidCopy", "")]
        [TestCase("Utils/ScreenSpaceCommon/DepthPyramid.compute", "KDepthPyramidDownsample", "")]
        [TestCase("Utils/ClusteredDeferred.compute", "ClusteredDeferred", "_CONTACT_SHADOWS_DEFERRED _ADDITIONAL_LIGHT_SHADOWS")]
        public void ProductionKernelIsUsableOnEditorDevice(string file, string kernel, string keywords)
        {
            if (!ScreenSpaceContactShadowsCapability.EvaluatePlatform().supported)
                Assert.Ignore("Contact compute requires a supported graphics device; this is not a GLES kernel test.");
            var source = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath(file));
            Assert.IsNotNull(source, ShaderPath(file));
            var shader = Object.Instantiate(source);
            try
            {
                foreach (var key in keywords.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) shader.EnableKeyword(key);
                Assert.IsTrue(shader.HasKernel(kernel), kernel);
                Assert.IsTrue(shader.IsSupported(shader.FindKernel(kernel)), kernel);
                Assert.IsEmpty(Errors(ShaderUtil.GetComputeShaderMessages(source)), file);
            }
            finally { Object.DestroyImmediate(shader); }
        }

        static string Errors(ShaderMessage[] messages) => string.Join("\n", messages
            .Where(m => m.severity == ShaderCompilerMessageSeverity.Error)
            .Select(m => $"{m.file}:{m.line}: {m.message} {m.messageDetails}"));
    }
}
