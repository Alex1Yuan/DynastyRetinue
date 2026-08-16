# -*- coding: utf-8 -*-
import sys, json
sys.path.insert(0, r'D:\RT_RetinueMod\ref\bbp\py')
from restr import zh, name, restrictions
OUT=open(r'D:/RT_RetinueMod/_tmp/slots.txt','w',encoding='utf-8')
def P(*a): print(*a, file=OUT)
BAD={'950565a69d144391897cdc0024747755','affa5fdded7e404b910b990f5d344a8c',
     'c7a53b43ac5f4f11a1aef612dd3f4f71','590cb5c394ee4b5eb213121c96063dec'}
bl=set(l.strip() for l in open(r'C:/Users/kyua805/AppData/LocalLow/Owlcat Games/Warhammer 40000 Rogue Trader/UnityModManager/KgdRetinue/_elite_gear_blacklist.txt') if l.strip())
d=json.load(open(r'D:/RT_RetinueMod/src/KgdRetinue/archetypes.json',encoding='utf-8'))
pool=set()
for a in d['archetypes']:
    for k in ('gearT1','gearT2','gearT3'):
        for x in a.get(k,[]):
            for y in str(x).split('|'): pool.add(y)
# desc map
desc={}
for i,l in enumerate(open(r'C:/Users/kyua805/AppData/LocalLow/Owlcat Games/Warhammer 40000 Rogue Trader/UnityModManager/KgdRetinue/features_zh.tsv',encoding='utf-8',errors='replace')):
    if i==0: continue
    p=l.rstrip('\n').split('\t')
    if len(p)>=5: desc[p[0]]=p[4]
for want in ('EquipmentHead','EquipmentShoulders'):
    P('#'*30, want)
    for g,r in zh.items():
        if not r['type'].endswith(want): continue
        if r.get('rar')!='Unique': continue
        if g in bl or g in pool: continue
        try: rs=restrictions(g)
        except Exception: continue
        hard=[]
        for k,inst,v,inh in rs:
            if 'facts' in v and not v['inverted']:
                if set(v['facts'])-BAD: hard.append('REQ:'+','.join(name.get(f,f) for f in v['facts']))
            elif k!='EquipmentRestrictionHasFacts': hard.append('%s:%s'%(k,v))
        if hard: continue
        P('  %-34s %-22s %s' % (g, r['zh'], (desc.get(r['zh']) or '')[:150]))
OUT.close()
