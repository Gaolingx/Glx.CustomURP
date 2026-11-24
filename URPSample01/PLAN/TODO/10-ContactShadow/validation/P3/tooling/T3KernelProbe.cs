using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

// Copied into Assets only in the disposable project. No Resources, global shader state or Editor fallback.
public sealed class T3KernelProbe : MonoBehaviour
{
    const string Source = "Packages/com.unity.render-pipelines.universal/Shaders/Utils/ContactShadows/ContactShadows.compute";
    const string Kernel = "KContactShadow", P = "_ContactShadow";
    const int N = 32;
    const uint Bit = 1u << 5;
    public ComputeShader shader; // Direct scene dependency on the production asset (not a shader copy in Resources).

    static string Arg(string key)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException(key);
    }

#if UNITY_EDITOR
    [Serializable] sealed class BuildEvidence
    {
        public string result, unity, source, guid;
        public int errors, warnings;
        public long bytes;
    }

    public static void Build()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12 });
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var probe = new GameObject("T3 kernel probe").AddComponent<T3KernelProbe>();
        probe.shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(Source);
        if (!probe.shader || !probe.shader.HasKernel(Kernel)) throw new InvalidOperationException(Source + "::" + Kernel);
        const string path = "Assets/Probe.unity";
        if (!EditorSceneManager.SaveScene(scene, path)) throw new IOException(path);
        var summary = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { path },
            locationPathName = Arg("-probeExe"), target = target, options = BuildOptions.None }).summary;
        File.WriteAllText(Path.Combine(Arg("-probeOut"), "BuildSummary.json"), JsonUtility.ToJson(new BuildEvidence {
            result = summary.result.ToString(), errors = summary.totalErrors, warnings = summary.totalWarnings,
            bytes = (long)summary.totalSize, unity = Application.unityVersion, source = Source, guid = AssetDatabase.AssetPathToGUID(Source)
        }, true));
        if (summary.result != BuildResult.Succeeded || summary.totalErrors != 0) throw new Exception("See BuildSummary.json");
    }
