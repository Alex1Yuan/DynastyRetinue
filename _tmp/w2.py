# -*- coding: utf-8 -*-
import sys
sys.path.insert(0, r'D:\RT_RetinueMod\ref\bbp\py')
from restr import zh, name
import wstat
OUT=open(r'D:/RT_RetinueMod/_tmp/w2.txt','w',encoding='utf-8')
def P(*a): print(*a, file=OUT)
def lab(g):
    r=zh.get(g)
    if r: return "%s [%s] <%s>" % (r['zh'] or '?', r.get('rar',''), r['int'])
    return "?? <%s>" % (name.get(g) or 'UNK')
W=['0adaa28e654e4fd6a74830eed3f63390','1ba5fc1726374b2a856b53ca172a3460',
 '0f1194cdb530486f83be00c653ccab49','dec66b3861c64c088c5f38fd49024d44',
 'b11dc12ae9e943c29cce0aa7d52bf980','594030b15093416c925dabc415eda101',
 # melee / 磐石 & 铁壁
 '8c304b20633145f39577a392201d2047','9c190bd2019742db8697ac91da1d985e',
 '8ca60374722b4eeea27d99c3b17fb09e','eaa742e43d8e48a3a450c4d59431b03e',
 '1c7a3dcf6afa4b48a7028642e596ab61','bb427b937c304ece913793a020aea87f',
 'e0e76a258d844385a16c9dc602caa316',
 # thunder hammer + kibellah swords + solomorne
 'a2ff030b0d264d1f9ea4ba4ac6a0b3b2',
 '298bd4183b9048b296ae1367870be2bd','4127a489515c4b9b9181d9b61fd33ec7',
 '7da9005aaf4d49969ff989cf0b1619cb','5f4091e7ebae4cfea6831d72db37ba33',
 '2059b9aeefb24b1fb1072c08b9859439',
 # shields
 '76f9b98e76854b459b50a73462429e2b','98b9d50803e64d13abdf8f0619421874',
]
P('%-34s %-46s %s' % ('guid','name','stats'))
for g in W:
    try: w=wstat.wrow(g)
    except Exception as e: w=None
    s = '' if not w else '2H=%d dmg=%d-%d pen=%d dpen=%d hit=%d rec=%d dist=%d ammo=%d rof=%d'%(
        w['two'],w['dmg'],w['mx'],w['pen'],w['dpen'],w['hc'],w['rec'],w['dist'],w['ammo'],w['rof'])
    P('%-34s %-46s %s' % (g, lab(g), s or '(no weapon block)'))
OUT.close()
