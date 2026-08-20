# -*- coding: utf-8 -*-
"""译文表体检。

三个方向都要查，缺一个都会漏掉一类错：

  源码 → 表    漏译：界面上这一句会显示中文
  表 → 源码    僵尸条目：改过文案后残留的旧 key，永远匹配不上
  表内自洽     占位符 / 富文本标签 / 残留中文

★ 已知的扫描盲区 ★
L.T 的参数不是字面量时（例如 RecruitDialog.cs 的 L.T(TextValue)，TextValue 是 const），
正则扫不到。这类必须手工登记在 KNOWN_NONLITERAL 里，否则会被误报成僵尸条目。
"""
import io, json, os, re, sys, glob

try:                      # Windows 控制台默认 GBK，中文/− 号会直接抛 UnicodeEncodeError
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "DynastyRetinue")
TAB = os.path.join(SRC, "l10n_en.json")

# L.T(TextValue) —— 参数是 const 标识符，正则扫不到，但运行时确实会查这个 key
KNOWN_NONLITERAL = [
    u"（护卫队）关于我的护卫队……",
    # ShipModelCatalog.HullName 走 L.T(Hull)，Hull 是 ShipModel 的只读字段，
    # 取值就是下面这 11 个字面量（见 ShipModelCatalog.All 的初始化列表）。
    # 没有把 L.T 贴到字面量上，是因为 Hull 同时还被当**数据**用
    # （存进 Settings.ProwLearnedFrom、拼 GameObject 名），本地化了会让存的值随语言变。
    u"混沌战列巡洋舰",
    u"帝国 Universe 级质量运输舰",
    u"帝国 Gothic 级巡洋舰",
    u"帝国 Dictator 级巡洋舰",
    u"混沌 Carnage 级巡洋舰",
    u"黑暗灵族轻巡洋舰",
    u"灵族巡洋舰",
    u"质量运输舰（巡洋尺度）",
    u"Sword 级护卫舰",
    u"Falchion 级护卫舰",
    u"Firestorm/Tempest 级护卫舰",
]

# 语言自己的名字不该被翻译
INTENTIONAL_UNTRANSLATED = [u"中文"]

CALL = re.compile(r'\bL\.(?:T|F)\s*\(')
LIT = re.compile(r'"((?:[^"\\]|\\.)*)"')
HAS_CJK = re.compile(u'[\u4e00-\u9fff]')


def unescape(s):
    return (s.replace('\\"', '"').replace('\\\\', '\\')
             .replace('\\n', '\n').replace('\\t', '\t'))


def strip_comments(t):
    """\u53bb\u6389 // \u884c\u6ce8\u91ca\u548c /* */ \u5757\u6ce8\u91ca\uff0c\u4f46\u4e0d\u80fd\u78b0\u5b57\u7b26\u4e32\u91cc\u7684 //\uff08URL\u3001\u8def\u5f84\uff09\u3002"""
    out, i, n = [], 0, len(t)
    while i < n:
        c = t[i]
        if c == '"':
            j = i + 1
            while j < n:
                if t[j] == '\\':
                    j += 2; continue
                if t[j] == '"':
                    break
                j += 1
            out.append(t[i:j + 1]); i = j + 1
        elif t.startswith('//', i):
            j = t.find('\n', i); i = n if j < 0 else j
        elif t.startswith('/*', i):
            j = t.find('*/', i); i = n if j < 0 else j + 2
        else:
            out.append(c); i += 1
    return ''.join(out)


