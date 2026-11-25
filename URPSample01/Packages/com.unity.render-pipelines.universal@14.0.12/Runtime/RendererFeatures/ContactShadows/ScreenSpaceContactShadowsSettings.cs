using System;

namespace UnityEngine.Rendering.Universal
{
    internal enum ContactShadowDebugMode { Off, Mask, Fade, Slot, Raw }

    [Serializable]
    internal sealed class ScreenSpaceContactShadowsSettings
    {
        public ContactShadowDebugMode debugMode = ContactShadowDebugMode.Off;
        [Range(0, 23)] public int debugSlot = 0;
        [Min(1), Tooltip("Maximum number of cameras whose contact shadow history is kept before the least recently used entry is released.")]
        public int maxCachedCameras = 8;
        [Min(1), Tooltip("Number of frames an idle camera may keep its contact shadow history before it is released.")]
        public int maxUnusedFrames = 60;
    }
}
