"""扫所有存档，看哪些档里出现过指定的物品蓝图 GUID。"""
import io, os, zipfile, re, sys

SG = r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Saved Games"

TARGETS = {
    "e0e76a258d844385a16c9dc602caa316": "碎骨霰弹枪(手枪型)",
    "a922a79edfc849ddba568bd152fa9f3e": "崇高虔诚(雷锤)",
    "8ca60374722b4eeea27d99c3b17fb09e": "痛苦尖叫",
    "3f677383da874ab1ba5c8e106cfa9c72": "人类拥护者(动力甲)",
    "8992baa7047e4736b43fa1b94b0407b6": "痛苦欢宴(手套)",
    "abb1cff0af5748898bad93a355817e4c": "轻型机械手(戒指)",
    "4b06abc0736449eb8adecad43a2b37df": "虚空老兵战靴",
    "18b727b71c794b558d229a7831ef7bee": "聚焦发射器MK猎人",
    "13757f3863ec47929fd914b7f306fa31": "相位振荡器MK战术家",
    "7e2b91b8cc944f4c951fb2058fcb2f22": "大脑逆变器MK特工",
}

WANT = ("party.json", "player.json")

for fn in sorted(os.listdir(SG)):
    if not fn.lower().endswith(".zks"):
        continue
    p = os.path.join(SG, fn)
    try:
        z = zipfile.ZipFile(p)
    except Exception:
        continue
    blob = []
    for n in WANT:
        try:
            blob.append(z.read(n).decode("utf-8", "replace"))
        except Exception:
            pass
    if not blob:
        continue
    txt = "\n".join(blob)
    hits = [name for g, name in TARGETS.items() if g in txt]
    if hits:
        print("%-38s %d/%d  %s" % (fn, len(hits), len(TARGETS), "、".join(hits)))
    else:
        print("%-38s 0/%d" % (fn, len(TARGETS)))
