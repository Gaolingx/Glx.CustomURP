import hashlib
import json
import math
import struct
from pathlib import Path


ROOT = Path(__file__).resolve().parent
WIDTH, HEIGHT = 640, 360


def load(stage, api, route):
    data = (ROOT / f'material-{stage}' / api / f'{route}.rgba32f').read_bytes()
    if len(data) != WIDTH * HEIGHT * 16:
        raise ValueError(f'Incomplete {stage}/{api}/{route} frame')
    return data


def compare(first, second):
    total = peak = nonfinite = 0
    mask = bytearray(WIDTH * HEIGHT)
    tiles = [[0] * 8 for _ in range(6)]
    for i, (a, b) in enumerate(zip(struct.iter_unpack('<4f', first), struct.iter_unpack('<4f', second))):
        rgb = [abs(x - y) * 255 for x, y in zip(a[:3], b[:3])]
        if not all(map(math.isfinite, rgb)):
            nonfinite += 1
            continue
        total += sum(rgb)
        delta = max(rgb)
        peak = max(peak, delta)
        if delta > 8:
            mask[i] = 1
            tiles[(i // WIDTH) // 60][(i % WIDTH) // 80] += 1
    return dict(mean=total / (3 * len(mask)), max=peak, nonfinite=nonfinite, over8=sum(mask),
                maskSHA256=hashlib.sha256(mask).hexdigest(), tilesBottomUp=tiles), mask


def measure(api):
    frames = {(s, r): load(s, api, r) for s in ('baseline', 'candidate') for r in ('clustered', 'forwardPlus')}
    result = {'frameSHA256': {f'{s}/{r}': hashlib.sha256(data).hexdigest() for (s, r), data in frames.items()}}
    masks = []
    for s in ('baseline', 'candidate'):
        result[s], mask = compare(frames[s, 'clustered'], frames[s, 'forwardPlus'])
        masks.append(mask)
    result['addedOver8'] = sum(not a and b for a, b in zip(*masks))
    result['removedOver8'] = sum(a and not b for a, b in zip(*masks))
    for r in ('clustered', 'forwardPlus'):
        result[r], _ = compare(frames['baseline', r], frames['candidate', r])
    return result


if __name__ == '__main__':
    print(json.dumps({api: measure(api) for api in ('Direct3D11', 'Direct3D12')}, indent=2))
