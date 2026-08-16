# -*- coding: utf-8 -*-
import sys
sys.path.insert(0, r'D:\RT_RetinueMod\ref\bbp\py')
from restr import zh, name, restrictions
import wstat
OUT=open(r'D:/RT_RetinueMod/_tmp/melee.txt','w',encoding='utf-8')
def P(*a): print(*a, file=OUT)
BAD={'950565a69d144391897cdc0024747755','affa5fdded7e404b910b990f5d344a8c',
     'c7a53b43ac5f4f11a1aef612dd3f4f71','590cb5c394ee4b5eb213121c96063dec'}
rows=[]
for g,r in zh.items():
    if r['type']!='Weapons.BlueprintItemWeapon': continue
    if r.get('rar') not in ('Unique','Pattern'): continue
    try: w=wstat.wrow(g)
    except Exception: w=None
    if not w or w['dist']!=1: continue      # melee only
    # restrictions
    req=[]; strmin=0; ok=True
    try: rs=restrictions(g)
    except Exception: rs=[]
    for k,inst,v,inh in rs:
        if 'facts' in v:
            if not v['inverted']:
                fs=set(v['facts'])
                if fs-BAD: req.append('REQ:'+','.join(name.get(f,f) for f in v['facts']))
        elif k=='EquipmentRestrictionStat':
            strmin=max(strmin, v.get('min',0)-(0))
    rows.append((w['mx'],w['dmg'],w['pen'],w['two'],strmin,req,g,r))
rows.sort(reverse=True)
P('%-4s %-4s %-4s %-3s %-5s %-34s %s'%('max','dmg','pen','2H','strM','guid','name'))
for mx,dmg,pen,two,sm,req,g,r in rows[:45]:
    P('%-4d %-4d %-4d %-3d %-5d %-34s %s [%s] <%s> %s'%(mx,dmg,pen,two,sm,g,r['zh'],r.get('rar'),r['int'],';'.join(req)))
OUT.close()
