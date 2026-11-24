#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using DebugSettings = ContactShadowTests.PlayMode.ContactShadowDebugRenderTests.DebugSettings;

namespace ContactShadowTests.PlayMode
{
    public sealed class ContactShadowT8MatrixTests : ContactShadowRenderFixture
    {
        static readonly RenderingMode[] Modes = { RenderingMode.Forward, RenderingMode.ForwardPlus, RenderingMode.Deferred };
        float Pixel => 2 * 5 * Mathf.Tan(View.fieldOfView * .5f * Mathf.Deg2Rad) / H;
        static int Bits(Color32 c) => c.r | c.g << 8 | c.b << 16;
        static IEnumerable<Vector2Int> Pixels(RectInt roi) { foreach(var p in roi.allPositionsWithin) yield return p; }
        static int Hits(Color32[] im, RectInt roi) => Pixels(roi).Count(p => im[p.y * W + p.x].r != 0);
        static RectInt Box(Vector3 p, int w, int h) => new RectInt(Mathf.RoundToInt(p.x)-w/2, Mathf.RoundToInt(p.y)-h/2,w,h);
        static void Metric(string label, object value) => TestContext.WriteLine(label + "=" + value);

        [UnityTest] public IEnumerator OpacityAndMaterials([ValueSource(nameof(Modes))] RenderingMode mode)
        {
            yield return Open(mode); Isolate(0, LightShadows.None);
            Settings.enabled.Override(false); var off = Capture(); Settings.enabled.Override(true);
            var drop = new List<float>();
            foreach (float opacity in new[]{0f,.5f,1f})
            {
                Settings.opacity.Override(opacity); yield return null;
                var pair = Pair("opacity"+opacity, off, Capture());
                var region = pair.Measure("behind", Contact); drop.Add(region.meanOffLuma-region.meanOnLuma);
                Metric("opacity/"+opacity+"/lumaDrop", drop.Last());
                if(opacity==0) pair.RequireEqual(pair.Measure("full",Full)); else pair.RequireDark(region);
                for(int i=0;i<Panels.Length;i++) pair.RequireEqual(pair.Measure(Panels[i],Panel(i)));
            }
            Assert.That(drop[1]/drop[2], Is.EqualTo(.5f).Within(.06f));
            // Positive controls at precisely the same pixels for every excluded surface.
            foreach(var n in Panels) Named<Renderer>(n).sharedMaterial=Named<Renderer>("Ground").sharedMaterial;
            yield return CompareVolume("lit-control",p=>{ for(int i=0;i<Panels.Length;i++) p.RequireDark(p.Measure(Panels[i],Panel(i))); });
        }

        [UnityTest] public IEnumerator ThreeViews()
        {
            yield return Open(RenderingMode.Forward); Isolate(0, LightShadows.None);
            Vector3 behind=View.ScreenToWorldPoint(new Vector3(131,130,5)), front=View.ScreenToWorldPoint(new Vector3(171,130,5));
            var start=View.transform.position; var rot=View.transform.rotation;
            var tiles=new List<Color32[]>();
            foreach(int angle in new[]{0,-12,12})
            {
                View.transform.SetPositionAndRotation(start, rot);
                View.transform.RotateAround(behind,View.transform.up,angle);
                var b=Box(View.WorldToScreenPoint(behind),16,8); var f=Box(View.WorldToScreenPoint(front),16,8);
                yield return CompareVolume("view"+angle,p=>{ var br=p.Measure("behind",b); var fr=p.Measure("facing",f);
                    p.Record(false,br,fr); p.RequireDark(br);p.RequireEqual(fr); });
                tiles.Add(Capture());
            }
            var args=Environment.GetCommandLineArgs();int flag=Array.IndexOf(args,"-contactShadowEvidence");
            if(flag>=0 && SystemInfo.graphicsDeviceType==GraphicsDeviceType.Direct3D11) {
                var sheet=new Texture2D(W*3,H,TextureFormat.RGBA32,false,true);
                try { for(int i=0;i<3;i++)sheet.SetPixels32(i*W,0,W,H,tiles[i]);sheet.Apply();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(args[flag+1],"ThreeViews.png"),sheet.EncodeToPNG());
                } finally {UnityEngine.Object.Destroy(sheet);}
            }
        }

