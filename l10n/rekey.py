#!/usr/bin/env python3
"""One-off: make the English text the translation key; Vietnamese becomes an ordinary translation table.

Run from the repo root. The source globs are arguments, so the same script works in a sister repo:
  python3 l10n/rekey.py --swift 'mac/Sources/*.swift' --swift-tests 'mac/Sources/main.swift' \
      --js 'windows/src/**/*.js' --js-tests 'windows/test/**/*.js'
--swift / --js rewrite the L("...") literals to their English key. --swift-tests / --js-tests also rewrite every
string literal that equals a known Vietnamese key (test expectations). Rewrites l10n/keys.json, l10n/tsv/*.tsv
(vi.* added, en.* removed, same numbered split), --windows (windows.json) and the globs. Refuses to run twice.
"""
import argparse, glob, json, os, re, sys
from collections import defaultdict

PH = re.compile(r'\{\d+\}|%[.\d]*[a-z@]')
# old key index -> English, applied before merging so that different meanings stay distinct
OVERRIDES = {175: 'Connecting…', 1013: 'Update available'}
# Vietnamese literals in the JS sources that no table has yet: the English is written here, with no translations
# (they fall back to the English key). KEEP: a deliberately unknown key in a fallback test, left as it is.
EXTRA_EN = {
    'Còn {0} GB trống': '{0} GB free',
    'Không nhập được cấu hình': "Couldn't import the settings",
    'Không lưu được mã ghép trên máy này: sẽ mất khi thoát': "Couldn't save the pairing code on this computer: it will be lost when you quit",
    'Không lưu được mã ghép: {0}': "Couldn't save the pairing code: {0}",
    'Thông báo sẽ hiện như thế này': 'This is how the notification will look',
}
KEEP = {'Khóa không có {0} và {1}'}
SW_ESC = {'n': '\n', 't': '\t', 'r': '\r', '0': '\0', '"': '"', "'": "'", '\\': '\\'}
JS_ESC = {'n': '\n', 't': '\t', 'r': '\r', 'b': '\b', 'f': '\f', 'v': '\v', '0': '\0'}

SW_L = re.compile(r'(\bL\(\s*")((?:[^"\\\n]|\\.)*)(")')
SW_STR = re.compile(r'"((?:[^"\\\n]|\\.)*)"')
SW_CALL = re.compile(r'\bL\(')
JS_L = re.compile(r"""(\bL\(\s*)(?:'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)"|`((?:[^`\\]|\\.)*)`)""")
JS_STR = re.compile(r"""'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)"|`((?:[^`\\]|\\.)*)`""")
JS_CALL = re.compile(r'\bL\(')


def sw_dec(s):
    """Swift literal body -> text; None for an interpolation or an escape it does not know."""
    if '\\(' in s:
        return None
    out, i = [], 0
    while i < len(s):
        if s[i] != '\\':
            out.append(s[i]); i += 1; continue
        n = s[i + 1]
        if n == 'u' and s[i + 2:i + 3] == '{':
            j = s.index('}', i); out.append(chr(int(s[i + 3:j], 16))); i = j + 1
        elif n in SW_ESC:
            out.append(SW_ESC[n]); i += 2
        else:
            return None
    return ''.join(out)


def sw_enc(t):
    return t.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\t', '\\t').replace('\r', '\\r')


def js_dec(s):
    out, i = [], 0
    while i < len(s):
        if s[i] != '\\':
            out.append(s[i]); i += 1; continue
        n = s[i + 1]
        if n == 'u' and s[i + 2:i + 3] == '{':
            j = s.index('}', i); out.append(chr(int(s[i + 3:j], 16))); i = j + 1
        elif n == 'u':
            out.append(chr(int(s[i + 2:i + 6], 16))); i += 6
        elif n == 'x':
            out.append(chr(int(s[i + 2:i + 4], 16))); i += 4
        elif n == '\n':
            i += 2
        else:
            out.append(JS_ESC.get(n, n)); i += 2
    return ''.join(out)


def js_enc(t, q):
    assert '${' not in t or q != '`', t
    t = t.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t').replace('\r', '\\r')
    return t.replace(q, '\\' + q)


def placeholders(s):
    return sorted(PH.findall(s))


def line_of(pos, text):
    return text.count('\n', 0, pos) + 1


def expand(pats):
    out = set()
    for p in pats:
        for f in glob.glob(p, recursive=True):
            if os.path.isfile(f) and not {'node_modules', 'build', 'dist'} & set(f.split(os.sep)):
                out.add(f)
    return sorted(out)


