"""把加点方案描述里的毕业装备名，匹配到物品蓝图 GUID。

数据来源：
  items_zh.tsv    游戏内导出的「中文名 -> guid/类型/内部名」全表（2940 条，2535 条有中文名）
  owned_items.tsv 从存档扫出来的「你实际拥有的物品蓝图」（1257 种）

匹配策略（按可信度降序，命中即停）：
  1 完全相等
  2 表里的名字包含查询名（查询名是简称，如「政委帽」-> 「政委的帽子」）
  3 查询名包含表里的名字（查询名带了修饰，如「改良重型爆矢枪」-> 「重型爆矢枪」）
  4 去掉「的」之后再比一次

拥有状态会标出来 —— 没拥有的不一定是匹配错，可能只是还没拿到。
"""
import io, os, collections

BASE = r"D:\RT_RetinueMod\ref"
ZH = r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\KgdRetinue\items_zh.tsv"
OWNED = os.path.join(BASE, "owned_items.tsv")
OUT = os.path.join(BASE, "gear_match.tsv")

PLANS = [
    ("灵能军官(辅助)", "政委帽/幽灵头盔 审判庭黑色图章 吸血法杖 星辰围巾 指挥官笔记 深渊织网者手套 悯慈的礼物 腐朽之靴"),
    ("政委军官(辅助)", "外围面甲 人类拥护者 作战计划 反载具左轮手枪 活力勋章 指挥官计时器 神射手护腕 冯瓦兰修斯斗篷"),
    ("火杖战士行刑者", "乔伊斯面具 灵能者胸甲 灵能猛袭之手 烈阳法杖 血色契约 活力项饰 虚空魔典 行刑者臂铠/植入物手套 马尔皮裹尸布 惯性之靴"),
    ("国教士兵首席连射", "枪手头盔/呢喃低语 改装工匠护甲 力量念珠 改良重型爆矢枪 愤怒化身 胜利回响 补偿器手套 战术背包 散兵战靴"),
]


def norm(s):
    return s.replace("的", "").replace(" ", "").strip()


def main():
    rows = []
    for i, line in enumerate(io.open(ZH, encoding="utf-8")):
        if i == 0:
            continue
        f = line.rstrip("\n").split("\t")
        if len(f) >= 4 and f[0]:
            rows.append((f[0], f[1], f[2], f[3]))   # zh, guid, type, internal
    print("有中文名的物品:", len(rows))

    owned = set()
    if os.path.exists(OWNED):
        for i, line in enumerate(io.open(OWNED, encoding="utf-8")):
            if i == 0:
                continue
            f = line.rstrip("\n").split("\t")
            if len(f) >= 3:
                owned.add(f[2])
    print("存档里拥有:", len(owned))

    def find(q):
        qn = norm(q)
        for tier, pred in (
            (1, lambda z: z == q),
            (2, lambda z: q in z),
            (3, lambda z: z in q and len(z) >= 2),
            (4, lambda z: norm(z) == qn),
            (5, lambda z: qn in norm(z)),
            (6, lambda z: norm(z) in qn and len(norm(z)) >= 2),
        ):
            hits = [r for r in rows if pred(r[0])]
            if hits:
                return tier, hits
        return 0, []

    out = io.open(OUT, "w", encoding="utf-8")
    out.write("plan\tquery\ttier\tzh\tguid\ttype\tinternal\towned\n")

    for plan, gear in PLANS:
        print("\n" + "=" * 70)
        print("【%s】" % plan)
        for token in gear.split():
            for q in token.split("/"):
                tier, hits = find(q)
                if not hits:
                    print("  %-14s  ✗ 没找到" % q)
                    out.write("%s\t%s\t0\t\t\t\t\t\n" % (plan, q))
                    continue
                hits = hits[:4]
                for j, (zh, guid, typ, internal) in enumerate(hits):
                    own = "有" if guid in owned else "无"
                    mark = "  " if j else ("=" if tier == 1 else "~")
                    print("  %-14s %s [%s] %-22s %s  %s" %
                          (q if j == 0 else "", mark, own, zh, guid, typ.split(".")[-1]))
                    out.write("%s\t%s\t%d\t%s\t%s\t%s\t%s\t%s\n" %
                              (plan, q, tier, zh, guid, typ, internal, own))
    out.close()
    print("\n明细 -> " + OUT)


if __name__ == "__main__":
    main()
