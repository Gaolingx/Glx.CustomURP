"""Run on the native Unity player host. No editor or project writes."""
from pathlib import Path
import hashlib, json, os, shutil, subprocess as sp, tempfile, uuid

out = Path(__file__).resolve().parent
scratch = Path(tempfile.mkdtemp(prefix='NativeIDs-', dir='D:/'))
editor = Path('D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data')
runner = Path('Assets/Tests/ClusteredDeferred/Runtime/ClusteredDeferredArenaRunner.cs')
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest()
def save(p,j): p.write_text(json.dumps(j,indent=1))
# Replace only the original PNG write; keep its exact bytes and use the original texture.
probe = '''                    var capturedFrame = Time.frameCount;
                    var pos = Shader.GetGlobalVectorArray("_AdditionalLightsPosition");
                    var col = Shader.GetGlobalVectorArray("_AdditionalLightsColor");
                    var binPath = Path.Combine(outputRoot, key + ".bin");
                    using (var writer = new BinaryWriter(File.Create(binPath)))
                        foreach (var arr in new[] {pos, col})
                            if (arr != null) foreach (var v in arr) {writer.Write(v.x);writer.Write(v.y);writer.Write(v.z);writer.Write(v.w);}
'''
receipt = '''                    var pngPath = Path.Combine(outputRoot, fileName);
                    File.WriteAllBytes(pngPath, texture.EncodeToPNG());
                    using (var hash = System.Security.Cryptography.SHA256.Create()) {
                        string Digest(string path) => BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
                        File.WriteAllLines(Path.Combine(outputRoot, key + ".tsv"), new[]{Environment.GetEnvironmentVariable("CD_NATIVE_RUN"),capturedFrame.ToString(),Time.frameCount.ToString(),SystemInfo.graphicsDeviceType.ToString(),mode.name,m_TransparentEnabled.ToString(),lightCount.ToString(),(pos?.Length ?? -1).ToString(),(col?.Length ?? -1).ToString(),Digest(binPath),Digest(pngPath),Shader.IsKeywordEnabled("_FORWARD_PLUS").ToString()});
                    }'''

def run():
    for version,root in [('C0',Path('D:/C7-warm0')),('candidate',Path('D:/C7candidate-D4wJLU'))]:
        source=root/'URPSample01'; build=scratch/version; build.mkdir(); src=source/runner
        text=src.read_text(encoding='utf-8-sig'); lines=text.splitlines(True); anchor=next(i for i,l in enumerate(lines) if 'if (screenshotRequested)' in l); i=next(i for i in range(anchor,len(lines)) if 'var texture = ScreenCapture.CaptureScreenshotAsTexture();' in lines[i]); lines.insert(i+1,probe); i=next(i for i in range(i+2,len(lines)) if 'File.WriteAllBytes(Path.Combine(outputRoot, fileName), texture.EncodeToPNG());' in lines[i]); lines[i]=receipt+'\n'; patched=build/runner.name; patched.write_text(''.join(lines),encoding='utf-8'); rsp,=(source/'Library/Bee/artifacts').glob('*PDevDbg.dag/Assembly-CSharp.rsp'); args=[]
        for l in rsp.read_text(encoding='utf-8-sig').splitlines():
            if l.startswith('-refout:'): continue
            if l.startswith('-out:'): l='-out:"'+(build/'Assembly-CSharp.dll').as_posix()+'"'
            if runner.as_posix() in l: l='"'+patched.as_posix()+'"'
            else: l=l.replace('"Library/',f'"{source.as_posix()}/Library/').replace('"Assets/',f'"{source.as_posix()}/Assets/')
            args.append(l)
        response=build/'player.rsp'; response.write_text('\n'.join(args)+'\n',encoding='utf-8'); sp.run([editor/'NetCoreRuntime/dotnet.exe',editor/'DotNetSdkRoslyn/csc.dll','@'+str(response)],cwd=build,check=True)
        for api in (11,12):
            orig=(source/'Builds/ClusteredDeferredArena' if version=='C0' else root/'candidate')/f'Direct3D{api}'; player=build/orig.name; shutil.copytree(orig,player); managed=Path('CdArena_Data/Managed'); changed={managed/('Assembly-CSharp.'+ext) for ext in ('dll','pdb')}
            for rel in changed: shutil.copyfile(build/rel.name,player/rel)
            manifest={}
            for f in orig.rglob('*'):
                if not f.is_file(): continue
                rel=f.relative_to(orig); a,b=sha(f),sha(player/rel); assert rel in changed or a==b; manifest[rel.as_posix()]=a if a==b else [a,b]
            save(out/f'{version}-{api}-build.json',dict(original=str(orig),runner=str(src),originalRunner=sha(src),instrumentedRunner=sha(patched),response=sha(rsp),instrumentedResponse=sha(response),files=manifest))
            for t in (False,True):
                name=f'{version}-{api}-{int(t)}'; dest=build/name; dest.mkdir(); rid=uuid.uuid4().hex; cmd=[player/'CdArena.exe',f'-force-d3d{api}','--cd-total-lights','256','--cd-light-counts','0,8,48,256','--cd-screenshot-lights','256','--cd-warmup','40','--cd-samples','150','--cd-output',dest,'-logFile',dest/'player.log']+(['--cd-transparent'] if t else []); sp.run(cmd,cwd=player,env=dict(os.environ,CD_NATIVE_RUN=rid),timeout=240,check=True); report=json.loads((dest/'CdPerf.json').read_text(encoding='utf-8-sig')); assert report['completed'] and not report['error'] and report['transparentFixture']==t and report['graphicsDeviceType']==orig.name and report['lightCounts']==[0,8,48,256]; modes=['ClusteredDeferred','ForwardPlus']+(['StencilDeferred'] if version=='C0' else []); assert {p.stem for p in dest.glob('*.png')}=={m+'_L256' for m in modes}; assert [m['name'] for m in report['modes']]==modes or set(m['name'] for m in report['modes'])==set(modes)
                for mode in modes:
                    stem=mode+'_L256'; fields=(dest/(stem+'.tsv')).read_text().splitlines(); runid,frame,end,device,route,trans,count,np,nc,hbin,hpng,kw=fields; assert runid==rid and frame==end and device==orig.name and route==mode and trans==str(t) and count=='256' and (kw=='True')==(mode=='ForwardPlus'); j=dict(run=rid,frame=int(frame),api=device,route=mode,transparent=t,lights=256,version=version,positions=int(np),colors=int(nc),forwardPlusKeyword=kw=='True',command=list(map(str,cmd)),fixture='ClusteredDeferredArena/256',source='Unity global vector array upload mirror; not a GPU binding readback')
                    for k,ext,h in [('ids','.bin',hbin),('native','.png',hpng)]:
                        f=dest/(stem+ext); assert sha(f)==h; target=out/(h+ext)
                        if not target.exists(): shutil.copyfile(f,target)
                        assert sha(target)==h; j[k]=dict(run=rid,frame=int(frame),path=target.name,sha256=h)
                    save(out/(name+'-'+mode+'.json'),j); print(name,mode,frame,np,nc,flush=True)
                shutil.copyfile(dest/'CdPerf.json',out/(name+'-sweep.json'))
if __name__=='__main__': run()
