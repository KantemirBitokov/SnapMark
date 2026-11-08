"""Сравнение обычной и урезанной сборок: результаты проверок и картинки окон."""
import re, sys, os
from pathlib import Path
import numpy as np
from PIL import Image

a, b = Path(sys.argv[1]), Path(sys.argv[2])

def read(p):
    try: return p.read_text(encoding='utf-8', errors='replace')
    except Exception: return ''

def results(text, pattern):
    out = {}
    for line in text.splitlines():
        m = re.match(pattern, line.strip())
        if m: out[m.group(2)] = m.group(1)
    return out

print('== sizes'); print('normal :', read(a/'size.txt').strip().replace('\n', ' | ')); print('trimmed:', read(b/'size.txt').strip().replace('\n', ' | '))
print('\n== trim warnings (trimmed build)'); print(read(b/'trim-warnings.txt').strip() or '(none)')

bad = 0
def compare(title, ra, rb):
    global bad
    print(f'\n== {title}')
    for k in sorted(set(ra) | set(rb)):
        va, vb = ra.get(k, '—'), rb.get(k, '—')
        flag = 'same' if va == vb else 'DIFF'
        if va != vb or 'FAIL' in vb.upper(): bad += 1
        print(f'  {k:28} normal={va:6} trimmed={vb:6} {flag}')

compare('feature checks', results(read(a/'feature-test.txt'), r'(PASS|FAIL) (\S+)'), results(read(b/'feature-test.txt'), r'(PASS|FAIL) (\S+)'))
def glass(p):
    r = {}
    for line in read(p/'glass'/'log.txt').splitlines():
        m = re.match(r'^([a-z][\w-]+): (OK|FAIL)', line)
        if m: r[m.group(1)] = m.group(2)
        m = re.match(r'^(browser-longshot): (\d+/\d+) OK', line)
        if m: r[m.group(1)] = m.group(2)
        if 'ERROR' in line: r['error:' + line.split()[0]] = 'ERROR'
    return r
compare('windows checks', glass(a), glass(b))
def e2e(p):
    return {m.group(1): m.group(2) for m in re.finditer(r'^([\w-]+): (\S+)', read(p/'e2e'/'e2e.txt'), re.M)}
compare('the app itself', e2e(a), e2e(b))

print('\n== window pictures (Screenshots harness): share of differing pixels')
for f in sorted((a/'shots').glob('*.png')):
    g = b/'shots'/f.name
    if not g.exists(): print(f'  {f.name:28} MISSING in trimmed'); bad += 1; continue
    x = np.asarray(Image.open(f).convert('RGB')).astype(int); y = np.asarray(Image.open(g).convert('RGB')).astype(int)
    if x.shape != y.shape: print(f'  {f.name:28} SIZE {x.shape} vs {y.shape}'); bad += 1; continue
    d = (np.abs(x - y).sum(axis=2) > 24).mean() * 100
    print(f'  {f.name:28} {d:6.2f} %')
print('\nRESULT:', 'no differences in checks' if bad == 0 else f'{bad} differences/failures in checks')
