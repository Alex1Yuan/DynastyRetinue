# -*- coding: utf-8 -*-
import sys,io,json,re
sys.stdout=io.TextIOWrapper(sys.stdout.buffer,encoding='utf-8')
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import blob,label,zh,name,restrictions
ZH=r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\KgdRetinue\features_zh.tsv"
desc={}
for i,l in enumerate(open(ZH,encoding='utf-8',errors='replace')):
    if i==0: continue
    p=l.rstrip('\n').split('\t')
    if len(p)>=4: desc[p[1]]={'zh':p[0],'int':p[3].strip(),'d':p[4] if len(p)>4 else ''}
NAMES=['阿贝拉德','阿尔金塔','卡西娅','海因里希','伊迪拉','洁','奇贝拉','马拉扎','帕斯卡','乌尔法','伊莉耶特','索洛莫恩','厄根','阿洁塔','焦特']
ENAMES=['Abelard','Argenta','Cassia','Heinrix','Idira','Jae','Kibellah','Marazhai','Pascal','Ulfar','Yrliet','Solomorne','Eogann']
GU=re.compile(rb'[0-9a-f]{32}')
J=json.load(open(r"D:\RT_RetinueMod\src\KgdRetinue\archetypes.json",encoding='utf-8'))
rows=[]
for a in J['archetypes']:
    for e in a.get('elites',[]):
        for i,x in enumerate(e.get('gear',[])):
            xs=[x] if isinstance(x,str) else x
            for j,g in enumerate(xs):
                rows.append((e['name'],i,j,g.replace('-','').lower()))
seen={}
for en,i,j,g in rows:
    if g in seen: seen[g].append((en,i,j)); continue
    seen[g]=[(en,i,j)]
print('=== 模式C 扫描：内部名或效果描述里含同伴名 / MainPlayer 限制 ===')
for g,users in seen.items():
    rec=zh.get(g,{})
    intn=rec.get('int','')
    flags=[]
    for n in ENAMES:
        if n.lower() in intn.lower(): flags.append('内部名含'+n)
    d=blob(g)
    hits=[]
    if d:
        for m in GU.finditer(d):
            s=m.group(0).decode()
            r=desc.get(s)
            if not r: continue
            txt=(r['d'] or '')
            for n in NAMES:
                if n in txt: hits.append((r['zh'],n,txt[:110]))
            for n in ENAMES:
                if n.lower() in (r['int'] or '').lower() and 'Proficiency' not in r['int']: hits.append((r['zh'],r['int'],txt[:110]))
    rs=restrictions(g)
    for k,inst,v,inh in rs:
        if 'MainPlayer' in k: flags.append('!!EquipmentRestrictionMainPlayer')
        if 'SpecialUnit' in k: flags.append('!!SpecialUnit '+str(v))
    if flags or hits:
        print(f"\n-- {rec.get('zh','?')}[{rec.get('rar','?')}] <{intn}> {g}")
        print('   使用者:', ', '.join(f'{u}#{i}.{j}' for u,i,j in users))
        for f in flags: print('   FLAG', f)
        for z,n,t in hits[:3]: print(f'   效果[{z}] 含<{n}>: {t}')
