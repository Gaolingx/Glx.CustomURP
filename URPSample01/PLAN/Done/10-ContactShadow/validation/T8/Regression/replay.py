"""Windows: python replay.py [--scratch-parent E:/tmp] [--stage regression|performance|shader]."""
import argparse, json, shutil, subprocess, tempfile, re
from pathlib import Path
from xml.etree import ElementTree as ET
from collections import Counter

here = Path(__file__).resolve().parent
project = next(p for p in here.parents if (p/'ProjectSettings/ProjectVersion.txt').exists())
p0 = here.parent.parent/'P0'
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--unity', default=r'D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe')
p.add_argument('--scratch-parent', default=None)
p.add_argument('--stage', choices=['regression','performance','shader'], default='regression')
p.add_argument('--batch-cpu',action='store_true',help='Measure command-buffer CPU separately in a batch player, not as a GPU substitute.')
p.add_argument('--resume',type=Path,help='Reuse only a marked scratch project for the same stage after a failure.')
a = p.parse_args()
scratch = (a.resume if a.resume else Path(tempfile.mkdtemp(prefix='csT8-', dir=a.scratch_parent))).resolve()
if scratch.is_relative_to(project.parent) or project.is_relative_to(scratch): raise ValueError('Scratch must be outside repository')
marker=scratch/'.csT8-isolated'
print('Isolated scratch (logs retained):', scratch, flush=True)
if a.resume:
    assert scratch.name.startswith('csT8-') and marker.read_text()==a.stage
else:
    marker.write_text(a.stage)
    for sub in ['ProjectSettings','Packages','Assets']:
        shutil.copytree(project/sub,scratch/sub)

def unity(name,*args, api=11):
    log=scratch/(name+'.log')
    subprocess.run([a.unity,'-batchmode','-projectPath',str(scratch),f'-force-d3d{api}','-logFile',str(log),*map(str,args)],check=True,timeout=3600)

def save(path,obj): path.write_text(json.dumps(obj,indent=2)+'\n',encoding='utf8')

if a.stage=='regression':
    for mode in ['EditMode','PlayMode']:
        ref=ET.parse(p0/(mode+'.xml'))
        fixtures=sorted({x.attrib['fullname'] for x in ref.findall('.//test-suite[@type="TestFixture"]')})
        xml=scratch/(mode+'.xml')
        unity(mode,'-runTests','-testPlatform',mode,'-testFilter',';'.join(fixtures),'-testResults',xml)
        run=ET.parse(xml)
        assert Counter(x.attrib['fullname'] for x in run.iter('test-case'))==Counter(x.attrib['fullname'] for x in ref.iter('test-case'))
        assert all(x.attrib['result']=='Passed' for x in run.iter('test-case'))
        shutil.copyfile(xml,here/xml.name)
    unity('T12','-executeMethod','ScreenSpaceLightingT12Verification.RunT12Verification','-ssl-t12-output',scratch/'T12','-quit')
    r=json.loads((scratch/'T12/T12Verification.json').read_text(encoding='utf-8-sig'))
    ref=json.loads((p0/'T12/T12Verification.json').read_text(encoding='utf-8-sig'))
    assert r['passed'] and [(m['name'],[c['name'] for c in m['cases']]) for m in r['modes']]==[(m['name'],[c['name'] for c in m['cases']]) for m in ref['modes']]
    assert all(m['passed'] and all(c['passed'] for c in m['cases']) for m in r['modes'])
    save(here/'T12.json',r)

