using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    internal static class ScreenSpaceTestUtils
    {
        internal static Camera CreateCamera(string name, CameraType cameraType = CameraType.Game)
        {
            var gameObject = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            var camera = gameObject.AddComponent<Camera>();
            camera.cameraType = cameraType;
            return camera;
        }

        internal static void DestroyCamera(Camera camera)
        {
            if (camera != null)
                Object.DestroyImmediate(camera.gameObject);
        }

        internal static CameraData CreateCameraData(
            Camera camera,
            CameraType cameraType = CameraType.Game,
            CameraRenderType renderType = CameraRenderType.Base,
            int width = 320,
            int height = 180)
        {
            var cameraData = new CameraData
            {
                camera = camera,
                cameraType = cameraType,
                renderType = renderType,
                cameraTargetDescriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 24),
                xr = new XRPass(),
            };
            // URP initializes these from the camera; tests that move the camera between renders need
            // the same matrices instead of CameraData's default identity.
            if (camera != null)
                cameraData.SetViewAndProjectionMatrix(camera.worldToCameraMatrix, camera.projectionMatrix);
            return cameraData;
        }

        internal static RenderingData CreateRenderingData(CameraData cameraData)
        {
            return new RenderingData { cameraData = cameraData };
        }

        internal sealed class TestRenderer : ScriptableRenderer
        {
            internal TestRenderer(UniversalRendererData data) : base(data)
            {
            }

            internal List<ScriptableRenderPass> queuedPasses => activeRenderPassQueue;

            public override void Setup(ScriptableRenderContext context, ref RenderingData renderingData)
            {
            }
        }
    }
}
