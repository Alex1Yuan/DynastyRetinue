# -*- coding: utf-8 -*-
"""把工作流产出的 zh->en 对照表并进 l10n_en.json。

可以反复跑：已有的条目保留，新的加进去，冲突时**保留旧值**并报告 ——
译文一旦人工校对过，不该被下一轮机器产出悄悄盖掉。
"""
import json, io, os, sys, re

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "DynastyRetinue", "l10n_en.json")


def load_pairs(path):
    """从任务输出文件里挖出那张表。外面可能裹着 {"json": "...."} 之类。"""
    raw = io.open(path, encoding="utf-8", errors="replace").read()
    # 输出文件本身是 JSON；表可能在 .json / .result 里，且是被字符串化过的
    cands = []
    try:
        top = json.loads(raw)
        cands.append(top)
    except Exception:
        pass
    # 兜底：直接找最大的一个 { ... } 块
    if not cands:
        m = re.search(r"\{.*\}", raw, re.S)
        if m:
            try:
                cands.append(json.loads(m.group(0)))
            except Exception:
                pass

    def dig(o, depth=0):
        if depth > 6:
            return None
        if isinstance(o, str):
            s = o.strip()
            if s.startswith("{"):
                try:
                    return dig(json.loads(s), depth + 1)
                except Exception:
                    return None
            return None
        if isinstance(o, list):
            # [{zh:..., en:...}, ...] 这种形状
            d = {}
            for it in o:
                if isinstance(it, dict) and "zh" in it and "en" in it:
                    d[it["zh"]] = it["en"]
            return d or None
        if isinstance(o, dict):
            # 已经是 zh->en 了？（值全是字符串，且键里有中文）
            if o and all(isinstance(v, str) for v in o.values()):
                if any(any("\u4e00" <= c <= "\u9fff" for c in k) for k in o):
                    return dict(o)
            for k in ("json", "result", "pairs", "table", "value"):
                if k in o:
                    r = dig(o[k], depth + 1)
                    if r:
                        return r
            for v in o.values():
                r = dig(v, depth + 1)
                if r:
                    return r
        return None

    for c in cands:
        r = dig(c)
        if r:
            return r
    return {}


def main(paths):
    old = {}
    if os.path.exists(OUT):
        try:
            old = json.loads(io.open(OUT, encoding="utf-8-sig").read())
        except Exception as e:
            print("! 现有 l10n_en.json 读不出来，当空表处理:", e)

    new = {}
    for p in paths:
        got = load_pairs(p)
        print("  %-28s %d 条" % (os.path.basename(p), len(got)))
        for k, v in got.items():
            if k in new and new[k] != v:
                print("    ! 两份产出对同一句给了不同译文，取先到的: %r" % k[:30])
                continue
            new[k] = v

    added, kept, conflict = 0, 0, 0
    merged = dict(old)
    for k, v in new.items():
        if k not in merged:
            merged[k] = v
            added += 1
        elif merged[k] != v:
            conflict += 1
            kept += 1
        else:
            kept += 1

    # 空 key / 空值一律不要：那种条目只会让 T() 回落中文，白占位置
    merged = {k: v for k, v in merged.items() if k and v}

    io.open(OUT, "w", encoding="utf-8", newline="\n").write(
        json.dumps(merged, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    )
    print("\n写入 %s" % OUT)
    print("  新增 %d　沿用旧译 %d（其中 %d 条与新产出不同，已保留旧的）　总计 %d"
          % (added, kept, conflict, len(merged)))


if __name__ == "__main__":
    main(sys.argv[1:])