if a.stage=='shader':
    shader_inputs=[Path('Packages/com.unity.render-pipelines.universal@14.0.12')/s for s in ['ShaderLibrary/Shadows.hlsl','Shaders/Utils/ClusteredDeferred.compute']]
    for rel in shader_inputs: shutil.copyfile(project/rel,scratch/rel)
    editor=scratch/'Assets/Editor';editor.mkdir(exist_ok=True)
    shutil.copyfile(here/'T8ShaderCheck.cs',editor/'T8ShaderCheck.cs')
    freeze=json.loads((p0/'Baseline.json').read_text())['implementationHead']
    regression=Path('Packages/com.unity.render-pipelines.universal@14.0.12/Tests/Editor/ClusteredDeferred/ClusteredDeferredEditorTests.cs')
    legacy_blob=subprocess.check_output(['git','-C',str(project),'rev-parse',f'HEAD:{project.name}/{regression.as_posix()}'],text=True).strip()
    assert legacy_blob==subprocess.check_output(['git','-C',str(project),'hash-object',str(regression)],text=True).strip()
    shutil.copyfile(project/regression,scratch/regression)
    folder=regression.parent.parent/'RendererFeatures/ContactShadows'
    for rel in (project/folder).glob('ContactShadowShader*'):
        shutil.copyfile(rel,scratch/folder/rel.name)
    def groups(source): return [line.strip().split()[2:] for line in source.splitlines() if line.strip().startswith('#pragma multi_compile')]
    compute=shader_inputs[1]
    frozen=groups(subprocess.check_output(['git','-C',str(project),'show',f'{freeze}:{project.name}/{compute.as_posix()}'],text=True))
    current=groups((scratch/compute).read_text())
    assert current==frozen+[['_', '_CONTACT_SHADOWS_DEFERRED']]
    failures=[]
    suites={}
    expected='UnityEditor.Rendering.Universal.Tests.ClusteredDeferredShaderTests.ComputeDeclaresTheDocumentedKeywordMatrix'
    for api in [11,12]:
        xml=scratch/f'Shader{api}.xml'
        api_name=f'Direct3D{api}'
        xml.unlink(missing_ok=True)
        try:
            unity(f'Compile{api}','-runTests','-testPlatform','EditMode','-testFilter','ContactShadowShaderCompileTests;ClusteredDeferredShaderTests;ContactShadowShaderBudgetTests','-testResults',xml,api=api)
        except subprocess.CalledProcessError as ex:
            if ex.returncode!=2 or not xml.exists(): raise
        tests=ET.parse(xml)
        counts={}
        for fixture,n in [('ContactShadowShaderCompileTests',19),('ClusteredDeferredShaderTests',4),('ContactShadowShaderBudgetTests',2)]:
            cases=[t for t in tests.iter('test-case') if t.attrib['classname'].split('.')[-1]==fixture]
            assert len({t.attrib['fullname'] for t in cases})==len(cases)==n
            counts[fixture]=dict(Counter(t.attrib['result'] for t in cases))
        suites[api_name]=counts
        bad=[t for t in tests.iter('test-case') if t.attrib['result']!='Passed']
        assert len(bad)==1 and bad[0].get('fullname')==expected and bad[0].get('result')=='Failed'
        assert 'The clustered deferred keyword groups changed' in bad[0].findtext('failure/message','')
        assert tests.getroot().get('result') in ('Failed','Failed(Child)') and len(list(tests.iter('test-case')))==25
        failures.extend((api,t.attrib['fullname']) for t in tests.iter('test-case') if t.attrib['result']!='Passed')
        shutil.copyfile(xml,here/xml.name)
        unity(f'ClusteredCheck{api}','-executeMethod','ClusteredDeferredShaderCheck.RunAndExit',api=api)
        txt=next((scratch/'PLAN').rglob('T3ShaderCheck.txt' if api==11 else 'T3ShaderCheck_Direct3D12.txt'))
        shutil.copyfile(txt,here/f'ClusteredCheck{api}.txt')
    reports={}
    for tag in ['current','baseline']:
        if tag=='baseline':
            # Identical settings/importer: restore the frozen existing shader inputs only in scratch.
            # Newly introduced standalone shaders cannot change these material shader variant axes.
            for rel in shader_inputs:
                data=subprocess.check_output(['git','-C',str(project),'show',f'{freeze}:{project.name}/{rel.as_posix()}']);(scratch/rel).write_bytes(data)
        for api in [11,12]:
            key=tag if api==11 else tag+'D3D12'
            unity(key,'-quit','-executeMethod','T8ShaderCheck.Run',api=api)
            reports[key]=json.loads((scratch/'ShaderReport.json').read_text(encoding='utf-8-sig'))
            assert reports[key]['api']==f'Direct3D{api}' and len(reports[key]['materials'])==5
    materials_match=all(reports['current'+suffix]==reports['baseline'+suffix] for suffix in ['', 'D3D12'])
    save(here/'ShaderComparison.json',{'baselineShaderInputs':freeze,'scope':'Imported material keyword inventories (actual player variant counts in PlayerStripping.json), per-subshader SRP Batcher code 0=compatible; identical current project with pre-contact shader inputs for comparison.',**reports})
    failed_cases=[{'api':f'Direct3D{api}','test':case} for api,case in failures]
    save(here/'ShaderGate.json',{'passed':False,'expectedFailureContractMet':materials_match,'legacySourceBlob':legacy_blob,'suites':suites,'expectedLegacyFailures':failed_cases,'unexpectedFailures':[],'declaredComputeMatrix':{'baselineGroups':[2,4,2,5,2,2,2,2],'baselineCombinations':1280,'addedAxis':'_CONTACT_SHADOWS_DEFERRED (compute only; off kernel must not declare the contact texture)','currentCombinations':2560},'materialComparison':'ShaderComparison.json','regressionContract':'Original eight widths and 1280 product retained; additionally assert exact nine declarations and 2560 product. Frozen source compared by token and position.','frozenDeclarations':frozen,'actualDeclarations':current})
    print('Final NUnit results (raw suites failed as expected):', suites, flush=True)
    assert materials_match, 'Shader counts, batching codes or diagnostics changed: inspect comparison.'

