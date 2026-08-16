# -*- coding: utf-8 -*-
import sys,os,struct,re
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import blob,label,zh,name
FIELDS=['2H','Dmg','MaxDmg','Pen','DodgePen','AddHit','Recoil','Dist','Ammo','RoF']
def wstat(g):
    g=g.replace('-','').lower()
    d=blob(g)
    if not d: return None
    # tail: [varint len][internalName][varint 32][guid]
    tail=d.rfind(g.encode())
    if tail<0: return None
    # back up: 1 byte len(32) before guid, then internal name, then its len byte
    p=tail-1
    if d[p]!=0x20: return None
    # find name start: scan back for a length byte matching
    ns=None
    for L in range(1,80):
        s=p-L
        if s>0 and d[s-1]==L and all(32<=c<127 for c in d[s:s+L]):
            ns=s-1; break
    if ns is None: return None
    best=None
    for gap in [13+33*k for k in range(0,10)]:
        P=ns-gap-37
        if P<0: continue
        v=[d[P]]+list(struct.unpack_from('<9i',d,P+1))
        th,dm,mx,pen,dp,ah,rc,di,am,rof=v
        if th not in (0,1): continue
        if not (0<=dm<=300 and dm<=mx<=400): continue
        if not (0<=pen<=200 and 0<=dp<=200 and -200<=ah<=200): continue
        if not (0<=rc<=300 and 0<=di<=300): continue
        if am!=-1 and not (0<=am<=999): continue
        if not (-1<=am<=999 and 0<=rof<=40): continue
        if not (dm>=1 and mx>=dm and 0<=rof<=40): continue
        best=(gap,v); break
    return best
rows=[]
for g in sys.argv[1:]:
    r=wstat(g)
    lb=label(g.replace('-','').lower())
    if not r: print(f"{'?':<70} {lb}"); continue
    gap,v=r
    s=' '.join(f"{n}={x}" for n,x in zip(FIELDS,v))
    print(f"gap{gap:<3} {s}")
    print(f"        {lb}")
