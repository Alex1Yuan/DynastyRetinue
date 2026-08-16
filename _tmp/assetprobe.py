import json, sys

BASE = r'H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles'

with open(BASE + r'\locationlist.json', encoding='utf-8') as f:
    loc = json.load(f)

print('top keys:', list(loc.keys()))
for k, v in loc.items():
    print(' ', k, type(v).__name__, (len(v) if hasattr(v, '__len__') else ''))

guids = loc.get('m_Guids')
# find the parallel list
other = None
for k, v in loc.items():
    if k != 'm_Guids' and isinstance(v, list):
        other = k
        break
print('parallel key:', other)
if other:
    print('sample:', loc[other][:5])

idx = {g: i for i, g in enumerate(guids)}

TARGETS = {
    '0f539babafb47fe4586b719d02aff7c4': 'CLUE: suspected ImperialCruiser10Named prefab',
    'a6bcda106bf8fd44da4286ee04a3ad8f': 'Sword prefab (known-good)',
    '26e3688a99a9eed44baa2e19e16be1a4': 'Falchion prefab (known-good)',
    '31da3f04de39e5446b16641deb3be42d': 'Firestorm prefab (known-good)',
    'ba5028dcf8d5bb14e81d2513d2682597': 'Pirate wingman prefab',
    '6f3f035a080710949bd40b7a1af533eb': 'Globalmap ship prefab',
    '0e95e4e270084eaeb406d798459f48cc': 'ImperialCruiser10Named_VisualSettings (BLUEPRINT, expect MISS)',
    '3b6609d444a34dfe83a56028b86abe90': 'ImperialCruiser10Named (BLUEPRINT, expect MISS)',
}

print()
print('=== lookup ===')
for g, label in TARGETS.items():
    if g in idx:
        i = idx[g]
        loc_v = loc[other][i] if other else '?'
        print('HIT   %s  bundle=%-45s  %s' % (g, loc_v, label))
    else:
        print('MISS  %s  %s' % (g, label))
