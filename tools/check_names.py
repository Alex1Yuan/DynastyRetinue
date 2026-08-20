# -*- coding: utf-8 -*-
"""检查会显示在**游戏世界里**的名字有没有用到生僻字。

★为什么需要这个★
  游戏自带的中文字体是**子集**，只覆盖常用字。用了子集外的字，
  游戏内的头顶名条会渲染成一个方框（豆腐块），而 mod 自己的设置面板
  用的是 Unity 默认字体、覆盖面大得多，所以在面板里看着完全正常 ——
  作者对着面板怎么检查都发现不了。

  实机踩到的例子：
    「怒火铳长·贝翠丝」→ 显示成「怒火□长·贝翠丝」  （铳 U+94F3）
    「谕令参谋」        → 谕 U+8C15 同样在二级字库

★安全集怎么定的★
  GB2312 一级字库 = 3755 个常用字，按使用频率排的。任何中文游戏字体
  都不会漏掉这一档。二级字库（3008 字）是次常用字，覆盖情况看字体而定 ——
  Owlcat 这套明显没全收。
  所以判据是「必须在一级字库内」，宁可保守。

  非汉字方面：ASCII、中文标点、U+00B7（间隔号，实机验证过能正常显示）放行。

★检查哪些字段★
  只查会变成 CustomName 写进存档、显示在名条上的：
    archetypes[].guardNames[]   三档军衔
    archetypes[].elites[].rank  精英军衔
    archetypes[].elites[].name  精英全名（直接当名字用的那条路径）
    guardNamePool[]             人名池
  面板文案、注释、装备名一律不查 —— 那些走 Unity 字体，没这个问题。
"""
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "src" / "DynastyRetinue" / "archetypes.json"

# 实机验证可显示的非汉字：间隔号、空格、ASCII、常见中文标点
ALLOW_NON_HAN = set(" ··、，。（）()·-—:：/") | {chr(c) for c in range(0x20, 0x7F)}


def in_gb2312_level1(ch: str) -> bool:
    """是否在 GB2312 一级字库（0xB0A1–0xD7F9，3755 个常用字）。"""
    try:
        b = ch.encode("gb2312")
    except UnicodeEncodeError:
        return False
    return len(b) == 2 and 0xB0 <= b[0] <= 0xD7


def offenders(text: str):
    bad = []
    for ch in text:
        if ch in ALLOW_NON_HAN:
            continue
        if "一" <= ch <= "鿿":
            if not in_gb2312_level1(ch):
                bad.append(ch)
        elif ord(ch) > 0x2E80:
            bad.append(ch)          # CJK 区其它符号，一律当可疑
    return bad


def main() -> int:
    data = json.loads(SRC.read_text(encoding="utf-8"))
    problems = []

    def check(where: str, value):
        if not isinstance(value, str) or not value:
            return
        bad = offenders(value)
        if bad:
            problems.append((where, value, bad))

    for arch in data.get("archetypes", []):
        an = arch.get("name", "?")
        for i, rank in enumerate(arch.get("guardNames") or []):
            check(f"{an} · guardNames[{i}]", rank)
        for j, elite in enumerate(arch.get("elites") or []):
            check(f"{an} · elites[{j}].rank", elite.get("rank"))
            check(f"{an} · elites[{j}].name", elite.get("name"))

    for i, person in enumerate(data.get("guardNamePool") or []):
        check(f"guardNamePool[{i}]", person)

    if not problems:
        print("OK 名字用字检查通过（全部在 GB2312 一级字库内）")
        return 0

    print("x 以下名字用到了生僻字，游戏内名条会显示成方框：\n")
    for where, value, bad in problems:
        chars = "  ".join(f"{c} U+{ord(c):04X}" for c in bad)
        print(f"  {where}")
        print(f"      「{value}」")
        print(f"      问题字：{chars}\n")
    print("改成常用字即可。判据是 GB2312 一级字库（3755 常用字），")
    print("不是「我认得这个字」—— 认得不代表游戏字体收了它。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
