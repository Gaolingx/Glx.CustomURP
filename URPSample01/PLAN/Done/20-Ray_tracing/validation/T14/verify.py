"""Gate the T14 replay (EditMode, PlayMode, T12 graphics matrix) against the frozen minimums.
Usage: verify.py [evidence-dir]"""
import json, sys, xml.etree.ElementTree as ET
from pathlib import Path

OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent
errors = []
def require(x, msg):
    if not x: errors.append(msg)

def load(p):
    return json.loads(p.read_text())

def nunit(path):
    root = ET.parse(path).getroot()
    run = root if root.tag == 'test-run' else root.find('test-run')
    require(run is not None, f'{path.name}: no test-run element')
    return run

MIN_EDITMODE_PASSED = 153
MIN_PLAY_MODE = 6
MIN_CASES = {'Forward': 34, 'ForwardPlus': 34, 'Deferred': 35}

edit = nunit(OUT / 'UnityEditModeTests.xml')
play = nunit(OUT / 'UnityPlayModeTests.xml')
edit_passed, edit_failed = int(edit.get('passed')), int(edit.get('failed'))
play_passed, play_failed = int(play.get('passed')), int(play.get('failed'))
require(edit_failed == 0 and edit_passed >= MIN_EDITMODE_PASSED,
        f'EditMode passed {edit_passed} failed {edit_failed}')
require(play_failed == 0 and play_passed >= MIN_PLAY_MODE,
        f'PlayMode passed {play_passed} failed {play_failed}')

t12 = load(OUT / 'Graphics' / 'T12Verification.json')
require(t12.get('completed') and t12.get('passed'), f"T12 completed/passed {t12.get('completed')}/{t12.get('passed')}")
modes = {mode['name']: mode for mode in t12.get('modes', [])}
for name, minimum in MIN_CASES.items():
    mode = modes.get(name)
    require(mode is not None, f'T12 missing {name}')
    if mode is None: continue
    require(mode['passed'] and not mode.get('issues'), f"T12 {name} issues {mode.get('issues')}")
    require(len(mode['cases']) >= minimum, f"T12 {name} cases {len(mode['cases'])} < {minimum}")
    require(mode['cases'] and all(case.get('passed', False) for case in mode['cases']),
            f'T12 {name} has failing cases')

restoration = {}
for name in ('UnityEditModeTestsSettingsRestoration', 'UnityPlayModeTestsSettingsRestoration',
             'UnityT14GraphicsRegressionSettingsRestoration'):
    path = OUT / (name + '.json') if 'UnityT14Graphics' not in name else OUT / 'Graphics' / (name + '.json')
    if not path.exists():
        errors.append(f'missing {path.relative_to(OUT)}')
        continue
    record = load(path)
    restoration[name] = {'exitCode': record.get('unityExitCode'),
                         'allSettingsRestored': record.get('allSettingsRestored'),
                         'invocationError': record.get('invocationError')}
    require(record.get('unityExitCode') == 0 and record.get('allSettingsRestored') is True and
            not record.get('invocationError'), f'{name} did not restore settings cleanly')

summary = {
    'passed': not errors,
    'errors': errors,
    'editMode': {'passed': edit_passed, 'failed': edit_failed,
                 'total': int(edit.get('total')), 'skipped': int(edit.get('skipped', 0))},
    'playMode': {'passed': play_passed, 'failed': play_failed,
                 'total': int(play.get('total')), 'skipped': int(play.get('skipped', 0))},
    't12': {'device': t12.get('graphicsDeviceType'), 'passed': t12.get('passed'),
            'modes': {name: {'cases': len(mode['cases']), 'passed': mode['passed']}
                      for name, mode in modes.items()}},
    'restoration': restoration,
}
(OUT / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(errors)
sys.exit(bool(errors))
