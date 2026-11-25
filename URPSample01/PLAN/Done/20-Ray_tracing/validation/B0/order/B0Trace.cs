using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    // Installed only in the disposable embedded URP assembly. No render pass is added.
    public static class B0Trace
    {
        public static Camera target;
        public static readonly List<string> rows = new List<string>();
        public static void Pass(ref RenderingData data, ScriptableRenderPass pass)
        {
            if (data.cameraData.camera != target) return;
            int e = (int)pass.renderPassEvent;
            if (e >= 201 && e <= 203) rows.Add(e + ":" + pass.GetType().Name);
        }
        public static void Mark(ref RenderingData data, string what)
        {
            if (data.cameraData.camera == target) rows.Add(what);
        }
    }
}
