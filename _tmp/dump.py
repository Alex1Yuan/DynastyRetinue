# -*- coding: utf-8 -*-
import sys,os,re,struct
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import blob,label,zh,name,restrictions,_entries
sys.path.insert(0,r"D:\RT_RetinueMod\_tmp")
from wstat import wstat,FIELDS
ZHDIR=r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\KgdRetinue"
desc={}
for i,line in enumerate(open(os.path.join(ZHDIR,'features_zh.tsv'),encoding='utf-8',errors='replace')):
    if i==0: continue
    p=line.rstrip('\n').split('\t')
    if len(p)>=5: desc[p[1]]=(p[0],p[3],p[4])
ARM={}
for i,line in enumerate(open(os.path.join(ZHDIR,'armor_rank.tsv'),encoding='utf-8',errors='replace')):
    if i==0: continue
    p=line.rstrip('\n').split('\t')
    if len(p)>=8: ARM[p[5]]=p
HX=re.compile(rb'(?<![0-9a-f])([0-9a-f]{32})(?![0-9a-f])')
def facts(g):
    d=blob(g); out=[]
    if not d: return out
    for m in HX.finditer(d):
        s=m.group(1).decode()
        if s in desc and s not in [x[0] for x in out]:
            out.append((s,)+desc[s])
    return out
for g in sys.argv[1:]:
    g=g.replace('-','').lower()
    print('='*100)
    print(label(g))
    r=zh.get(g,{})
    print('   type=%s rar=%s'%(r.get('type','?'),r.get('rar','?')))
    w=wstat(g)
    if w: print('   STAT '+' '.join(f"{n}={x}" for n,x in zip(FIELDS,w[1])))
    if g in ARM:
        a=ARM[g]; print(f"   ARMOR abs={a[0]} defl={a[1]} cat={a[3]} prof={a[4]}")
        print(f"   ARMORDESC {a[7][:400]}")
    rs=restrictions(g)
    for k,inst,v,inh in rs:
        if v and 'facts' in v:
            print(f"   {'EXCL' if v['inverted'] else 'REQ '} {'ALL' if v['all'] else 'ANY'}: "+', '.join((zh.get(f,{}) or {}).get('int',f) for f in v['facts']))
        else: print(f"   {k.replace('EquipmentRestriction','RESTR:')} {v}")
    for f in facts(g):
        if f[3].strip() or f[1].strip():
            print(f"   FACT [{f[1]}] <{f[2]}> :: {f[3][:300]}")
