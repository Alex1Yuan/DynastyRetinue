# -*- coding: utf-8 -*-
import json,sys,os
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import zh,name
d=json.load(open('party.json',encoding='utf-8'))
ents=d['m_EntityData']
e=ents[0]
parts=e.get('Parts')
print('parts type',type(parts))
if isinstance(parts,dict):
    print(list(parts.keys()))
    lst=parts.get('m_Parts')
    if isinstance(lst,list):
        for p in lst:
            print('  ',p.get('$type','?')[:90])
