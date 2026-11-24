using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace UnityEngine.Rendering.Universal
{
    internal sealed class ScreenSpaceContactShadowLights
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct LightRecord
        {
            public Vector3 position;
            public uint type;
            public Vector3 direction;
            public uint mask;
        }

        internal readonly LightRecord[] records = new LightRecord[24];
        internal readonly float[] additionalMasks = new float[UniversalRenderPipeline.maxVisibleAdditionalLights];
        internal uint mainMask { get; private set; }
        internal int count { get; private set; }

        internal void Build(in LightData data)
        {
            Array.Clear(additionalMasks, 0, additionalMasks.Length);
            Array.Clear(records, 0, count);
            count = 0;
            mainMask = 0;

            var visible = data.visibleLights;
            int main = data.mainLightIndex;
            if ((uint)main < (uint)visible.Length)
                mainMask = AddIfMarked(visible[main].light);

            for (int i = 0, additional = 0; i < visible.Length && additional < additionalMasks.Length && count < records.Length; ++i)
            {
                if (i == main)
                    continue;
                additionalMasks[additional++] = math.asfloat(AddIfMarked(visible[i].light));
            }
        }

        uint AddIfMarked(Light light)
        {
            if (!ContactShadowLight.IsMarked(light))
                return 0;

            uint mask = 1u << count;
            uint type = EncodeLightType(light.type);
            var transform = light.transform;
            records[count++] = new LightRecord
            {
                position = type == 0 ? Vector3.zero : transform.position,
                type = type,
                direction = type == 0 ? -transform.forward : Vector3.zero,
                mask = mask
            };
            return mask;
        }

        static uint EncodeLightType(LightType type) => type switch
        {
            LightType.Directional => 0u,
            LightType.Point => 1u,
            LightType.Spot => 2u,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }
}
