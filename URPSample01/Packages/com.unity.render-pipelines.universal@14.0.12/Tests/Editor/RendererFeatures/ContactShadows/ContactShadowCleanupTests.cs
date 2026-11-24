using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using IDs = UnityEngine.Rendering.Universal.ScreenSpaceContactShadowsShaderIDs;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class ContactShadowCleanupTests
    {
        static int MaskCount => UniversalRenderPipeline.maxVisibleAdditionalLights;

        static void RequireGpu()
        {
            if (!ScreenSpaceContactShadowsCapability.EvaluatePlatform().supported || !SystemInfo.supportsAsyncGPUReadback ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_UInt, FormatUsage.Render))
                Assert.Ignore("Requires contact shadows, R32_UInt rendering and GPU readback.");
        }

        static void CheckNeutral(RenderTexture rt, bool array)
        {
            Assert.IsTrue(rt && rt.IsCreated(), "No live neutral texture");
            Assert.AreEqual(1, rt.width);
            Assert.AreEqual(1, rt.height);
            Assert.AreEqual(GraphicsFormat.R32_UInt, rt.graphicsFormat);
            Assert.AreEqual(array ? TextureDimension.Tex2DArray : TextureDimension.Tex2D, rt.dimension);
            Assert.AreEqual(array ? 2 : 1, rt.volumeDepth);
            var read = AsyncGPUReadback.Request(rt, 0);
            read.WaitForCompletion();
            Assert.IsFalse(read.hasError);
            for (int slice = 0; slice < rt.volumeDepth; ++slice)
                Assert.AreEqual(0u, read.GetData<uint>(slice)[0], $"Neutral packed texel, slice {slice}");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralTextureIsReusedRecoveredAfterReleaseAndDestroyedOnDispose(bool array)
        {
            RequireGpu();
            if (array && !SystemInfo.supports2DArrayTextures) Assert.Ignore("Requires texture arrays.");
            var cleanup = new ScreenSpaceContactShadowsCleanupPass(MaskCount);
            Assert.AreEqual(RenderPassEvent.AfterRenderingOpaques, cleanup.renderPassEvent);
            // Exercise the XR layout without requiring a headset or changing the runtime API.
            var get = typeof(ScreenSpaceContactShadowsCleanupPass).GetMethod("GetNeutral", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(get);
            RenderTexture Get() => (RenderTexture)get.Invoke(cleanup, new object[] { array });
            var previous = RenderTexture.active;
            try
            {
                var neutral = Get();
                CheckNeutral(neutral, array);
                Assert.AreSame(neutral, Get());
                neutral.Release();
                CheckNeutral(Get(), array);
                Assert.IsTrue(neutral.IsCreated(), "Released backing store was not recovered");
                cleanup.Dispose();
                Assert.IsTrue(neutral == null, "Cleanup retained the native texture");
                cleanup.Dispose();
            }
            finally
            {
                cleanup.Dispose();
                RenderTexture.active = previous;
            }
        }

        [Test]
        public void MissingComputeOnActiveCameraClearsDirtyBindings_InactiveCameraInheritsWithoutWriting()
        {
            RequireGpu();
            const int layer = 30;
            var quality = QualitySettings.renderPipeline;
            var oldSun = RenderSettings.sun;
            var oldActive = RenderTexture.active;
            var texture = Shader.GetGlobalTexture(IDs.texture);
            var masks = Shader.GetGlobalFloatArray(IDs.additionalLightMasks);
            var main = Shader.GetGlobalInteger(IDs.mainLightMask);
            var count = Shader.GetGlobalInteger(IDs.lightCount);
            var opacity = Shader.GetGlobalFloat(IDs.opacity);
            bool keyword = Shader.IsKeywordEnabled(IDs.deferredKeyword);
            var root = new GameObject("T6 cleanup") { hideFlags = HideFlags.HideAndDontSave, layer = layer };
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            var contact = ScriptableObject.CreateInstance<ScreenSpaceContactShadowsRendererFeature>();
            UniversalRenderPipelineAsset pipeline = null;
            var target = new RenderTexture(64, 48, 24);
            var dirty = new float[MaskCount];
            for (int i = 0; i < dirty.Length; ++i) dirty[i] = 7;
            void Seed(RenderTexture stale)
            {
                Shader.SetGlobalTexture(IDs.texture, stale);
                Shader.SetGlobalFloatArray(IDs.additionalLightMasks, dirty);
                Shader.SetGlobalInteger(IDs.mainLightMask, 1);
                Shader.SetGlobalInteger(IDs.lightCount, 3);
                Shader.SetGlobalFloat(IDs.opacity, 0.75f);
                Shader.EnableKeyword(IDs.deferredKeyword);
            }
            Camera CameraFor(string name, int volumeMask)
            {
                var camera = ScreenSpaceTestUtils.CreateCamera(name);
                camera.transform.SetParent(root.transform, false);
                camera.enabled = false;
                camera.cullingMask = 1 << layer;
                camera.targetTexture = target;
                var data = camera.GetUniversalAdditionalCameraData();
                data.volumeLayerMask = volumeMask;
                data.allowXRRendering = false;
                return camera;
            }
            void CheckBindings()
            {
                Assert.AreEqual(0, Shader.GetGlobalInteger(IDs.mainLightMask));
                Assert.AreEqual(0, Shader.GetGlobalInteger(IDs.lightCount));
                Assert.AreEqual(0f, Shader.GetGlobalFloat(IDs.opacity));
                CollectionAssert.AreEqual(new float[MaskCount], Shader.GetGlobalFloatArray(IDs.additionalLightMasks));
                Assert.IsFalse(Shader.IsKeywordEnabled(IDs.deferredKeyword));
            }
            try
            {
                Assert.IsTrue(target.Create());
                var active = CameraFor("Active", 1 << layer);
                var inactive = CameraFor("Inactive", 0);
                var sun = root.AddComponent<Light>();
                sun.type = LightType.Directional;
                root.AddComponent<ContactShadowLight>();
                RenderSettings.sun = sun;
                var volume = root.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = float.MaxValue;
                volume.sharedProfile = profile;
                profile.Add<ContactShadowsVolume>(true).enabled.Override(true);
                ResourceReloader.ReloadAllNullIn(renderer, UniversalRenderPipelineAsset.packagePath);
                using (var so = new SerializedObject(contact))
                {
                    ScreenSpaceContactShadowsEditor.AssignDefaults(so);
                    so.Update();
                    Assert.IsNotNull(so.FindProperty("m_Resources.depthPyramidCompute").objectReferenceValue);
                    Assert.IsNotNull(so.FindProperty("m_Resources.contactShadowsCompute").objectReferenceValue);
                }
                renderer.rendererFeatures.Add(contact);
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                QualitySettings.renderPipeline = pipeline;
                int dispatches = ScreenSpaceContactShadowsPass.editorDispatchCount;
                active.Render();
                Assert.AreEqual(++dispatches, ScreenSpaceContactShadowsPass.editorDispatchCount, "Active camera did not dispatch");
                var firstNeutral = Shader.GetGlobalTexture(IDs.texture) as RenderTexture;
                CheckNeutral(firstNeutral, false);
                CheckBindings();
                inactive.Render();
                Assert.AreEqual(dispatches, ScreenSpaceContactShadowsPass.editorDispatchCount, "Inactive camera dispatched");
                Assert.AreSame(firstNeutral, Shader.GetGlobalTexture(IDs.texture));
                CheckBindings();

                using (var so = new SerializedObject(contact))
                {
                    so.FindProperty("m_Resources.contactShadowsCompute").objectReferenceValue = null;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                Assert.IsTrue(contact.TryGetHistory(active, 0, out var history));
                var stale = history.packedTexture.rt;
                Seed(stale);
                LogAssert.Expect(LogType.Warning, "Screen Space Contact Shadows requires both a depth pyramid compute shader and a contact shadows compute shader.");
                active.Render();
                var neutral = Shader.GetGlobalTexture(IDs.texture) as RenderTexture;
                CheckNeutral(neutral, false);
                CheckBindings();
                inactive.Render();
                Assert.AreSame(neutral, Shader.GetGlobalTexture(IDs.texture));
                CheckBindings();
                // Negative control: the second camera must not silently repair stale state itself.
                Seed(stale);
                inactive.Render();
                Assert.AreSame(stale, Shader.GetGlobalTexture(IDs.texture));
                Assert.AreEqual(3, Shader.GetGlobalInteger(IDs.lightCount));
                Assert.AreEqual(dispatches, ScreenSpaceContactShadowsPass.editorDispatchCount);
            }
            finally
            {
                QualitySettings.renderPipeline = quality;
                RenderSettings.sun = oldSun;
                RenderTexture.active = oldActive;
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(pipeline);
                contact.Dispose();
                Object.DestroyImmediate(contact);
                Object.DestroyImmediate(renderer);
                Object.DestroyImmediate(target);
                foreach (var component in profile.components) Object.DestroyImmediate(component);
                Object.DestroyImmediate(profile);
                Shader.SetGlobalTexture(IDs.texture, texture ? texture : null);
                Shader.SetGlobalInteger(IDs.mainLightMask, main);
                Shader.SetGlobalInteger(IDs.lightCount, count);
                Shader.SetGlobalFloat(IDs.opacity, opacity);
                Shader.SetGlobalFloatArray(IDs.additionalLightMasks, masks != null && masks.Length > 0 ? masks : new float[MaskCount]);
                if (keyword) Shader.EnableKeyword(IDs.deferredKeyword); else Shader.DisableKeyword(IDs.deferredKeyword);
            }
        }
    }
}
