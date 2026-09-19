"""Approximate Roslyn lambda/local-function ordinal assignment (scope-tree order)."""
import re
from cslite import match_brace, iter_lambdas, lambda_body

KW_NOT_TYPE = {'return', 'yield', 'else', 'new', 'throw', 'await', 'case', 'goto', 'break', 'continue', 'if', 'while', 'for',
               'foreach', 'switch', 'using', 'lock', 'do', 'try', 'catch', 'finally', 'checked', 'unchecked', 'fixed', 'default', 'var'}
DECL_RX = re.compile(r'^\s*(?:const\s+|ref\s+)?(?:var|[A-Za-z_][\w\.]*(?:<[\w\s,<>\.\[\]\?\(\)]*>)?(?:\[[,\s]*\])*\??(?:\[[,\s]*\])*)\s+([A-Za-z_]\w*)\s*(?:=(?!=)|;|,)')
LOCALFUNC_RX = re.compile(r'^\s*(?:static\s+)?(?:async\s+)?(?:unsafe\s+)?[A-Za-z_][\w\.]*(?:<[^;{}()]*>)?(?:\[\])?\??\s+([A-Za-z_]\w*)\s*(?:<[^;{}()]*>)?\s*\(')
PATVAR_RX = re.compile(r'\bis\s+(?:not\s+)?[A-Za-z_][\w\.]*(?:<[^;{}]*?>)?\s+([a-z_]\w*)\b|\bout\s+(?:var|[A-Za-z_][\w\.<>]*)\s+([A-Za-z_]\w*)')

def statements_at_depth0(s, a, b):
    """split block content a..b (exclusive of braces) into top-level statement texts (start,end)"""
    out = []
    depth = 0
    st = a
    i = a
    while i < b:
        ch = s[i]
        if ch in '([': depth += 1
        elif ch in ')]': depth -= 1
        elif ch == '{':
            if depth == 0:
                e = match_brace(s, i)
                # block statement or initializer; treat block end as statement end if header is a control stmt / localfunc
                hdr = s[st:i]
                i = e + 1
                # if followed by ';' or ',' or ')' it's an initializer/lambda expression, continue statement
                j = i
                while j < b and s[j] in ' \t\r\n': j += 1
                if j < b and s[j] in ';),.':
                    continue
                out.append((st, i, hdr))
                st = i
                continue
            depth += 1
        elif ch == '}':
            depth -= 1
        elif ch == ';' and depth == 0:
            out.append((st, i + 1, s[st:i + 1]))
            st = i + 1
        i += 1
    if s[st:b].strip():
        out.append((st, b, s[st:b]))
    return out

class Scope:
    def __init__(self, a, b, kind):
        self.a, self.b, self.kind = a, b, kind
        self.children = []
        self.funcs = []   # (pos, Func)

class Func:
    def __init__(self, pos, body_a, body_b, kind, name=None):
        self.pos, self.body_a, self.body_b, self.kind, self.name = pos, body_a, body_b, kind, name
        self.scope = None
        self.ordinal = None

def block_decl_names(s, a, b, header):
    names = set()
    h = header.strip()
    m = re.match(r'^(?:foreach)\s*\(\s*(?:var|[\w\.<>\[\],\s\?]+?)\s+(\w+)\s+in\b', h)
    if m: names.add(m.group(1))
    m = re.match(r'^(?:for|using|fixed)\s*\(\s*(?:var|[\w\.<>\[\],\?]+)\s+(\w+)\s*=', h)
    if m: names.add(m.group(1))
    m = re.match(r'^catch\s*\(\s*[\w\.]+\s+(\w+)', h)
    if m: names.add(m.group(1))
    for (x, y, txt) in statements_at_depth0(s, a, b):
        t = s[x:y].lstrip()
        first = re.match(r'[A-Za-z_]\w*', t)
        head = t.split('{')[0]
        if first and first.group(0) in KW_NOT_TYPE and first.group(0) != 'var':
            if first.group(0) in ('if', 'while', 'switch', 'return', 'yield'):
                for pm in PATVAR_RX.finditer(head):
                    names.add(pm.group(1) or pm.group(2))
            continue
        dm = DECL_RX.match(t)
        if dm: names.add(dm.group(1))
        for pm in PATVAR_RX.finditer(head):
            names.add(pm.group(1) or pm.group(2))
    return names

