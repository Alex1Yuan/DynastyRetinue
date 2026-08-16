# -*- coding: utf-8 -*-
import sys,os,re,struct
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import blob,label,zh,name,_entries,rdstr
for g in sys.argv[1:]:
    g=g.replace('-','').lower()
    print('#'*90)
    print(label(g))
    d=blob(g)
    print('  blobLen',len(d) if d else None)
    ents,_=_entries(g)
    for k,inst,pg,pi,fp,i in ents:
        print(f"   - {k:<50} inst={inst[:8]} proto={(pg or '')[:8]} @{fp}")
