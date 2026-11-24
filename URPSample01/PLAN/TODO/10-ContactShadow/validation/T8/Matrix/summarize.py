"""Summarize real NUnit results, including failures/skips; never infer PASS from discovery."""
import json
from pathlib import Path
from xml.etree import ElementTree as ET

root = Path(__file__).resolve().parent
for api in ('D3D11', 'D3D12'):
    out = root / api
    xml = out / 'Lighting.xml'
    if not xml.is_file():
        continue
    run = ET.parse(xml).getroot()
    cases = []
    for case in run.iter('test-case'):
        entry = {'name': case.attrib['name'], 'result': case.attrib['result']}
        message = case.findtext('failure/message') or case.findtext('reason/message')
        if message:
            entry['message'] = message.strip()
        metrics = [s.strip() for s in (case.findtext('output') or '').splitlines()
                   if '=' in s and not s.strip().startswith(('at ', 'UnityEngine.'))]
        if metrics:
            entry['metrics'] = metrics
        cases.append(entry)
    settings = json.loads((out / 'LightingSettings.json').read_text(encoding='utf-8-sig'))
    report = {'apiRequested': api, 'finishedUtc': settings['finishedUtc'],
              'allSettingsRestored': settings['allSettingsRestored'], 'nunit': run.attrib['result'], 'cases': cases}
    (out / 'matrix.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
