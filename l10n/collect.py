"""Every key the apps ask L() for (from the sources), plus keys.json's extras (data shown through L: sprite names,
server push texts…). The keys are English texts now (the L("…") literals are English), so new literals are added in
English. Writes l10n/keys.json, in a stable order."""
import glob, json, re, sys
sys.path.insert(0, 'mac/Tools')  # l10n_wrap.decode, run from the repo root
from l10n_wrap import decode
files = [f for f in glob.glob('mac/Sources/*.swift') if '/build/' not in f]
found = []
for f in files:
    for m in re.finditer(r'\bL\("((?:[^"\\\n]|\\.)*)"', open(f, encoding='utf-8').read()):
        k = decode(m.group(1))
        if k not in found: found.append(k)
old = json.load(open('l10n/keys.json'))
keys = old + [k for k in found if k not in old]
json.dump(keys, open('l10n/keys.json', 'w'), ensure_ascii=False, indent=0)
print(len(found), 'in code,', len(keys), 'total')
