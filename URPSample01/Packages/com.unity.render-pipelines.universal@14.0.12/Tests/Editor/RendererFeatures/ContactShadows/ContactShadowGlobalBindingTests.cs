using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using IDs = UnityEngine.Rendering.Universal.ScreenSpaceContactShadowsShaderIDs;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class ContactShadowGlobalBindingTests
    {
        const int Width = 80, Height = 48, Layer = 30;
        const float Opacity = 0.625f;
        const uint Stamp = 0xad000003u;
        static int MaskCount => UniversalRenderPipeline.maxVisibleAdditionalLights;

        // This material has no local contact properties. Snapshot exactly what an opaque (209)
        // or post-opaque (301) draw sees, not Shader.GetGlobal* after Camera.Render returns.
        const string ProbeSource = @"Shader ""Hidden/Tests/ContactShadowBindings"" { SubShader { Pass {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            Texture2D<uint> _ContactShadowTexture;
            uint _MainLightContactShadowMask, _ContactShadowLightCount;
            float _ContactShadowOpacity, _AdditionalLightsContactShadowMasks[MASK_COUNT];
            float4 Vert(uint id : SV_VertexID) : SV_POSITION {
                return float4((id == 1 ? 3 : -1), (id == 2 ? 3 : -1), 0, 1);
            }
            uint Frag(float4 position : SV_POSITION) : SV_Target {
                uint x = (uint)position.x, w, h;
                _ContactShadowTexture.GetDimensions(w, h);
                if (x == 0) return w;
                if (x == 1) return h;
                if (x == 2) return _ContactShadowTexture.Load(int3(0, 0, 0));
                if (x == 3) return _MainLightContactShadowMask;
                if (x == 4) return _ContactShadowLightCount;
                if (x == 5) return asuint(_ContactShadowOpacity);
                return (uint)_AdditionalLightsContactShadowMasks[x - 6];
            }
            ENDHLSL
        } Pass {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Vert(uint id : SV_VertexID) : SV_POSITION { return float4((id == 1 ? 3 : -1), (id == 2 ? 3 : -1), 0, 1); }
            uint Frag() : SV_Target { return 0xad000003u; }
            ENDHLSL
        } } }";

        class ProbeFeature : ScriptableRendererFeature
        {
            internal Camera camera;
            internal Light point;
            internal int pointIndex;
            internal Snapshot early, late;
            public override void Create() { }
            public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
            {
                if (data.cameraData.camera != camera) return;
                // Additional indices follow culling order with the main light removed, not
                // hierarchy order. Leave every other (unmarked or unused) array element zero.
                pointIndex = -1;
                for (int i = 0, additional = 0; i < data.lightData.visibleLights.Length; ++i)
                {
                    if (i == data.lightData.mainLightIndex) continue;
                    if (data.lightData.visibleLights[i].light == point) pointIndex = additional;
                    ++additional;
                }
                renderer.EnqueuePass(early);
                renderer.EnqueuePass(late);
            }
        }

        class Snapshot : ScriptableRenderPass
        {
            readonly Material m_Material;
            readonly RTHandle m_Output, m_Depth;
            readonly ScreenSpaceContactShadowsRendererFeature m_Contact;
            readonly RenderTexture m_Stamp;
            internal int draws;

            internal Snapshot(int evt, Material material, ScreenSpaceContactShadowsRendererFeature contact = null, RenderTexture stamp = null)
            {
                renderPassEvent = (RenderPassEvent)evt;
                m_Material = material;
                m_Contact = contact;
                m_Stamp = stamp;
                m_Output = RTHandles.Alloc(MaskCount + 6, 1, colorFormat: GraphicsFormat.R32_UInt, name: "T5 snapshot " + evt);
                m_Depth = RTHandles.Alloc(MaskCount + 6, 1, depthBufferBits: DepthBits.Depth16, name: "T5 snapshot depth");
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data) => ConfigureTarget(m_Output, m_Depth);

            public override void Execute(ScriptableRenderContext context, ref RenderingData data)
            {
                var cmd = data.commandBuffer;
                if (m_Contact && m_Contact.TryGetHistory(data.cameraData.camera, 0, out var history) && history.packedTexture?.rt != null)
                {
                    // Mark one texel on the actual history, without binding it to our material.
                    // Dimensions alone cannot distinguish the right texture from a stale history.
                    cmd.CopyTexture(m_Stamp, 0, 0, 0, 0, 1, 1, history.packedTexture.rt, 0, 0, 0, 0);
                }
                cmd.DrawProcedural(Matrix4x4.identity, m_Material, 0, MeshTopology.Triangles, 3);
                ++draws;
            }

            internal void Check(bool active, int additional)
            {
                var request = AsyncGPUReadback.Request(m_Output.rt, 0);
                request.WaitForCompletion();
                string at = $"event {(int)renderPassEvent}, active={active}";
                Assert.IsFalse(request.hasError, at);
                var words = request.GetData<uint>();
                Assert.AreEqual(MaskCount + 6, words.Length, at);
                Assert.AreEqual(active ? Width : 1, words[0], at + ": texture width");
                Assert.AreEqual(active ? Height : 1, words[1], at + ": texture height");
                Assert.AreEqual(active ? Stamp : 0u, words[2], at + ": packed texel");
                Assert.AreEqual(active ? 1u : 0u, words[3], at + ": main mask");
                Assert.AreEqual(active ? 2u : 0u, words[4], at + ": participating lights");
                Assert.AreEqual(math.asuint(active ? Opacity : 0f), words[5], at + ": opacity");
                for (int i = 0; i < MaskCount; ++i)
                    Assert.AreEqual(active && i == additional ? 2u : 0u, words[6 + i], at + $": additional[{i}]");
            }

            internal void Release()
            {
                m_Output.Release();
                m_Depth.Release();
            }
        }

        [Test]
        public void OpaqueDrawSeesThisCamerasBindings_PostOpaqueAndInactiveDrawsSeeNeutral()
        {
            if (!ScreenSpaceContactShadowsCapability.EvaluatePlatform().supported || !SystemInfo.supportsAsyncGPUReadback ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_UInt, FormatUsage.Render) ||
                (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) == 0)
                Assert.Ignore("Needs contact compute, R32_UInt render/readback and RT-to-RT copy support.");

            var previousTexture = Shader.GetGlobalTexture(IDs.texture);
            var previousMasks = Shader.GetGlobalFloatArray(IDs.additionalLightMasks);
            int previousMain = Shader.GetGlobalInteger(IDs.mainLightMask), previousCount = Shader.GetGlobalInteger(IDs.lightCount);
            float previousOpacity = Shader.GetGlobalFloat(IDs.opacity);
            var previousPipeline = QualitySettings.renderPipeline;
            var previousSun = RenderSettings.sun;
            var previousActive = RenderTexture.active;
            bool previousAsync = ShaderUtil.allowAsyncCompilation;
            var root = new GameObject("T5 binding fixture") { hideFlags = HideFlags.DontSave };
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            var contact = ScriptableObject.CreateInstance<ScreenSpaceContactShadowsRendererFeature>();
            var probe = ScriptableObject.CreateInstance<ProbeFeature>();
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            var stamp = new RenderTexture(1, 1, 0, GraphicsFormat.R32_UInt);
            Shader shader = null;
            Material material = null;
            UniversalRenderPipelineAsset pipeline = null;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                shader = ShaderUtil.CreateShaderAsset(ProbeSource.Replace("MASK_COUNT", MaskCount.ToString()), true);
                Assert.IsFalse(ShaderUtil.ShaderHasError(shader), "Binding probe did not compile");
                material = new Material(shader);
                Assert.IsTrue(stamp.Create());
                using (var initialize = new CommandBuffer())
                {
                    initialize.SetRenderTarget(stamp);
                    initialize.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3);
                    Graphics.ExecuteCommandBuffer(initialize);
                }
                probe.early = new Snapshot(209, material, contact, stamp);
                probe.late = new Snapshot(301, material);

                GameObject Child(string name)
                {
                    var go = new GameObject(name) { layer = Layer, hideFlags = HideFlags.DontSave };
                    go.transform.SetParent(root.transform, false);
                    return go;
                }
                var camera = Child("Camera").AddComponent<Camera>();
                probe.camera = camera;
                camera.enabled = false;
                camera.cullingMask = 1 << Layer;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.GetUniversalAdditionalCameraData().volumeLayerMask = 1 << Layer;
                camera.GetUniversalAdditionalCameraData().allowXRRendering = false;
                Assert.IsTrue(target.Create());
                camera.targetTexture = target;
                var sun = Child("Marked sun").AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.gameObject.AddComponent<ContactShadowLight>();
                RenderSettings.sun = sun;
                probe.point = Child("Marked point").AddComponent<Light>();
                probe.point.type = LightType.Point;
                probe.point.transform.localPosition = Vector3.forward * 4;
                probe.point.gameObject.AddComponent<ContactShadowLight>();
                var receiver = Child("Opaque receiver");
                receiver.transform.localPosition = Vector3.forward * 5;
                receiver.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                receiver.AddComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                    UniversalRenderPipelineAsset.packagePath + "/Runtime/Materials/Lit.mat");
                var volume = Child("Volume").AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = float.MaxValue;
                volume.sharedProfile = profile;
                var settings = profile.Add<ContactShadowsVolume>(true);
                settings.enabled.Override(true);
                settings.opacity.Override(Opacity);

                ResourceReloader.ReloadAllNullIn(renderer, UniversalRenderPipelineAsset.packagePath);
                using (var so = new SerializedObject(contact)) ScreenSpaceContactShadowsEditor.AssignDefaults(so);
                renderer.rendererFeatures.Add(contact);
                renderer.rendererFeatures.Add(probe);
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                pipeline.msaaSampleCount = 1;
                pipeline.renderScale = 1;
                QualitySettings.renderPipeline = pipeline;

                void RenderAndCheck(bool active)
                {
                    int dispatches = ScreenSpaceContactShadowsPass.editorDispatchCount;
                    int early = probe.early.draws, late = probe.late.draws;
                    camera.Render();
                    Assert.AreEqual(active ? 1 : 0, ScreenSpaceContactShadowsPass.editorDispatchCount - dispatches, "Contact dispatch delta");
                    Assert.AreEqual(early + 1, probe.early.draws);
                    Assert.AreEqual(late + 1, probe.late.draws);
                    Assert.That(probe.pointIndex, Is.InRange(0, MaskCount - 1), "Fixture point not in visible additional lights");
                    probe.early.Check(active, probe.pointIndex);
                    probe.late.Check(false, probe.pointIndex);
                }
                RenderAndCheck(true);
                settings.enabled.Override(false);
                RenderAndCheck(false);
                settings.enabled.Override(true);
                using (var so = new SerializedObject(contact))
                {
                    so.FindProperty("m_Resources.contactShadowsCompute").objectReferenceValue = null;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, "Screen Space Contact Shadows requires both a depth pyramid compute shader and a contact shadows compute shader.");
                RenderAndCheck(false);
                RenderAndCheck(false);
                UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                QualitySettings.renderPipeline = previousPipeline;
                RenderSettings.sun = previousSun;
                ShaderUtil.allowAsyncCompilation = previousAsync;
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(pipeline);
                contact.Dispose();
                probe.early?.Release();
                probe.late?.Release();
                Object.DestroyImmediate(probe);
                Object.DestroyImmediate(contact);
                Object.DestroyImmediate(renderer);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(stamp);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(shader);
                foreach (var component in profile.components) Object.DestroyImmediate(component);
                Object.DestroyImmediate(profile);
                Shader.SetGlobalTexture(IDs.texture, previousTexture ? previousTexture : null);
                Shader.SetGlobalInteger(IDs.mainLightMask, previousMain);
                Shader.SetGlobalInteger(IDs.lightCount, previousCount);
                Shader.SetGlobalFloat(IDs.opacity, previousOpacity);
                Shader.SetGlobalFloatArray(IDs.additionalLightMasks,
                    previousMasks != null && previousMasks.Length != 0 ? previousMasks : new float[MaskCount]);
            }
        }
    }
}
