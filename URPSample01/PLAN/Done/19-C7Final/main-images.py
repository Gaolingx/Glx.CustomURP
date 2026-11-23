"""Compare uninstrumented native T5 PNGs. These captures have no frame-bound light ID stream."""
import csv
from pathlib import Path
import sys
sys.dont_write_bytecode = True
from compare import compare

root, frozen, output = map(Path, sys.argv[1:])
with output.open('x', newline='') as f:
    writer = csv.writer(f)
    writer.writerow(['ref', 'api', 'transparent', 'rep', 'file', 'refSHA', 'candidateSHA', 'pixels', 'meanRGB8', 'peakRGB8', 'pxGT8', 'fractionGT8', 'meanPeakPass'])
    for api in (11, 12):
        for transparent in (0, 1):
            for rep in (1, 2, 3):
                prefix = f'{api}-{transparent}-{rep}'
                for route in ('ForwardPlus', 'ClusteredDeferred'):
                    for lights in (0, 8, 48, 256):
                        name = f'{route}_L{lights}.png'
                        candidate = root / (prefix + '-candidate') / name
                        for label, ref in [('frozenPNG', frozen / f't5-d3d{api}' / ('cluster-on' if transparent else 'control')),
                                           ('C0', root / (prefix + '-C0')), ('parent', root / (prefix + '-parent'))]:
                            writer.writerow([label, api, transparent, rep, name, *compare(ref/name, candidate)])