        [UnityTest] public IEnumerator DistanceSequence()
        {
            yield return Open(RenderingMode.Forward); Isolate(0,LightShadows.None);
            var behind=View.ScreenToWorldPoint(new Vector3(131,130,5)); var start=View.transform.position;
            Settings.minDistance.Override(3);Settings.maxDistance.Override(7);Settings.fadeDistance.Override(2);Settings.fadeInDistance.Override(2);
            using var debug=new DebugSettings(Feature); debug.Select("Fade");
            foreach(float d in new[]{2.5f,4f,5f,6f,9.5f})
            {
                View.transform.position=start+View.transform.forward*(5-d); yield return null;
                var roi=Box(View.WorldToScreenPoint(behind),8,4); var im=Capture();
                int observed=Pixels(roi).Max(p=>(int)im[p.y*W+p.x].r);
                int expected=(int)(255*Mathf.Clamp01((d-3)/2)*Mathf.Clamp01((7-d)/2));
                Metric("distance/"+d,observed); Assert.That(observed,Is.EqualTo(expected).Within(2));
            }
        }

        [UnityTest] public IEnumerator EdgeBands()
        {
            yield return Open(RenderingMode.Forward); Isolate(0,LightShadows.None);
            using var debug=new DebugSettings(Feature); debug.Select("Fade"); var start=View.transform.position;
            var values=new List<float>();
            foreach(int y in new[]{151,177,182,186,190})
            {
                View.transform.position=start-View.transform.up*((y-130)*Pixel);yield return null;
                var im=Capture(); var samples=Pixels(new RectInt(122,y-1,17,2)).Select(p=>(int)im[p.y*W+p.x].r).Where(v=>v>0).ToArray();
                Assert.That(samples.Length,Is.GreaterThan(3));values.Add((float)samples.Average()); Metric("edge/"+y,values.Last());
            }
            Assert.That(values[0],Is.EqualTo(255));for(int i=2;i<values.Count;i++)Assert.That(values[i],Is.LessThan(values[i-1]));
            Assert.That(values.Last(),Is.LessThan(130));
        }

        [UnityTest] public IEnumerator TraceBoundaries()
        {
            yield return Open(RenderingMode.Forward); Isolate(0,LightShadows.None);
            using var debug=new DebugSettings(Feature);debug.Select("Mask");var counts=new List<int>();
            foreach(var v in new[]{new Vector3(4,.15f,.2f),new Vector3(10,.15f,.2f),new Vector3(64,.15f,.2f),new Vector3(10,.02f,.2f),new Vector3(10,1,.2f),new Vector3(10,.15f,0),new Vector3(10,.15f,1)})
            {
                Settings.sampleCount.Override((int)v.x);Settings.thicknessScale.Override(v.y);Settings.rayBias.Override(v.z);yield return null;
                var im=Capture();counts.Add(Hits(im,Contact));Metric("samples,thickness,bias/"+v,counts.Last());
                Assert.That(Hits(im,Facing),Is.Zero,"Self shadow on unobstructed floor: "+v);
            }
            Assert.That(counts[1],Is.GreaterThan(30)); Assert.That(counts[4],Is.GreaterThan(counts[3]));
        }

        [UnityTest] public IEnumerator MaskAcrossModes()
        {
            Color32[] reference=null;
            foreach(var mode in Modes)
            {
                yield return Open(mode); using(var debug=new DebugSettings(Feature))
                {
                    debug.Select("Raw");var im=Capture(); Assert.That(im.Any(c=>Bits(c)!=0),Is.True);
                    if(reference==null)reference=im;else { int diff=reference.Zip(im,(a,b)=>Bits(a)!=Bits(b)).Count(x=>x);Metric(mode+"/differentMaskPixels",diff);Assert.That(diff,Is.Zero); }
                }
                yield return Close();
            }
        }

