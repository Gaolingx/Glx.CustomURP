using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

// Test-only bridge between the Screen Space Lighting / GTAO constant buffers and the binding probe
// kernel. It runs after the transparents so SSGI/SSR have recorded this frame's values, refraction
// still has its globals bound (they are reset only in OnCameraCleanup), and GTAO can recompute the
// values it uploaded at event 204 from its current history.
//
// The probe re-binds the values the passes computed to an identically declared cbuffer. A mismatch
// in a C# field, an HLSL name or the packing rules fails the comparison; the production dispatch's
// own constant bindings are covered by the RenderDoc census and pixel output instead.
public sealed class ScreenSpaceLightingBindingProbeFeature : ScriptableRendererFeature
{
    public const int FieldCount = 35;

    public static readonly string[] FieldNames =
    {
        "_SsgiScreenSize",
        "_SsgiTraceSize",
        "_SsgiRayMarchingParams",
        "_SsgiRayMarchingParams2",
        "_SsgiZBufferParams",
        "_SsgiTemporalParams",
        "_SsgiDenoiserParams",
        "_SsgiCameraPositionWS",
        "_SsrScreenSize",
        "_SsrTraceSize",
        "_SsrRayMarchingParams",
        "_SsrRoughnessFadeParams",
        "_SsrPyramidParams",
        "_SsrPbrParams",
        "_SsrPbrFlags",
        "_GtaoBufferSize",
        "_GtaoScreenSize",
        "_GtaoParams0",
        "_GtaoParams1",
        "_GtaoParams2",
        "_GtaoParams3",
        "_GtaoParams4",
        "_GtaoFirstTwoDepthMipOffsets",
        "_GtaoDepthToViewParams",
        "_GtaoZBufferParams",
        "_GtaoOrthoHalfSize",
        "_GtaoViewRow0",
        "_GtaoViewRow1",
        "_GtaoViewRow2",
        "_SslRefractionParams",
        "_SslRefractionColorPyramidScaleAndLimit",
        "_SslRefractionProbeCount",
        "_SslRefractionProbeMin[0]",
        "_SslRefractionProbeMax[0]",
        "_SslRefractionProbeParams[0]",
    };

    static readonly string[] k_SsgiFields =
    {
        "_SsgiScreenSize",
        "_SsgiTraceSize",
        "_SsgiRayMarchingParams",
        "_SsgiRayMarchingParams2",
        "_SsgiZBufferParams",
        "_SsgiTemporalParams",
        "_SsgiDenoiserParams",
        "_SsgiCameraPositionWS",
    };

    static readonly string[] k_SsrFields =
    {
        "_SsrScreenSize",
        "_SsrTraceSize",
        "_SsrRayMarchingParams",
        "_SsrRoughnessFadeParams",
        "_SsrPyramidParams",
        "_SsrPbrParams",
        "_SsrPbrFlags",
    };

    static readonly string[] k_GtaoFields =
    {
        "_GtaoBufferSize",
        "_GtaoScreenSize",
        "_GtaoParams0",
        "_GtaoParams1",
        "_GtaoParams2",
        "_GtaoParams3",
        "_GtaoParams4",
        "_GtaoFirstTwoDepthMipOffsets",
        "_GtaoDepthToViewParams",
        "_GtaoZBufferParams",
        "_GtaoOrthoHalfSize",
        "_GtaoViewRow0",
        "_GtaoViewRow1",
        "_GtaoViewRow2",
    };

    public ComputeShader probeCompute;
    public ScreenSpaceLightingRendererFeature lightingFeature;
    public GroundTruthAmbientOcclusionRendererFeature ambientOcclusionFeature;
    public bool probeEnabled;

    static readonly BindingFlags k_InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly int k_OutputId = Shader.PropertyToID("_SslBindingProbeOutput");
    static readonly int k_IndexId = Shader.PropertyToID("_SslBindingProbeIndex");
    static readonly int k_RowId = Shader.PropertyToID("_SslBindingProbeRow");

    readonly Vector4[] m_Expected = new Vector4[FieldCount];
    readonly bool[] m_Applicable = new bool[FieldCount];
    // Snapshot taken when the readback is requested: the live arrays keep changing while the
    // asynchronous readback is in flight, and the comparison must use the frame the RT contains.
    readonly Vector4[] m_ReadbackExpected = new Vector4[FieldCount];
    readonly bool[] m_ReadbackApplicable = new bool[FieldCount];
    ProbePass m_Pass;
    RenderTexture m_Output;
    int m_Kernel = -1;
    bool m_ReadbackDone;
    Vector4[] m_ReadbackValues;

