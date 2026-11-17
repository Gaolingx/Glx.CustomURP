using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

public static class ScreenSpaceLightingTestRunner
{
    internal const double TimeoutSeconds = 1800.0;

    // T14 release regression: the PlayMode suite drives the real player loop for all three renderers.
    public static void RunPlayModeTests()
    {
        var api = ScriptableObject.CreateInstance<TestRunnerApi>();
        api.RegisterCallbacks(new Callbacks("PlayMode"));
        api.Execute(new ExecutionSettings(new Filter
        {
            testMode = TestMode.PlayMode,
            assemblyNames = new[] { "Unity.ScreenSpaceLighting.Tests.PlayMode" },
        }));
    }

    public static void RunEditModeTests()
    {
        var api = ScriptableObject.CreateInstance<TestRunnerApi>();
        api.RegisterCallbacks(new Callbacks("EditMode"));
        api.Execute(new ExecutionSettings(new Filter
        {
            testMode = TestMode.EditMode,
            assemblyNames = new[] { "Unity.RenderPipelines.Universal.Editor.Tests" },
            testNames = new[]
            {
                "UnityEditor.Rendering.Universal.Tests.UniversalRendererPassInputTests",
                "UnityEditor.Rendering.Universal.Tests.CameraHistoryStoreTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceCapabilityTests",
                "UnityEditor.Rendering.Universal.Tests.PluginIsolationTests",
                "UnityEditor.Rendering.Universal.Tests.PackedMipChainInfoTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingVolumeTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingRendererFeatureTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingHistoryTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingSsgiTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingSsrTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingSsrPbrTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingSsrDenoiserParamsTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingSsrDebugViewTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingTransparentSsrTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingRefractionTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingRefractionImproveTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingT12SchedulingTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingMaterialTests",
                "UnityEditor.Rendering.Universal.Tests.ScreenSpaceLightingProbeAtlasTests",
                "UnityEditor.Rendering.Universal.Tests.GroundTruthAmbientOcclusionRendererFeatureTests",
                "UnityEditor.Rendering.Universal.Tests.GroundTruthAmbientOcclusionHistoryTests",
                "UnityEditor.Rendering.Universal.Tests.GroundTruthAmbientOcclusionCBufferTests",
                "UnityEditor.Rendering.Universal.Tests.GroundTruthAmbientOcclusionFullResolutionTests",
                "UnityEditor.Rendering.Universal.Tests.GroundTruthAmbientOcclusionOrthographicTests"
            }
        }));
    }

    sealed class Callbacks : ICallbacks
    {
        double m_Started;
        bool m_Finished;
        readonly string m_Mode;

        public Callbacks(string mode)
        {
            m_Mode = mode;
        }

        public void RunStarted(ITestAdaptor testsToRun)
        {
            m_Started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Watchdog;
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            m_Finished = true;
            EditorApplication.update -= Watchdog;
            Debug.Log($"{m_Mode} tests finished: status={result.TestStatus} passed={result.PassCount} failed={result.FailCount} skipped={result.SkipCount}");
            EditorApplication.Exit(result.TestStatus == TestStatus.Passed ? 0 : 1);
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (result.TestStatus == TestStatus.Failed)
                Debug.LogError($"FAILED {result.Name}: {result.Message}");
        }

        void Watchdog()
        {
            if (m_Finished || EditorApplication.timeSinceStartup - m_Started < TimeoutSeconds)
                return;
            Debug.LogError($"{m_Mode} test run timed out.");
            EditorApplication.update -= Watchdog;
            EditorApplication.Exit(4);
        }
    }
}
