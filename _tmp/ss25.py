# -*- coding: utf-8 -*-
"""Extract per-companion equipped gear (by slot) from the 55-level save."""
import json, os, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
sys.path.insert(0, r'D:\RT_RetinueMod\ref\bbp\py')
from restr import zh, name

SAVE = r'D:\RT_RetinueMod\_tmp\save24'

items = {}      # uniqueId -> blueprint guid
units = []      # (blueprint, body, descr)

def walk(o, cb):
    st = [o]
    while st:
        x = st.pop()
        if isinstance(x, dict):
            cb(x)
            st.extend(x.values())
        elif isinstance(x, list):
            st.extend(x)

def collect_items(x):
    t = x.get('$type', '')
    if 'Items.ItemEntity' in t or ('UniqueId' in x and 'Blueprint' in x and 'Item' in t):
        u = x.get('UniqueId'); b = x.get('Blueprint')
        if u and b: items[u] = b

for fn in ('player.json', 'party.json'):
    d = json.load(open(os.path.join(SAVE, fn), encoding='utf-8'))
    walk(d, collect_items)
print('items indexed:', len(items), file=sys.stderr)

party = json.load(open(os.path.join(SAVE, 'party.json'), encoding='utf-8'))

def lab(g):
    if not g: return '-'
    r = zh.get(g)
    if r: return "%s[%s]<%s>" % (r['zh'] or '?', r.get('rar', ''), r['int'])
    return "<%s>" % (name.get(g) or g)

SLOTS = ['Head', 'Neck', 'Gloves', 'Feet', 'Shoulders', 'Ring1', 'Ring2',
         'Armor', 'Shirt', 'Belt', 'Glasses', 'Wrist', 'PetProtocol']

out = []
for e in party['m_EntityData']:
    parts = (e.get('Parts') or {}).get('Container') or []
    body = None; desc = None
    for p in parts:
        if not isinstance(p, dict): continue
        t = p.get('$type', '')
        if 'PartUnitBody' in t: body = p
        if 'PartUnitDescription' in t: desc = p
    if body is None: continue
    ub = e.get('Blueprint')
    nm = name.get(ub) or ub
    cn = ''
    if desc:
        cn = desc.get('CustomName') or desc.get('m_CustomName') or ''
    print('=' * 100)
    print('### %s   unit=%s  %s' % (nm, ub, cn))
    # hands
    for i, s in enumerate(body.get('m_HandsEquipmentSets') or []):
        for hand in ('PrimaryHand', 'SecondaryHand'):
            r = (s.get(hand) or {}).get('m_ItemRef')
            g = items.get(r)
            if g: print('   set%d.%-14s %s' % (i, hand, lab(g)))
    for s in SLOTS:
        r = (body.get(s) or {}).get('m_ItemRef')
        g = items.get(r)
        if g:
            print('   %-18s %s' % (s, lab(g)))
            out.append((nm, s, g, lab(g)))
    aug = (body.get('Augments') or {}).get('m_Slots') or []
    for kv in aug:
        r = (kv.get('Value') or {}).get('m_ItemRef')
        g = items.get(r)
        if g:
            print('   %-18s %s' % ('Augment', lab(g)))
            out.append((nm, 'Augment', g, lab(g)))

with open(r'D:\RT_RetinueMod\_tmp\save_slots.tsv', 'w', encoding='utf-8') as f:
    f.write('unit\tslot\tguid\tlabel\n')
    for r in out: f.write('\t'.join(r) + '\n')
