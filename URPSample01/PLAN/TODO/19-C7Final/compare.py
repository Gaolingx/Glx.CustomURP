"""RGB8 metrics. Paths in output are file names; hashes identify both images. Do not mix routes."""
from pathlib import Path
from PIL import Image,ImageChops
import csv,hashlib,sys

def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def compare(a,b):
 with Image.open(a) as x,Image.open(b) as y:
  assert x.size==y.size
  d=ImageChops.difference(x.convert('RGB'),y.convert('RGB'));r,g,b1=d.split();h=ImageChops.lighter(r,ImageChops.lighter(g,b1)).histogram();n=x.width*x.height;over=sum(h[9:]);peak=max(i for i,c in enumerate(h) if c);mean=sum((i%256)*c for i,c in enumerate(d.histogram()))/(3*n)
 return [sha(a),sha(b),n,round(mean,8),peak,over,round(over/n,10),mean<=2 and peak<=24]
if __name__=='__main__':
 # Generic replay: python compare.py REFERENCE_DIR CANDIDATE_DIR
 a,b=map(Path,sys.argv[1:]);w=csv.writer(sys.stdout);w.writerow(['file','refSHA256','candidateSHA256','pixels','meanRGB8','peakRGB8','pxGT8','fractionGT8','meanPeakPass']);
 for f in sorted(a.glob('*.png')):
  if (b/f.name).is_file():w.writerow([f.name,*compare(f,b/f.name)])
