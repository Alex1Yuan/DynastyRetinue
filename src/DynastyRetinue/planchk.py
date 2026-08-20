import json,io,collections,sys,os
sys.path.insert(0,r'D:\RT_RetinueMod\ref\bbp\py')
from restr import name
J=json.load(io.open('plans.json',encoding='utf-8'))
for p in J['plans']:
    if p.get('id')!='rt_abelard_vanguard': continue
    acc=[]
    def walk(o):
        if isinstance(o,dict):
            for v in o.values(): walk(v)
        elif isinstance(o,list):
            for v in o: walk(v)
        elif isinstance(o,str): acc.append(o)
    walk(p)
    c=collections.Counter()
    for s in acc:
        nm=name.get(s.lower())
        if nm and ('Advancement' in nm or 'StatTraining' in nm): c[nm]+=1
    print('PLAN rt_abelard_vanguard  total refs',len(acc))
    for k,v in sorted(c.items()): print('   %-50s x%d'%(k,v))
