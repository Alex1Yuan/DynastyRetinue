# -*- coding: utf-8 -*-
"""装备互斥自检：同一档里，先装上的件授予的 fact 会不会把后面的件排除掉。

★ 为什么需要这个 ★
RT 里不少装备会 AddFactToEquipmentWielder 一组「熟练度」fact（艾达灵族装备 /
星际战士装备 / 黑暗灵族装备…），而另一些装备的 EquipmentRestrictionHasFacts
恰恰把这几个 fact 列为**排除项**。于是：

    先装 A（授予 X） → 再装 B（EXCLUDE X）⇒ B 被静默拒绝

失败是**静默**的：GearTool 装不上就跳过，日志里只是少一行，槽位空着或停在前档。
谁先谁后完全由 archetypes.json 里数组的**书写顺序**决定 —— 一个纯粹的偶然。

实例：灵能靴（T1 灵能者战靴 / T3 灵能者的鞋子）同时授予艾达灵族/星际战士/黑暗灵族
三个熟练度；而以太面甲把这三个全列为排除项。数组里头(3)排在靴(6)前面，所以
T2→T3 逐级晋升时侥幸没事；但 T1 直跳 T3 时脚上还是 T1 靴，面甲就被拒。

★ 还要考虑跨档残留 ★ GearTool 只增不减，前档的件会留在身上。所以判定某一档时，
要把**前面所有档**授予的 fact 也算进来。
"""
import io, json, os, re, subprocess, sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCH = os.path.join(ROOT, r"src\DynastyRetinue\archetypes.json")
PY = os.path.join(ROOT, r"ref\bbp\py")

_cache = {}


def run(script, guid):
    key = (script, guid)
    if key in _cache:
        return _cache[key]
    try:
        out = subprocess.run([sys.executable, os.path.join(PY, script), guid],
                             capture_output=True, timeout=60)
        txt = out.stdout.decode("utf-8", "replace")
        if not txt.strip():
            txt = out.stdout.decode("cp936", "replace")
    except Exception as e:
        txt = ""
    _cache[key] = txt
    return txt


GUID = re.compile(r"\b([0-9a-f]{32})\b")


def grants(guid):
    """这件装备会给穿戴者哪些 fact。

    ★坑★ eff.py 把「AddFactToEquipmentWielder 授予的 fact」和
    「EquipmentRestrictionHasFacts 里引用的 fact」用同一种 `fact ` 行打印，
    不加区分。直接全当成"授予"会得到完全相反的结论 —— 我就这么误判过一次：
    灵能靴子明明是**排除**艾达灵族/星际战士/黑暗灵族，被读成了**授予**，
    于是推出"靴子会锁死面甲"这个不存在的冲突，一次扫出 97 处假阳性。

    restr.py 给的恰好就是限制侧那些 fact，所以：授予 = eff − restr。
    """
    return {g for g in _eff_facts(guid)} - excludes(guid) - requires(guid)


def _eff_facts(guid):
    out = set()
    for line in run("eff.py", guid).splitlines():
        s = line.strip()
        if s.startswith("fact "):
            m = GUID.search(s)
            if m:
                out.add(m.group(1))
    return out


def requires(guid):
    """REQUIRE 侧的 fact —— 也要从"授予"里刨掉，否则同样算成假阳性。"""
    out, on = set(), False
    for line in run("restr.py", guid).splitlines():
        s = line.strip()
        if s.startswith("REQUIRE"):
            on = True; continue
        if s.startswith("EXCLUDE"):
            on = False; continue
        if on and s.startswith("-"):
            m = GUID.search(s)
            if m:
                out.add(m.group(1))
    return out


def excludes(guid):
    """这件装备排除哪些 fact（有其一即不能装）。"""
    out, on = set(), False
    for line in run("restr.py", guid).splitlines():
        s = line.strip()
        if s.startswith("EXCLUDE"):
            on = True; continue
        if s.startswith("REQUIRE"):
            on = False; continue
        if on and s.startswith("-"):
            m = GUID.search(s)
            if m:
                out.add(m.group(1))
    return out


def name_of(guid):
    t = run("restr.py", guid).splitlines()
    return t[0].replace("####", "").strip()[:52] if t else guid[:8]


def main():
    d = json.loads(io.open(ARCH, encoding="utf-8-sig").read())
    arcs = d["archetypes"] if isinstance(d, dict) and "archetypes" in d else d
    tiers = ["gearT1", "gearT2", "gearT3"]
    problems = 0

    for a in arcs:
        held = set()                      # 已穿的件累积授予的 fact（跨档残留）
        for t in tiers:
            entries = a.get(t) or []
            # 第一遍：这一档在它自己之前（数组更靠前）的件会授予什么
            for i, ch in enumerate(entries):
                cands = [g.strip() for g in (ch or "").split("|") if g.strip()]
                for g in cands:
                    ex = excludes(g)
                    bad = ex & held
                    if bad:
                        problems += 1
                        print("\n★ %s / %s 槽%d  %s" % (a.get("name"), t, i, name_of(g)))
                        print("   被这些已在身上的 fact 排除：")
                        for b in sorted(bad):
                            print("      %s" % b)
                        print("   ⇒ 会被静默拒绝，该槽停在前档或空着")
                    break             # 只看首选：候选链后面的本来就是兜底
                # 装上之后它授予的 fact 加进来（按数组顺序）
                if cands:
                    held |= grants(cands[0])

    print("\n互斥冲突: %d 处" % problems)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
