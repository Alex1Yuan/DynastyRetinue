import json,struct,re,collections
PACK=r"H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles\blueprints-pack.bbp"
CHEAT=r"H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles\cheatdata.json"
LOC=r"H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles\locationlist.json"

data=open(PACK,'rb').read()
n=struct.unpack_from('<I',data,0)[0]
idx={}
offs=[]
for i in range(n):
    p=4+i*20
    g=data[p:p+16]
    import uuid
    gn=uuid.UUID(bytes_le=g).hex
    off=struct.unpack_from('<I',data,p+16)[0]
    offs.append((off,gn))
offs_sorted=sorted(offs)
end={}
for k,(o,g) in enumerate(offs_sorted):
    end[g]= offs_sorted[k+1][0] if k+1<len(offs_sorted) else len(data)
start={g:o for o,g in offs}

cd=json.load(open(CHEAT,encoding='utf-8'))
ents=cd['Entries']
print("cheat entries",len(ents), "sample keys", list(ents[0].keys()))
ships=[e for e in ents if e.get('TypeFullName','').startswith('Warhammer.SpaceCombat.Blueprints.BlueprintStarship,')]
print("BlueprintStarship count:", len(ships))

loc=json.load(open(LOC,encoding='utf-8'))
locmap=dict(zip(loc['m_Guids'],loc['m_Bundles']))

SIZENAME={0:'Fine',1:'Diminutive',2:'Tiny',3:'Small',4:'Medium',5:'Large',6:'Huge',7:'Gargantuan',8:'Colossal',9:'Raider_1x1',10:'Frigate_1x2',11:'Cruiser_2x4',12:'GrandCruiser_3x6'}
HEX=re.compile(rb'[0-9a-f]{32}')

def analyze(gid):
    gid=gid.replace('-','').lower()
    if gid not in start: return None
    blob=data[start[gid]:end[gid]]
    # size: int32 followed by 4 floats where 4th == 1.0f (00 00 80 3f)
    size=None
    for m in range(0,len(blob)-20):
        if blob[m+16:m+20]==b'\x00\x00\x80\x3f':
            v=struct.unpack_from('<i',blob,m)[0]
            if 0<=v<=12:
                size=v; break
    # prefab: 32-hex ascii strings present in locationlist
    hits=[h.decode() for h in HEX.findall(blob) if h.decode() in locmap]
    return size, hits, blob

rows=[]
for e in ships:
    gid=e['Guid'].replace('-','').lower()
    r=analyze(gid)
    if r is None: 
        rows.append((e['Name'],gid,None,[]))
        continue
    size,hits,_=r
    rows.append((e['Name'],gid,size,hits))

# stats
bysize=collections.Counter()
prefabs_by_size=collections.defaultdict(set)
multi=0; none=0
for name,gid,size,hits in rows:
    bysize[SIZENAME.get(size,str(size))]+=1
    if len(hits)==1: prefabs_by_size[SIZENAME.get(size,str(size))].add(hits[0])
    elif len(hits)==0: none+=1
    else:
        multi+=1
        prefabs_by_size[SIZENAME.get(size,str(size))].add(hits[0])
print("\n== size histogram ==")
for k,v in bysize.most_common(): print(" %-18s %d"%(k,v))
print("\nblueprints with 0 loadable-asset hits:",none," with >1 hits:",multi)
print("\n== distinct prefabs per size ==")
allp=set()
for k,v in prefabs_by_size.items():
    print(" %-18s %d"%(k,len(v)))
    allp|=v
print(" TOTAL distinct prefabs:",len(allp))
