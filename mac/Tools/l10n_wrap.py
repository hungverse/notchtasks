#!/usr/bin/env python3
"""Wraps every user-facing Vietnamese string literal in Swift sources with L(...), the runtime translator.

"Cấp \\(n)"  ->  L("Cấp {0}", n)      (each interpolation becomes a {i} placeholder and an argument)
Literals in comments, raw/multi-line strings, case patterns, attributes and log calls are left alone.

Usage: l10n_wrap.py [--write] [--keys out.json] files...
Without --write it only reports. --keys collects every key (the runtime text, escapes decoded) in source order.
"""
import json, re, sys

VI = re.compile(r'[àáảãạăằắẳẵặâầấẩẫậèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵđ'
                r'ÀÁẢÃẠĂẰẮẲẴẶÂẦẤẨẪẬÈÉẺẼẸÊỀẾỂỄỆÌÍỈĨỊÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢÙÚỦŨỤƯỪỨỬỮỰỲÝỶỸỴĐ]')
SKIP_LINE = re.compile(r'^\s*(assert|precondition|assertionFailure|preconditionFailure|print|NSLog|os_log|logger\.|log\.|Logger|@available|#)')
CASE_BEFORE = re.compile(r'\bcase\s*(?:\w+\s*=\s*)?(?:"(?:[^"\\]|\\.)*"\s*,\s*)*$')


def decode(src):
    """The runtime text of a Swift literal body (no interpolations)."""
    out, i = [], 0
    while i < len(src):
        c = src[i]
        if c == '\\' and i + 1 < len(src):
            n = src[i + 1]
            if n == 'n': out.append('\n'); i += 2; continue
            if n == 't': out.append('\t'); i += 2; continue
            if n == '0': out.append('\0'); i += 2; continue
            if n in '"\\\'': out.append(n); i += 2; continue
            if n == 'u' and src[i + 2:i + 3] == '{':
                j = src.index('}', i)
                out.append(chr(int(src[i + 3:j], 16))); i = j + 1; continue
        out.append(c); i += 1
    return ''.join(out)


class Swift:
    def __init__(self, text):
        self.t = text
        self.keys = []
        self.report = []

    def line_before(self, pos):
        start = self.t.rfind('\n', 0, pos) + 1
        return self.t[start:pos]

    def parse_literal(self, i):
        """i at the opening quote. Returns (end after the closing quote, parts): ('s', text) or ('x', expr)."""
        j, parts, buf = i + 1, [], []
        while True:
            c = self.t[j]
            if c == '\\' and self.t[j + 1] == '(':
                if buf: parts.append(('s', ''.join(buf))); buf = []
                k, depth = j + 2, 1
                while depth:
                    ch = self.t[k]
                    if ch == '"':
                        k, _ = self.parse_literal(k); continue
                    if ch == '(': depth += 1
                    elif ch == ')': depth -= 1
                    k += 1
                parts.append(('x', self.t[j + 2:k - 1])); j = k; continue
            if c == '\\':
                buf.append(self.t[j:j + 2]); j += 2; continue
            if c == '"':
                if buf: parts.append(('s', ''.join(buf)))
                return j + 1, parts
            if c == '\n':
                raise ValueError('unterminated literal at %d' % i)
            buf.append(c); j += 1

    def wrap(self, start, end, parts):
        """The replacement for the literal t[start:end], or None to keep it."""
        text = ''.join(p[1] for p in parts if p[0] == 's')
        exprs = [p[1] for p in parts if p[0] == 'x']
        subs = [Swift(e) for e in exprs]
        inner = [s.transform() for s in subs]  # Vietnamese inside interpolations too
        for s in subs: self.keys += s.keys; self.report += s.report
        if not VI.search(text):
            if any(a != b for a, b in zip(inner, exprs)):
                body, n = '', 0
                for p in parts:
                    if p[0] == 's': body += p[1]
                    else: body += '\\(' + inner[n] + ')'; n += 1
                return '"' + body + '"'
            return None
        before = self.line_before(start)
        if SKIP_LINE.search(before) or CASE_BEFORE.search(before):
            self.report.append(('skip', before.strip()[:60], text[:40]))
            return None
        key, args, n = '', [], 0
        for p in parts:
            if p[0] == 's': key += p[1]
            else:
                if re.search(r',\s*(style|format|specifier)\s*:', p[1]) or p[1].lstrip().startswith('Image('):
                    self.report.append(('special', p[1][:60], text[:40]))
                key += '{%d}' % n; args.append(inner[n]); n += 1
        self.keys.append(decode(key))
        call = 'L("' + key + '"' + ''.join(', ' + a for a in args) + ')'
        if '**' in key or '](' in key:  # markdown: Text keeps rendering it
            call = 'LocalizedStringKey(' + call + ')'
        return call

    def transform(self):
        t, out, i = self.t, [], 0
        n = len(t)
        while i < n:
            c = t[i]
            if t.startswith('//', i):
                j = t.find('\n', i); j = n if j < 0 else j
                out.append(t[i:j]); i = j; continue
            if t.startswith('/*', i):
                depth, j = 1, i + 2
                while depth and j < n:
                    if t.startswith('/*', j): depth += 1; j += 2
                    elif t.startswith('*/', j): depth -= 1; j += 2
                    else: j += 1
                out.append(t[i:j]); i = j; continue
            if c == '#' and re.match(r'#+"', t[i:]):
                hashes = re.match(r'#+', t[i:]).group(0)
                if t.startswith(hashes + '"""', i):
                    j = t.index('"""' + hashes, i + len(hashes) + 3) + 3 + len(hashes)
                else:
                    j = t.index('"' + hashes, i + len(hashes) + 1) + 1 + len(hashes)
                out.append(t[i:j]); i = j; continue
            if t.startswith('"""', i):
                j = t.index('"""', i + 3) + 3
                if VI.search(t[i:j]): self.report.append(('multiline', '', t[i:i + 40]))
                out.append(t[i:j]); i = j; continue
            if c == '"':
                end, parts = self.parse_literal(i)
                rep = self.wrap(i, end, parts)
                out.append(rep if rep is not None else t[i:end]); i = end; continue
            out.append(c); i += 1
        return ''.join(out)


def main():
    args = sys.argv[1:]
    write = '--write' in args
    keys_out = args[args.index('--keys') + 1] if '--keys' in args else None
    files = [a for a in args if a.endswith('.swift')]
    all_keys, count = [], 0
    for f in files:
        src = open(f, encoding='utf-8').read()
        s = Swift(src)
        new = s.transform()
        count += len(s.keys)
        all_keys += s.keys
        for r in s.report: print(f'{f}: {r}')
        if write and new != src:
            open(f, 'w', encoding='utf-8').write(new)
    seen, ordered = set(), []
    for k in all_keys:
        if k not in seen: seen.add(k); ordered.append(k)
    print(f'{count} literals, {len(ordered)} keys')
    if keys_out:
        json.dump(ordered, open(keys_out, 'w', encoding='utf-8'), ensure_ascii=False, indent=0)


if __name__ == '__main__':
    main()
