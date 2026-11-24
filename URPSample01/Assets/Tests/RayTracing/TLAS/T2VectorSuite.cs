using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace T2Validation
{
    // Shared by EditMode and the isolated development Player (no NUnit/Editor dependency).
    internal static class T2VectorSuite
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        internal static void Run(RayTracingShader shader, Shader surface, bool requireDxr, Action<string> log)
        {
            var objects = new List<Object>();
            T Own<T>(T o) where T : Object { objects.Add(o); return o; }
            var service = new RayTracingSceneResources();
            try
            {
                CameraData Camera(string name) {
                    var c = Own(new GameObject(name)).AddComponent<Camera>(); c.enabled = false;
                    return new CameraData { camera = c, cameraType = CameraType.Game, renderType = CameraRenderType.Base, cameraTargetDescriptor = new RenderTextureDescriptor(32,32) };
                }
                var first = Camera("T2 A"); var second = Camera("T2 B");
                var mat = Own(new Material(surface));
                var cut = Own(new Material(surface)); cut.EnableKeyword("_ALPHATEST_ON");
                var no = Own(new Material(surface)); no.renderQueue = 3000;
                var list = new List<RayTracingMember>();
                service.SetRequests(RayTracingRequests.GI | RayTracingRequests.Reflections);
                RayTracingAccelerationStructure snapshot = null;
                int rev = 1;
                bool Build(CameraData camera, RayTracingRequests demand) => service.TryBuild(camera, true, true, shader, mat, rev, int.MinValue, demand, list, out snapshot);
                log($"api={SystemInfo.graphicsDeviceType} GPU={SystemInfo.graphicsDeviceName} supportsRT={SystemInfo.supportsRayTracing}");
                Check(!service.TryBuild(first, false, true, shader, mat, rev, int.MinValue, RayTracingRequests.GI, list, out snapshot)
                    && snapshot == null && service.liveResources == 0 && service.buildCount == 0, "disabled control");
                log("opt-in off: zero builds/resources");
                if (!requireDxr) {
                    Check(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 && !Build(first, RayTracingRequests.GI) && snapshot == null && service.liveResources == 0 && service.buildCount == 0, "D3D11 must deny");
                    log("D3D11 denied, zero builds/resources"); return;
                }
                log($"pass={mat.FindPass("GlxDXR")} queue={mat.renderQueue} alpha={cut.IsKeywordEnabled("_ALPHATEST_ON")} opaque={RayTracingSceneInstances.Classify(mat)} cut={RayTracingSceneInstances.Classify(cut)}");
                Check(RayTracingSceneInstances.Classify(mat) == (RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly) && RayTracingSceneInstances.Classify(cut) == RayTracingSubMeshFlags.Enabled && RayTracingSceneInstances.Classify(no) == RayTracingSubMeshFlags.Disabled, "submesh classification");
                var tri = Own(new GameObject("known winding")); tri.layer = 31; tri.transform.position = new Vector3(20,0,4);
                var mesh = Own(new Mesh()); mesh.vertices = new[]{new Vector3(-1,-1,0), new Vector3(0,1,0), new Vector3(1,-1,0), new Vector3(2,-1,0), new Vector3(3,1,0), new Vector3(4,-1,0)};
                mesh.subMeshCount = 2; mesh.SetTriangles(new[]{0,1,2},0); mesh.SetTriangles(new[]{3,4,5},1); mesh.RecalculateNormals(); mesh.RecalculateBounds();
                tri.AddComponent<MeshFilter>().sharedMesh = mesh; var r = tri.AddComponent<MeshRenderer>(); r.sharedMaterials = new[]{mat,no}; r.rayTracingMode = RayTracingMode.Static;
                var box = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); box.layer = 31; box.transform.position = new Vector3(30,0,4);
                var br = box.GetComponent<MeshRenderer>(); br.sharedMaterial = mat; br.rayTracingMode = RayTracingMode.DynamicTransform;
                list.Add(new RayTracingMember(r,71,0x80)); list.Add(new RayTracingMember(br,92,1));
                Check(!GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(first.camera),r.bounds), "triangle must be offscreen");
                Check(Build(first, RayTracingRequests.GI), "initial build"); var identity = snapshot;
                using (var buffer = new ComputeBuffer(1,16)) {
                    void State(int builds, int count) {
                        Check(Build(second,RayTracingRequests.Reflections) && snapshot == identity && service.buildCount == builds && service.instanceCount == count, "shared snapshot/build count");
                        log($"frame={Time.frameCount} revision={rev} build={builds} instances={count} disabledSubmeshes={service.disabledSubmeshes}");
                    }
                    void Ray(float x,float z,int dz,int mask,int flags,float t,int id,int kind) {
                        using (var cmd = new CommandBuffer()) {
                            cmd.SetRayTracingShaderPass(shader,"GlxDXR"); cmd.SetRayTracingAccelerationStructure(shader,"_Scene",snapshot);
                            cmd.SetRayTracingBufferParam(shader,"_Result",buffer); cmd.SetRayTracingVectorParam(shader,"_Origin",new Vector4(x,0,z,0));
                            cmd.SetRayTracingVectorParam(shader,"_Direction",new Vector4(0,0,dz,0)); cmd.SetRayTracingIntParam(shader,"_Mask",mask); cmd.SetRayTracingIntParam(shader,"_Flags",flags);
                            cmd.DispatchRays(shader,"T2Ray",1,1,1,null); Graphics.ExecuteCommandBuffer(cmd);
                        }
                        var result = new Vector4[1]; buffer.GetData(result); var v = result[0];
                        log($"rev={rev} origin=({x},0,{z}) dz={dz} mask={mask} flags={flags} expected=({t},{id},{kind}) gpu=({v.x},{v.y},{v.z})");
                        Check(v.w == 1 && Mathf.Abs(v.x-t)<=0.001f && v.y==id && v.z==kind,"analytic ray mismatch");
                    }
                    second.camera.transform.position = new Vector3(20,0,0);
                    Check(GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(second.camera),r.bounds), "second camera must see triangle");
                    State(1,2); Ray(20,0,1,128,0,4,71,254);
                    second.camera.transform.position = Vector3.zero;
                    State(1,2); Ray(20,0,1,128,0,4,71,254);
                    const int front=254, back=255, cullBack=16;
                    for (int i=0;i<2;i++) {
                        bool cull=i==0; if (!cull) { list[0]=new RayTracingMember(r,71,128,false); ++rev; }
                        State(i+1,2); Check(service.disabledSubmeshes==1,"unsupported slot");
                        foreach (int flags in new[]{0,cullBack}) {
                            bool reject=cull && flags==cullBack;
                            Ray(20,0,1,128,flags,4,71,front); Ray(20,8,-1,128,flags,reject?-1:4,reject?0:71,reject?0:back);
                        }
                    }
                    Ray(30,4,1,1,0,0.5f,92,back); Ray(30,4,1,1,cullBack,-1,0,0);
                    Ray(20,0,1,1,0,-1,0,0); Ray(23,0,1,128,0,-1,0,0);
                    var light = Own(new GameObject("light only")).AddComponent<Light>(); light.intensity=3; ++rev; State(2,2);
                    tri.transform.position += Vector3.forward; ++rev; State(3,2); State(3,2); Ray(20,0,1,128,0,5,71,front);
                    list[0]=new RayTracingMember(r,71,2,false); ++rev; State(4,2); Ray(20,0,1,128,0,-1,0,0); Ray(20,0,1,2,0,5,71,front);
                    r.sharedMaterials=new[]{no,cut}; ++rev; State(5,2); Ray(20,0,1,2,0,-1,0,0); Ray(23,0,1,2,0,5,71,front);
                    r.enabled=false; ++rev; State(6,1); Ray(23,0,1,2,0,-1,0,0);
                    r.enabled=true; ++rev; State(7,2); Ray(23,0,1,2,0,5,71,front);
                    box.layer=7; ++rev; State(8,1); Ray(30,4,1,1,0,-1,0,0);
                    list.RemoveAt(0); ++rev; State(9,0); Ray(23,0,1,2,0,-1,0,0);
                    box.layer=31; box.transform.position+=Vector3.forward; ++rev; State(10,1);
                    Ray(30,5,1,1,0,0.5f,92,back);
                    Object.DestroyImmediate(box); ++rev; State(11,0); Ray(30,5,1,1,0,-1,0,0);
                }
                service.SetRequests(0); Check(service.liveResources==0,"teardown");
            }
            finally { service.Dispose(); for(int i=objects.Count-1;i>=0;i--) if(objects[i]) Object.DestroyImmediate(objects[i]); }
        }
    }
}
