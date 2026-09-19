import re, os, json, sys
sys.path.insert(0, os.path.dirname(__file__))
from cslite import *
from ordinals import build

RW = "/home/claude/RWDecomp"
SKIP = ('DelaunatorSharp', 'Gilzoide', 'Ionic', 'KTrie', 'Unity.Collections', 'UnityStandardAssets', 'System.Collections')
reg = json.load(open(os.path.join(os.path.dirname(__file__), 'mpreg.json')))

lam_reg = {}
for r in reg['lambda']:
    lam_reg.setdefault((r['type'], r['method']), {})[r['ordinal']] = r
method_reg = {(r['type'], r['method']) for r in reg['method']}
method_names = {r['method'] for r in reg['method']}
patch_reg = {(r['type'], r['method']) for r in reg['patch']}
patched_lambda = {(r['type'], r['method'], int(r['ordinal'])) for r in reg['patch'] if r.get('ordinal') not in (None, '')}
localfuncs = {(r['type'], r['method']) for r in reg['localfunc']}
types_ref = set(reg['types'])

GIZMO_METHODS = sys.argv[1].split(',') if len(sys.argv) > 1 else [
    'GetGizmos', 'CompGetGizmosExtra', 'CompGetWornGizmosExtra', 'CompGetEquippedGizmosExtra', 'GetWornGizmos',
    'GetGizmosExtra', 'GetCaravanGizmos', 'GetJobGizmos', 'GetPawnGizmos', 'GetMechGizmos', 'GetFloatMenuOptions',
    'CompFloatMenuOptions', 'GetMultiSelectFloatMenuOptions', 'CompMultiSelectFloatMenuOptions', 'ExtraFloatMenuOptions',
    'GetOptionsFor', 'GetSingleOptionFor', 'GetSingleOption', 'GetOptions', 'GetExtraFloatMenuOptionsFor',
    'CompGetGizmos', 'GetWornGizmosExtra', 'GetTransferableGizmos', 'GetCaravanGizmos', 'GetShipGizmos',
    'GetTransportersGizmos', 'GetGizmosForPawn', 'GetFloatMenuOptionsFor', 'ProcessInput', 'GizmoOnGUI', 'DoWindowContents', 'FillTab']

UI_ONLY_TOKENS = ['Find.WindowStack.Add', 'Find.Targeter.BeginTargeting', 'Find.WorldTargeter.BeginTargeting', 'SoundDefOf', 'Messages.Message',
                  'CameraJumper', 'Find.Selector.Select', 'Find.MainTabsRoot', 'Find.WindowStack.TryRemove', 'Close(', 'OpenTab', 'Find.WorldSelector',
                  'StartChoosingDestination', 'BeginTargeting', 'DefDatabase', 'Find.LetterStack.OpenLetter', 'TutorSystem', 'PlayOneShot']
MUT_RX = re.compile(r'(?<![=!<>+\-*/])=(?!=|>)|\+\+|--|\+=|-=|\.Add\(|\.Remove\w*\(|\.Clear\(|Destroy\(|\bSet\w+\(|\bTry\w+\(|\bNotify_\w+\(|\bStart\w*\(|\bCancel\w*\(|\bEject\w*\(|\bToggle\w*\(|\bDrop\w*\(|\bLaunch\w*\(|\bSpawn\w*\(|\bKill\(|\bFinish\w*\(|\bComplete\w*\(|\bReset\w*\(|\bAdd\w+\(|\bRemove\w+\(')

def classify(body):
    b = body
    mut = MUT_RX.search(b)
    # strip calls that are clearly ui
    stripped = b
    for t in UI_ONLY_TOKENS:
        stripped = stripped.replace(t, '')
    return bool(MUT_RX.search(stripped))

def role_of(s, pos, head_start):
    pre = s[max(0, head_start-80):head_start]
    m = re.search(r'(\w+)\s*=\s*$', pre)
    if m: return m.group(1)
    m = re.search(r'(new\s+[\w\.]+|[\w\.]+)\s*\((?:[^()]|\([^()]*\))*$', pre)
    if m: return 'arg:' + m.group(1).split('.')[-1]
    return '?'

results = []
for path in load_cs_files(RW, SKIP):
    if 'UnitySourceGenerated' in path: continue
    raw = open(path, encoding='utf-8-sig', errors='replace').read()
    s = sanitize(raw)
    for (cname, bases, ob, cb, cpath) in iter_classes(s):
        # method search restricted to class body but excluding nested classes
        nested = [(o, c) for (_, _, o, c, p) in iter_classes(s[ob+1:cb]) ] if False else None
        for mname in GIZMO_METHODS:
            for (params, mob, mcb) in find_methods(s, ob + 1, cb, mname):
                # ensure the method belongs to this class, not nested class: check no class decl between ob and mob at depth
                depth_ok = True
                seg = s[ob+1:mob]
                if seg.count('{') - seg.count('}') != 0:
                    continue
                funcs = build(s, mob, mcb)
                for f in sorted(funcs, key=lambda f: f.ordinal):
                    ordn = f.ordinal
                    lp = f.pos
                    bs, be = f.body_a, f.body_b
                    body = raw[bs:be] if f.kind != 'query' else raw[f.pos:f.pos+120]
                    ctx_start = max(s.rfind('new Command', mob, lp), s.rfind('new FloatMenuOption', mob, lp), s.rfind('yield return', mob, lp))
                    ctx = raw[ctx_start:be] if ctx_start > 0 else raw[max(mob, lp-300):be]
                    pre = raw[max(mob, lp-800):lp]
                    dev = bool(re.search(r'"(DEV|Dev|DEBUG|Debug)[: ]', ctx)) or 'DebugSettings.godMode' in pre[-200:]
                    role = role_of(s, lp, lp) if f.kind == 'lambda' else f.kind + (':' + f.name if f.name else '')
                    key = (cname, mname)
                    status = ''
                    r = lam_reg.get(key, {}).get(ordn)
                    if r: status = 'REG'
                    elif (cname, mname, ordn) in patched_lambda: status = 'PATCHED'
                    results.append(dict(file=os.path.relpath(path, RW), cls=cname, bases=bases[:60], method=mname, params=params[:80], ord=ordn,
                                        role=role, dev=dev, mut=classify(body), status=status, mp=(r or {}).get('comment', ''), mpdebug=(r or {}).get('debug'),
                                        body=' '.join(body.split())[:220], fullbody=' '.join(body.split()), line=raw.count('\n', 0, lp) + 1, kind=f.kind,
                                        nlam=len(funcs), clsreg=key in lam_reg, mreg=key in method_reg or key in patch_reg))
json.dump(results, open(os.path.join(os.path.dirname(__file__), 'gizmos_' + os.environ.get('ORDMODE','hybrid_any') + '.json'), 'w'), indent=0)
print(len(results))
