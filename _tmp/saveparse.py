# -*- coding: utf-8 -*-
import json,sys,io,os
sys.stdout=io.TextIOWrapper(sys.stdout.buffer,encoding='utf-8')
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import zh,name
T=r'D:\RT_RetinueMod\_tmp\m24'
party=json.load(open(os.path.join(T,'party.json'),encoding='utf-8'))
player=json.load(open(os.path.join(T,'player.json'),encoding='utf-8'))
IDX={}
def index(o):
    st=[o]
    while st:
        c=st.pop()
        if isinstance(c,dict):
            i=c.get('$id')
            if i is not None: IDX[str(i)]=c
            st.extend(c.values())
        elif isinstance(c,list): st.extend(c)
index(party); index(player)
def dr(o):
    if isinstance(o,dict) and '$ref' in o: return IDX.get(str(o['$ref']),o)
    return o
# map item UniqueId -> blueprint
ITEM={}
def scan(o):
    st=[o]
    while st:
        c=st.pop()
        if isinstance(c,dict):
            t=c.get('$type','')
            if 'Kingmaker.Items.ItemEntity' in t and c.get('UniqueId'):
                ITEM[c['UniqueId']]=c.get('Blueprint')
            st.extend(c.values())
        elif isinstance(c,list): st.extend(c)
scan(party); scan(player)
def lb(g):
    if not g: return '(空)'
    g=g.replace('-','').lower()
    r=zh.get(g)
    if r: return f"{r['zh'] or '?'}[{r.get('rar','?')}] <{r['int']}> {g}"
    return f"?? <{name.get(g,'?')}> {g}"
def item(ref):
    if not ref: return '(空)'
    bp=ITEM.get(ref)
    return lb(bp) if bp else f'(未找到 {ref})'
SLOTS=['Armor','Head','Glasses','Neck','Gloves','Feet','Shoulders','Ring1','Ring2','Wrist','Belt','Shirt','PetProtocol']
rows=[]
for e in party['m_EntityData']:
    if not isinstance(e,dict): continue
    parts=e.get('Parts',{}).get('Container',[])
    body=None; desc=None
    for p in parts:
        p=dr(p); 
        if not isinstance(p,dict): continue
        t=p.get('$type','')
        if t.startswith('Kingmaker.Items.PartUnitBody'): body=p
    if not body: continue
    bp=e.get('Blueprint')
    # find custom name
    nm=None
    for p in parts:
        p=dr(p)
        if isinstance(p,dict) and 'PartUnitDescription' in p.get('$type',''):
            nm=p.get('m_CustomName')
    print('='*110)
    print(f"### 单位 blueprint={lb(bp)}  name={nm}  uid={e.get('UniqueId')}")
    sets=body.get('m_HandsEquipmentSets') or []
    cur=body.get('m_CurrentHandsEquipmentSetIndex')
    for i,s in enumerate(sets):
        s=dr(s)
        if not isinstance(s,dict): continue
        ph=dr(s.get('PrimaryHand') or {}); sh=dr(s.get('SecondaryHand') or {})
        m='*' if str(i)==str(cur) else ' '
        print(f"  {m}套组{i}: 主={item(ph.get('m_ItemRef'))}   副={item(sh.get('m_ItemRef'))}")
    for sl in SLOTS:
        v=dr(body.get(sl) or {})
        if not isinstance(v,dict): continue
        r=v.get('m_ItemRef')
        if r: print(f"   {sl:<12} {item(r)}")
    au=dr(body.get('Augments') or {})
    if isinstance(au,dict):
        for kv in au.get('m_Slots') or []:
            v=dr(kv.get('Value') or {})
            r=v.get('m_ItemRef') if isinstance(v,dict) else None
            if r: print(f"   AUG[{kv.get('Key')[:8]}] {item(r)}")
