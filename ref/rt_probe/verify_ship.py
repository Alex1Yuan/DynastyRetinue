"""离线核查舰船调研里标为「待验」的几条 —— 直读 blueprints-pack.bbp，不用进游戏。

验的是：
  H  DLC2_Heartless_Starship 的 StarshipSpeed 是不是 0、槽位是不是 Port*2/Starboard*2/Dorsal*1
  K  StarshipAllyUnitMark 的内容（所有己方舰都挂了它，可能承载「友方舰」识别语义）
  +  两条僚舰召唤链各自引用了谁
"""
import sys, re, json
import bbp

TARGETS = {
    "28bb899d01334a77bf1c723f131028c3": "LaunchWingman_LaunchAbility",
    "79ecefdb63164229aa07c5d438924d48": "LauncherWingman_LaunchAbility",
    "1dd14297fad5478a95ea1ce0ac384b1e": "Launcher_Wingman_Weapon",
    "0c893e03a8a34415a4eeb59d7fc2e34a": "PlayerWingman_Starship",
    "ca2239e8737940f9bf106961ef6f490d": "PlayerWingmanPirate_Starship",
    "7c87442966644058b67e8be10c56b861": "DLC2_Heartless_Starship",
    "7708c385eb3740618774e2ae848454b5": "StarshipAllyUnitMark",
    "1793b8c1fd824805908bee8ed0e95041": "SwordClassFrigatePlayer_Starship",
}

def main():
    byguid, _ = bbp.load_cheat()
    f, spans = bbp.load_pack()
    print("pack 记录数:", len(spans))

    for guid, name in TARGETS.items():
        got = bbp.refs(f, spans, guid)
        if got is None:
            print("\n=== %s (%s)  ** pack 里找不到 **" % (name, guid))
            continue
        raw, refset = got
        txt = raw.decode("utf-8", "replace")
        print("\n=== %s  (%s)  %d bytes" % (name, guid, len(raw)))

        # 数值/布尔字段
        for key in ("StarshipSpeed", "HullIntegrity", "Initiative", "IsSoftUnit",
                    "ActionPointCost", "CooldownRounds", "Militarum", "MilitarumSize"):
            for m in re.finditer(r'"?%s"?\s*[:=]\s*("?[-\w.]+"?)' % key, txt):
                print("    %-18s = %s" % (key, m.group(1)))

        # 槽位类型序列
        slots = re.findall(r'"?Type"?\s*[:=]\s*"?(Prow|Port|Starboard|Dorsal|Keel|None)"?', txt)
        if slots:
            print("    槽位序列          =", slots)

        # 引用到的蓝图，按类型归类
        refset = set(refset)
        refset.discard(guid)
        buckets = {}
        for r in refset:
            e = byguid.get(r)
            if not e:
                continue
            t = e["Type"].split(".")[-1]
            buckets.setdefault(t, []).append(e["Name"])
        for t in sorted(buckets):
            vals = sorted(buckets[t])
            show = ", ".join(vals[:8]) + (" ..." if len(vals) > 8 else "")
            print("    ref %-26s %s" % (t, show))

if __name__ == "__main__":
    main()
