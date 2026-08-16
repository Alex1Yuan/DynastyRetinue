# -*- coding: utf-8 -*-
import json,sys,os
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import zh,name
d=json.load(open('party.json',encoding='utf-8'))
e=d['m_EntityData'][0]
c=e['Parts']['Container']
print(type(c))
if isinstance(c,dict): print(list(c.keys())[:20])
if isinstance(c,list):
    for p in c:
        print('  ',(p.get('$type') or '?').split(',')[0])
