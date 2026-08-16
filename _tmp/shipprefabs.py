import json, re, subprocess, struct, sys

BASE = r'H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles'
PACK = BASE + r'\blueprints-pack.bbp'

with open(BASE + r'\locationlist.json', encoding='utf-8') as f:
    loc = json.load(f)
ASSETS = dict(zip(loc['m_Guids'], loc['m_Bundles']))

CAT = {}
with open(r'D:\RT_RetinueMod\ref\bbp\catalog.tsv', encoding='utf-8') as f:
    for line in f:
        p = line.rstrip('\n').split('\t')
        if len(p) >= 2:
            CAT[p[0]] = p[1]
NAME2GUID = {v: k for k, v in CAT.items()}

# Build a full offset index once by scanning the pack index via bbp for each guid is slow.
# Instead: bbp prints off/len. Batch it.
BBP = r'D:\RT_RetinueMod\ref\bbp\bin\Debug\net7.0\bbp.dll'


def blob_of(guid):
    out = subprocess.run(['dotnet', BBP, 'dump', guid],
                         capture_output=True, text=True,
                         cwd=r'D:\RT_RetinueMod\ref\bbp')
    m = re.search(r'off=(\d+) len=(\d+)', out.stdout)
    if not m:
        return None
    off, ln = int(m.group(1)), int(m.group(2))
    with open(PACK, 'rb') as f:
        f.seek(off)
        return f.read(ln)


def assets_in(blob):
    res = []
    for m in re.finditer(rb'[0-9a-f]{32}', blob):
        s = m.group().decode()
        if s in ASSETS:
            res.append((m.start(), s, ASSETS[s]))
    return res


# candidate ship blueprints: name has a class keyword, and no known suffix
SUFFIX = ('_Brain', '_VisualSettings', '_feature', '_Shield', '_Shields', '_Ammo',
          '_PlasmaDrive', '_AugerArray', '_Weapon', '_Ability', '_Buff', '_Area',
          '_CraftBay', '_Preset', '_Post', 'Settings', '_Upgrade', '_Table')
KEY = ('Cruiser', 'Frigate', 'Raider', 'Transport', 'Destroyer', 'Escort')

cands = []
for g, n in CAT.items():
    if any(k in n for k in KEY) and not any(n.endswith(s) for s in SUFFIX):
        cands.append((n, g))
cands.sort()

print('candidates:', len(cands))
print()
rows = []
for n, g in cands:
    b = blob_of(g)
    if b is None:
        continue
    a = assets_in(b)
    if a:
        for off, s, bundle in a:
            rows.append((n, g, s, bundle, len(b)))

print('%-42s %-32s %-32s %s' % ('BLUEPRINT', 'BP GUID', 'PREFAB ASSETID', 'BUNDLE'))
for n, g, s, bundle, ln in rows:
    print('%-42s %-32s %-32s %s' % (n[:42], g, s, bundle))

print()
print('total with prefab:', len(rows), '/', len(cands))
