"""Tiny C# helpers: sanitize strings/comments, find classes & methods, enumerate lambdas."""
import re, os

def sanitize(src):
    """Replace string/char literal contents and comments with spaces (same length).
    Interpolated string holes are kept (they are code)."""
    out = list(src)
    i, n = 0, len(src)
    def blank(a, b):
        for k in range(a, b):
            if out[k] != '\n':
                out[k] = ' '
    while i < n:
        c = src[i]
        if c == '/' and i + 1 < n and src[i+1] == '/':
            j = src.find('\n', i)
            j = n if j < 0 else j
            blank(i, j); i = j; continue
        if c == '/' and i + 1 < n and src[i+1] == '*':
            j = src.find('*/', i + 2)
            j = n if j < 0 else j + 2
            blank(i, j); i = j; continue
        if c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == '\\' else 1
            blank(i + 1, j); i = j + 1; continue
        if c in '$@"':
            # detect string prefix
            m = re.match(r'(\$@|@\$|\$|@)?"', src[i:i+3])
            if not m:
                i += 1; continue
            pre = m.group(1) or ''
            verbatim = '@' in pre
            interp = '$' in pre
            j = i + len(m.group(0))
            start = j
            depth = 0
            while j < n:
                ch = src[j]
                if interp and depth == 0 and ch == '{':
                    if j + 1 < n and src[j+1] == '{':
                        blank(j, j+2); j += 2; continue
                    depth = 1; j += 1; continue
                if interp and depth > 0:
                    if ch == '{': depth += 1
                    elif ch == '}':
                        depth -= 1
                    elif ch == '"':
                        # nested simple string inside hole
                        k = j + 1
                        while k < n and src[k] != '"':
                            k += 2 if src[k] == '\\' else 1
                        blank(j + 1, k); j = k + 1; continue
                    j += 1; continue
                if verbatim:
                    if ch == '"':
                        if j + 1 < n and src[j+1] == '"':
                            out[j] = out[j+1] = ' '; j += 2; continue
                        break
                    out[j] = ' ' if ch != '\n' else ch
                    j += 1; continue
                if ch == '\\':
                    out[j] = ' '
                    if j + 1 < n: out[j+1] = ' '
                    j += 2; continue
                if ch == '"':
                    break
                if ch == '}' and interp:
                    out[j] = ' '
                else:
                    out[j] = ' '
                j += 1
            i = j + 1
            continue
        i += 1
    return ''.join(out)

def match_brace(s, i):
    """s[i] == '{' ; return index of matching '}'"""
    depth = 0
    for j in range(i, len(s)):
        if s[j] == '{': depth += 1
        elif s[j] == '}':
            depth -= 1
            if depth == 0:
                return j
    return -1

CLASS_RE = re.compile(r'\b(?:class|struct|interface)\s+([A-Za-z_]\w*)(?:<[^>{]*>)?\s*(?::\s*([^{]+))?\{')
NS_RE = re.compile(r'\bnamespace\s+([\w\.]+)')

def iter_classes(s):
    """yield (name, bases, body_start, body_end, nesting path) including nested classes"""
    res = []
    def walk(a, b, path):
        pos = a
        while True:
            m = CLASS_RE.search(s, pos, b)
            if not m: break
            ob = m.end() - 1
            cb = match_brace(s, ob)
            if cb < 0: break
            name = m.group(1)
            res.append((name, (m.group(2) or '').strip(), ob, cb, path + [name]))
            walk(ob + 1, cb, path + [name])
            pos = cb + 1
    walk(0, len(s), [])
    return res

METHOD_RE_T = r'(?:(?:public|private|protected|internal|static|virtual|override|sealed|new|abstract|unsafe|extern|async)\s+)*[\w<>\[\],\.\? ]+?\s+{name}\s*(?:<[^>]*>)?\s*\(([^)]*(?:\([^)]*\)[^)]*)*)\)\s*(?:where[^{{]*)?\{{'

_DECL_PREFIX = re.compile(r'^\s*(?:(?:public|private|protected|internal|static|virtual|override|sealed|new|abstract|unsafe|extern|async|readonly)\s+)*[\w<>\[\],\.\? ]+\s+$')
_rx_cache = {}
def find_methods(s, a, b, name):
    out = []
    rx = _rx_cache.get(name)
    if rx is None:
        rx = _rx_cache[name] = re.compile(r'\b' + re.escape(name) + r'\s*(?:<[^<>()]*>)?\(')
    pos = a
    while True:
        m = rx.search(s, pos, b)
        if not m: break
        pos = m.end()
        ls = s.rfind('\n', 0, m.start()) + 1
        prefix = s[ls:m.start()]
        if not _DECL_PREFIX.match(prefix) or prefix.strip().startswith(('return', 'yield', 'else', 'new ')):
            continue
        # match paren
        depth = 0; j = m.end() - 1
        while j < b:
            if s[j] == '(': depth += 1
            elif s[j] == ')':
                depth -= 1
                if depth == 0: break
            j += 1
        params = s[m.end():j]
        k = j + 1
        while k < b and s[k] in ' \t\r\n': k += 1
        if s.startswith('where', k):
            k = s.find('{', k)
        if k < 0 or k >= b or s[k] != '{':
            continue
        cb = match_brace(s, k)
        out.append((params, k, cb))
        pos = cb
    return out

