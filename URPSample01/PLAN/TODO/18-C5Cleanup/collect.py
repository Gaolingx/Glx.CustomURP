"""Extract comparable T5 counters from fresh arena builds; never copy old results."""
import hashlib
import json
from pathlib import Path
import re
import sys

project = Path(sys.argv[1]).resolve()
output = Path(__file__).resolve().parent
report = {}
for api in ('11', '12'):
    log = project / f'build-{api}.log'
    if not log.exists():
        report[api] = {'status': 'no build log'}
        continue
    raw = log.read_bytes()
    text = raw.decode('utf-8', errors='replace')
    match = re.search(r'Compiling shader "Universal Render Pipeline/Lit" pass "ForwardLit" \(fp\)\s+'
                      r'Full variant space:\s*(\d+)\s+After settings filtering:\s*(\d+)\s+'
                      r'After built-in stripping:\s*(\d+)\s+After scriptable stripping:\s*(\d+)', text)
    compute = re.search(r'Compiling compute shader "ClusteredDeferred"\s+starting stripping\.\.\.\s+'
                        r'finished in [\d.]+ seconds\. (\d+) of (\d+) variants left', text)
    summary = project / f'PLAN/Done/16-CleanupBaseline/c4/player/CdArenaBuildSummary-Direct3D{api}.json'
    report[api] = {
        'log': str(log), 'logSHA256': hashlib.sha256(raw).hexdigest(),
        'buildSummary': json.loads(summary.read_text()) if summary.exists() else None,
        'LitForwardLitFp_full_settings_builtin_scriptable': list(map(int, match.groups())) if match else None,
        'clusteredCompute_retained_declared': list(map(int, compute.groups())) if compute else None,
        'shaderErrorLines': len(re.findall(r'^Shader error', text, re.M | re.I)),
        'shaderWarningLines': len(re.findall(r'^Shader warning', text, re.M | re.I)),
        'missingReferenceOrScriptLines': len(re.findall(r'^.*(?:MissingReferenceException|referenced script.*missing).*$', text, re.M | re.I)),
        'obsoleteKeywords': sorted(set(re.findall(r'_DEFERRED_(?:FIRST_LIGHT|STENCIL)\b', text))),
    }
(output / 'variants.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
