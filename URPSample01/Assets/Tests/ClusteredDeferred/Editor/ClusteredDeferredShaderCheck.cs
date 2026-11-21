using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

// Compiles the clustered deferred compute/include and the two helper shaders on the current
// graphics API and writes T3ShaderCheck.txt. T2/T3 evidence; cross-API coverage is a player-build
// task (AC-9), not available from a single editor batch run.
public static class ClusteredDeferredShaderCheck
{
    const string OutputRoot = "PLAN/TODO/11-LightCulling/validation/T6-preflight";
    const string ReportName = "T3ShaderCheck.txt";

    static string ReportFileName =>
        SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11
            ? ReportName
            : "T3ShaderCheck_" + SystemInfo.graphicsDeviceType + ".txt";

    [MenuItem("Tests/Clustered Deferred/Run Shader Check")]
    public static void RunFromMenu()
    {
        Run();
    }

    // Preflight entry: rescan coverage (D16) and compile the new shaders in one batch launch.
    public static void RunScanAndCheckAndExit()
    {
        bool ok;
        try
        {
            ok = ClusteredDeferredCoverageScan.Run();
            ok &= Run();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ok = false;
        }

        EditorApplication.Exit(ok ? 0 : 1);
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
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);

        var builder = new StringBuilder();
        builder.AppendLine("Clustered deferred shader check");
        builder.AppendLine("utc: " + DateTime.UtcNow.ToString("O"));
        builder.AppendLine("unity: " + Application.unityVersion);
        builder.AppendLine("graphics API: " + SystemInfo.graphicsDeviceType);
        builder.AppendLine();

        var failed = false;
        var computeCount = 0;

        foreach (var compute in FindAssets<ComputeShader>("ClusteredDeferred"))
        {
            computeCount++;
            var path = AssetDatabase.GetAssetPath(compute);
            var count = ShaderUtil.GetComputeShaderMessageCount(compute);
            var messages = ShaderUtil.GetComputeShaderMessages(compute);
            builder.AppendLine("[compute] " + path + " messages=" + count);
            AppendMessages(builder, messages, ref failed);
            builder.AppendLine();
        }

        if (computeCount == 0)
        {
            builder.AppendLine("[compute] ClusteredDeferred.compute NOT FOUND");
            failed = true;
        }

        var shaderCount = 0;
        foreach (var shaderName in new[] { "ClusteredDeferredMask", "ClusteredDeferredResolve" })
        {
            // Load by known package path first; FindAssets alone can miss freshly imported package
            // shaders in a batch run.
            var shaderPath = "Packages/com.unity.render-pipelines.universal/Shaders/Utils/" + shaderName + ".shader";
            var shaders = new List<Shader>();
            var direct = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (direct != null)
                shaders.Add(direct);
            shaders.AddRange(FindAssets<Shader>(shaderName));

            foreach (var shader in shaders.Distinct())
            {
                shaderCount++;
                var path = AssetDatabase.GetAssetPath(shader);
                var count = ShaderUtil.GetShaderMessageCount(shader);
                var messages = ShaderUtil.GetShaderMessages(shader);
                builder.AppendLine("[shader] " + path + " messages=" + count);
                AppendMessages(builder, messages, ref failed);
                builder.AppendLine();
            }
        }

        if (shaderCount < 2)
        {
            builder.AppendLine("[shader] expected 2 helper shaders, found " + shaderCount);
            failed = true;
        }

        var reportPath = Path.Combine(outputDirectory, ReportFileName);
        File.WriteAllText(reportPath, builder.ToString());
        Debug.Log("Clustered deferred shader check: failed=" + failed + " -> " + reportPath);
        return !failed;
    }

    static IEnumerable<T> FindAssets<T>(string name) where T : UnityEngine.Object
    {
        var typeName = typeof(T).Name;
        return AssetDatabase.FindAssets(name + " t:" + typeName)
            .Select(AssetDatabase.GUIDToAssetPath)
            .Distinct()
            .Select(AssetDatabase.LoadAssetAtPath<T>)
            .Where(asset => asset != null && asset.name == name);
    }

    static void AppendMessages(StringBuilder builder, ShaderMessage[] messages, ref bool failed)
    {
        foreach (var message in messages)
        {
            if (message.severity == ShaderCompilerMessageSeverity.Error)
                failed = true;

            builder.AppendLine("  [" + message.severity + "] " +
                (string.IsNullOrEmpty(message.file) ? string.Empty : message.file + ":" + message.line + " ") +
                message.message);
        }
    }
}