import re, os, json, sys
sys.path.insert(0, os.path.dirname(__file__))
from cslite import sanitize

MP = "/home/claude/RimworldMP/Source Codes/Source"
roots = [MP + "/Client", MP + "/MultiplayerLoader"]

T = r'typeof\(\s*([\w\.]+)(?:<[^>]*>)?\s*\)'
N = r'(?:nameof\(\s*(?:[\w\.]+\.)?(\w+)\s*\)|"(\w+)"|null)'

lam_rx = re.compile(r'(SyncMethod|SyncDelegate)\.(Lambda|LambdaInGetter)\(\s*' + T + r'\s*,\s*' + N + r'\s*,\s*(?:lambdaOrdinal:\s*)?(\d+)')
loc_rx = re.compile(r'(SyncMethod|SyncDelegate)\.LocalFunc\(\s*' + T + r'\s*,\s*' + N + r'\s*,\s*"(\w+)"')
reg_rx = re.compile(r'SyncMethod\.Register\(\s*' + T + r'\s*,\s*' + N)
field_rx = re.compile(r'(?:Sync\.Field|SyncField)\w*\(\s*' + T + r'\s*,\s*("[^"]*"|nameof\([^)]*\))')
patch_rx = re.compile(r'(?:HarmonyPatch|MpPrefix|MpPostfix|MpTranspiler)\(\s*' + T + r'\s*,\s*' + N + r'(?:[^)]*lambdaOrdinal:\s*(\d+))?')
getlam_rx = re.compile(r'GetLambda\(\s*' + T + r'\s*,\s*' + N + r'(?:[^)]*lambdaOrdinal:\s*(\d+))?')
dialog_rx = re.compile(r'RegisterSyncDialogNodeTree\(\s*' + T + r'\s*,\s*' + N)
accm_rx = re.compile(r'AccessTools\.(?:Method|DeclaredMethod|PropertyGetter|PropertySetter)\(\s*' + T + r'\s*,\s*' + N)
typeof_rx = re.compile(T)

reg = {"lambda": [], "localfunc": [], "method": [], "field": [], "patch": [], "types": set(), "dialog": []}
for root in roots:
    for dp, dn, fn in os.walk(root):
        for f in fn:
            if not f.endswith('.cs'): continue
            p = os.path.join(dp, f)
            raw = open(p, encoding='utf-8-sig', errors='replace').read()
            lines = raw.split('\n')
            rel = os.path.relpath(p, MP)
            for i, line in enumerate(lines):
                code = line.split('//')[0] if not line.strip().startswith('//') else ''
                if not code.strip():
                    continue
                cm = line.split('//', 1)[1].strip() if '//' in line else ''
                for m in lam_rx.finditer(code):
                    reg["lambda"].append(dict(kind=m.group(1)+'.'+m.group(2), type=m.group(3).split('.')[-1], method=m.group(4) or m.group(5), ordinal=int(m.group(6)), file=rel, line=i+1, comment=cm, debug='SetDebugOnly' in line))
                for m in loc_rx.finditer(code):
                    reg["localfunc"].append(dict(type=m.group(2).split('.')[-1], method=m.group(3) or m.group(4), name=m.group(5), file=rel, line=i+1))
                for m in reg_rx.finditer(code):
                    reg["method"].append(dict(type=m.group(1).split('.')[-1], method=m.group(2) or m.group(3), file=rel, line=i+1, comment=cm))
                for m in field_rx.finditer(code):
                    reg["field"].append(dict(type=m.group(1).split('.')[-1], field=m.group(2), file=rel, line=i+1))
                for m in patch_rx.finditer(code):
                    reg["patch"].append(dict(type=m.group(1).split('.')[-1], method=m.group(2) or m.group(3), ordinal=m.group(4), file=rel, line=i+1))
                for m in getlam_rx.finditer(code):
                    reg["patch"].append(dict(type=m.group(1).split('.')[-1], method=m.group(2) or m.group(3), ordinal=m.group(4) or '0', file=rel, line=i+1))
                for m in accm_rx.finditer(code):
                    reg["patch"].append(dict(type=m.group(1).split('.')[-1], method=m.group(2) or m.group(3), ordinal=None, file=rel, line=i+1))
                for m in dialog_rx.finditer(code):
                    reg["dialog"].append(dict(type=m.group(1).split('.')[-1], method=m.group(2) or m.group(3)))
                for m in typeof_rx.finditer(code):
                    reg["types"].add(m.group(1).split('.')[-1])
            # nameof(Type.X) references too
            for m in re.finditer(r'nameof\(\s*([A-Z]\w*)\.', raw):
                reg["types"].add(m.group(1))

reg["types"] = sorted(reg["types"])
json.dump(reg, open(os.path.join(os.path.dirname(__file__), 'mpreg.json'), 'w'), indent=1)
print({k: len(v) for k, v in reg.items()})
