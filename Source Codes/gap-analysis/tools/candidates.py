import json,re,sys
reg=json.load(open('mpreg.json'))
res={m:json.load(open(f'gizmos_{m}.json')) for m in ['hybrid_any']}
covered_bodies=set()
for m,r in res.items():
    for x in r:
        if x['status'] in ('REG','PATCHED'): covered_bodies.add((x['cls'],x['method'],x['body']))
synced=set(r['method'] for r in reg['method'] if r['method'])|{'TryTakeOrderedJob','AddDesignation','RemoveDesignation','TryAssignPawn','TryUnassignPawn','OrderForceTarget','Forbidden','Drafted','TakeOrderedJob','StartJob','SetForbidden','EndCurrentJob','CurrentPolicy','CurrentApparelPolicy','CurrentFoodPolicy','Master','AreaRestrictionInPawnCurrentMap','Name','Priority','Medical','Active','Paused','Title'}
wholesynced={(r['type'],r['method']) for r in reg['method']}
fieldtypes={r['type'] for r in reg['field']}
def strip_locals(b):
    b=re.sub(r'\b(?:var|[A-Z]\w*(?:<[^>]*>)?(?:\[\])?\??)\s+[a-z_]\w*\s*=(?!=)',' ',b)   # local decl
    b=re.sub(r'\b(?:int|float|bool|string|double|long)\s+\w+\s*=(?!=)',' ',b)
    b=re.sub(r'\bfor\s*\([^;]*;',' ',b)
    b=re.sub(r'\b(num\d*|i|j|k)\s*(\+\+|--|\+=|-=|=)',' ',b)
    return b
MUT_RX = re.compile(r'(?<![=!<>+\-*/])=(?!=|>)|\+\+|--|\+=|-=|\.Add\(|\.Remove\w*\(|\.Clear\(|Destroy\w*\(|\bSet\w+\(|\bTry\w+\(|\bNotify_\w+\(|\bStart\w*\(|\bCancel\w*\(|\bEject\w*\(|\bToggle\w*\(|\bDrop\w*\(|\bLaunch\w*\(|\bSpawn\w*\(|\bKill\(|\bFinish\w*\(|\bComplete\w*\(|\bReset\w*\(|\bAdd\w+\(|\bRemove\w+\(')
UI=['Find.WindowStack.Add','Find.Targeter.BeginTargeting','Find.WorldTargeter.BeginTargeting','Messages.Message','CameraJumper','Find.Selector.Select','Find.Selector.ClearSelection','Find.MainTabsRoot','Find.WindowStack.TryRemove','Close(','OpenTab','PlayOneShot','TutorSystem.Notify_Event','Find.WindowStack.WindowOfType','GenDraw.','Widgets.','Text.','GUI.','tmp']
out=[]
for x in res['hybrid_any']:
    if x['kind']!='lambda' or x['dev']: continue
    if (x['cls'],x['method'],x['body']) in covered_bodies: continue
    if (x['cls'],x['method']) in wholesynced: continue
    role=x['role']
    if role in ('isActive','disabledReason','getter','onHover','groupKey','validator','labelGetter','tooltip','iconGetter','mouseoverGuiAction','extraPartOnGUI','revalidateClickTarget','onMouseover','orderInPriority','order','Disabled'): continue
    body=x['fullbody']
    b=strip_locals(body)
    for u in UI: b=b.replace(u,'')
    if not MUT_RX.search(b): continue
    calls=set(re.findall(r'\b([A-Z]\w*)\s*\(',body))|set(re.findall(r'\.(\w+)\s*\(',body))|set(re.findall(r'\.(\w+)\s*=(?!=)',body))
    via=sorted(calls & synced)
    if via and '--all' not in sys.argv: continue
    out.append(x|{'via':via})
json.dump(out,open('candidates.json','w'),indent=0)
print(len(out))
for x in out: print(f"{x['cls']}.{x['method']}#{x['ord']} ({x['role']}) {x['file']}:{x['line']} :: {x['body'][:140]}")
