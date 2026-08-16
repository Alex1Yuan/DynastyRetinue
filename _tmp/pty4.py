# -*- coding: utf-8 -*-
import json,sys
d=json.load(open('party.json',encoding='utf-8'))
e=d['m_EntityData'][0]
for p in e['Parts']['Container']:
    t=(p.get('$type') or '')
    if 'PartUnitBody' in t:
        print(json.dumps(p,ensure_ascii=False)[:6000]); break
