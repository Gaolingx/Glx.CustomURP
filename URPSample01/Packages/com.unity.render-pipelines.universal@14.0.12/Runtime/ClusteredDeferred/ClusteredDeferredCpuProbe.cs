using System.Diagnostics;

namespace UnityEngine.Rendering.Universal.Internal
{
    // Frame-level CPU decomposition for the deferred light path. Disabled by default; the arena
    // runner turns it on (--cd-cpu) and reads the accumulators through reflection. Every call site
    // pays one bool check when disabled.
    internal static class ClusteredDeferredCpuProbe
    {
        public static bool enabled;
        public static int frames;

        public static long preSetup;
        public static long clusterJobsComplete;
        public static long clusterUpload;
        public static long lightConstants;
        public static long resolveMixedLighting;
        public static long setupLights;
        public static long deferredSetupLights;
        public static long precomputeLights;

        public static long Begin()
        {
            return enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void End(long start, ref long accumulator)
        {
            if (start != 0L)
                accumulator += Stopwatch.GetTimestamp() - start;
        }

        public static void CountFrame()
        {
            if (enabled)
                frames++;
        }

        public static void Reset()
        {
            frames = 0;
            preSetup = 0L;
            clusterJobsComplete = 0L;
            clusterUpload = 0L;
            lightConstants = 0L;
            resolveMixedLighting = 0L;
            setupLights = 0L;
            deferredSetupLights = 0L;
            precomputeLights = 0L;
        }
    }
}
