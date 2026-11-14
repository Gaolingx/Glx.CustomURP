using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

public static class ScreenSpaceLightingTestRunner
{
    internal const double TimeoutSeconds = 1800.0;

    public static void RunEditModeTests()
    {
        var api = ScriptableObject.CreateInstance<TestRunnerApi>();
        api.RegisterCallbacks(new Callbacks());
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
                "UnityEditor.Rendering.Universal.Tests.GroundTruthAmbientOcclusionRendererFeatureTests",
                "UnityEditor.Rendering.Universal.Tests.GroundTruthAmbientOcclusionHistoryTests",
                "UnityEditor.Rendering.Universal.Tests.GroundTruthAmbientOcclusionCBufferTests"
            }
        }));
    }

    sealed class Callbacks : ICallbacks
    {
        double m_Started;
        bool m_Finished;

        public void RunStarted(ITestAdaptor testsToRun)
        {
            m_Started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Watchdog;
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            m_Finished = true;
            EditorApplication.update -= Watchdog;
            Debug.Log($"EditMode tests finished: status={result.TestStatus} passed={result.PassCount} failed={result.FailCount} skipped={result.SkipCount}");
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
            Debug.LogError("EditMode test run timed out.");
            EditorApplication.update -= Watchdog;
            EditorApplication.Exit(4);
        }
    }
}
