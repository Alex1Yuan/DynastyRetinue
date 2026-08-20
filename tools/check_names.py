# -*- coding: utf-8 -*-
"""检查会显示在**游戏世界里**的名字，有没有用到游戏字体装不下的字。

★判据是「字体里有没有」，不是「这字常不常用」★

  这个脚本的第一版用的是「必须在 GB2312 一级字库（3755 常用字）内」，
  理由听着很有道理：任何中文字体都不会漏掉常用字。实机连着证伪了两次：

    · `裴`(U+88F4) 在一级字库里，检查放行 → 游戏内显示「怒火□长·柳照」的□
    · 第一次"修" `邵岐` 时换掉了 `岐`，可实际缺的是 `邵` —— 改完还是方框

  Owlcat 这套字体的子集**不按 GB2312 分档切**，所以任何基于字库等级、
  使用频率、"我认得这个字"的推断都是猜。

  现在的白名单 `font_charset.txt` 是从游戏进程里实测导出的：
  mod 开发区的「字体覆盖检查」把所有已加载 TMP 字体（含 fallback 链）
  的字符表并起来写成文件。判据从推断变成了测量。

★为什么 mod 面板里看不出问题★
  面板走 Unity 内置字体，覆盖面大得多，这些字在面板里显示完全正常 ——
  对着面板怎么检查都发现不了，只有游戏内的头顶名条会露馅。

★检查哪些字段★
  只查会变成 CustomName、显示在名条上的：
    archetypes[].guardNames[]   三档军衔
    archetypes[].elites[].rank  精英军衔
    archetypes[].elites[].name  精英全名
    guardNamePool[]             人名池
  面板文案、装备名走别的字体，不在此列。

★字符集过期了怎么办★
  游戏更新可能换字体。进游戏点一次「字体覆盖检查」，
  把 mod 目录下新生成的 font_charset.txt 拷回 tools/ 即可。
"""
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "src" / "DynastyRetinue" / "archetypes.json"
CHARSET = pathlib.Path(__file__).resolve().parent / "font_charset.txt"

# 实测可显示的非汉字：间隔号、空格、ASCII、常见中文标点
ALLOW_NON_HAN = set(" ··、，。（）()·-—:：/") | {chr(c) for c in range(0x20, 0x7F)}


def load_charset():
    if not CHARSET.exists():
        print(f"x 找不到 {CHARSET.name}")
        print("  这个文件是从游戏里实测导出的字体字符集，没有它就只能靠猜 ——")
        print("  而靠猜已经漏过两次了（裴、邵）。")
        print("  生成方法：进游戏 → mod 面板「开发 · 测试」→「字体覆盖检查」，")
        print("  然后把 mod 目录下的 font_charset.txt 拷到 tools/ 下。")
        return None
    text = CHARSET.read_text(encoding="utf-8")
    return {c for c in text if 0x2E80 <= ord(c) <= 0x9FFF}


def offenders(text: str, charset):
    bad = []
    for ch in text:
        if ch in ALLOW_NON_HAN:
            continue
        if ord(ch) < 0x2E80:
            continue          # ASCII / 西文，字体一定有
        if ch not in charset:
            bad.append(ch)
    return bad


def main() -> int:
    charset = load_charset()
    if charset is None:
        return 1
    if len(charset) < 1000:
        print(f"x font_charset.txt 只有 {len(charset)} 字，明显不完整 —— 疑似导出时游戏没加载完字体。")
        print("  进游戏读档之后再点一次「字体覆盖检查」重新导出。")
        return 1

    data = json.loads(SRC.read_text(encoding="utf-8"))
    problems = []

    def check(where: str, value):
        if not isinstance(value, str) or not value:
            return
        bad = offenders(value, charset)
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
        print(f"OK 名字用字检查通过（对照实测字符集 {len(charset)} 字）")
        return 0

    print("x 以下名字用到了游戏字体没有的字，名条上会显示成方框：\n")
    for where, value, bad in problems:
        chars = "  ".join(f"{c} U+{ord(c):04X}" for c in bad)
        print(f"  {where}")
        print(f"      「{value}」")
        print(f"      缺字：{chars}\n")
    print("换成字符集里有的字。★不要凭「这字常用」判断★ —— 那个判据已经错过两次。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
