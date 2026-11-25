using System;

namespace UnityEngine.Rendering.Universal
{
    internal enum ContactShadowDebugMode { Off, Mask, Fade, Slot, Raw }

    [Serializable]
    internal sealed class ScreenSpaceContactShadowsSettings
    {
        [SerializeField]
        internal ContactShadowDebugMode debugMode = ContactShadowDebugMode.Off;

        [SerializeField, Range(0, 23)]
        internal int debugSlot;

        [SerializeField]
        [Range(1, 64)]
        [Tooltip("Maximum number of cameras whose contact shadow history is kept before the least recently used entry is released.")]
        internal int maxCachedCameras = 8;

        [SerializeField]
        [Range(0, 10000)]
        [Tooltip("Number of frames an idle camera may keep its contact shadow history before it is released. Zero releases idle cameras immediately.")]
        internal int maxUnusedFrames = 60;
    }
}
