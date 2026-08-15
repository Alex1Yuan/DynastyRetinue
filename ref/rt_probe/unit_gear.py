import io, os, json, zipfile, sys, collections

SG = r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Saved Games"
CD2 = r"D:\RT_RetinueMod\ref\rt_probe\cd2.txt"
ZH = r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\KgdRetinue\items_zh.tsv"
SAVE = sys.argv[1] if len(sys.argv) > 1 else "Manual_24___________15_38_27.zks"
WHO = sys.argv[2] if len(sys.argv) > 2 else "heinrix"

bpname = {}
for line in io.open(CD2, encoding="utf-8", errors="replace"):
    i = line.find('"Name":"')
    if i < 0: continue
    j = line.find('","Guid":"', i)
    if j < 0: continue
    k = line.find('"', j + 10)
    bpname[line[j+10:k]] = line[i+8:j]

zh = {}
for i, l in enumerate(io.open(ZH, encoding="utf-8")):
    if i == 0: continue
    f = l.rstrip("\n").split("\t")
    if len(f) >= 4: zh[f[1]] = (f[0], f[2].split(".")[-1])

z = zipfile.ZipFile(os.path.join(SG, SAVE))

# 建立 UniqueId -> 物品蓝图 的全局索引（两份 json 都扫）
uid2bp = {}
def collect(o):
    if isinstance(o, dict):
        u, b = o.get("UniqueId"), o.get("Blueprint")
        if isinstance(u, str) and isinstance(b, str): uid2bp[u] = b
        for v in o.values(): collect(v)
    elif isinstance(o, list):
        for v in o: collect(v)

party = json.loads(z.read("party.json").decode("utf-8", "replace"))
collect(party)
try: collect(json.loads(z.read("player.json").decode("utf-8", "replace")))
except Exception as e: print("player.json 读取失败:", e)
print("UniqueId 索引 %d 条" % len(uid2bp))

def show(label, ref):
    if not ref: return
    b = uid2bp.get(ref)
    if not b: print("   %-12s <未解析 %s>" % (label, ref[:8])); return
    n, t = zh.get(b, (bpname.get(b, "?"), "?"))
    print("   %-12s %-26s %-16s %s" % (label, n, t.replace("BlueprintItem","").replace("Equipment",""), b))

for e in party.get("m_EntityData", []):
    if WHO.lower() not in str(bpname.get(e.get("Blueprint"), "")).lower(): continue
    print("\n=== %s  (%s) ===" % (bpname.get(e.get("Blueprint")), SAVE))
    body = None
    for p in (e.get("Parts", {}).get("Container") or []):
        if "PartUnitBody" in str(p.get("$type", "")): body = p; break
    if body is None: print("   没有 PartUnitBody"); continue

    for si, s in enumerate(body.get("m_HandsEquipmentSets") or []):
        show("套组%d主手" % (si+1), (s.get("PrimaryHand") or {}).get("m_ItemRef"))
        show("套组%d副手" % (si+1), (s.get("SecondaryHand") or {}).get("m_ItemRef"))
    for key, label in (("Armor","护甲"),("Head","头"),("Neck","项链"),("Gloves","手套"),
                       ("Feet","靴"),("Shoulders","披风"),("Ring1","戒指1"),("Ring2","戒指2"),
                       ("Wrist","腕"),("Glasses","眼镜"),("Belt","腰带"),("Shirt","衬衣")):
        show(label, (body.get(key) or {}).get("m_ItemRef"))
    for qi, q in enumerate(body.get("m_QuickSlots") or []):
        show("快捷%d" % (qi+1), q.get("m_ItemRef"))
    aug = body.get("Augments") or {}
    slots = aug.get("m_Slots") or aug.get("Slots") or []
    filled = 0
    if isinstance(slots, list):
        for s in slots:
            v = s.get("Value") if isinstance(s, dict) else None
            ref = (v or {}).get("m_ItemRef") if isinstance(v, dict) else None
            if ref:
                filled += 1
                show("植入物", ref)
    print("   植入位 %d 个，已装 %d 个" % (len(slots) if isinstance(slots, list) else 0, filled))
