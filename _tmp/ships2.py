import json,struct,re,collections,uuid
B=r"H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles"
data=open(B+r"\blueprints-pack.bbp",'rb').read()
n=struct.unpack_from('<I',data,0)[0]
offs=[]
for i in range(n):
    p=4+i*20
    gn=uuid.UUID(bytes_le=data[p:p+16]).hex
    offs.append((struct.unpack_from('<I',data,p+16)[0],gn))
srt=sorted(offs); end={}
for k,(o,g) in enumerate(srt): end[g]=srt[k+1][0] if k+1<len(srt) else len(data)
start={g:o for o,g in offs}

cd=json.load(open(B+r"\cheatdata.json",encoding='utf-8'))['Entries']
name_by_guid={e['Guid'].replace('-','').lower():e['Name'] for e in cd}
guid_by_name={}
for e in cd: guid_by_name.setdefault(e['Name'],e['Guid'].replace('-','').lower())
ships=[e for e in cd if e.get('TypeFullName','').startswith('Warhammer.SpaceCombat.Blueprints.BlueprintStarship,')]

loc=json.load(open(B+r"\locationlist.json",encoding='utf-8'))
locmap=dict(zip(loc['m_Guids'],loc['m_Bundles']))
SZ={9:'Raider_1x1',10:'Frigate_1x2',11:'Cruiser_2x4',12:'GrandCruiser_3x6',5:'Large',4:'Medium'}
HEX=re.compile(rb'[0-9a-f]{32}')

rows=[]
for e in ships:
    g=e['Guid'].replace('-','').lower(); nm=e['Name']
    blob=data[start[g]:end[g]]
    size=None
    for m in range(0,len(blob)-20):
        if blob[m+16:m+20]==b'\x00\x00\x80\x3f':
            v=struct.unpack_from('<i',blob,m)[0]
            if 0<=v<=12: size=v; break
    vs=guid_by_name.get(nm+'_VisualSettings')
    toks=[(mm.start(),mm.group().decode()) for mm in HEX.finditer(blob)]
    prefab=None
    if vs:
        vspos=[p for p,t in toks if t==vs]
        if vspos:
            cands=[t for p,t in toks if p<vspos[0] and t in locmap]
            if cands: prefab=cands[-1]
    if prefab is None:
        c=[t for p,t in toks if t in locmap]
        prefab=c[-1] if c else None
    rows.append((nm,g,size,prefab,bool(vs)))

bysize=collections.Counter(); pmap=collections.defaultdict(set); users=collections.defaultdict(list)
noprefab=[]
for nm,g,size,pf,hasvs in rows:
    s=SZ.get(size,str(size)); bysize[s]+=1
    if pf: pmap[s].add(pf); users[pf].append((nm,s))
    else: noprefab.append(nm)
print("BlueprintStarship:",len(rows))
print("== size ==");  [print("  %-18s %d"%(k,v)) for k,v in bysize.most_common()]
print("no prefab resolved:",len(noprefab), noprefab[:5])
allp=set()
print("== distinct prefabs ==")
for k in ['GrandCruiser_3x6','Cruiser_2x4','Frigate_1x2','Raider_1x1','Large','Medium']:
    if k in pmap: print("  %-18s %d"%(k,len(pmap[k]))); allp|=pmap[k]
print("  TOTAL distinct:",len(allp))
# cross-size reuse check
inv=collections.defaultdict(set)
for k,v in pmap.items():
    for p in v: inv[p].add(k)
print("  prefabs used across >1 size tier:",[p for p,s in inv.items() if len(s)>1])
print()
for tier in ['GrandCruiser_3x6','Cruiser_2x4']:
    print("=== %s ==="%tier)
    for p in sorted(pmap[tier]):
        print("  %s  bundle=%s"%(p,locmap[p]))
        for nm,s in users[p]: print("       <- %s"%nm)
