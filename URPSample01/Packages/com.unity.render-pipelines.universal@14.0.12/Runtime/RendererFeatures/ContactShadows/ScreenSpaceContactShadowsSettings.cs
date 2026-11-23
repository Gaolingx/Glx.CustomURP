using System;

namespace UnityEngine.Rendering.Universal
{
    internal enum ContactShadowDebugMode { Off, Mask, Fade, Slot, Raw }

    [Serializable]
    internal sealed class ScreenSpaceContactShadowsSettings
    {
        public ContactShadowDebugMode debugMode = ContactShadowDebugMode.Off;
        [Range(0, 23)] public int debugSlot = 0;
        [Min(1)] public int maxCachedCameras = 8;
        [Min(1)] public int maxUnusedFrames = 60;
    }
}
