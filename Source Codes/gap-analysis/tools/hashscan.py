import re,os,json,sys
sys.path.insert(0,'.')
from cslite import *
inh=json.load(open('inherit.json')); parent=inh['parent']; files=inh['files']
RW="/home/claude/RWDecomp"
# classes overriding GetHashCode
over=set()
for c,f in files.items():
    pass
for p in load_cs_files(RW,('DelaunatorSharp','Gilzoide','Ionic','KTrie','Unity.Collections','UnityStandardAssets')):
    if 'UnitySource' in p: continue
    t=open(p,encoding='utf-8-sig',errors='replace').read()
    if 'override int GetHashCode' in t:
        s=sanitize(t)
        for (n,b,o,c,path) in iter_classes(s):
            body=s[o:c]
            # only direct (not nested) - approximate
            if 'override int GetHashCode' in body: over.add(n)
def hashed(c):
    seen=set()
    while c and c not in seen:
        if c in over: return True
        seen.add(c); c=parent.get(c)
    return False
structs=set()
for p in load_cs_files(RW,()):
    if 'UnitySource' in p: continue
    t=open(p,encoding='utf-8-sig',errors='replace').read()
    structs.update(re.findall(r'\bstruct\s+(\w+)',t)); structs.update(re.findall(r'\benum\s+(\w+)',t))
prim={'int','string','float','bool','long','short','byte','uint','ulong','char','double','Type','object','IntVec3','IntVec2','PlanetTile'}
rx=re.compile(r'(HashSet|Dictionary)<\s*([A-Z]\w*)\b')
res={}
for p in load_cs_files(RW,('DelaunatorSharp','Gilzoide','Ionic','KTrie','Unity.Collections','UnityStandardAssets','LudeonTK')):
    if 'UnitySource' in p: continue
    t=open(p,encoding='utf-8-sig',errors='replace').read()
    for m in rx.finditer(t):
        T=m.group(2)
        if T in prim or T in structs or hashed(T) or T not in parent: continue
        if T.endswith('Def') or parent.get(T,'').endswith('Def'): continue
        res.setdefault(T,set()).add(os.path.relpath(p,RW))
for T,fs in sorted(res.items()):
    print(T, '<-', ' '.join(sorted(fs))[:220])