# lambda detection
LAMBDA_RE = re.compile(r'(\((?:[^()]|\([^()]*\))*\)|\b[A-Za-z_]\w*)\s*=>|\bdelegate\s*(\([^)]*\))?\s*\{')

def iter_lambdas(s, a, b):
    """return list of (pos, arrow_end, text-start-of-body) in lexical order.
    Excludes switch-expression arms heuristically."""
    res = []
    for m in LAMBDA_RE.finditer(s, a, b):
        g = m.group(0)
        if g.startswith('delegate'):
            res.append((m.start(), m.end() - 1))
            continue
        head = m.group(1)
        # exclude switch arms: preceded by pattern-ish context. Check char before head
        k = m.start() - 1
        while k >= 0 and s[k] in ' \t\r\n': k -= 1
        prev = s[k] if k >= 0 else ''
        if head.startswith('('):
            # could be cast/pattern? treat as lambda if prev is one of = ( , return etc
            pass
        else:
            # identifier lambda: previous token must be ( , = ? : return or => or {
            if prev == '.':
                continue  # Enum.Member => (switch arm)
            if head in ('_',) and prev in ',{':
                continue
        # switch arm check: prev char ',' or '{' and inside a 'switch' block: look back for 'switch' within 300 chars w/o ';'
        if prev in ',{' :
            back = s[max(a, m.start()-400):m.start()]
            sw = back.rfind('switch')
            semi = back.rfind(';')
            if sw >= 0 and sw > semi and not head.startswith('('):
                # likely arm like `TickerType.Never =>` handled; identifiers like `null =>` too
                if head in ('null', 'true', 'false') or head[0].isupper() or head.isdigit():
                    continue
        if head in ('null', 'true', 'false', 'default'):
            continue
        res.append((m.start(), m.end()))
    res.extend(query_lambdas(s, a, b))
    res.sort()
    return res

QFROM = re.compile(r'\bfrom\s+(?:[\w<>\.]+\s+)?(\w+)\s+in\s')
QKW = re.compile(r'\b(where|select|orderby|let|join|group|from|into)\b')
def query_lambdas(s, a, b):
    """pseudo-lambda positions for LINQ query syntax clauses"""
    out = []
    pos = a
    while True:
        m = QFROM.search(s, pos, b)
        if not m: break
        # find end of query expression: depth-0 ; or ) or , after start
        depth = 0; k = m.end()
        while k < b:
            ch = s[k]
            if ch in '([{': depth += 1
            elif ch in ')]}':
                if depth == 0: break
                depth -= 1
            elif ch in ';,' and depth == 0: break
            k += 1
        qe = k
        rangevars = [m.group(1)]
        clauses = [(mm.group(1), mm.start(), mm.end()) for mm in QKW.finditer(s, m.end(), qe)]
        # only depth-0 clauses (relative)
        def depth_at(x):
            d = 0
            for ch in s[m.end():x]:
                if ch in '([{': d += 1
                elif ch in ')]}': d -= 1
            return d
        clauses = [c for c in clauses if depth_at(c[1]) == 0]
        prev_kinds = []
        for idx, (kw, cs, ce) in enumerate(clauses):
            if kw == 'where':
                out.append((cs, ce))
            elif kw == 'select':
                nxt = clauses[idx+1][1] if idx + 1 < len(clauses) else qe
                expr = s[ce:nxt].strip()
                if kw == 'select' and re.fullmatch(r'\w+', expr) and expr in rangevars and prev_kinds and prev_kinds[-1] in ('where', 'orderby') :
                    pass  # degenerate select omitted
                else:
                    out.append((cs, ce))
                if idx + 1 < len(clauses) and clauses[idx+1][0] == 'into':
                    pass
            elif kw == 'orderby':
                nxt = clauses[idx+1][1] if idx + 1 < len(clauses) else qe
                keys = s[ce:nxt]
                d = 0; n = 1
                for ch in keys:
                    if ch in '([{': d += 1
                    elif ch in ')]}': d -= 1
                    elif ch == ',' and d == 0: n += 1
                for _ in range(n): out.append((cs, ce))
            elif kw == 'let':
                out.append((cs, ce))
            elif kw == 'join':
                out.extend([(cs, ce)] * 3)
            elif kw == 'group':
                out.append((cs, ce))
            elif kw == 'from':
                out.extend([(cs, ce)] * 2)
            elif kw == 'into':
                mm = re.match(r'\s*(\w+)', s[ce:])
                if mm: rangevars.append(mm.group(1))
            prev_kinds.append(kw)
        pos = qe
    return out

def lambda_body(s, arrow_end, limit):
    """body text after => (block or expression)"""
    j = arrow_end
    while j < limit and s[j] in ' \t\r\n': j += 1
    if j < limit and s[j] == '{':
        e = match_brace(s, j)
        return j, e + 1
    # expression: until , ; or unbalanced ) or }
    depth = 0
    k = j
    while k < limit:
        ch = s[k]
        if ch in '([{': depth += 1
        elif ch in ')]}':
            if depth == 0: break
            depth -= 1
        elif ch in ',;' and depth == 0: break
        k += 1
    return j, k

def load_cs_files(root, skip=()):
    for dp, dn, fn in os.walk(root):
        if any(x in dp for x in skip):
            continue
        for f in fn:
            if f.endswith('.cs'):
                yield os.path.join(dp, f)