    public Vector4[] expectedValues => m_ReadbackExpected;
    public bool[] applicableFields => m_ReadbackApplicable;
    public bool probeWritten { get; private set; }
    public string probeError { get; private set; }
    public bool readbackDone => m_ReadbackDone;

    public override void Create()
    {
        m_Pass = new ProbePass(this);
        probeWritten = false;
        probeError = null;
        // Create on the thread that builds the renderer: creating the target from inside Execute
        // races the render thread and lets Unity drop it as a temporary texture.
        EnsureOutput();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!probeEnabled || probeCompute == null || m_Pass == null)
            return;

        var kernel = probeCompute.FindKernel("BindingProbe");
        if (kernel < 0)
            return;

        m_Kernel = kernel;
        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || m_Output == null)
            return;

        m_Output.Release();
        DestroyProbeObject(m_Output);
        m_Output = null;
    }

    public void RequestReadback()
    {
        m_ReadbackDone = false;
        m_ReadbackValues = null;
        if (m_Output == null || !probeWritten || !m_Output.IsCreated())
            return;

        Array.Copy(m_Expected, m_ReadbackExpected, FieldCount);
        Array.Copy(m_Applicable, m_ReadbackApplicable, FieldCount);
        AsyncGPUReadback.Request(m_Output, 0, TextureFormat.RGBAFloat, OnReadbackComplete);
    }

    public bool TryTakeReadback(out Vector4[] values)
    {
        values = m_ReadbackDone ? m_ReadbackValues : null;
        m_ReadbackValues = null;
        return values != null && values.Length >= FieldCount;
    }

    void OnReadbackComplete(AsyncGPUReadbackRequest request)
    {
        m_ReadbackDone = true;

        if (request.hasError)
        {
            probeError = "AsyncGPUReadback failed.";
            return;
        }

        var data = request.GetData<Vector4>();
        m_ReadbackValues = new Vector4[FieldCount];
        for (var i = 0; i < FieldCount && i < data.Length; i++)
            m_ReadbackValues[i] = data[i];
    }

    static void DestroyProbeObject(Object target)
    {
#if UNITY_EDITOR
        Object.DestroyImmediate(target);
#else
        Object.Destroy(target);
#endif
    }

    void EnsureOutput()
    {
        if (m_Output != null && m_Output.IsCreated())
            return;

        if (m_Output == null)
        {
            m_Output = new RenderTexture(1, FieldCount, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "SSL Binding Probe",
                enableRandomWrite = true,
                useMipMap = false,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        m_Output.Create();
    }

    void GatherAndDispatch(CommandBuffer cmd, ref CameraData cameraData)
    {
        EnsureOutput();
        probeWritten = false;
        probeError = null;

        try
        {
            var stack = VolumeManager.instance.stack;
            var ssgi = stack != null ? stack.GetComponent<ScreenSpaceGlobalIllumination>() : null;
            var ssr = stack != null ? stack.GetComponent<ScreenSpaceReflection>() : null;
            var refraction = stack != null ? stack.GetComponent<ScreenSpaceRefraction>() : null;

            bool ssgiActive = ssgi != null && ssgi.enabled.value;
            bool ssrActive = ssr != null && ssr.enabled.value;
            bool refractionActive = refraction != null && refraction.enabled.value && refraction.intensity.value > 0f;

            ReadShaderVariables(lightingFeature, "ssgiPass", k_SsgiFields, 0, ssgiActive);
            ReadSsrVariables(ref cameraData, ssr, ssrActive);
            ReadGtaoVariables(ref cameraData);
            ReadRefractionVariables(refractionActive, refraction);

            for (var i = 0; i < FieldCount; i++)
            {
                cmd.SetComputeVectorParam(probeCompute, FieldNames[i], m_Expected[i]);
                cmd.SetComputeIntParam(probeCompute, k_IndexId, i);
                cmd.SetComputeIntParam(probeCompute, k_RowId, i);
                cmd.SetComputeTextureParam(probeCompute, m_Kernel, k_OutputId, m_Output);
                cmd.DispatchCompute(probeCompute, m_Kernel, 1, 1, 1);
            }

            probeWritten = true;
        }
        catch (Exception exception)
        {
            probeError = exception.GetType().Name + ": " + exception.Message;
        }
    }

    void ReadShaderVariables(
        ScreenSpaceLightingRendererFeature feature,
        string passPropertyName,
        string[] fieldNames,
        int offset,
        bool active)
    {
        for (var i = 0; i < fieldNames.Length; i++)
            SetField(offset + i, Vector4.zero, false);

        if (feature == null)
            return;

        var pass = feature.GetType().GetProperty(passPropertyName, k_InstanceFlags)?.GetValue(feature);
        if (pass == null)
            return;

        var variables = pass.GetType().GetField("m_ShaderVariables", k_InstanceFlags)?.GetValue(pass);
        if (variables == null)
            return;

        for (var i = 0; i < fieldNames.Length; i++)
        {
            var field = variables.GetType().GetField(fieldNames[i], k_InstanceFlags);
            if (field == null)
                continue;
            SetField(offset + i, (Vector4)field.GetValue(variables), active);
        }
    }

    // The SSR pass builds its constant set locally for each dispatch and does not keep it in a
    // field, so the probe re-invokes the same helper for the current camera and settings.
    void ReadSsrVariables(ref CameraData cameraData, ScreenSpaceReflection settings, bool active)
    {
        const int offset = 8;
        for (var i = 0; i < k_SsrFields.Length; i++)
            SetField(offset + i, Vector4.zero, false);

        if (!active || settings == null || lightingFeature == null)
            return;

        var pass = lightingFeature.GetType().GetProperty("ssrPass", k_InstanceFlags)?.GetValue(lightingFeature);
        if (pass == null)
            return;

        var history = pass.GetType().GetField("m_History", k_InstanceFlags)?.GetValue(pass);
        if (history == null)
            return;

        int width = ReadInt(history, "width");
        int height = ReadInt(history, "height");
        if (width <= 0 || height <= 0)
            return;

        bool pbr = settings.usedAlgorithm.value == ScreenSpaceReflectionAlgorithm.PBRAccumulation;
        bool halfResolution = pbr && !settings.fullResolution.value;
        int traceWidth = halfResolution ? Mathf.Max(1, width >> 1) : width;
        int traceHeight = halfResolution ? Mathf.Max(1, height >> 1) : height;
        bool historyValid = pbr && ReadBool(history, "ssrPbrHasValidHistory");

        var method = pass.GetType().GetMethod("CreateShaderVariables", k_InstanceFlags);
        if (method == null)
            return;

        var arguments = new object[]
        {
            cameraData, settings, width, height, traceWidth, traceHeight, historyValid,
        };
        object cb;
        try
        {
            cb = method.Invoke(pass, arguments);
        }
        catch (TargetInvocationException)
        {
            return;
        }

        if (cb == null)
            return;

        for (var i = 0; i < k_SsrFields.Length; i++)
        {
            var field = cb.GetType().GetField(k_SsrFields[i], k_InstanceFlags);
            if (field != null)
                SetField(offset + i, (Vector4)field.GetValue(cb), true);
        }
    }

    void ReadGtaoVariables(ref CameraData cameraData)
    {
        const int offset = 15;
        for (var i = 0; i < k_GtaoFields.Length; i++)
            SetField(offset + i, Vector4.zero, false);

        if (ambientOcclusionFeature == null)
            return;

        var pass = ambientOcclusionFeature.GetType().GetProperty("pass", k_InstanceFlags)?.GetValue(ambientOcclusionFeature);
        if (pass == null)
            return;

        var settings = pass.GetType().GetField("m_Settings", k_InstanceFlags)?.GetValue(pass);
        var intensity = settings?.GetType().GetField("intensity", k_InstanceFlags)?.GetValue(settings);
        if (intensity == null || Convert.ToSingle(intensity) <= 0f)
            return;

        var history = pass.GetType().GetField("m_History", k_InstanceFlags)?.GetValue(pass);
        if (history == null)
            return;

        var method = pass.GetType().GetMethod("ComputeShaderVariables", k_InstanceFlags);
        if (method == null)
            return;

        var arguments = new object[] { cameraData, null };
        try
        {
            method.Invoke(pass, arguments);
        }
        catch (TargetInvocationException)
        {
            return;
        }

        var cb = arguments[1];
        if (cb == null)
            return;

        for (var i = 0; i < k_GtaoFields.Length; i++)
        {
            var field = cb.GetType().GetField(k_GtaoFields[i], k_InstanceFlags);
            if (field != null)
                SetField(offset + i, (Vector4)field.GetValue(cb), true);
        }

        // The pass advances effectFrameCount after it uploads the constant buffer, so the recomputed
        // value uses the next frame number. Rebuild the two frame-derived lanes for the upload frame.
        int effectFrame = ReadInt(history, "effectFrameCount");
        if (effectFrame > 0)
        {
            int frame = effectFrame - 1;
            var params1Field = cb.GetType().GetField("_GtaoParams1", k_InstanceFlags);
            if (params1Field != null)
            {
                var params1 = (Vector4)params1Field.GetValue(cb);
                params1.z = (frame / 6) % 4;
                params1.w = frame % 6;
                SetField(offset + 3, params1, true);
            }
        }
    }

    void ReadRefractionVariables(bool active, ScreenSpaceRefraction settings)
    {
        const int offset = 29;
        for (var i = 0; i < 6; i++)
            SetField(offset + i, Vector4.zero, false);

        if (!active || settings == null || lightingFeature == null)
            return;

        var pass = lightingFeature.GetType().GetProperty("refractionPass", k_InstanceFlags)?.GetValue(lightingFeature);
        if (pass == null)
            return;

        var history = pass.GetType().GetField("m_History", k_InstanceFlags)?.GetValue(pass);
        if (history == null)
            return;

        int width = ReadInt(history, "width");
        int height = ReadInt(history, "height");
        int mipCount = ReadInt(history, "colorPyramidMipCount");
        if (width <= 0 || height <= 0)
            return;

        var parameters = new Vector4(
            Mathf.Max(0, mipCount),
            1f / Mathf.Max(0.001f, settings.screenFadeDistance.value),
            settings.intensity.value,
            0f);
        var scaleAndLimit = new Vector4(1f, 1f, (width - 0.5f) / width, (height - 0.5f) / height);

        SetField(offset + 0, parameters, true);
        SetField(offset + 1, scaleAndLimit, true);

        int probeCount = Convert.ToInt32(
            pass.GetType().GetField("m_ProbeCount", k_InstanceFlags)?.GetValue(pass) ?? 0);
        SetField(offset + 2, new Vector4(probeCount, 0f, 0f, 0f), true);

        if (pass.GetType().GetField("m_ProbeMin", k_InstanceFlags)?.GetValue(pass) is Vector4[] min && min.Length > 0)
            SetField(offset + 3, min[0], true);
        if (pass.GetType().GetField("m_ProbeMax", k_InstanceFlags)?.GetValue(pass) is Vector4[] max && max.Length > 0)
            SetField(offset + 4, max[0], true);
        if (pass.GetType().GetField("m_ProbeParams", k_InstanceFlags)?.GetValue(pass) is Vector4[] proxyParams && proxyParams.Length > 0)
            SetField(offset + 5, proxyParams[0], true);
    }

    static int ReadInt(object target, string name)
    {
        var type = target.GetType();
        var property = type.GetProperty(name, k_InstanceFlags);
        if (property != null)
            return Convert.ToInt32(property.GetValue(target));
        var field = type.GetField(name, k_InstanceFlags);
        return field != null ? Convert.ToInt32(field.GetValue(target)) : 0;
    }

    static bool ReadBool(object target, string name)
    {
        var type = target.GetType();
        var property = type.GetProperty(name, k_InstanceFlags);
        if (property != null)
            return Convert.ToBoolean(property.GetValue(target));
        var field = type.GetField(name, k_InstanceFlags);
        return field != null && Convert.ToBoolean(field.GetValue(target));
    }

    void SetField(int index, Vector4 value, bool applicable)
    {
        if (index < 0 || index >= FieldCount)
            return;
        m_Expected[index] = value;
        m_Applicable[index] = applicable;
    }

    sealed class ProbePass : ScriptableRenderPass
    {
        readonly ScreenSpaceLightingBindingProbeFeature m_Owner;

        public ProbePass(ScreenSpaceLightingBindingProbeFeature owner)
        {
            m_Owner = owner;
            // 500: after refraction (448) and the transparent draw (450), before OnCameraCleanup
            // resets the refraction globals.
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!m_Owner.probeEnabled || m_Owner.probeCompute == null)
                return;

            var cmd = CommandBufferPool.Get("SSL Binding Probe");
            try
            {
                m_Owner.GatherAndDispatch(cmd, ref renderingData.cameraData);
                context.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
        }
    }
}
