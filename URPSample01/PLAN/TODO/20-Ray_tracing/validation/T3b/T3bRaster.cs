using System;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

public static class T3bRaster
{
    const string ResourceName = "T3bRasterOracle";
    const string ShaderAsset = "Assets/T3bRaster.shader";
    static readonly string[] Keywords = { "_NORMALMAP", "_METALLICSPECGLOSSMAP", "_EMISSION", "_ALPHATEST_ON" };

#if UNITY_EDITOR
    // Called by T3bPlayer.Build in the generated scratch project before BuildPlayer.
    public static void PrepareBuild()
    {
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderAsset);
        if (!shader) throw new InvalidOperationException("Missing " + ShaderAsset);
        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            AssetDatabase.CreateFolder("Assets", "Resources");
        const string path = "Assets/Resources/" + ResourceName + ".mat";
        Material reference = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!reference) AssetDatabase.CreateAsset(new Material(shader), path);
        else if (reference.shader != shader)
        {
            reference.shader = shader;
            EditorUtility.SetDirty(reference);
        }
        AssetDatabase.SaveAssets();
    }
#endif

    public static Vector4 Sample(MeshRenderer renderer, Vector3 rayOrigin, int mode)
    {
        var material = renderer.sharedMaterial;
        if (mode < 16 || mode > 19) throw new ArgumentOutOfRangeException(nameof(mode));
        Material reference = Resources.Load<Material>(ResourceName);
        Shader shader = reference ? reference.shader : null;
#if UNITY_EDITOR
        if (!shader) shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderAsset);
#endif
        if (!shader || !shader.isSupported) throw new InvalidOperationException("T3b raster shader missing or unsupported");
        if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
            throw new NotSupportedException("T3b raster requires floating-point render and readback textures");

        // Odd viewport: the center pixel's sample lies exactly at NDC (0,0),
        // including when the render-texture projection flips Y. One pixel spans .001 world units.
        const int size = 33;
        const float halfWidth = size * .0005f;
        var camera = new GameObject("Lit surface reference").AddComponent<Camera>();
        camera.enabled = false;
        camera.transform.position = rayOrigin;
        camera.orthographic = true;
        camera.orthographicSize = halfWidth;
        camera.aspect = 1;
        camera.nearClipPlane = .001f;
        camera.farClipPlane = 1000;
        camera.cullingMask = 1 << 30;
        camera.allowMSAA = false;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        var sourceLayer = renderer.gameObject.layer;
        RenderTexture previous = RenderTexture.active;
        Material draw = null;
        Texture2D pixel = null;
        RenderTexture target = null;
        try
        {
            draw = new Material(shader);
            draw.CopyPropertiesFromMaterial(material);
            foreach (string keyword in Keywords)
            {
                if (material.IsKeywordEnabled(keyword)) draw.EnableKeyword(keyword);
                else draw.DisableKeyword(keyword);
            }
            draw.SetInteger("_T3bMode", mode);
            target = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, 1);
            renderer.gameObject.layer = 30;
            renderer.sharedMaterial = draw;
            camera.targetTexture = target;
            camera.Render();

            RenderTexture.active = target;
            pixel = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            pixel.ReadPixels(new Rect(size / 2, size / 2, 1, 1), 0, 0, false);
            return (Vector4)pixel.GetPixel(0, 0);
        }
        finally
        {
            RenderTexture.active = previous;
            camera.targetTexture = null;
            renderer.sharedMaterial = material;
            renderer.gameObject.layer = sourceLayer;
            Release(camera.gameObject);
            if (target) RenderTexture.ReleaseTemporary(target);
            Release(pixel);
            Release(draw);
        }
    }

    static void Release(UnityEngine.Object obj)
    {
        if (!obj) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
        else UnityEngine.Object.DestroyImmediate(obj);
    }
}
