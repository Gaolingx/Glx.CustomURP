using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
#endif

// Shared XR bring-up for the T13 verification.
//
// The project has no HMD, so the Mock HMD XR Plugin supplies a stereo display subsystem. The plugin
// and XR Plugin Management are test dependencies: every API is accessed through reflection so that
// removing the packages only disables the XR cases instead of breaking compilation.
//
// XR Plugin Management stores its settings as assets under Assets/XR. The manager is created with
// "Initialize XR on Startup" disabled on purpose: only the T13 verification starts the display, so
// every other verification keeps rendering the mono path.
//
// Mock HMD only creates XR render passes in play mode, so the display is started from a play-mode
// test; the editor-only half below creates the settings assets and the per-eye mode configuration.
public static class ScreenSpaceLightingT13XrEnvironment
{
    public const string LoaderTypeName = "Unity.XR.MockHMD.MockHMDLoader";
    public const string MockHmdSettingsKey = "xr.sdk.mock-hmd.settings";
    public const string SettingsFolder = "Assets/XR";
    public const string MockHmdSettingsPath = "Assets/XR/Settings/MockHmdBuildSettings.asset";

    public enum StereoMode
    {
        MultiPass = 0,
        SinglePassInstanced = 1,
    }

    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    static bool s_XrPackageAvailableChecked;
    static bool s_XrPackageAvailable;

    public static bool xrPackageAvailable
    {
        get
        {
            if (!s_XrPackageAvailableChecked)
            {
                s_XrPackageAvailable = Type.GetType("Unity.XR.MockHMD.MockHMDLoader, Unity.XR.MockHMD") != null &&
                                       Type.GetType("UnityEngine.XR.Management.XRManagerSettings, Unity.XR.Management") != null;
                s_XrPackageAvailableChecked = true;
            }

            return s_XrPackageAvailable;
        }
    }

    public static bool IsDisplayRunning()
    {
        var displays = new List<XRDisplaySubsystem>();
        SubsystemManager.GetInstances(displays);
        return displays.Any(d => d != null && d.running);
    }

    public static XRDisplaySubsystem GetDisplay()
    {
        var displays = new List<XRDisplaySubsystem>();
        SubsystemManager.GetInstances(displays);
        return displays.FirstOrDefault(d => d != null && d.running);
    }

    public static string DescribeEnvironment()
    {
        var display = GetDisplay();
        if (display == null)
            return "no running XR display";

        return $"display='{display.subsystemDescriptor.id}' running={display.running} " +
               $"textureLayout={display.textureLayout}";
    }

    static object GetManager()
    {
        var settingsType = Type.GetType("UnityEngine.XR.Management.XRGeneralSettings, Unity.XR.Management");
        var instance = settingsType?.GetProperty("Instance", StaticFlags)?.GetValue(null);
        return instance?.GetType().GetProperty("Manager", InstanceFlags)?.GetValue(instance);
    }

#if UNITY_EDITOR
    // Creates Assets/XR settings with the Mock HMD loader assigned and startup initialization off.
    // Returns false when the XR packages are not installed.
    public static bool EnsureAssets()
    {
        if (!xrPackageAvailable)
            return false;

        Directory.CreateDirectory(SettingsFolder + "/Settings");
        var generalSettingsType = Type.GetType(
            "UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget, Unity.XR.Management.Editor");
        if (generalSettingsType == null)
            return false;

        var perTarget = generalSettingsType.GetMethod("GetOrCreate", StaticFlags)?.Invoke(null, null);
        if (perTarget == null)
            return false;

        bool hasManager = (bool)generalSettingsType
            .GetMethod("HasManagerSettingsForBuildTarget", InstanceFlags)
            .Invoke(perTarget, new object[] { BuildTargetGroup.Standalone });
        if (!hasManager)
        {
            generalSettingsType
                .GetMethod("CreateDefaultManagerSettingsForBuildTarget", InstanceFlags)
                .Invoke(perTarget, new object[] { BuildTargetGroup.Standalone });
        }

        var manager = generalSettingsType
            .GetMethod("ManagerSettingsForBuildTarget", InstanceFlags)
            .Invoke(perTarget, new object[] { BuildTargetGroup.Standalone });
        if (manager == null)
            return false;

        EnsureLoader(manager);
        EnsureMockHmdSettings();

        var generalSettings = generalSettingsType
            .GetMethod("SettingsForBuildTarget", InstanceFlags)
            .Invoke(perTarget, new object[] { BuildTargetGroup.Standalone });
        if (generalSettings != null)
        {
            // Keep the display off for every other verification; T13 starts it explicitly.
            var initField = generalSettings.GetType().GetField("m_InitManagerOnStart", InstanceFlags);
            if (initField != null)
                initField.SetValue(generalSettings, false);
            EditorUtility.SetDirty((UnityEngine.Object)generalSettings);
        }

        AssetDatabase.SaveAssets();
        return ActiveLoaderCount(manager) > 0;
    }

