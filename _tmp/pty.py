# -*- coding: utf-8 -*-
import json,sys,os
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import zh,name
d=json.load(open('party.json',encoding='utf-8'))
ents=d['m_EntityData']
print('units',len(ents))
def nm(g):
    g=(g or '').replace('-','').lower()
    r=zh.get(g)
    if r: return f"{r['zh']}<{r['int']}>[{r.get('rar','')}]"
    return f"{name.get(g,g)}"
for e in ents[:2]:
    print(json.dumps(e,ensure_ascii=False)[:3000])
    print('KEYS',list(e.keys()))
