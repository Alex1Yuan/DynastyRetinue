# -*- coding: utf-8 -*-
import json,sys,os,re
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import zh,name
raw=open('party.json',encoding='utf-8').read()
d=json.loads(raw)
# build UniqueId -> Blueprint map by walking everything
items={}
def walk(o):
    if isinstance(o,dict):
        u=o.get('UniqueId'); b=o.get('Blueprint')
        if isinstance(u,str) and isinstance(b,str) and len(u)==32 and len(b)==32:
            items.setdefault(u,b)
        for v in o.values(): walk(v)
    elif isinstance(o,list):
        for v in o: walk(v)
walk(d)
print('id->bp entries',len(items))
def nm(g):
    if not g: return '(空)'
    g=g.replace('-','').lower()
    r=zh.get(g)
    if r: return f"{r['zh'] or '?'}<{r['int']}>[{r.get('rar','')}] {g}"
    return f"{name.get(g,'?')} {g}"
def it(ref):
    if not ref: return '(空)'
    bp=items.get(ref)
    return nm(bp) if bp else f'?item {ref}'
SLOTS=['Head','Neck','Gloves','Feet','Shoulders','Ring1','Ring2','Armor','Belt','Shirt','Glasses','Wrist','PetProtocol']
out=[]
for e in d['m_EntityData']:
    if 'UnitEntity' not in (e.get('$type') or ''): continue
    ubp=e.get('Blueprint')
    body=None; descr=None
    for p in e['Parts']['Container']:
        t=p.get('$type') or ''
        if 'PartUnitBody' in t: body=p
        if 'PartUnitDescription' in t: descr=p
    if not body: continue
    cn=''
    if descr:
        cn=json.dumps(descr,ensure_ascii=False)[:200]
    out.append('='*100)
    out.append(f"UNIT {nm(ubp)}")
    if cn: out.append('  descr:'+cn)
    for i,s in enumerate(body.get('m_HandsEquipmentSets') or []):
        out.append(f"  套组{i+1} 主手: {it((s.get('PrimaryHand') or {}).get('m_ItemRef'))}")
        out.append(f"  套组{i+1} 副手: {it((s.get('SecondaryHand') or {}).get('m_ItemRef'))}")
    for s in SLOTS:
        v=body.get(s)
        if isinstance(v,dict):
            out.append(f"  {s:<12}: {it(v.get('m_ItemRef'))}")
    aug=(body.get('Augments') or {}).get('m_Slots') or []
    for a in aug:
        r=(a.get('Value') or {}).get('m_ItemRef')
        if r: out.append(f"  Augment[{a.get('Key')[:8]}]: {it(r)}")
open('party_gear.txt','w',encoding='utf-8').write('\n'.join(out))
print('written',len(out))