        [UnityTest] public IEnumerator SlotIdentityAndOverflow()
        {
            yield return Open(RenderingMode.ForwardPlus,stress:true);
            var markers=new[]{Markers[0],Markers[1],Markers[2]}.Concat(Enumerable.Range(3,22).Select(i=>Named<ContactShadowLight>("Stress"+i.ToString("00")))).ToArray();
            using var debug=new DebugSettings(Feature); debug.Select("Raw");
            var signatures=new List<bool[]>();
            foreach(var m in markers){ foreach(var x in markers)x.castContactShadows=x==m; var im=Capture(); signatures.Add(im.Select(c=>(Bits(c)&1)!=0).ToArray()); }
            // Identify each bit from its independently rendered full-frame signature (not name or cull order).
            foreach(var selection in new[]{new[]{0},new[]{0,3,5,7},Enumerable.Range(0,24).ToArray(),Enumerable.Range(1,24).ToArray(),Enumerable.Range(0,25).ToArray()})
            {
                Lights[0].enabled=selection.Contains(0);for(int i=0;i<25;i++)markers[i].castContactShadows=selection.Contains(i);
                var im=Capture();var used=new List<int>();
                for(int b=0;b<24;b++)
                {
                    var bit=im.Select(c=>(Bits(c)&(1<<b))!=0).ToArray();
                    if(b>=selection.Length){Assert.That(bit.Any(x=>x),Is.False);continue;}
                    var matches=selection.Where(i=>signatures[i].SequenceEqual(bit)).ToArray();Assert.That(matches.Length,Is.EqualTo(1),"ambiguous or wrong slot "+b);used.Add(matches[0]);
                }
                Metric("active/"+selection.Length+"/slotToFixture",string.Join(",",used));Assert.That(used.Distinct().Count(),Is.EqualTo(Math.Min(24,selection.Length)));if(Lights[0].enabled)Assert.That(used[0],Is.Zero);
                if(selection.Length==25)
                {
                    int overflow=selection.Except(used).Single();
                    debug.Select("Off");var all=Capture();markers[overflow].castContactShadows=false;
                    var unchanged=Pair("overflow-not-applied",all,Capture());unchanged.RequireEqual(unchanged.Measure("full",Full));
                    // With the overflow marker still off there is no replacement for the removed light.
                    // Use the same physical light across APIs/reruns; slot order is intentionally unstable.
                    int receiver=used.Contains(10)?10:9;
                    debug.Select("Fade"); var fadeOn=Capture();
                    markers[receiver].castContactShadows=false; var fadeOff=Capture();
                    debug.Select("Raw"); var remaining=Capture();
                    var expected=used.Where(i=>i!=receiver).ToList();
                    for(int b=0;b<24;b++)
                    {
                        var bit=remaining.Select(c=>(Bits(c)&(1<<b))!=0).ToArray();
                        if(b==23) Assert.That(bit.Any(v=>v),Is.False,"stale overflow slot");
                        else expected.Remove(expected.Single(i=>signatures[i].SequenceEqual(bit)));
                    }
                    debug.Select("Off"); var crowdedOff=Capture();
                    // Trace masks are per light, but the packed edge fade is the maximum over ALL hits,
                    // even rays pointing at lights outside their direct lighting range.
                    var witness=Pixels(new RectInt(223,155,12,8)).Select(p=>p.y*W+p.x)
                        .Where(i=>!all[i].Equals(crowdedOff[i])).ToArray();
                    Assert.That(remaining.Any(c=>(Bits(c)&(1<<23))!=0),Is.False,"Stale or newly admitted 24th slot");
                    Metric("overflow/removed",overflow+"/"+receiver);
                    Metric("receiver24/crowdedChanged",witness.Length);
                    foreach(int i in witness) {
                        Metric("receiver24/x,y/fadeOn,Off",$"{i%W},{i/W}/{fadeOn[i].r},{fadeOff[i].r}");
                        Assert.That(signatures[receiver][i] && Bits(remaining[i])!=0 && fadeOn[i].r>fadeOff[i].r,Is.True);
                    }
                    // Keep all 25 physical lights illuminating the scene. With no competing marker
                    // there is no shared fade or newly admitted slot to confound the lighting A/B.
                    foreach(var m in markers)m.castContactShadows=false;
                    var none=Capture(); markers[receiver].castContactShadows=true;
                    var single=Pair("one-marker",none,Capture());
                    int x=23+(receiver-1)%6*42,y=30+(receiver-1)/6*43;
                    var localRoi=new RectInt(x-14,y-7,18,14);
                    var crowded=Pair("crowded-marker",crowdedOff,all);
                    crowded.RequireDark(crowded.Measure("local",localRoi));
                    var local=single.Measure("local",localRoi);single.RequireDark(local);
                    single.Record(false,local,single.Measure("receiver24",new RectInt(223,155,12,8)));
                    for(int j=1;j<=24;j++)if(j!=receiver)
                        {
                            var roi=new RectInt(23+(j-1)%6*42-10,30+(j-1)/6*43-4,12,8);
                            foreach(var p in Pixels(roi)) {
                                int i=p.y*W+p.x;
                                if(!all[i].Equals(crowdedOff[i]))
                                    Assert.That(signatures[receiver][i] && Bits(remaining[i])!=0 && fadeOn[i].r>fadeOff[i].r,
                                        Is.True,$"Unexplained crowded lighting change on receiver {j} at {p}");
                            }
                            var r=single.Measure("other-"+j,roi);
                            if(r.changedRGBA>0)single.Record(false,local,r);
                            single.RequireEqual(r);
                        }
                    debug.Select("Raw");
                }
            }
        }

