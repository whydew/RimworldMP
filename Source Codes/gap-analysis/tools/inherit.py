import re,os,json,sys
sys.path.insert(0,'.')
from cslite import load_cs_files
RW="/home/claude/RWDecomp"
SKIP=('DelaunatorSharp','Gilzoide','Ionic','KTrie','Unity.Collections','UnityStandardAssets','System.Collections')
rx=re.compile(r'\b(?:public|internal|private|protected)?\s*(?:static\s+|abstract\s+|sealed\s+|partial\s+)*class\s+(\w+)(?:<[^>{]*>)?\s*(?::\s*([\w\.<>, ]+))?')
parent={}; files={}
for p in load_cs_files(RW,SKIP):
    if 'UnitySourceGenerated' in p: continue
    t=open(p,encoding='utf-8-sig',errors='replace').read()
    for m in rx.finditer(t):
        b=(m.group(2) or '').split(',')[0].strip()
        b=re.sub(r'<.*','',b).split('.')[-1]
        parent.setdefault(m.group(1),b); files.setdefault(m.group(1),os.path.relpath(p,RW))
def isa(c,base):
    seen=set()
    while c and c not in seen:
        if c==base: return True
        seen.add(c); c=parent.get(c)
    return False
json.dump({'parent':parent,'files':files},open('inherit.json','w'))
reg=json.load(open('mpreg.json')); types=set(reg['types'])
# also any identifier mention in MP source
import subprocess
mpsrc=subprocess.run(['bash','-c','cat "/home/claude/RimworldMP/Source Codes/Source/Client/"**/*.cs "/home/claude/RimworldMP/Source Codes/Source/Client/"*.cs 2>/dev/null; find "/home/claude/RimworldMP/Source Codes/Source/Client" "/home/claude/RimworldMP/Source Codes/Source/MultiplayerLoader" -name "*.cs" -exec cat {} +'],capture_output=True,text=True).stdout
words=set(re.findall(r'\b[A-Z]\w+\b',mpsrc))
for base in sys.argv[1:]:
    subs=sorted(c for c in parent if isa(c,base) and c!=base)
    unref=[c for c in subs if c not in words]
    print(f"== {base}: {len(subs)} subclasses, {len(unref)} not mentioned in MP")
    print(' '.join(unref))