    static void EnsureLoader(object manager)
    {
        var loaderType = Type.GetType("Unity.XR.MockHMD.MockHMDLoader, Unity.XR.MockHMD");
        if (loaderType == null)
        {
            return;
        }

        var listField = manager.GetType().GetField("m_Loaders", InstanceFlags);
        if (!(listField?.GetValue(manager) is System.Collections.IList list))
            return;
        if (list.Cast<object>().Any(l => l != null && l.GetType() == loaderType))
            return;

        var loader = ScriptableObject.CreateInstance(loaderType);
        loader.name = "Mock HMD Loader";
        var managerPath = AssetDatabase.GetAssetPath((UnityEngine.Object)manager);
        if (!string.IsNullOrEmpty(managerPath))
            AssetDatabase.AddObjectToAsset(loader, managerPath);

        list.Add(loader);
        EditorUtility.SetDirty((UnityEngine.Object)manager);
    }

    static void EnsureMockHmdSettings()
    {
        if (EditorBuildSettings.TryGetConfigObject(
                MockHmdSettingsKey, out ScriptableObject existing) && existing != null)
            return;

        var settingsType = Type.GetType("Unity.XR.MockHMD.MockHMDBuildSettings, Unity.XR.MockHMD");
        if (settingsType == null)
            return;

        var settings = AssetDatabase.LoadAssetAtPath<ScriptableObject>(MockHmdSettingsPath);
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance(settingsType);
            settings.name = "MockHmdBuildSettings";
            AssetDatabase.CreateAsset(settings, MockHmdSettingsPath);
        }

        EditorBuildSettings.AddConfigObject(MockHmdSettingsKey, settings, true);
        AssetDatabase.SaveAssets();
    }