if a.stage=='performance':
    import statistics, math
    folder=here.parent/'Performance'
    shutil.copyfile(folder/'T8Performance.cs',scratch/'Assets/T8Performance.cs')
    shutil.copyfile(project/'Assets/Tests/ContactShadow/Scenes/ContactShadowStress.unity',scratch/'Assets/Tests/ContactShadow/Scenes/ContactShadowStress.unity')
    (scratch/'Player').mkdir(exist_ok=True)
    unity('Build','-quit','-executeMethod','T8Performance.Build')
    build=(scratch/'Build.log').read_text(encoding='utf-8',errors='replace')
    blocks=re.findall(r'Compiling shader "Universal Render Pipeline/Lit" pass "ForwardLit" \((\w+)\)\s+Full variant space:\s+(\d+)\s+After settings filtering:\s+(\d+)\s+After built-in stripping:\s+(\d+)\s+After scriptable stripping:\s+(\d+)',build)
    assert {b[0] for b in blocks}=={'vp','fp'}, 'Missing player shader stripping statistics'
    compute_counts={}
    for block in build.split('Compiling compute shader "')[1:]:
        name=block.split('"',1)[0]
        if name not in ('ClusteredDeferred','ContactShadows','ClusterBuilder','DepthPyramid'): continue
        kept,total=map(int,re.search(r'(\d+) of (\d+) variants left',block).groups())
        local,remote,compiled=map(int,re.search(r'Local cache hits (\d+), remote cache hits (\d+), compiled (\d+) variants',block).groups())
        assert local+remote+compiled==kept
        compute_counts[name]=dict(total=total,kept=kept,localCache=local,remoteCache=remote,compiled=compiled)
    assert set(compute_counts)=={'ClusteredDeferred','ContactShadows','ClusterBuilder','DepthPyramid'}
    save(here/'PlayerStripping.json',{'computeBuildTotals':compute_counts,'source':'T8 development Windows player, Direct3D11/12 enabled; build log shared by both runtimes','shader':'Universal Render Pipeline/Lit','pass':'ForwardLit','stages':[dict(zip(['stage','full','settings','builtin','scriptable'],b)) for b in blocks],'reference':'C0VariantBaseline.json T5 budget: 32 vertex / 6144 fragment after scriptable stripping. Not the shadow-matrix T4 configuration.'})
    for api in [11,12]:
        exe=scratch/'Player/T8.exe'
        result=exe.parent/'timings.json'
        result.unlink(missing_ok=True)
        subprocess.run([exe,f'-force-d3d{api}','-screen-fullscreen','0','-screen-width','320','-screen-height','180',*(['-batchmode'] if a.batch_cpu else []),'-logFile',scratch/f'Player{api}.log'],cwd=scratch,check=True,timeout=1800)
        r=json.loads(result.read_text());assert r['api']==f'Direct3D{api}' and len(r['rows'])==36
        for row in r['rows']:
            for key in ['gpuMs','cmdCpuMs','inlineCpuMs']:
                v=sorted(row[key]);row[key]={'validFrames':len(v),'median':statistics.median(v) if v else None,'p95':v[math.ceil(.95*len(v))-1] if v else None}
        r['sampling']='Windowed development player (batchmode produced no GPU marker samples). One explicit T7 stress camera render per frame to 1920x1080 RT. Contact Shadows compute-pass marker only (no pyramid/cleanup). 60 warm frames, then 120 previous-frame recorder reads per cell. Keep positive durations with exactly one block; null = unavailable, not zero. CPU command-buffer and inline timings are distinct; neither is GPU or whole-frame time.'
        if a.batch_cpu: r['sampling']=r['sampling'].replace('Windowed development player (batchmode produced no GPU marker samples)', 'Batchmode development player')
        save(folder/(('BatchCPU-' if a.batch_cpu else '')+f'D3D{api}.json'),r)