def read_tsv(code):
    table, part_of = {}, {}
    for part in glob.glob(f'l10n/tsv/{code}.*.tsv'):
        n = int(os.path.basename(part).split('.')[1])
        for line in open(part, encoding='utf-8').read().split('\n'):
            if not line.strip():
                continue
            i, _, text = line.partition('\t')
            table[int(i)] = text.replace('\\n', '\n')
            part_of[int(i)] = n
    return table, part_of


def close_index(src, p, quotes):
    """Index of the ')' that closes the '(' at p, skipping string literals; None if unbalanced."""
    depth, j = 0, p
    while j < len(src):
        c = src[j]
        if c in quotes:
            j += 1
            while j < len(src) and src[j] != c:
                j += 2 if src[j] == '\\' else 1
            j += 1
            continue
        if c == '(':
            depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0:
                return j
        j += 1
    return None


def inarg(src, lang, path, v2e, rep):
    """String literals inside a dynamic L( ... ) argument (ternaries like L(x ? "A" : "B")) are keys too: rewrite
    the ones that are known. A call whose argument starts with a literal is the L pass's job, so it is skipped."""
    spans, last_end = [], 0
    for m in re.finditer(r'\bL\(', src):
        k = m.end()
        while k < len(src) and src[k] in ' \t\n':
            k += 1
        if k < len(src) and src[k] in '"\'`':
            continue
        if re.search(r'\b(func|function)\s+$', src[max(0, m.start() - 12):m.start()]):  # a definition of L itself
            continue
        end = close_index(src, m.end() - 1, '"' if lang == 'swift' else '"\'`')
        if end is None or m.end() < last_end:
            continue
        spans.append((m.end(), end)); last_end = end
    out, last = [], 0
    for a, b in spans:
        seg = src[a:b]
        if lang == 'swift':
            def sub(m):
                dec = sw_dec(m.group(1))
                if dec is None or dec not in v2e or v2e[dec] == dec:
                    return m.group(0)
                rep['A'][path] += 1
                return '"' + sw_enc(v2e[dec]) + '"'
            seg = SW_STR.sub(sub, seg)
        else:
            def sub(m):
                g = next(i for i in (1, 2, 3) if m.group(i) is not None)
                q, body = "'\"`"[g - 1], m.group(g)
                if '${' in body:
                    return m.group(0)
                dec = js_dec(body)
                if dec not in v2e or v2e[dec] == dec:
                    return m.group(0)
                rep['A'][path] += 1
                return q + js_enc(v2e[dec], q) + q
            seg = JS_STR.sub(sub, seg)
        out.append(src[last:a]); out.append(seg); last = b
    out.append(src[last:])
    return ''.join(out)


def rewrite_swift(path, src, v2e, l_pass, generic_pass, rep):
    if l_pass:
        src = inarg(src, 'swift', path, v2e, rep)
    out = src
    if l_pass:
        matched = set()

        def sub(m):
            matched.add(m.start())
            where = f'{path}:{line_of(m.start(), src)}'
            dec = sw_dec(m.group(2))
            if dec is None:
                rep['dynamic'].append((where, m.group(0)[:100])); return m.group(0)
            if dec in KEEP:
                return m.group(0)
            if dec not in v2e:
                rep['missing'].append((where, dec)); return m.group(0)
            if v2e[dec] == dec:
                return m.group(0)
            rep['L'][path] += 1
            return m.group(1) + sw_enc(v2e[dec]) + m.group(3)
        out = SW_L.sub(sub, src)
        for m in SW_CALL.finditer(src):
            if m.start() in matched or src[max(0, m.start() - 5):m.start()] == 'func ':
                continue
            rep['dynamic'].append((f'{path}:{line_of(m.start(), src)}', src[m.start():m.start() + 100].split('\n')[0]))
    if generic_pass:
        def sub(m):
            dec = sw_dec(m.group(1))
            if dec is None or dec not in v2e or v2e[dec] == dec:
                return m.group(0)
            rep['G'][path] += 1
            return '"' + sw_enc(v2e[dec]) + '"'
        out = SW_STR.sub(sub, out)
    return out


