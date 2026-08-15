"""查指定存档里存在哪些爆矢枪类武器。"""
import io, os, zipfile

SG = r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Saved Games"
SAVES = ["Manual_25_____________13_19_01.zks", "Auto_4.zks", "Manual_24___________15_38_27.zks"]

BOLTERS = {
    "dec66b3861c64c088c5f38fd49024d44": "改良重型爆矢枪 (Sarragus, 现用)",
    "b11dc12ae9e943c29cce0aa7d52bf980": "重型爆矢枪 (HeavyBolter 基础)",
    "4915e47413cd48598679daa1119b5027": "重型爆矢枪 (Sororitas DLC3)",
    "781b90112a784f03843bb8faa34d1ae7": "歼灭型阿斯塔特爆矢枪 (Annihilator)",
    "a17e98b7e2c643a6bdcc7f4c8440f82c": "阿斯塔特台风爆矢枪 (OneAndDone)",
    "3415d3e981cc4c10a573e6fc2e06ddf8": "阿斯塔特风暴爆矢枪",
    "347392af130049c0b3c90c3b0aca8f36": "风暴爆矢枪 (Halbrandt 特殊)",
    "df0daf689be7420fbc39e7ffaad8a508": "风暴爆矢枪 (StormBolter)",
    "0b1a6f7920114dafb5ffc2bdea37bf8d": "阿斯塔特爆矢枪",
    "072a9436539d4469915b121da1671a9f": "精准爆矢枪",
    "6aae6fd5deed4157a985d4db85787503": "聚合爆矢枪 (帕斯卡任务)",
    "5a086adc8f024026a236e8e33fcc17bf": "改造过的爆矢枪 (T1)",
    "e339f2b8e5854b54bf5af6c7642f35c0": "[莱托比型] 爆矢枪",
}

for s in SAVES:
    p = os.path.join(SG, s)
    if not os.path.exists(p):
        print("缺: %s" % s); continue
    z = zipfile.ZipFile(p)
    txt = ""
    for n in ("party.json", "player.json"):
        try: txt += z.read(n).decode("utf-8", "replace")
        except Exception: pass
    print("=== %s ===" % s)
    for g, name in BOLTERS.items():
        c = txt.count(g)
        if c: print("   %-34s x%d  %s" % (name, c, g))
    print()