def block_declares(s, a, b, header):
    h = header.strip()
    if re.match(r'^(foreach|for|using|fixed)\s*\(', h) or re.match(r'^catch\s*\(\s*[\w\.]+\s+\w+', h):
        return True
    for (x, y, txt) in statements_at_depth0(s, a, b):
        t = s[x:y].lstrip()
        first = re.match(r'[A-Za-z_]\w*', t)
        if first and first.group(0) in KW_NOT_TYPE and first.group(0) != 'var':
            # control statements: pattern vars in if/while conditions are scoped to enclosing block
            if first.group(0) in ('if', 'while', 'switch', 'return', 'yield') :
                # only the condition part
                cond_end = t.find('{')
                cond = t if cond_end < 0 else t[:cond_end]
                if PATVAR_RX.search(cond):
                    return True
            continue
        if DECL_RX.match(t):
            return True
        if PATVAR_RX.search(t.split('{')[0]):
            return True
    return False

import os as _os
MODE = _os.environ.get('ORDMODE', 'hybrid_any')
HOIST_LOCAL_ORDER = _os.environ.get('HOIST', '1') == '1'
INIT_SCOPES = _os.environ.get('INITSC', '1') == '1'
def build(s, mob, mcb, mode=None):
    mode = mode or MODE
    if mode.startswith('hybrid'):
        is_iter = re.search(r'\byield\s+(return|break)\b', s[mob:mcb]) is not None
        mode = 'lexical' if is_iter else ('roslyn_any' if mode == 'hybrid_any' else 'roslyn_cap')
    """returns ordered list of Func with ordinals"""
    funcs = []
    for (p, e) in iter_lambdas(s, mob + 1, mcb):
        tok = s[p:e]
        if re.match(r'(where|select|orderby|let|join|group|from)\b', tok):
            funcs.append(Func(p, e, e, 'query'))
            continue
        if tok.startswith('delegate'):
            bs = e
            be = match_brace(s, bs) + 1
        else:
            bs, be = lambda_body(s, e, mcb)
        funcs.append(Func(p, bs, be, 'lambda'))
    # local functions: statement-level declarations inside the method body
    for m in re.finditer(r'(?m)^[ \t]*(?:static\s+)?[A-Za-z_][\w\.]*(?:<[^;{}()\n]*>)?(?:\[\])?\??[ \t]+([A-Za-z_]\w*)\s*\([^;{}]*\)\s*(\{|=>)', s[mob + 1:mcb]):
        first = m.group(0).strip().split()[0]
        if first in KW_NOT_TYPE or first in ('return',):
            continue
        p = mob + 1 + m.start(1)
        brace = mob + 1 + m.end(2) - 1
        if m.group(2) == '{':
            be = match_brace(s, brace) + 1
            funcs.append(Func(p, brace, be, 'local', m.group(1)))
        else:
            bs, be = lambda_body(s, brace + 2, mcb)
            funcs.append(Func(p, bs, be, 'local', m.group(1)))
    # drop lambdas detected inside local function *headers* etc.; sort
    funcs.sort(key=lambda f: f.pos)
    # remove lambda entries that are actually the '=>' of expression-bodied local functions
    funcs = [f for f in funcs if not (f.kind == 'lambda' and any(g.kind == 'local' and g.pos < f.pos < g.body_a and s[f.pos:f.pos+2] != '()' for g in funcs))]

    # scopes: method root + blocks with declarations + function bodies
    root = Scope(mob, mcb, 'method')
    scopes = [root]
    # enumerate blocks
    i = mob + 1
    blocks = []
    stack = []
    for j in range(mob + 1, mcb):
        if s[j] == '{':
            stack.append(j)
        elif s[j] == '}':
            if stack:
                o = stack.pop()
                blocks.append((o, j))
    fn_bodies = {f.body_a: f for f in funcs if f.kind in ('lambda', 'local') and s[f.body_a:f.body_a+1] == '{'}
    for (o, c) in blocks:
        if o in fn_bodies:
            continue
        # header: text back to previous ; { } at same level
        k = o - 1
        depth = 0
        while k > mob:
            ch = s[k]
            if ch == ')': depth += 1
            elif ch == '(':
                depth -= 1
                if depth < 0: break
            elif ch in ';{}' and depth == 0: break
            k -= 1
        header = s[k + 1:o]
        hflat = header
        while True:
            h2 = re.sub(r'\([^()]*\)', '~', hflat)
            if h2 == hflat: break
            hflat = h2
        # object/collection initializer blocks aren't scopes
        if re.search(r'(\bnew\b[^;{}]*|=)\s*$', hflat) or re.search(r'\bnew\s*\[\s*\]\s*$', hflat):
            # object/collection initializer: lowered into a sequence with a temp -> its own closure scope
            if mode.startswith('roslyn') and INIT_SCOPES and re.search(r'\bnew\b[^;{}=]*$', hflat) and not re.search(r'\bnew\s*\[\s*\]\s*$', hflat):
                nm = list(re.finditer(r'\bnew\b', hflat))
                # map position of last 'new' in hflat back into s: search from the right in header text
                nm = [mm for mm in re.finditer(r'\bnew\b', s[k + 1:o]) if s[k+1+mm.start():o].count('(') - s[k+1+mm.start():o].count(')') == 0]
                start = k + 1 + nm[-1].start() if nm else o
                scopes.append(Scope(start, c, 'block'))
            continue
        if mode == 'lexical':
            continue
        if mode in ('any', 'roslyn_any'):
            if block_declares(s, o + 1, c, header):
                scopes.append(Scope(o, c, 'block'))
            continue
        names = block_decl_names(s, o + 1, c, header)
        if not names:
            continue
        captured = False
        for f in funcs:
            if o < f.pos < c:
                txt = s[f.body_a:f.body_b] if f.kind != 'query' else s[f.pos:f.pos + 200]
                if any(re.search(r'(?<![\w\.])' + re.escape(nm) + r'\b', txt) for nm in names):
                    captured = True; break
        if captured:
            scopes.append(Scope(o, c, 'block'))
    for f in funcs:
        sc = Scope(f.pos, f.body_b, 'func')
        sc.owner = f
        sc.hoist_at = f.pos
        f.scope = sc
        scopes.append(sc)

    # parent assignment: innermost enclosing scope (by containment)
    def contains(sc, x):
        return sc.a <= x < sc.b if sc.kind != 'func' else sc.a <= x < sc.b
    scopes_sorted = sorted(scopes, key=lambda sc: (sc.a, -(sc.b)))
    for sc in scopes_sorted:
        if sc is root: continue
        parent = None
        for cand in scopes_sorted:
            if cand is sc: continue
            if cand.a <= sc.a and sc.b <= cand.b and (cand.b - cand.a) > (sc.b - sc.a) or (cand.a <= sc.a and sc.b <= cand.b and cand.kind != 'func' and sc.kind == 'func' and (cand.a, cand.b) == (sc.a, sc.b)):
                if parent is None or (parent.b - parent.a) > (cand.b - cand.a):
                    parent = cand
        parent = parent or root
        if sc.kind == 'func':
            fobj = next(f for f in funcs if f.scope is sc)
            parent.funcs.append((fobj.pos, fobj))
            if fobj.kind == 'local':
                sc.hoist_at = parent.a if parent.kind != 'func' else parent.owner.body_a
        else:
            parent.children.append(sc)
    counter = [0]
    if mode.startswith('roslyn'):
        # Roslyn: per scope, number direct nested functions first, then recurse into nested scopes
        # (block scopes and function-body scopes) in creation order. Local function bodies are
        # created at the start of their containing block (hoisted).
        def creation_key(sc):
            if sc.kind == 'func':
                f = sc.owner
                if f.kind == 'local':
                    return (sc.hoist_at, 0, f.pos)
                return (f.pos, 1, 0)
            return (sc.a, 1, 0)
        def visit2(sc):
            direct = sorted(sc.funcs, key=lambda t: (0 if t[1].kind == 'local' else 1, t[0]) if HOIST_LOCAL_ORDER else t[0])
            for _, f in direct:
                f.ordinal = counter[0]; counter[0] += 1
            kids = list(sc.children) + [f.scope for _, f in sc.funcs]
            for ch in sorted(kids, key=creation_key):
                visit2(ch)
        visit2(root)
        return funcs
    counter = [0]
    def visit(sc):
        for _, f in sorted(sc.funcs, key=lambda t: t[0]):
            f.ordinal = counter[0]; counter[0] += 1
            visit(f.scope)
        for ch in sorted(sc.children, key=lambda c: c.a):
            visit(ch)
    # func scopes' children: blocks inside function bodies were attached to func scope via containment
    for f in funcs:
        pass
    visit(root)
    return funcs