#endif

    static int ActiveLoaderCount(object manager)
    {
        if (manager == null)
            return 0;

        var loaders = manager.GetType().GetProperty("activeLoaders", InstanceFlags)?.GetValue(manager) as System.Collections.IEnumerable;
        return loaders == null ? 0 : loaders.Cast<object>().Count(l => l != null);
    }

    // Starts the Mock HMD display. Returns false with a message when the package is missing or the
    // subsystem could not start (for example when the native plugin is not available).
    public static bool Start(StereoMode mode, out string message)
    {
        message = string.Empty;
        if (!xrPackageAvailable)
        {
            message = "Mock HMD / XR Management packages are not installed.";
            return false;
        }

#if UNITY_EDITOR
        if (!Application.isPlaying && !EnsureAssets())
        {
            message = "XR settings assets could not be created.";
            return false;
        }
#endif

        var manager = GetManager();
        if (manager == null)
        {
            message = "XRManagerSettings is missing.";
            return false;
        }

        SetStereoMode(mode);
        if (Application.isPlaying)
            SetEyeResolution(512, 512);

        // The Mock HMD native plugin latches the stereo mode while the loader initializes, so a mode
        // change always restarts the manager instead of calling SetRenderMode on a running display.
        if (manager.GetType().GetProperty("activeLoader", InstanceFlags)?.GetValue(manager) != null)
        {
            manager.GetType().GetMethod("StopSubsystems", InstanceFlags)?.Invoke(manager, null);
            manager.GetType().GetMethod("DeinitializeLoader", InstanceFlags)?.Invoke(manager, null);
        }

        manager.GetType().GetMethod("InitializeLoaderSync", InstanceFlags)?.Invoke(manager, null);
        if (manager.GetType().GetProperty("activeLoader", InstanceFlags)?.GetValue(manager) == null)
        {
            message = "The Mock HMD loader did not initialize.";
            return false;
        }

        manager.GetType().GetMethod("StartSubsystems", InstanceFlags)?.Invoke(manager, null);
        if (!IsDisplayRunning())
        {
            message = "The Mock HMD display subsystem did not start.";
            return false;
        }

        // The native plugin may apply the build settings mode while it initializes; reapply the
        // requested mode afterwards so a runtime mode change reaches the display before its render
        // passes are built for the next frame.
        if (Application.isPlaying)
            SetStereoMode(mode);

        return true;
    }

    // The display creates its eye textures on start, so the size has to be set before Start.
    public static void SetEyeResolution(int width, int height)
    {
        var mockType = Type.GetType("Unity.XR.MockHMD.MockHMD, Unity.XR.MockHMD");
        mockType?.GetMethod("SetEyeResolution", StaticFlags)?.Invoke(null, new object[] { width, height });
    }

    public static void SetStereoMode(StereoMode mode)
    {
        var mockType = Type.GetType("Unity.XR.MockHMD.MockHMD, Unity.XR.MockHMD");
        var renderModeType = Type.GetType("Unity.XR.MockHMD.MockHMDBuildSettings+RenderMode, Unity.XR.MockHMD");
        if (mockType != null && renderModeType != null)
        {
            var value = Enum.ToObject(renderModeType, (int)mode);
            mockType.GetMethod("SetRenderMode", StaticFlags)?.Invoke(null, new[] { value });

#if UNITY_EDITOR
            // Persist the mode as well so a reloaded domain and the loader agree.
            if (EditorBuildSettings.TryGetConfigObject(MockHmdSettingsKey, out ScriptableObject settings) && settings != null)
            {
                var field = settings.GetType().GetField("renderMode", InstanceFlags);
                if (field != null)
                {
                    field.SetValue(settings, value);
                    EditorUtility.SetDirty(settings);
                }
            }
#endif
        }
    }

    public static void Stop()
    {
        if (!xrPackageAvailable)
            return;

        var manager = GetManager();
        if (manager == null)
            return;

        if (manager.GetType().GetProperty("activeLoader", InstanceFlags)?.GetValue(manager) != null)
        {
            manager.GetType().GetMethod("StopSubsystems", InstanceFlags)?.Invoke(manager, null);
            manager.GetType().GetMethod("DeinitializeLoader", InstanceFlags)?.Invoke(manager, null);
        }
    }

    // The XR eye texture is owned by the display subsystem. URP renders into it instead of the
    // camera target texture, so verification reads it back through the public accessor.
    public static RenderTexture GetEyeTexture(int renderPassIndex = 0)
    {
        var display = GetDisplay();
        if (display == null)
            return null;

        var method = display.GetType().GetMethod("GetRenderTextureForRenderPass", InstanceFlags);
        return method?.Invoke(display, new object[] { renderPassIndex }) as RenderTexture;
    }

    public static Vector2Int GetEyeResolution()
    {
        var texture = GetEyeTexture();
        return texture != null ? new Vector2Int(texture.width, texture.height) : Vector2Int.zero;
    }
}