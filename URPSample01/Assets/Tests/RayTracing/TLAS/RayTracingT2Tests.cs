using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class RayTracingT2Tests
    {
        [Test]
        public void ManualSceneKnownVectors()
        {
            const string path = "Assets/Tests/RayTracing/TLAS/T2Fixture";
            T2Validation.T2VectorSuite.Run(AssetDatabase.LoadAssetAtPath<RayTracingShader>(path+".raytrace"),
                AssetDatabase.LoadAssetAtPath<Shader>(path+".shader"), SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11,
                s => TestContext.Out.WriteLine(s));
        }
    }
}
