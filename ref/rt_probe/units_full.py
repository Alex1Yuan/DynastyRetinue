"""全量 BlueprintUnit 抽取 —— 体型 / brain / 种族 / 阵营 / prefab。

方法与 _tmp/ships2.py 同源（那次解出 39 个船模，Sword/Falchion/Firestorm 三条
明文 .jbp 逐字校准过）。这里换成 BlueprintUnit，字段序来自反编译的 BlueprintUnit.cs：

    m_Army → LocalizedName → Gender → Size(enum) → Color(4×float, a=1.0f)
           → m_Race → m_Portrait → Prefab → m_CustomizationPreset
           → m_Faction → m_Brain → AlternativeBrains → ...

★ Size 靠 Color 的 alpha 锚定 ★
  Color 是 4 个 float，最后一个恒为 1.0f（0x0000803f）。找到它，往前 16 字节
  就是 Color 的起点，再往前 4 字节就是 Size 的 int32。BlueprintStarship 用的是
  同一套布局，所以这个锚点是复用的、不是新猜的。

★ 引用一律按 guid 归属分类，不按位置数 ★
  m_Race / m_Portrait / m_Faction / m_Brain 都可能为空，为空时不产生 token，
  按位置数第 N 个引用必然错位。改成：先按 TypeFullName 建好各类型的 guid 集合，
  再看 blob 里出现的 32-hex token 落在哪个集合里。bbp.py 的文档里就是这个思路。

★ 血量抽不出来 ★
  BlueprintUnit 上没有血量字段 —— GetDefaultLevel() 是把组件里的 Ranks 加起来算的，
  血量同理由组件 + 属性推导。要血量只能用游戏内探针（阿斯塔特那批数字就是实测来的）。
  好在需要血量的只是最后入选的那几个单位，不是 3069 个。

用法: py ref/rt_probe/units_full.py  ->  写出 tools/units_full.tsv
"""
import json, io, struct, re, uuid, collections, os

B = r"H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles"
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__)))), "tools", "units_full.tsv")

SIZE = ["Fine", "Diminutive", "Tiny", "Small", "Medium", "Large", "Huge",
        "Gargantuan", "Colossal", "Raider_1x1", "Frigate_1x2", "Cruiser_2x4",
        "GrandCruiser_3x6"]
HEX = re.compile(rb'[0-9a-f]{32}')


def load_bbp():
    data = open(os.path.join(B, "blueprints-pack.bbp"), 'rb').read()
    n = struct.unpack_from('<I', data, 0)[0]
    offs = []
    for i in range(n):
        p = 4 + i * 20
        offs.append((struct.unpack_from('<I', data, p + 16)[0],
                     uuid.UUID(bytes_le=data[p:p + 16]).hex))
    srt = sorted(offs)
    end = {g: (srt[k + 1][0] if k + 1 < len(srt) else len(data))
           for k, (o, g) in enumerate(srt)}
    start = {g: o for o, g in offs}
    return data, start, end


def main():
    data, start, end = load_bbp()
    cd = json.load(io.open(os.path.join(B, "cheatdata.json"), encoding='utf-8'))['Entries']

    name = {}
    byset = collections.defaultdict(set)
    for e in cd:
        g = e['Guid'].replace('-', '').lower()
        t = e['TypeFullName'].split(',')[0]
        name[g] = e['Name']
        if 'Brain' in t:            byset['brain'].add(g)
        elif t.endswith('BlueprintRace'):    byset['race'].add(g)
        elif t.endswith('BlueprintFaction'): byset['faction'].add(g)

    loc = json.load(io.open(os.path.join(B, "locationlist.json"), encoding='utf-8'))
    assets = set(loc['m_Guids'])

    units = [e for e in cd
             if e['TypeFullName'].split(',')[0] == 'Kingmaker.Blueprints.BlueprintUnit']

    rows, nosize = [], 0
    for e in units:
        g = e['Guid'].replace('-', '').lower()
        if g not in start:
            continue
        blob = data[start[g]:end[g]]

        size = None
        for m in range(0, len(blob) - 20):
            if blob[m + 16:m + 20] == b'\x00\x00\x80\x3f':
                v = struct.unpack_from('<i', blob, m)[0]
                if 0 <= v <= 12:
                    size = v
                    break
        if size is None:
            nosize += 1

        toks = [mm.group().decode() for mm in HEX.finditer(blob)]
        seen = set()
        uniq = [t for t in toks if not (t in seen or seen.add(t))]

        def pick(kind):
            for t in uniq:
                if t in byset[kind]:
                    return name.get(t, t)
            return ""

        prefab = next((t for t in uniq if t in assets), "")
        rows.append((e['Name'], g,
                     SIZE[size] if size is not None else "?",
                     pick('brain'), pick('race'), pick('faction'), prefab))

    with io.open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        f.write("name\tguid\tsize\tbrain\trace\tfaction\tprefab\n")
        for r in rows:
            f.write("\t".join(r) + "\n")

    print("BlueprintUnit: %d 条，写出 %s" % (len(rows), OUT))
    print("体型解不出的: %d" % nosize)
    c = collections.Counter(r[2] for r in rows)
    print("== 体型分布 ==")
    for k in SIZE + ["?"]:
        if c.get(k):
            print("  %-18s %d" % (k, c[k]))

    # ★自检：拿独立来源的已知值对★ 这几个数字是当初游戏内探针实测的，
    #   与本脚本的抽取路径没有任何共用中间量，对得上才说明布局猜对了。
    print("== 自检（对照游戏内实测）==")
    known = {"88651654158644c699669d2ecb1ebc94": ("Large", "BloodRaven_Brain", "佐拉尔=血鸦")}
    idx = {r[1]: r for r in rows}
    for g, (sz, br, why) in known.items():
        r = idx.get(g)
        if not r:
            print("  ✗ %s 没抽到" % why); continue
        ok = (r[2] == sz) and (br.lower() in r[3].lower())
        print("  %s %s: size=%s(want %s) brain=%s(want contains %s)"
              % ("OK " if ok else "BAD", why, r[2], sz, r[3] or "-", br))


if __name__ == '__main__':
    main()
