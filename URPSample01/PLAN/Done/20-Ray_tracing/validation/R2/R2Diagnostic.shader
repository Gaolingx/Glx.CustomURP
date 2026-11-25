Shader "Hidden/R2/InstanceDiagnostic" {
 SubShader {
  Pass { Name "GlxDXR" Tags { "LightMode"="RayTracing" }
   HLSLPROGRAM
   #pragma raytracing R2Diagnostic
   #include "Packages/com.unity.render-pipelines.universal/Shaders/RayTracing/GlxBasicPayload.hlsl"
   [shader("closesthit")]
   void Diagnostic(inout GlxBasicPayload p : SV_RayPayload, BuiltInTriangleIntersectionAttributes a : SV_IntersectionAttributes) {
    p.radiance=float3(InstanceID(),0,0); p.hitT=RayTCurrent(); p.flags=GlxHit;
   }
   ENDHLSL
  }
 }
}