def read_arg(t, i):
    """\u4ece L.T( \u7684\u5de6\u62ec\u53f7\u4e4b\u540e\u5f00\u59cb\uff0c\u628a\u7b2c\u4e00\u4e2a\u5b9e\u53c2\u91cc\u6240\u6709\u7528 + \u62fc\u63a5\u7684\u5b57\u9762\u91cf\u8fde\u8d77\u6765\u3002

    \u2605\u5fc5\u987b\u8fd9\u4e48\u505a\u2605 C# \u7684 "a" + "b" \u662f\u7f16\u8bd1\u671f\u5e38\u91cf\u6298\u53e0\uff0c\u8fd0\u884c\u65f6\u7684 key \u662f\u6574\u53e5 "ab"\u3002
    \u53ea\u6293\u7b2c\u4e00\u6bb5\u4f1a\u628a\u957f\u6587\u6848\u7cfb\u7edf\u6027\u8bef\u62a5\u6210\u6f0f\u8bd1 \u2014\u2014 \u800c\u957f\u6587\u6848\u6070\u6070\u662f\u6700\u9700\u8981\u7ffb\u8bd1\u7684\u90a3\u4e9b\u3002
    \u53c2\u6570\u4e0d\u662f\u5b57\u9762\u91cf\uff08\u4f8b\u5982 L.T(TextValue)\uff09\u65f6\u8fd4\u56de None\uff0c\u4ea4\u7ed9 KNOWN_NONLITERAL \u5904\u7406\u3002
    """
    parts, depth, n = [], 0, len(t)
    while i < n:
        c = t[i]
        if c == '"':
            m = LIT.match(t, i)
            if not m:
                return None
            parts.append(unescape(m.group(1))); i = m.end(); continue
        if c in '([{':
            depth += 1; i += 1; continue
        if c in ')]}':
            if depth == 0:
                break
            depth -= 1; i += 1; continue
        if c == ',' and depth == 0:
            break
        if c in ' \t\r\n+':
            i += 1; continue
        return None          # \u53d8\u91cf\u3001\u65b9\u6cd5\u8c03\u7528\u7b49 \u2014\u2014 \u4e0d\u662f\u7eaf\u5b57\u9762\u91cf
    return ''.join(parts) if parts else None


def main():
    tab = json.loads(io.open(TAB, encoding="utf-8-sig").read())

    used = {}
    nonlit = []
    for path in glob.glob(os.path.join(SRC, "*.cs")):
        name = os.path.basename(path)
        if name == "L10n.cs":
            continue          # 它的文档注释里有 L.F 示例，会被当成真调用
        txt = strip_comments(io.open(path, encoding="utf-8-sig", errors="replace").read())
        for m in CALL.finditer(txt):
            k = read_arg(txt, m.end())
            if k:
                used.setdefault(k, []).append(name)
            else:
                # 参数不是字符串常量（变量 / 数组下标 / 方法调用）。扫不到 =
                # 既进不了表也不会被报成漏译，是**静默**漏译。必须显式报出来。
                nonlit.append("%s: %s" % (name, txt[m.start():m.end() + 46].replace("\n", " ")))
    for k in KNOWN_NONLITERAL:
        used.setdefault(k, []).append("(非字面量)")

    fail = 0

    missing = [k for k in used if HAS_CJK.search(k) and k not in tab
               and k not in INTENTIONAL_UNTRANSLATED]
    print("源码里调用了但表里没有（=界面会显示中文）: %d" % len(missing))
    for k in sorted(missing)[:25]:
        print("    %-58s  %s" % (k[:56], ",".join(sorted(set(used[k])))))
    fail += len(missing)

    zombie = [k for k in tab if k not in used]
    print("\n表里有但源码没调用（僵尸条目）: %d" % len(zombie))
    for k in sorted(zombie)[:25]:
        print("    %s" % k[:70])

    bad = [(k, v) for k, v in tab.items()
           if set(re.findall(r"\{(\d+)\}", k)) != set(re.findall(r"\{(\d+)\}", v))]
    print("\n占位符不匹配: %d" % len(bad))
    for k, v in bad[:10]:
        print("    %s\n      -> %s" % (k[:60], v[:60]))
    fail += len(bad)

    tag = [(k, v) for k, v in tab.items()
           if sorted(re.findall(r"</?[a-zA-Z][^>]*>", k)) != sorted(re.findall(r"</?[a-zA-Z][^>]*>", v))]
    print("\n富文本标签不一致: %d" % len(tag))
    for k, v in tag[:10]:
        print("    %s\n      -> %s" % (k[:60], v[:60]))
    fail += len(tag)

    cjk = [v for v in tab.values() if HAS_CJK.search(v)]
    print("\n译文里仍含中文: %d" % len(cjk))
    for v in cjk[:10]:
        print("    %s" % v[:70])
    fail += len(cjk)

    # 不计入 fail：不是错，是**扫不到**。但必须让人看见，否则漏的那格永远没人发现。
    # 处理办法二选一：把 L.T 贴到字面量上（首选，代码自解释），或登记进 KNOWN_NONLITERAL。
    print("\n参数不是字面量、扫描器看不到的调用: %d　←需人工确认这些串已进表" % len(nonlit))
    for s in nonlit[:10]:
        print("    %s" % s)

    print("\n表 %d 条　源码调用 %d 处　%s"
          % (len(tab), len(used), "全部通过" if fail == 0 else "★ %d 处问题 ★" % fail))
    return 1 if fail else 0


if __name__ == "__main__":
    sys.exit(main())
