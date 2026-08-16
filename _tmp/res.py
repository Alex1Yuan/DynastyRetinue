# -*- coding: utf-8 -*-
import sys, os, json
sys.path.insert(0, r'D:\RT_RetinueMod\ref\bbp\py')
from restr import zh, name
OUT=open(r'D:/RT_RetinueMod/_tmp/res.txt','w',encoding='utf-8')
def P(*a): print(*a, file=OUT)
def lab(g):
    r=zh.get(g)
    if r: return "%s [%s] <%s> %s" % (r['zh'] or '?', r.get('rar',''), r['int'], r['type'].split('.')[-1])
    return "?? <%s>" % (name.get(g) or 'UNKNOWN')
d=json.load(open(r'D:/RT_RetinueMod/src/KgdRetinue/archetypes.json',encoding='utf-8'))
def flat(x):
    if isinstance(x,str): return [y for y in x.split('|')]
    if isinstance(x,list):
        o=[]
        for e in x: o+=flat(e)
        return o
    return []
allg={}
for a in d['archetypes']:
    P('#### ARCH', a.get('name'))
    for e in a.get('elites',[]):
        P('  ==', e.get('name'))
        for i,x in enumerate(e.get('gear',[])):
            ch=flat(x)
            P('   slot%-2d n=%d' % (i,len(ch)))
            for j,g in enumerate(ch):
                P('        %s%s  %s' % ('*' if j==0 else ' ', g, lab(g)))
                allg.setdefault(g,[]).append((e.get('name'),i))
P(''); P('#### DUPES across elites (same guid used by >1 elite)')
for g,us in allg.items():
    el=set(u[0] for u in us)
    if len(el)>1: P('  %s %s  ->  %s' % (g, lab(g), ' | '.join('%s#%d'%u for u in us)))
OUT.close()
