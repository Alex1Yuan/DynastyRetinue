# -*- coding: utf-8 -*-
import sys,io,json
sys.stdout=io.TextIOWrapper(sys.stdout.buffer,encoding='utf-8')
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
sys.path.insert(0,r"D:\RT_RetinueMod\_tmp")
from restr import zh,name,proto
from pool import ELITES,legal,BL
from melee import stats
MELEEPROTO=('Sword','Axe','Hammer','Knife','Blade','Maul','Spear','Chain','Claw','Staff','Whip','Pick','Baton','Eviscerator','Fist')
RANGED=('Pistol','Rifle','Gun','Bolter','Stubber','Shotgun','Launcher','Flamer','Melta','Plasma','Las','Cannon','Webber','Needle')
ei=int(sys.argv[1]); want=sys.argv[2] if len(sys.argv)>2 else 'melee'
E=ELITES[ei]
used={}
for k,o in enumerate(ELITES):
    if k==ei: continue
    for ch in o['gear']:
        for g in ch: used.setdefault(g,[]).append(o['name'])
mine=set(g for ch in E['gear'] for g in ch)
print(f"# [{ei}] {E['arch']} / {E['name']}   ({want})")
rows=[]
for g,rec in zh.items():
    if rec.get('type')!='Weapons.BlueprintItemWeapon': continue
    if rec.get('rar') not in ('Unique','Pattern'): continue
    pg=proto(g); pn=name.get(pg,'') if pg else ''
    ismelee = any(k in pn for k in MELEEPROTO) and not any(k in pn for k in RANGED)
    if want=='melee' and not ismelee: continue
    if want=='ranged' and ismelee: continue
    b=legal(g,E['facts'])
    if b: continue
    s=stats(g)
    if not s: continue
    rows.append((-(s['dmg']+s['mx'])/2 - s['pen']*0.5, g,rec,s,pn))
rows.sort()
for sc,g,rec,s,pn in rows[:34]:
    tag=''
    if g in mine: tag+='[本精英已用]'
    if g in used: tag+='[★撞:'+','.join(used[g])+']'
    if g in BL: tag+='[BL]'
    th='2H' if s['twoH'] is True else ('1H' if s['twoH'] is False else '?')
    print(f"  {rec['zh']:<20}[{rec['rar']:<7}] {th} dmg={s['dmg']}-{s['mx']} pen={s['pen']:<3} {g} <{rec['int']}> ({pn}){tag}")
