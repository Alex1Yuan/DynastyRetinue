# -*- coding: utf-8 -*-
import sys,io,json,os
sys.stdout=io.TextIOWrapper(sys.stdout.buffer,encoding='utf-8')
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py"); sys.path.insert(0,r"D:\RT_RetinueMod\_tmp")
from restr import zh,name
from pool import ELITES,legal,BL
SLOT={'Head':'Equipment.BlueprintItemEquipmentHead','Neck':'Equipment.BlueprintItemEquipmentNeck',
      'Gloves':'Equipment.BlueprintItemEquipmentGloves','Feet':'Equipment.BlueprintItemEquipmentFeet',
      'Shoulders':'Equipment.BlueprintItemEquipmentShoulders','Ring':'Equipment.BlueprintItemEquipmentRing'}
# save provenance
SAVE={}
import subprocess
exec(open(r"D:\RT_RetinueMod\_tmp\saveitems.py",encoding='utf-8').read()) if os.path.exists(r"D:\RT_RetinueMod\_tmp\saveitems.py") else None
GUIDE={}
for i,l in enumerate(open(r"D:\RT_RetinueMod\ref\gear_match.tsv",encoding='utf-8')):
    if i==0: continue
    p=l.rstrip('\n').split('\t')
    if len(p)>=5 and p[4]: GUIDE.setdefault(p[0],set()).add(p[4].lower())
ei=int(sys.argv[1]); sl=sys.argv[2]
E=ELITES[ei]
used={}
for k,o in enumerate(ELITES):
    if k==ei: continue
    for ch in o['gear']:
        for g in ch: used.setdefault(g,[]).append(o['name'])
mine=set(g for ch in E['gear'] for g in ch)
allguide=set()
for v in GUIDE.values(): allguide|=v
print(f"# [{ei}] {E['name']}  slot={sl}")
rows=[]
for g,rec in zh.items():
    if rec.get('type')!=SLOT[sl]: continue
    if rec.get('rar') not in ('Unique','Pattern'): continue
    if legal(g,E['facts']): continue
    rows.append((0 if rec['rar']=='Unique' else 1, rec.get('zh') or '?',g,rec))
rows.sort()
for _,z,g,rec in rows[:40]:
    t=''
    if g in mine: t+='[本已用]'
    if g in used: t+='[★撞:'+','.join(used[g])+']'
    if g in allguide: t+='[攻略]'
    if g in BL: t+='[BL]'
    print(f"  {z:<22}[{rec['rar']:<7}] {g} <{rec['int']}>{t}")
