import re,os,json,sys
sys.path.insert(0,'.')
from cslite import *
from ordinals import build
inh=json.load(open('inherit.json'))
parent=inh['parent']; files=inh['files']
def isa(c,base):
    seen=set()
    while c and c not in seen:
        if c==base: return True
        seen.add(c); c=parent.get(c)
    return False
bases=sys.argv[1].split(',')
targets=[c for c in parent if any(isa(c,b) for b in bases)]
RW="/home/claude/RWDecomp"
HDR=re.compile(r'(?m)^\t\t(?:(?:public|private|protected|internal|static|virtual|override|sealed|new|abstract|unsafe|extern)\s+)+[\w<>\[\],\.\? ]+?\s+(\w+)\s*(?:<[^>]*>)?\(')
out=[]
for c in sorted(set(targets)):
    p=os.path.join(RW,files[c]); raw=open(p,encoding='utf-8-sig',errors='replace').read(); s=sanitize(raw)
    for (cname,b,ob,cb,path) in iter_classes(s):
        if cname!=c or len(path)!=1: continue
        names=set(m.group(1) for m in HDR.finditer(s,ob,cb))
        for mn in names:
            for (params,mob,mcb) in find_methods(s,ob+1,cb,mn):
                if mcb<0: continue
                for f in build(s,mob,mcb):
                    body=' '.join(raw[f.body_a:f.body_b].split()) if f.kind!='query' else ''
                    out.append(dict(cls=c,method=mn,ord=f.ordinal,kind=f.kind,body=body,line=raw.count('\n',0,f.pos)+1,file=files[c]))
json.dump(out,open('allm_'+bases[0]+'.json','w'))
print(len(out))
