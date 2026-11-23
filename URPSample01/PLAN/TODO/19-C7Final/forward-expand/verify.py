"""Windows replay: run after candidate builds and performance measurements finish."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess as sp
import tempfile

out = Path(__file__).resolve().parent
project = out.parents[3]
root = Path('D:/C7candidate-D4wJLU')
source = root / 'URPSample01'
runner = Path('Assets/Tests/ClusteredDeferred/Runtime/ClusteredDeferredArenaRunner.cs')
shader = Path('Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ClusterBuilder.compute')
editor = Path('D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data')
scratch = Path(tempfile.mkdtemp(prefix='ForwardExpand-', dir='D:/'))


def digest(p):
    with p.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def idle():
    query = "Get-CimInstance Win32_Process | Where-Object {$_.Name -eq 'CdArena.exe' -or ($_.Name -match 'python|pwsh' -and $_.CommandLine -match 'perf\\.(py|ps1)')} | Select-Object ProcessId,Name,CommandLine | ConvertTo-Json"
    result = sp.check_output(['powershell', '-NoProfile', '-Command', query], text=True).strip()
    if result:
        raise RuntimeError('Performance/arena process active: ' + result)


idle()
for api in (11, 12):
    receipt = root / f'candidate/edge{api}/CdEdgeTiles.json'
    assert json.loads(receipt.read_text())['passed']
assert digest(project / shader) == digest(source / shader)
proof = dict(scratch=str(scratch), candidate=str(source), shader=str(shader), shaderSHA256=digest(source / shader),
             runnerSHA256=digest(project / runner), players={})
rsp = next((source / 'Library/Bee/artifacts').glob('*PDevDbg.dag/Assembly-CSharp.rsp'))
lines = []
for line in rsp.read_text(encoding='utf-8-sig').splitlines():
    if line.startswith('-refout:'):
        continue
    if line.startswith('-out:'):
        line = f'-out:"{scratch.as_posix()}/Assembly-CSharp.dll"'
    line = line.replace('"Library/', f'"{source.as_posix()}/Library/')
    line = line.replace('"Assets/', f'"{source.as_posix()}/Assets/') if runner.as_posix() not in line else f'"{(project / runner).as_posix()}"'
    lines.append(line)
response = out / 'player-compiler.rsp'
response.write_text('\n'.join(lines) + '\n', encoding='utf-8')
cmd = [editor/'NetCoreRuntime/dotnet.exe', editor/'DotNetSdkRoslyn/csc.dll', '@'+str(response)]
proof.update(compile=list(map(str, cmd)), originalResponse=str(rsp), originalResponseSHA256=digest(rsp))
with (out / 'compiler.log').open('w') as f:
    sp.run(cmd, cwd=scratch, stdout=f, stderr=sp.STDOUT, check=True)
for api in (11, 12):
    idle()
    src = root / f'candidate/Direct3D{api}'
    dst = scratch / src.name
    shutil.copytree(src, dst)
    managed = Path('CdArena_Data/Managed')
    for ext in ('dll', 'pdb'):
        filename = 'Assembly-CSharp.' + ext
        shutil.copyfile(scratch / filename, dst / managed / filename)
    # Compare the complete copied player, including all serialized shader data and native binaries.
    hashes = {}
    for file in src.rglob('*'):
        if file.is_file():
            rel = file.relative_to(src)
            a, b = digest(file), digest(dst / rel)
            if rel not in (managed/'Assembly-CSharp.dll', managed/'Assembly-CSharp.pdb'):
                assert a == b, rel
            hashes[rel.as_posix()] = a if a == b else {'candidate': a, 'verification': b}
    dest = out / src.name
    dest.mkdir(exist_ok=True)
    cmd = [dst/'CdArena.exe', f'-force-d3d{api}', '--cd-forward-expand', '--cd-output', dest, '-logFile', dest/'player.log']
    idle()
    rc = sp.run(cmd, cwd=dst, timeout=90).returncode
    j = json.loads((dest/'CdForwardExpand.json').read_text())
    proof['players'][src.name] = dict(command=list(map(str, cmd)), exitCode=rc, candidate=str(src), sha256=hashes,
                                     api=j['api'], passed=j['passed'])
    (out/'provenance.json').write_text(json.dumps(proof, indent=2))
    print(api, rc, [(c['name'],c['passed'],c['prunedTiles']) for c in j['cases']], flush=True)
assert all(p['exitCode'] == 0 and p['passed'] and p['api'] == name for name,p in proof['players'].items())
