"""从存档里挖出海因里希的装备。

party.json 里实体是一棵带 $id/$ref 的对象树。做法：
  1. 先把所有 {"$id": n, ...} 建成索引
  2. 找到 Blueprint 指向海因里希的那个实体
  3. 在它的 PartUnitBody 里顺着槽位的 m_ItemRef 找到物品实体
  4. 物品实体的 Blueprint 就是我们要的物品蓝图 GUID
"""
import io, os, json, zipfile, sys, collections

SG = r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Saved Games"
CD2 = r"D:\RT_RetinueMod\ref\rt_probe\cd2.txt"
ZH = r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\KgdRetinue\items_zh.tsv"
SAVE = sys.argv[1] if len(sys.argv) > 1 else "Manual_25_____________13_19_01.zks"

# guid -> 蓝图内部名
name = {}
for line in io.open(CD2, encoding="utf-8", errors="replace"):
    i = line.find('"Name":"')
    if i < 0: continue
    j = line.find('","Guid":"', i)
    if j < 0: continue
    k = line.find('"', j + 10)
    name[line[j+10:k]] = line[i+8:j]

zh = {}
for i, l in enumerate(io.open(ZH, encoding="utf-8")):
    if i == 0: continue
    f = l.rstrip("\n").split("\t")
    if len(f) >= 4: zh[f[1]] = (f[0], f[2])

z = zipfile.ZipFile(os.path.join(SG, SAVE))
data = json.loads(z.read("party.json").decode("utf-8", "replace"))

byid = {}
def index(o):
    if isinstance(o, dict):
        i = o.get("$id")
        if i is not None: byid[str(i)] = o
        for v in o.values(): index(v)
    elif isinstance(o, list):
        for v in o: index(v)
index(data)
print("实体节点 %d 个" % len(byid))

def deref(o):
    if isinstance(o, dict) and "$ref" in o:
        return byid.get(str(o["$ref"]))
    return o

# 找海因里希
targets = []
def scan(o):
    if isinstance(o, dict):
        bp = o.get("Blueprint")
        if isinstance(bp, str) and "heinrix" in name.get(bp, "").lower():
            targets.append((name.get(bp), o))
        for v in o.values(): scan(v)
    elif isinstance(o, list):
        for v in o: scan(v)
scan(data)

print("找到海因里希实体 %d 个" % len(targets))
for bpname, ent in targets[:3]:
    print("\n=== %s ===" % bpname)
    found = collections.OrderedDict()
    seen = set()

    def walk(o, depth=0):
        if depth > 24: return
        if isinstance(o, dict):
            # 跟着 $ref 解引用 —— 装备是独立实体，槽位里只放引用
            if "$ref" in o:
                key = str(o["$ref"])
                if key in seen: return
                seen.add(key)
                t = byid.get(key)
                if t is not None: walk(t, depth + 1)
                return
            i = o.get("$id")
            if i is not None:
                if str(i) in seen: return
                seen.add(str(i))
            b = o.get("Blueprint")
            if isinstance(b, str) and b in zh:
                n, t = zh[b]
                found[b] = (n, t.split(".")[-1])
            for v in o.values(): walk(v, depth + 1)
        elif isinstance(o, list):
            for v in o: walk(v, depth + 1)
    walk(ent)

    if not found:
        print("   （在该实体子树里没找到物品，装备可能存在独立实体里）")
    for g, (n, t) in found.items():
        print("   %-22s %-28s %s" % (n, t, g))
