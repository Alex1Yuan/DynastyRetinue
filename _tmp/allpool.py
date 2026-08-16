# -*- coding: utf-8 -*-
import sys
sys.path.insert(0,r"D:\RT_RetinueMod\_tmp")
import pool
TYPES=['EquipmentHead','EquipmentNeck','EquipmentGloves','EquipmentFeet','EquipmentShoulders','EquipmentRing']
for en in pool.ELITE:
    print('#'*110); print('###### ',en, pool.ELITE[en]['arch'])
    for t in TYPES:
        pool.run(en,t,limit=8)
