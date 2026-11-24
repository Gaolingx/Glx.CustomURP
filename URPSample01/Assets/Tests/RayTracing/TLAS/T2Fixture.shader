Shader "Hidden/T2/IntersectionFixture" {
 SubShader {
  Pass { Name "Raster" ColorMask 0 }
  Pass {
   Name "GlxDXR"
   Tags { "LightMode"="RayTracing" }
   HLSLPROGRAM
   #pragma raytracing T2Fixture
   #pragma multi_compile_local _ _ALPHATEST_ON
   struct Hit { float t; uint id; uint kind; };
   [shader("closesthit")]
   void T2Hit(inout Hit h : SV_RayPayload, BuiltInTriangleIntersectionAttributes a : SV_IntersectionAttributes) {
    h.t = RayTCurrent(); h.id = InstanceID(); h.kind = HitKind();
   }
   ENDHLSL
  }
 }
}