#endif

    [StructLayout(LayoutKind.Sequential)] struct LightRecord { public Vector3 position; public uint type; public Vector3 direction; public uint mask; }
    [Serializable] sealed class Result
    {
        public string api, device, platform, kernel = Kernel, hitWord, litWord, mirrorWord, error;
        public int hitPixels, unexpectedMaskPixels, nonzeroWithoutLights;
        public bool passed;
    }

    void Start()
    {
        var r = new Result { api = SystemInfo.graphicsDeviceType.ToString(), device = SystemInfo.graphicsDeviceName,
            platform = Application.platform.ToString() };
        string dir = Arg("-probeOut");
        try
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer || r.api != Arg("-probeApi"))
                throw new Exception("Expected WindowsPlayer on " + Arg("-probeApi") + "; got " + r.platform + "/" + r.api);
            if (!shader || !shader.HasKernel(Kernel) || !SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_UInt, FormatUsage.LoadStore)) throw new Exception("Missing kernel or GPU capability");
            Run(dir, r);
            r.passed = true;
        }
        catch (Exception ex) { r.error = ex.Message; Debug.LogException(ex); }
        finally
        {
            try { File.WriteAllText(Path.Combine(dir, "Probe.json"), JsonUtility.ToJson(r, true)); }
            finally { Application.Quit(r.passed ? 0 : 1); }
        }
    }

    static uint[] ReadMask(RenderTexture output)
    {
        var read = AsyncGPUReadback.Request(output, 0); // Raw R32_UInt, no format conversion.
        read.WaitForCompletion();
        if (read.hasError) throw new IOException("GPU readback failed");
        var data = read.GetData<uint>();
        if (data.Length != N * N) throw new IOException("Wrong readback size");
        return data.ToArray();
    }

    void Run(string dir, Result r)
    {
        // D3D reversed-Z orthographic fixture: z=-2 maps to .5. A nearer 4x8 blocker maps to .53125.
        // Ray points +X, spans 8 pixels. (14,12) sees the blocker, (22,12) and (14,19) cannot.
        var atlas = new Texture2D(N, N + N / 2, TextureFormat.RFloat, false, true);
        var depth = new float[atlas.width * atlas.height];
        for (int mip = 0; mip <= 1; ++mip)
            for (int y = 0; y < (N >> mip); ++y)
                for (int x = 0; x < (N >> mip); ++x)
                {
                    int xx = x << mip, yy = y << mip;
                    depth[(mip * N + y) * N + x] = xx >= 16 && xx < 20 && yy >= 8 && yy < 16 ? .53125f : .5f;
                }
        atlas.SetPixelData(depth, 0); atlas.Apply(false, true);
        var output = new RenderTexture(N, N, 0, GraphicsFormat.R32_UInt) { enableRandomWrite = true };
        if (!output.Create()) throw new Exception("R32_UInt UAV creation failed");
        if (Marshal.SizeOf(typeof(LightRecord)) != 32) throw new Exception("Light stride != 32");
        using (var lights = new ComputeBuffer(1, 32))
        {
            lights.SetData(new[] { new LightRecord { direction = Vector3.right, mask = Bit } });
            int k = shader.FindKernel(Kernel);
            shader.SetTexture(k, P + "DepthPyramid", atlas); shader.SetTexture(k, P + "Output", output);
            shader.SetBuffer(k, P + "LightBuffer", lights);
            shader.SetVector(P + "Mip0", new Vector4(0, 0, N, N));
            shader.SetVector(P + "Mip1", new Vector4(0, N, N / 2, N / 2));
            shader.SetVector(P + "ScreenSize", new Vector4(N, N, 1f / N, 1f / N));
            shader.SetVector(P + "TraceParams", new Vector4(1, 0, .2f, .15f));
            shader.SetVector(P + "DistanceParams", new Vector4(0, 50, 0, 5));
            shader.SetInt(P + "SampleCount", 10); shader.SetInt(P + "FrameIndex", 0); shader.SetInt(P + "TaaEnabled", 0);
            var proj = Matrix4x4.identity; proj.m22 = .25f; proj.m23 = 1;
            void SetMatrices(string key, Matrix4x4 m) => shader.SetMatrixArray(P + key, new[] { m, m });
            SetMatrices("View", Matrix4x4.identity); SetMatrices("Projection", proj);
            SetMatrices("ViewProjection", proj); SetMatrices("InvViewProjection", proj.inverse);
            shader.SetVectorArray(P + "CameraPosWS", new[] { Vector4.zero, Vector4.zero });
            shader.GetKernelThreadGroupSizes(k, out uint gx, out uint gy, out uint gz);
            if (gz != 1) throw new Exception("Expected a single view per group");
            void Dispatch(int count) { shader.SetInt(P + "LightCount", count); shader.Dispatch(k, (N + (int)gx - 1) / (int)gx, (N + (int)gy - 1) / (int)gy, 1); }
            Dispatch(1);
            var pixels = ReadMask(output);
            var colors = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; ++i)
            {
                uint mask = pixels[i] & 0xffffffu;
                if ((mask & Bit) != 0) ++r.hitPixels;
                if ((mask & ~Bit) != 0) ++r.unexpectedMaskPixels;
                colors[i] = new Color32((byte)(mask != 0 ? 255 : 0), (byte)(pixels[i] >> 24), 0, 255);
            }
            var image = new Texture2D(N, N, TextureFormat.RGBA32, false, true);
            image.SetPixels32(colors); image.Apply();
            File.WriteAllBytes(Path.Combine(dir, "MaskFade.png"), image.EncodeToPNG()); // R=mask present, G=fade byte.
            Destroy(image);
            uint hit = pixels[12*N+14], lit = pixels[12*N+22], mirror = pixels[19*N+14];
            r.hitWord = hit.ToString("X8"); r.litWord = lit.ToString("X8"); r.mirrorWord = mirror.ToString("X8");
            Dispatch(0); // Same UAV previously held hits: a skipped dispatch cannot satisfy this control.
            foreach (uint word in ReadMask(output)) if (word != 0) ++r.nonzeroWithoutLights;
            if (hit != (0xff000000u | Bit) || lit != 0 || mirror != 0 || r.unexpectedMaskPixels != 0 || r.nonzeroWithoutLights != 0)
                throw new Exception("Packed hit/lit/mirror or no-light control mismatch");
        }
        output.Release(); Destroy(output); Destroy(atlas);
    }
}
