import json,sys,os,subprocess
from truth import T
modes=sys.argv[1:]
for m in modes:
    res={(x['cls'],x['method'],x['ord']):x for x in json.load(open(f'gizmos_{m}.json'))}
    ok=0; bad=[]
    for k,sub in T.items():
        if not sub: continue
        b=(res.get(k) or {}).get('body','')
        if sub in b: ok+=1
        else: bad.append(k)
    print(m, ok, '/', sum(1 for v in T.values() if v), 'bad:', [f"{a}.{b[:12]}#{c}" for a,b,c in bad][:40])
