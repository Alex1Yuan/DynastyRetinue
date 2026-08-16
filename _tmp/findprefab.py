import json, re, subprocess, sys, os

BASE = r'H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles'
PACK = BASE + r'\blueprints-pack.bbp'

with open(BASE + r'\locationlist.json', encoding='utf-8') as f:
    loc = json.load(f)
ASSETS = dict(zip(loc['m_Guids'], loc['m_Bundles']))

# catalog guid -> name
CAT = {}
with open(r'D:\RT_RetinueMod\ref\bbp\catalog.tsv', encoding='utf-8') as f:
    for line in f:
        p = line.rstrip('\n').split('\t')
        if len(p) >= 2:
            CAT[p[0]] = p[1]

# Build offset index by running bbp dump header for a guid
def get_blob(guid):
    out = subprocess.run(
        ['dotnet', r'D:\RT_RetinueMod\ref\bbp\bin\Debug\net7.0\bbp.dll', 'dump', guid],
        capture_output=True, text=True, cwd=r'D:\RT_RetinueMod\ref\bbp')
    m = re.search(r'off=(\d+) len=(\d+)', out.stdout)
    if not m:
        return None
    off, ln = int(m.group(1)), int(m.group(2))
    with open(PACK, 'rb') as f:
        f.seek(off)
        return f.read(ln)


def raw_to_net(b):
    """Owlcat pack stores .NET Guid little-endian; convert 16 bytes -> canonical hex."""
    return (b[3::-1] + b[5:3:-1] + b[7:5:-1] + b[8:16]).hex()


def scan(guid, label):
    blob = get_blob(guid)
    if blob is None:
        print('!! no blob for', guid)
        return
    print('=== %s  %s  (%d bytes) ===' % (label, guid, len(blob)))

    found = []
    # (a) plain ascii 32-hex strings
    for m in re.finditer(rb'[0-9a-f]{32}', blob):
        s = m.group().decode()
        if s in ASSETS:
            found.append(('ascii-hex', m.start(), s, ASSETS[s]))
    # (b) 16-byte binary guids at every offset, both raw->net and as-is
    for i in range(0, len(blob) - 16):
        chunk = blob[i:i + 16]
        for kind, s in (('bin-net', raw_to_net(chunk)), ('bin-asis', chunk.hex())):
            if s in ASSETS:
                found.append((kind, i, s, ASSETS[s]))

    seen = set()
    for kind, off, s, bundle in found:
        if s in seen:
            continue
        seen.add(s)
        print('   ASSET  %-9s off=%-6d %s  bundle=%s' % (kind, off, s, bundle))
    if not seen:
        print('   (no asset guids found)')

    # also list referenced blueprints (for context)
    bps = set()
    for i in range(0, len(blob) - 16):
        s = raw_to_net(blob[i:i + 16])
        if s in CAT:
            bps.add((i, s, CAT[s]))
    for off, s, nm in sorted(bps)[:25]:
        print('   bp     off=%-6d %s  %s' % (off, s, nm))


for guid, label in [
    ('1793b8c1fd824805908bee8ed0e95041', 'SwordClassFrigatePlayer_Starship (CONTROL)'),
    ('3b6609d444a34dfe83a56028b86abe90', 'ImperialCruiser10Named (TARGET)'),
]:
    scan(guid, label)
    print()
