# -*- coding: utf-8 -*-
import sys,re
sys.path.insert(0, r'D:\RT_RetinueMod\ref\bbp\py')
from restr import blob, zh, name, proto, rdstr
OUT=open(r'D:/RT_RetinueMod/_tmp/sh.txt','w',encoding='utf-8')
def P(*a): print(*a, file=OUT)
HEX32 = re.compile(rb'(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])')
def lab(g):
    r=zh.get(g)
    if r: return "%s[%s]<%s>%s" % (r['zh'] or '?', r.get('rar',''), r['int'], r['type'].split('.')[-1])
    return "<%s>" % (name.get(g) or g)
for g,n in [('76f9b98e76854b459b50a73462429e2b','坚固盾牌'),
            ('98b9d50803e64d13abdf8f0619421874','枪手盾牌'),
            ('0adaa28e654e4fd6a74830eed3f63390','血腥礼赞'),
            ('1ba5fc1726374b2a856b53ca172a3460','泪如泉涌'),
            ('dec66b3861c64c088c5f38fd49024d44','改良重型爆矢枪'),
            ('eb8c58901bb049ec9ad3775c31eeabfe','命运之星Fury'),
            ('9dec89a18f9a4175852ffa669fc05f6d','命运之星Fire')]:
    d=blob(g)
    P('='*80); P(n, g, lab(g), 'len=%s'%(len(d) if d else None), 'proto=%s'%proto(g))
    if not d: continue
    # ascii strings
    ss=set(re.findall(rb'[\x20-\x7e]{6,}', d))
    P('  STR:', sorted(x.decode() for x in ss)[:40])
    for m in HEX32.finditer(d):
        r=m.group(0).decode()
        if r==g: continue
        nm=name.get(r); rec=zh.get(r)
        if nm or rec: P('   ->', r, lab(r))
OUT.close()
