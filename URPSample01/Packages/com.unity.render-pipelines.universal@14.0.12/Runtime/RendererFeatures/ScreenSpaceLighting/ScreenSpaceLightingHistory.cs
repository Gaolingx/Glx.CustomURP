namespace UnityEngine.Rendering.Universal
{
    // Owned by ScreenSpaceLightingRendererFeature. The SSGI/SSR/color histories added in later
    // tasks share this per-camera entry so they reset together.
    internal sealed class ScreenSpaceLightingHistory : IScreenSpaceHistoryEntry
    {
        public int width { get; private set; }
        public int height { get; private set; }
        public int resetCount { get; private set; }
        public int lastResetFrame { get; private set; }
        public int bufferIndex { get; private set; }
        public bool hasValidHistory { get; private set; }

        public void Reset(int width, int height, int frameIndex)
        {
            this.width = width;
            this.height = height;
            lastResetFrame = frameIndex;
            resetCount++;
            bufferIndex = 0;
            hasValidHistory = false;
        }

        public void SwapBuffers()
        {
            bufferIndex ^= 1;
            hasValidHistory = true;
        }

        public void Dispose()
        {
            width = 0;
            height = 0;
            bufferIndex = 0;
            hasValidHistory = false;
        }
    }
}
