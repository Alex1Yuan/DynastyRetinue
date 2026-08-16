# -*- coding: utf-8 -*-
import sys,struct
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import blob,label
g=sys.argv[1].replace('-','').lower()
d=blob(g); tail=d.rfind(g.encode()); p=tail-1
print(label(g),'len',len(d),'tail',hex(tail),'byte',hex(d[p]))
ns=None
for L in range(1,80):
    s=p-L
    if s>0 and d[s-1]==L and all(32<=c<127 for c in d[s:s+L]):
        ns=s-1; print('name',d[s:s+L].decode(),'ns',hex(ns)); break
for gap in range(0,60):
    P=ns-gap-37
    v=[d[P]]+list(struct.unpack_from('<9i',d,P+1))
    print(gap, v)
