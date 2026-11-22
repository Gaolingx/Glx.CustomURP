using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace UnityEngine.Rendering.Universal.Internal
{
    // Frozen input stream for the G1 GPU list builder (HANDOFF §3.4). One record per bitmap item, in
    // bitmap order: non-directional visible lights in culling order, then the reflection probes
    // after PreSetup's stable importance sort. The bounds kernel consumes authored light ranges
    // and spot angles, not packed _AdditionalLights* constants.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ClusterItemRecord
    {
        public const int Stride = 48;

        // Light: localToWorld column 3, w = range. Probe: bounds center, w unused.
        public float4 positionRange;

        // Light: localToWorld column 2, w = spotAngle in degrees (0 for point). Probe: bounds
        // extents, w unused.
        public float4 directionExtent;

        public ClusterItemType itemType;
        public LightType lightType;     // Directional never appears in the stream; probes store Point
        public uint layerMask;          // RenderingLayerUtils.ToValidRenderingLayers, 0 for probes
        public uint sourceIndex;        // index into VisibleLight[] / VisibleReflectionProbe[]

        public static ClusterItemRecord FromVisibleLight(VisibleLight light, int sourceIndex, uint layerMask)
        {
            var localToWorld = light.localToWorldMatrix;
            var position = localToWorld.GetColumn(3);
            var direction = localToWorld.GetColumn(2);
            return new ClusterItemRecord
            {
                positionRange = new float4(position.x, position.y, position.z, light.range),
                directionExtent = new float4(direction.x, direction.y, direction.z,
                    light.lightType == LightType.Spot ? light.spotAngle : 0f),
                itemType = ClusterItemType.Light,
                lightType = light.lightType,
                layerMask = layerMask,
                sourceIndex = (uint)sourceIndex,
            };
        }

        public static ClusterItemRecord FromReflectionProbe(VisibleReflectionProbe probe, int sourceIndex)
        {
            var center = (float3)probe.bounds.center;
            var extents = (float3)probe.bounds.extents;
            return new ClusterItemRecord
            {
                positionRange = new float4(center, 0f),
                directionExtent = new float4(extents, 0f),
                itemType = ClusterItemType.Probe,
                lightType = LightType.Point,
                layerMask = 0,
                sourceIndex = (uint)sourceIndex,
            };
        }
    }

    internal enum ClusterItemType : uint
    {
        Light = 0,
        Probe = 1,
    }

    internal static class ClusterItemStream
    {
        // Mirrors PreSetup: the leading directional block is skipped (Forward+ lights it separately)
        // and only the probes inside the maxVisibleReflectionProbes clamp are appended. Probe count 0
        // is the common case; pass the clamped value, not the raw culling count.
        public static ClusterItemRecord[] Build(ref RenderingData renderingData, int lightOffset, int lightCount, int reflectionProbeCount)
        {
            var records = new ClusterItemRecord[lightCount + reflectionProbeCount];
            Fill(ref renderingData, lightOffset, lightCount, reflectionProbeCount, records);
            return records;
        }

        // Per-frame variant: keeps one array alive instead of allocating every frame. Returns the
        // used count; the array can be longer.
        public static int Build(ref RenderingData renderingData, int lightOffset, int lightCount, int reflectionProbeCount, ref ClusterItemRecord[] buffer)
        {
            int count = lightCount + reflectionProbeCount;
            if (buffer == null || buffer.Length < count)
                buffer = new ClusterItemRecord[count];
            if (count != 0)
                Fill(ref renderingData, lightOffset, lightCount, reflectionProbeCount, buffer);
            return count;
        }

        static void Fill(ref RenderingData renderingData, int lightOffset, int lightCount, int reflectionProbeCount, ClusterItemRecord[] records)
        {
            var lights = renderingData.lightData.visibleLights;
            // The kernels read position/direction/type only; the authored layer mask is recorded
            // solely for the parity snapshot, so skip the per-light component lookup when it is off.
            bool captureLayerMask = ClusterListParityCapture.enabled;
            for (int i = 0; i < lightCount; i++)
            {
                var index = lightOffset + i;
                uint layerMask = 0;
                if (captureLayerMask)
                {
                    var light = lights[index].light;
                    if (light != null)
                        layerMask = RenderingLayerUtils.ToValidRenderingLayers(light.GetUniversalAdditionalLightData().renderingLayers);
                }
                records[i] = ClusterItemRecord.FromVisibleLight(lights[index], index, layerMask);
            }

            var probes = renderingData.cullResults.visibleReflectionProbes;
            for (int i = 0; i < reflectionProbeCount; i++)
                records[lightCount + i] = ClusterItemRecord.FromReflectionProbe(probes[i], i);
        }
    }
}
