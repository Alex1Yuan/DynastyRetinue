import sys, re

PACK = r'H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles\blueprints-pack.bbp'

off = int(sys.argv[1])
ln = int(sys.argv[2])

with open(PACK, 'rb') as f:
    f.seek(off)
    b = f.read(ln)

print('--- hex ---')
for i in range(0, len(b), 16):
    chunk = b[i:i + 16]
    h = ' '.join('%02x' % c for c in chunk)
    a = ''.join(chr(c) if 32 <= c < 127 else '.' for c in chunk)
    print('%06x  %-47s  %s' % (i, h, a))

print()
print('--- ascii runs (>=4) ---')
for m in re.finditer(rb'[\x20-\x7e]{4,}', b):
    print(m.start(), repr(m.group().decode('ascii')))
