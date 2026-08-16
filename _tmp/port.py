import json,struct,re,uuid,collections
B=r"H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles"
data=open(B+r"\blueprints-pack.bbp",'rb').read()
n=struct.unpack_from('<I',data,0)[0]
offs=[]
for i in range(n):
    p=4+i*20
    offs.append((struct.unpack_from('<I',data,p+16)[0],uuid.UUID(bytes_le=data[p:p+16]).hex))
srt=sorted(offs); end={}
for k,(o,g) in enumerate(srt): end[g]=srt[k+1][0] if k+1<len(srt) else len(data)
start={g:o for o,g in offs}
cd=json.load(open(B+r"\cheatdata.json",encoding='utf-8'))['Entries']
nm_by={e['Guid'].replace('-','').lower():e['Name'] for e in cd}
gid_by={}
for e in cd: gid_by.setdefault(e['Name'],e['Guid'].replace('-','').lower())
loc=json.load(open(B+r"\locationlist.json",encoding='utf-8'))
locmap=dict(zip(loc['m_Guids'],loc['m_Bundles']))
HEX=re.compile(rb'[0-9a-f]{32}')
targets=['ChaosGrand10','ImperialTransport3','DLC2_ImperialTransport_1','ImperialCruiser10Named','ChaosCruiser5','OrkCruiser10','PirateCruiser7','DrukhariCruiser6','DLC3_AeldariBoss_var1','DLC1_ChaosTransport','PirateCruiser12Named','DLC1_ImperialCruiser5','DLC2_PirateCruiser_Boss','SwordClassFrigatePlayer_Starship','FounderShip_FalchionClassFrigatePlayer_Starship','AlternateShip_FirestormClassFrigatePlayer_Starship']
for t in targets:
    g=gid_by.get(t)
    if not g: print(t,"NOT IN CHEATDATA"); continue
    blob=data[start[g]:end[g]]
    toks=[(m.start(),m.group().decode()) for m in HEX.finditer(blob)]
    vs=gid_by.get(t+'_VisualSettings')
    prefab=None;portrait=None
    if vs:
        vp=[p for p,x in toks if x==vs]
        if vp:
            before=[(p,x) for p,x in toks if p<vp[0]]
            load=[x for p,x in before if x in locmap]
            if load: prefab=load[-1]
            # portrait = last blueprint-guid token before prefab
            if prefab:
                pp=[p for p,x in before if x==prefab][-1]
                bps=[x for p,x in before if p<pp and x in nm_by]
                if bps: portrait=bps[-1]
    print("%-48s prefab=%s  portrait=%s (%s)"%(t,prefab,nm_by.get(portrait,'?'),portrait))