def rewrite_js(path, src, v2e, l_pass, generic_pass, rep):
    if l_pass:
        src = inarg(src, 'js', path, v2e, rep)
    out = src
    if l_pass:
        matched = set()

        def sub(m):
            matched.add(m.start())
            g = next(i for i in (2, 3, 4) if m.group(i) is not None)
            q, body = "'\"`"[g - 2], m.group(g)
            where = f'{path}:{line_of(m.start(), src)}'
            if '${' in body:
                rep['dynamic'].append((where, m.group(0)[:100])); return m.group(0)
            dec = js_dec(body)
            if dec in KEEP:
                return m.group(0)
            if dec not in v2e:
                rep['missing'].append((where, dec)); return m.group(0)
            if v2e[dec] == dec:
                return m.group(0)
            rep['L'][path] += 1
            s, e = m.span(g)
            return m.string[m.start():s] + js_enc(v2e[dec], q) + m.string[e:m.end()]
        out = JS_L.sub(sub, src)
        for m in JS_CALL.finditer(src):
            if m.start() in matched or src[max(0, m.start() - 9):m.start()] == 'function ':
                continue
            rep['dynamic'].append((f'{path}:{line_of(m.start(), src)}', src[m.start():m.start() + 100].split('\n')[0]))
    if generic_pass:
        def sub(m):
            g = next(i for i in (1, 2, 3) if m.group(i) is not None)
            q, body = "'\"`"[g - 1], m.group(g)
            if '${' in body:
                return m.group(0)
            dec = js_dec(body)
            if dec not in v2e or v2e[dec] == dec:
                return m.group(0)
            rep['G'][path] += 1
            return q + js_enc(v2e[dec], q) + q
        out = JS_STR.sub(sub, out)
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--swift', action='append', default=[], help='glob: rewrite L("...") literals')
    ap.add_argument('--swift-tests', action='append', default=[], help='glob: also rewrite string literals equal to a key')
    ap.add_argument('--js', action='append', default=[], help="glob: rewrite L('...') / L(\"...\") / L(`...`)")
    ap.add_argument('--js-tests', action='append', default=[], help='glob: also rewrite string literals equal to a key')
    ap.add_argument('--windows', default='windows/l10n/windows.json')
    args = ap.parse_args()
    if not (args.swift or args.swift_tests or args.js or args.js_tests):
        sys.exit('give at least one source glob')
    if glob.glob('l10n/tsv/vi.*.tsv'):
        sys.exit('already re-keyed (l10n/tsv/vi.*.tsv exists)')

    # ---- 1. vi -> english from the old keys and the en table
    keys = json.load(open('l10n/keys.json', encoding='utf-8'))
    tables, part_of = {}, {}
    for code in sorted({os.path.basename(p).split('.')[0] for p in glob.glob('l10n/tsv/*.tsv')}):
        tables[code], part_of[code] = read_tsv(code)
    en = tables['en']
    assert all(len(t) == len(keys) for t in tables.values()), 'a tsv table is incomplete'
    assert keys[175] == 'Đang nối' and keys[1013] == 'Có bản' and keys[542] == 'Đang mở' and en[542] == 'Open', \
        'override indices moved'

    def en_of(i):
        return OVERRIDES.get(i, en[i])

    new_keys, index_of, members = [], {}, defaultdict(list)
    for i in range(len(keys)):
        e = en_of(i)
        if e not in index_of:
            index_of[e] = len(new_keys); new_keys.append(e)
        members[index_of[e]].append(i)
    assert all(e.strip() for e in new_keys), 'empty english key'
    vi_to_en = {keys[i]: en_of(i) for i in range(len(keys))}
    merged = [n for n in members if len(members[n]) > 1]

    # ---- 2. windows.json: {code: {vi: text}} -> {code: {english: text}}, its en block gives the english
    win_path = args.windows
    win = json.load(open(win_path, encoding='utf-8')) if os.path.exists(win_path) else None
    v2e_js = dict(vi_to_en)
    new_win = None
    win_stats = {}
    if win:
        assert 'vi' not in win
        for vi, e in EXTRA_EN.items():
            win['en'].setdefault(vi, e)
        ven, seen = {}, set()
        for vi, e in win['en'].items():
            if vi in vi_to_en:  # a string both apps show: keys.json's English, so the JS literal and the table agree
                if vi_to_en[vi] != e:
                    print(f'note: windows.json {vi!r} -> {e!r}; keys.json says {vi_to_en[vi]!r}, that one is used')
                e = vi_to_en[vi]
            else:
                v2e_js[vi] = e
            assert e not in seen, f'windows.json: two keys become {e!r}'
            seen.add(e); ven[vi] = e
        new_win = {'vi': {e: vi for vi, e in ven.items()}}
        for code, block in win.items():
            if code == 'en':
                continue
            assert set(block) <= set(ven), f'windows.json {code}: keys not in en'
            new_win[code] = {ven[vi]: block[vi] for vi in block}
        win_stats = {'windows keys': len(ven), 'also in keys.json': sum(1 for vi in ven if vi in vi_to_en),
                     'english equal to a main key': sum(1 for e in ven.values() if e in index_of)}

    # ---- 3. all tables in the new layout
    codes = [c for c in tables if c != 'en']
    texts = {c: [tables[c][members[n][0]] for n in range(len(new_keys))] for c in codes}
    texts['vi'] = [keys[members[n][0]] for n in range(len(new_keys))]
    bad = []
    for code, ts in texts.items():
        for n, e in enumerate(new_keys):
            if placeholders(ts[n]) != placeholders(e):
                bad.append((code, n, e, ts[n]))
            assert '\t' not in ts[n] and '\r' not in ts[n], (code, n)
    assert not bad, f'placeholder mismatch: {bad[:5]}'

    # ---- 4. write keys, tsv, windows.json
    part_for = [part_of['en'][members[n][0]] for n in range(len(new_keys))]
    for code in tables:
        for f in glob.glob(f'l10n/tsv/{code}.*.tsv'):
            os.remove(f)
    for code, ts in texts.items():
        parts = defaultdict(list)
        for n in range(len(new_keys)):
            parts[part_for[n]].append(f"{n}\t{ts[n].replace(chr(10), chr(92) + 'n')}")
        for p, rows in sorted(parts.items()):
            open(f'l10n/tsv/{code}.{p}.tsv', 'w', encoding='utf-8').write('\n'.join(rows) + '\n')
    json.dump(new_keys, open('l10n/keys.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
    # every old Vietnamese text (keys.json and windows-only) -> its English key: the runtime maps a Vietnamese text
    # through it first, so persisted or data-held Vietnamese still shows translated
    json.dump(v2e_js, open('l10n/legacy-vi.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
    if new_win is not None:
        json.dump(new_win, open(win_path, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)

    # ---- 5. rewrite literals in the source globs
    jobs = {}
    # tests get both passes: their L("...") calls and their string literals
    for pats, modes in ((args.swift, 'l'), (args.swift_tests, 'lg')):
        for f in expand(pats):
            jobs.setdefault(f, set()).update(modes)
    js_jobs = {}
    for pats, modes in ((args.js, 'l'), (args.js_tests, 'lg')):
        for f in expand(pats):
            js_jobs.setdefault(f, set()).update(modes)
    rep = {'L': defaultdict(int), 'G': defaultdict(int), 'A': defaultdict(int), 'dynamic': [], 'missing': []}
    for f, modes in sorted(jobs.items()):
        src = open(f, encoding='utf-8').read()
        out = rewrite_swift(f, src, vi_to_en, 'l' in modes, 'g' in modes, rep)
        if out != src:
            open(f, 'w', encoding='utf-8').write(out)
    for f, modes in sorted(js_jobs.items()):
        src = open(f, encoding='utf-8').read()
        out = rewrite_js(f, src, v2e_js, 'l' in modes, 'g' in modes, rep)
        if out != src:
            open(f, 'w', encoding='utf-8').write(out)

    # ---- 6. report and post-checks
    allowed = set(new_keys) | (set(new_win['vi']) if new_win else set())
    unresolved = []
    for f in sorted(set(jobs) | set(js_jobs)):
        src = open(f, encoding='utf-8').read()
        if f in jobs:
            for m in SW_L.finditer(src):
                d = sw_dec(m.group(2))
                if d is not None and d not in allowed and d not in KEEP:
                    unresolved.append((f, line_of(m.start(), src), d))
        if f in js_jobs:
            for m in JS_L.finditer(src):
                g = next(i for i in (2, 3, 4) if m.group(i) is not None)
                if '${' not in m.group(g) and js_dec(m.group(g)) not in allowed | KEEP:
                    unresolved.append((f, line_of(m.start(), src), js_dec(m.group(g))))

    print(f'\nkeys: {len(keys)} old -> {len(new_keys)} new; {len(merged)} merged groups')
    for n in merged:
        print(f'  merged -> {new_keys[n]!r}: ' + ' | '.join(repr(keys[i]) for i in members[n]))
    print('overrides:', {i: (keys[i], OVERRIDES[i]) for i in OVERRIDES})
    if win_stats:
        print('windows.json:', win_stats)
    print('\nliterals rewritten per file (L("...") / test strings / literals inside a dynamic L( ) argument):')
    for f in sorted(set(rep['L']) | set(rep['G']) | set(rep['A'])):
        print(f'  {f}: {rep["L"][f]} / {rep["G"][f]} / {rep["A"][f]}')
    print(f'\ndynamic (non-literal) L( calls to review: {len(rep["dynamic"])}')
    for where, snip in rep['dynamic']:
        print(f'  {where}  {snip}')
    print(f'\nL literal keys not in keys.json or windows.json: {len(rep["missing"])}')
    for where, k in rep['missing']:
        print(f'  {where}  {k!r}')
    print(f'\nunresolved L literals after rewrite: {len(unresolved)}')
    for f, ln, d in unresolved:
        print(f'  {f}:{ln}  {d!r}')
    assert not unresolved, 'unresolved literals'
    print('placeholder self-check: ok')


if __name__ == '__main__':
    main()
