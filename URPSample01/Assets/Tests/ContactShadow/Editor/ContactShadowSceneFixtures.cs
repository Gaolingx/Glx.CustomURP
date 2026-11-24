using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace ContactShadowTests.Editor
{
    // Batch entry: -executeMethod ContactShadowTests.Editor.ContactShadowSceneFixtures.Generate
    public static class ContactShadowSceneFixtures
    {
        const string Root = "Assets/Tests/ContactShadow/";
        const int W = 256, H = 192;
        const float GroundDepth = 5, BlockerDepth = 4.84f;
        static readonly Quaternion Down = Quaternion.Euler(90, 0, 0);
        static readonly string[] Surfaces = { "ReceiverOff", "Transparent", "BakedLit", "Custom" };

        // 256x192 bottom-left viewport pixels. Rotate T3's +Z depth axis onto world -Y
        // so the receiver is a horizontal floor and the thin boxes are above it.
        static Vector3 World(float x, float y, float depth)
        {
            float unit = 2 * Mathf.Tan(25 * Mathf.Deg2Rad) * depth / H;
            return new Vector3(0, GroundDepth, 0) + Down * new Vector3((x - W / 2) * unit, (y - H / 2) * unit, depth);
        }

        public static void Generate()
        {
            foreach (string dir in new[] { "Scenes", "Profiles" })
                if (!AssetDatabase.IsValidFolder(Root + dir)) AssetDatabase.CreateFolder(Root.TrimEnd('/'), dir);
            foreach (string scene in new[] { "Baseline", "Stress" })
                if (SceneManager.GetSceneByPath(PathFor(scene)).isLoaded)
                    throw new InvalidOperationException("Close the fixture before regenerating: " + scene);

            var lit = MaterialAsset("Lit", "Universal Render Pipeline/Lit", Color.gray);
            var panels = new[] {
                MaterialAsset(Surfaces[0], "Universal Render Pipeline/Lit", new Color(.85f,.45f,.2f)),
                MaterialAsset(Surfaces[1], "Universal Render Pipeline/Lit", new Color(.3f,.8f,.9f,.72f)),
                MaterialAsset(Surfaces[2], "Universal Render Pipeline/Baked Lit", new Color(.55f,.85f,.35f)),
                MaterialAsset(Surfaces[3], "ContactShadowTests/DirectMainLight", new Color(.8f,.35f,.7f)) };
            var profile = Profile();
            foreach (string scene in new[] { "Baseline", "Stress" })
                Build(scene, profile, lit, panels);
        }

        static string PathFor(string name) => Root + "Scenes/ContactShadow" + name + ".unity";

        static Material MaterialAsset(string name, string shaderName, Color color)
        {
            string path = Root + "Materials/" + name + ".mat";
            var shader = Shader.Find(shaderName);
            if (!shader || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException(shaderName);
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0);
            if (shaderName == "Universal Render Pipeline/Lit")
            {
                m.SetFloat("_ReceiveShadows", name == "ReceiverOff" ? 0 : 1);
                m.SetFloat("_Surface", name == "Transparent" ? 1 : 0);
                BaseShaderGUI.SetMaterialKeywords(m);
            }
            EditorUtility.SetDirty(m);
            AssetDatabase.SaveAssetIfDirty(m);
            return m;
        }

        static VolumeProfile Profile()
        {
            string path = Root + "Profiles/ContactShadowVolumeProfile.asset";
            var p = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (!p) { p = ScriptableObject.CreateInstance<VolumeProfile>(); AssetDatabase.CreateAsset(p, path); }
            if (!p.TryGet(out ContactShadowsVolume v))
            {
                v = p.Add<ContactShadowsVolume>(true);
                v.name = nameof(ContactShadowsVolume);
                AssetDatabase.AddObjectToAsset(v, p);
            }
            v.enabled.Override(true);
            v.length.Override(.64f);
            EditorUtility.SetDirty(v);
            EditorUtility.SetDirty(p);
            AssetDatabase.SaveAssetIfDirty(p);
            return p;
        }

        static void Build(string name, VolumeProfile profile, Material lit, Material[] panels)
        {
            var previous = SceneManager.GetActiveScene();
            // Unity batchmode starts in a clean untitled scene; it forbids opening an
            // additive scene alongside that placeholder. Never replace an author's scene.
            bool placeholder = Application.isBatchMode && !previous.isDirty &&
                string.IsNullOrEmpty(previous.path) && SceneManager.sceneCount == 1;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                placeholder ? NewSceneMode.Single : NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            try
            {
                RenderSettings.skybox = null;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(.22f, .22f, .22f);
                RenderSettings.reflectionIntensity = 0;
                RenderSettings.fog = false;
                CameraObject("ContactCamera").tag = "MainCamera";
                var volume = new GameObject("ContactVolume").AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 1000;
                volume.sharedProfile = profile;
                var ground = Slab("Ground", -6, -6, W + 12, H + 12, GroundDepth, lit);
                // A real caster keeps Deferred punctual shadow variants eligible. Only the
                // ground casts: nothing between the light and the sampled top surface casts.
                ground.shadowCastingMode = ShadowCastingMode.On;
                var main = MakeLight("Main", LightType.Directional, World(128, 96, 1), Down * new Vector3(-1, 0, .25f), 2, 0);
                RenderSettings.sun = main;
                if (name == "Baseline")
                {
                    CameraObject("ContactCameraOrtho").orthographic = true;
                    CameraObject("ContactCameraMSAA").allowMSAA = true;
                    CameraObject("ContactCameraDynamic").allowDynamicResolution = true;
                    var target = World(145, 130, GroundDepth);
                    var source = target + Down * new Vector3(3.2f, 0, -.85f);
                    MakeLight("Point", LightType.Point, source, target - source, 16, 9).shadows = LightShadows.Hard;
                    MakeLight("Spot", LightType.Spot, source + Vector3.forward * .25f, target - source - Vector3.forward * .25f, 16, 9).shadows = LightShadows.Hard;
                    Slab("Blocker", 145, 121, 10, 18, BlockerDepth, lit);
                    for (int i = 0; i < panels.Length; i++)
                    {
                        int x = 12 + 60 * i;
                        // Transparent must not blend an underlying contact-darkened Lit floor.
                        if (i == 1) Slab("TransparentBacking", x, 44, 48, 30, 4.99f, panels[2]);
                        Slab(Surfaces[i], x, 44, 48, 30, 4.97f, panels[i]);
                        Slab(Surfaces[i] + "Blocker", x + 31, 52, 8, 14, 4.81f, lit);
                    }
                }
                else
                {
                    // Center/ROI for punctual index i=1..24. Toggle Stress24.enabled
                    // (Light, not GameObject) for 24 vs 25; name order is NOT URP slot order.
                    for (int i = 1; i <= 24; ++i)
                    {
                        int x = 23 + (i - 1) % 6 * 42, y = 30 + (i - 1) / 6 * 43;
                        var center = World(x, y, GroundDepth);
                        var pos = center + Down * new Vector3(.38f, 0, -.16f);
                        MakeLight(i == 1 ? "Point" : i == 2 ? "Spot" : $"Stress{i:D2}",
                            i == 2 ? LightType.Spot : LightType.Point, pos, center - pos, .65f, .75f);
                        // Separate renderers give each cell its own Forward per-object light list.
                        Slab($"Receiver{i:D2}", x - 15, y - 15, 30, 30, 4.98f, lit);
                        Slab($"Blocker{i:D2}", x + 5, y - 6, 5, 12, BlockerDepth, lit);
                    }
                }
                if (!EditorSceneManager.SaveScene(scene, PathFor(name))) throw new InvalidOperationException(name);
            }
            finally
            {
                if (placeholder) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                else
                {
                    SceneManager.SetActiveScene(previous);
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        static Camera CameraObject(string name)
        {
            var c = new GameObject(name).AddComponent<Camera>();
            c.enabled = name == "ContactCamera";
            c.transform.SetPositionAndRotation(World(128, 96, 0), Down);
            c.fieldOfView = 50;
            c.orthographicSize = GroundDepth * Mathf.Tan(25 * Mathf.Deg2Rad);
            c.nearClipPlane = 1;
            c.farClipPlane = 15;
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = Color.black;
            c.allowHDR = c.allowMSAA = false;
            var data = c.GetUniversalAdditionalCameraData();
            data.allowXRRendering = data.renderPostProcessing = false;
            return c;
        }

        static Light MakeLight(string name, LightType type, Vector3 pos, Vector3 direction, float intensity, float range)
        {
            var l = new GameObject(name).AddComponent<Light>();
            l.type = type;
            l.lightmapBakeType = LightmapBakeType.Realtime;
            l.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(direction));
            l.intensity = intensity;
            if (type != LightType.Directional) l.range = range;
            if (type == LightType.Spot) { l.spotAngle = 115; l.innerSpotAngle = 85; }
            l.gameObject.AddComponent<ContactShadowLight>();
            return l;
        }

        static MeshRenderer Slab(string name, int x, int y, int w, int h, float depth, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(World(x + w * .5f, y + h * .5f, depth) + Down * Vector3.forward * .002f, Down);
            float unit = 2 * Mathf.Tan(25 * Mathf.Deg2Rad) * depth / H;
            go.transform.localScale = new Vector3(w * unit, h * unit, .004f);
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            return r;
        }
    }
}
