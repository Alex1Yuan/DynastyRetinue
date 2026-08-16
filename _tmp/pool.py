# -*- coding: utf-8 -*-
import json,sys,os,re
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import zh,name,restrictions
ZH=r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\KgdRetinue"
J=json.load(open(r"D:\RT_RetinueMod\src\KgdRetinue\archetypes.json",encoding='utf-8'))
BL=set(l.strip().lower() for l in open(os.path.join(ZH,'_elite_gear_blacklist.txt')) if l.strip())
# owned
owned={}
for i,l in enumerate(open(r"D:\RT_RetinueMod\ref\owned_items.tsv",encoding='utf-8',errors='replace')):
    if i==0: continue
    p=l.rstrip('\n').split('\t')
    if len(p)>=5: owned[p[2]]=(int(p[3]),p[4])
# save usage: parse party_gear.txt
use={}
cur=None
for l in open(r"D:\RT_RetinueMod\_tmp\party_gear.txt",encoding='utf-8'):
    if l.startswith('UNIT '): cur=l.strip()[5:].split(' ')[0]
    m=re.search(r'([0-9a-f]{32})\s*$',l.strip())
    if m and cur: use.setdefault(m.group(1),set()).add(cur)
# elites
ELITE={}
for a in J['archetypes']:
    gf=[x.lower() for x in a.get('grantFeatures',[])]+[x.lower() for x in (a.get('preGrant') or [])]
    for e in a.get('elites',[]):
        f=gf+[x.lower() for x in (e.get('preGrant') or [])]
        ELITE[e['name']]=dict(arch=a['name'],facts=set(f),race=e.get('race'))
def ok(g,facts):
    bad=[]
    for k,inst,v,inh in restrictions(g):
        if not v: continue
        if 'facts' in v:
            hit = all(f in facts for f in v['facts']) if v['all'] else any(f in facts for f in v['facts'])
            if v['inverted'] and hit: bad.append('EXCL')
            elif (not v['inverted']) and not hit:
                bad.append('MISS:'+'/'.join((zh.get(f,{}) or {}).get('int',f)[:30] for f in v['facts']))
        elif 'ERR' in v: pass
        elif k=='EquipmentRestrictionMainPlayer': bad.append('MAINPLAYER')
        elif k=='EquipmentRestrictionStat': bad.append('STAT%s>=%d(-%d)'%(v['stat'],v['min'],v['sub']))
        elif k=='EquipmentRestrictionAugmentTier': bad.append('AUGTIER')
    return bad
RAR={'Unique':0,'Quest':1,'Pattern':2,'Common':3,'':4,None:4}
def run(ename,typ,limit=25,ownedonly=True):
    E=ELITE[ename]; facts=E['facts']
    rows=[]
    for g,r in zh.items():
        if typ not in r.get('type',''): continue
        if ownedonly and g not in owned: continue
        bad=ok(g,facts)
        hard=[b for b in bad if b.startswith('EXCL') or b.startswith('MISS') or b=='MAINPLAYER']
        if hard: continue
        rows.append((RAR.get(r.get('rar'),4), -(owned.get(g,(0,''))[0]), r['zh'], r['int'], r.get('rar'), g, ','.join(sorted(use.get(g,[]))), ';'.join(bad), 'BL' if g in BL else ''))
    rows.sort()
    print(f"### {ename} | {typ} | owned-only={ownedonly}")
    for t in rows[:limit]:
        print(f"   {t[5]} {t[4]:<8} refs{-t[1]:<3} {t[2][:22]:<24} <{t[3][:44]:<44}> save={t[6][:40]:<40} {t[7]} {t[8]}")
    print()
if __name__=='__main__':
    en=sys.argv[1]; 
    for t in sys.argv[2:]:
        run(en,t)