        [UnityTest] public IEnumerator SceneViewCamera()
        {
            yield return Open(RenderingMode.Forward); Isolate(0,LightShadows.None);
            var sv=ScriptableObject.CreateInstance<UnityEditor.SceneView>();
            try {
            sv.ShowUtility();yield return null;
            if(!sv.camera) Assert.Ignore("No SceneView camera available in this editor session");
            var cam=sv.camera; Assert.That(cam.cameraType,Is.EqualTo(CameraType.SceneView));
            sv.sceneLighting=true;sv.drawGizmos=false;
            cam.targetTexture=Target;cam.aspect=View.aspect;cam.fieldOfView=View.fieldOfView;cam.orthographic=false;
            cam.nearClipPlane=View.nearClipPlane;cam.farClipPlane=View.farClipPlane;cam.cullingMask=View.cullingMask;
            cam.transform.SetPositionAndRotation(View.transform.position,View.transform.rotation);cam.ResetWorldToCameraMatrix();cam.ResetProjectionMatrix();
            // No yield: an editor repaint can reposition its camera between the A/B renders.
            Settings.enabled.Override(false);var off=Capture(cam);
            Settings.enabled.Override(true);var pair=Pair("SceneView",off,Capture(cam));var behind=pair.Measure("behind",Contact);
            pair.Record(false,behind,pair.Measure("whole",Full));pair.RequireDark(behind);
            } finally {sv.Close();}
        }

        static readonly string[] CameraCases = { "ortho", "msaa", "dynamic" };
        [UnityTest] public IEnumerator CameraConfigurations([ValueSource(nameof(CameraCases))] string config)
        {
            bool msaa=config=="msaa", dynamic=config=="dynamic";
            yield return Open(RenderingMode.Forward,msaa:msaa); Isolate(0,LightShadows.None);
            View.orthographic=config=="ortho";View.allowMSAA=msaa;View.allowDynamicResolution=dynamic;
            var desc=Target.descriptor;desc.msaaSamples=msaa?4:1;desc.useDynamicScale=dynamic;
            var rt=new RenderTexture(desc);float sx=ScalableBufferManager.widthScaleFactor,sy=ScalableBufferManager.heightScaleFactor;
            try{
                Assert.That(rt.Create(),Is.True);View.targetTexture=rt;Assert.That(rt.antiAliasing,Is.EqualTo(desc.msaaSamples));
                if(dynamic)ScalableBufferManager.ResizeBuffers(.75f,.75f);yield return null;
                Metric("scaledWidth",View.scaledPixelWidth);Metric("targetSamples",rt.antiAliasing);
                if(dynamic && View.scaledPixelWidth==W) Assert.Ignore("Requested 75% DRS; camera remained at native width " + W);
                int w=View.scaledPixelWidth,h=View.scaledPixelHeight;
                var scaled=new Texture2D(w,h,TextureFormat.RGBA32,false,true);
                try {
                    Settings.enabled.Override(false);yield return null;var off=Capture(View,scaled);
                    Settings.enabled.Override(true);yield return null;
                    var p=new ContactShadowImagePair(config,off,Capture(View,scaled),w,h);
                    RectInt Scale(RectInt r)=>new RectInt(r.x*w/W,r.y*h/H,r.width*w/W,r.height*h/H);
                    var behind=p.Measure("behind",Scale(Contact));Metric("darkened",behind.darkened);p.RequireDark(behind);p.RequireEqual(p.Measure("facing",Scale(Facing)));
                } finally { UnityEngine.Object.Destroy(scaled); }
            }finally{if(dynamic)ScalableBufferManager.ResizeBuffers(sx,sy);View.targetTexture=Target;rt.Release();UnityEngine.Object.Destroy(rt);}
        }
    }
}
#endif
