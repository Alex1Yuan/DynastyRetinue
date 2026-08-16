# -*- coding: utf-8 -*-
import sys,os,re
sys.path.insert(0,r"D:\RT_RetinueMod\ref\bbp\py")
from restr import blob,label,zh,name
HX=re.compile(rb'(?<![0-9a-f])([0-9a-f]{32})(?![0-9a-f])')
for g in sys.argv[1:]:
    g=g.replace('-','').lower()
    print('#'*90); print(label(g))
    d=blob(g)
    if not d: print('  NO BLOB'); continue
    seen=[]
    for m in HX.finditer(d):
        s=m.group(1).decode()
        if s in seen or s==g: continue
        seen.append(s)
        print('   ->',label(s))
    # printable ascii strings
    print('  --- strings ---')
    for m in re.finditer(rb'[ -~]{6,}',d):
        s=m.group(0).decode()
        if re.fullmatch(r'[0-9a-f]{32}',s): continue
        print('   *',s[:160])
