"""Builds l10n-<code>.json (key -> text) from l10n/tsv/<code>.*.tsv ("index<TAB>text", \\n for a new line),
checks each text keeps its key's placeholders ({0}, %.1f…) and edge spaces, and writes it for both apps:
mac/Sources (the Mac app; the Windows app packs the same files). Run from the repo root."""
import glob, json, re, sys
keys = json.load(open('l10n/keys.json'))
PH = re.compile(r'\{\d+\}|%[.\d]*[a-z@]')
codes = sorted({p.split('/')[-1].split('.')[0] for p in glob.glob('l10n/tsv/*.tsv')})
bad = 0
for code in codes:
    table = {}
    for part in sorted(glob.glob(f'l10n/tsv/{code}.*.tsv')):
        for n, line in enumerate(open(part, encoding='utf-8').read().split('\n'), 1):
            if not line.strip(): continue
            i, _, text = line.partition('\t')
            k = keys[int(i)]
            text = text.replace('\\n', '\n')
            if sorted(PH.findall(k)) != sorted(PH.findall(text)):
                print(f'{code} #{i} placeholders: {k!r} -> {text!r}'); bad += 1
            for edge, test in (('leading', str.startswith), ('trailing', str.endswith)):
                wide = text.endswith(('：', '）', '，', '。')) if edge == 'trailing' else text.startswith(('（', '，', '・'))
                if test(k, ' ') != test(text, ' ') and not wide:  # full-width punctuation carries its own space
                    print(f'{code} #{i} {edge} space: {k!r} -> {text!r}'); bad += 1
            table[k] = text
    missing = [i for i, k in enumerate(keys) if k not in table]
    print(f'{code}: {len(table)}/{len(keys)}' + (f', missing {missing[:12]}' if missing else ''))
    for d in ('mac/Sources',):
        json.dump(table, open(f'{d}/l10n-{code}.json', 'w', encoding='utf-8'), ensure_ascii=False, separators=(',', ':'))
sys.exit(1 if bad else 0)
