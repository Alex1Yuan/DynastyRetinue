"""从存档里抽出「玩家实际拥有的物品蓝图」。

做法：items.tsv 是全部 2940 件物品蓝图的 guid 集合；把存档 json 里出现的
所有 32 位十六进制串跟它求交集，就得到这个存档里真实存在的物品。
比在全量 2940 件里模糊匹配中文名可靠得多 —— 候选集小一个数量级，
而且能天然排除「游戏里有但你没有」的同名/近名物品。

输出 owned_items.tsv：guid / 类型 / 内部名 / 在存档里出现次数
（次数≈拥有数量，但同一件装备会在多处被引用，只能当强弱信号看，不是精确件数）
"""
import io, re, os, collections

BASE = r"D:\RT_RetinueMod\ref"
PEEK = os.path.join(BASE, "savepeek")
ITEMS = r"D:\RT_RetinueMod\ref\rt_probe\items.tsv"
OUT = os.path.join(BASE, "owned_items.tsv")

GUID_RE = re.compile(r"\b[0-9a-f]{32}\b")

def main():
    meta = {}
    for line in io.open(ITEMS, encoding="utf-8"):
        line = line.rstrip("\n")
        if not line:
            continue
        f = line.split("\t")
        if len(f) >= 3:
            meta[f[0]] = (f[1], f[2])
    print("物品蓝图总数:", len(meta))

    counts = collections.Counter()
    where = collections.defaultdict(set)
    for fn in ("player.json", "party.json"):
        p = os.path.join(PEEK, fn)
        if not os.path.exists(p):
            print("缺:", p)
            continue
        txt = io.open(p, encoding="utf-8", errors="replace").read()
        hits = 0
        for m in GUID_RE.finditer(txt):
            g = m.group(0)
            if g in meta:
                counts[g] += 1
                where[g].add(fn.replace(".json", ""))
                hits += 1
        print("%-12s %d 字符, 命中物品引用 %d 次" % (fn, len(txt), hits))

    rows = []
    for g, c in counts.items():
        t, name = meta[g]
        rows.append((name, t, g, c, "+".join(sorted(where[g]))))
    rows.sort(key=lambda r: (r[1], r[0]))

    with io.open(OUT, "w", encoding="utf-8") as f:
        f.write("internal\ttype\tguid\trefs\twhere\n")
        for r in rows:
            f.write("%s\t%s\t%s\t%d\t%s\n" % r)

    print("\n拥有的不同物品蓝图: %d 种  ->  %s" % (len(rows), OUT))
    bytype = collections.Counter(r[1] for r in rows)
    for t, n in bytype.most_common():
        print("   %4d  %s" % (n, t))

if __name__ == "__main__":
    main()
