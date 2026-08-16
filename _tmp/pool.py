# -*- coding: utf-8 -*-
"""Per-elite legal-item pool with save/guide provenance."""
import sys,io,json,os,csv
if __name__=='__main__': sys.stdout=io.TextIOWrapper(sys.stdout.buffer,encoding='utf-8')
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import restrictions, zh, name
MOD=r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\KgdRetinue"
BL=set(l.strip().lower() for l in open(os.path.join(MOD,'_elite_gear_blacklist.txt')) if l.strip())
A=json.load(open(r"D:\RT_RetinueMod\src\KgdRetinue\archetypes.json",encoding='utf-8'))
# ---- elite fact sets
ELITES=[]
for a in A['archetypes']:
    base=[g.lower() for g in (a.get('grantFeatures') or [])]+[g.lower() for g in (a.get('preGrant') or [])]
    for e in a.get('elites',[]):
        f=set(base)|set(g.lower() for g in (e.get('preGrant') or []))
        gear=[]
        for x in e.get('gear',[]):
            gear.append([x.lower()] if isinstance(x,str) else [y.lower() for y in x])
        ELITES.append({'arch':a['name'],'name':e['name'],'facts':f,'gear':gear,'race':e.get('race')})
def legal(g,facts):
    """returns (ok, reasons)"""
    bad=[]
    for k,inst,v,inh in restrictions(g):
        if not v: continue
        if 'ERR' in v: bad.append('ERR'); continue
        if 'facts' in v:
            hit = all(f in facts for f in v['facts']) if v['all'] else any(f in facts for f in v['facts'])
            if v['inverted'] and hit:
                bad.append('EXCL:'+','.join(zh.get(f,{}).get('int',f[:8]) for f in v['facts'] if f in facts))
            if (not v['inverted']) and not hit:
                bad.append('MISS:'+'|'.join(zh.get(f,{}).get('int',f[:8]) for f in v['facts']))
        elif 'MainPlayer' in k: bad.append('!!仅主控')
        elif 'SpecialUnit' in k: bad.append('!!特定单位')
        elif k=='EquipmentRestrictionStat':
            st,mn,fa,sub=v['stat'],v['min'],v.get('fact'),v.get('sub',0)
            eff=mn-(sub if (fa and fa in facts) else 0)
            bad.append(f'STAT{st}>={eff}' if eff>45 else '')
        elif 'AugmentTier' in k: bad.append('层级门')
        else: bad.append(k.replace('EquipmentRestriction',''))
    return [b for b in bad if b]
if __name__=='__main__':
    ei=int(sys.argv[1]); typ=sys.argv[2]; rar=sys.argv[3] if len(sys.argv)>3 else None
    E=ELITES[ei]
    used=set()
    for k,o in enumerate(ELITES):
        if k==ei: continue
        for ch in o['gear']:
            for g in ch: used.add(g)
    mine=set(g for ch in E['gear'] for g in ch)
    print(f"# [{ei}] {E['arch']} / {E['name']}  facts={len(E['facts'])}  type={typ} rar={rar}")
    rows=[]
    for g,rec in zh.items():
        if rec.get('type')!=typ: continue
        if rar and rec.get('rar')!=rar: continue
        b=legal(g,E['facts'])
        rows.append((len(b),rec.get('zh') or '?',g,rec.get('int'),rec.get('rar'),';'.join(b)))
    rows.sort(key=lambda r:(r[0],r[1]))
    for n,z,g,i,r,b in rows:
        tag=''
        if g in mine: tag+='[本精英已用]'
        if g in used: tag+='[★与他人撞]'
        if g in BL: tag+='[BL]'
        print(('OK ' if n==0 else '-- ')+f'{g} {z:<24}[{r}] <{i}>{tag} {b}')
