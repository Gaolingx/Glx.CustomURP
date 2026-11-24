"""Keep only compact diagnostics in evidence; move raw logs to a local temp folder."""
import collections, gzip, json, re, shutil, tempfile
from pathlib import Path


def extract(output):
    archive = Path(tempfile.mkdtemp(prefix='T8PlayerLogs-'))
    diagnostics = {}
    for path in sorted(output.glob('*/*.log.gz')):
        key = path.parent.name + '/' + path.name
        counts = collections.Counter()
        with gzip.open(path, 'rt', encoding='utf-8', errors='replace') as lines:
            for raw in lines:
                line = raw.strip()
                m = re.match(r"Shader (warning|error) in '([^']+)': (.*?) at (?:kernel|.*?\(\d+\))", line)
                if m: counts['shader %s: %s: %s' % m.groups()] += 1
                elif re.search(r'\b(?:warning|error) [A-Z]+\d+:', line): counts[re.sub(r'^.*?((?:warning|error) [A-Z]+\d+:)',r'\1',line)] += 1
                elif re.search(r'(?i)(^(?:error|exception|warning)\b|\b(?:exception|error|warning):|\b(?:not supported|missing shader|shader is unsupported)\b)',line): counts[line] += 1
        dest=archive/key; dest.parent.mkdir(exist_ok=True); shutil.move(path,dest)
        diagnostics[key]=dict(counts)
    (output/'diagnostics.json').write_text(json.dumps({'localLogDirectory':str(archive),'diagnosticLineGroups':diagnostics},indent=2))

if __name__ == '__main__':
    import sys
    extract(Path(sys.argv[1]))
