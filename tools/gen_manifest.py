# -*- coding: utf-8 -*-
"""生成 BuildManifest.cs —— 把随包发布的数据文件指纹编进 DLL。

★为什么编进 DLL 而不是并排放一个 manifest.json★
指纹要是和数据文件放在一起，改配表的人顺手把清单一起改了就没了。
编进 DLL 之后，想让指纹对上就得连 DLL 一起重编 ——「改 JSON」和「重编 DLL」
的门槛差着量级，正好卡住最常见的那类改动。

★这不是防篡改，是防误诊★
能改 DLL 的人当然照样能绕过，这是死结。真正的用途是：别人（或用户自己）
改过配表之后发来一份 bug 报告，诊断包顶部会直接标出来，省得照着原版代码
去 debug 一个不存在的问题。对正常用户则完全无感 —— 只在导出诊断包时出现。

由 bump.sh 在 dotnet build **之前**调用。
"""
import hashlib, io, os, sys

SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "DynastyRetinue")
OUT = os.path.join(SRC, "BuildManifest.cs")
FILES = ["archetypes.json", "plans.json", "l10n_en.json"]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ver = sys.argv[1] if len(sys.argv) > 1 else "dev"
    rows = []
    for f in FILES:
        p = os.path.join(SRC, f)
        if not os.path.exists(p):
            print("  ! %s 不存在，跳过" % f)
            continue
        rows.append((f, sha256(p)))
        print("  %-18s %s" % (f, rows[-1][1][:16] + "…"))

    body = "\n".join(
        '            { "%s", "%s" },' % (n, h) for n, h in rows)

    io.open(OUT, "w", encoding="utf-8", newline="\r\n").write(u'''// ★自动生成，不要手改★ 由 tools/gen_manifest.py 在每次 bump 时重写。
using System.Collections.Generic;

namespace DynastyRetinue
{
    /// <summary>
    /// 随包发布的数据文件指纹。<b>只用于诊断，不做任何拦截</b> ——
    /// 对不上也照常运行，只是在导出的诊断包里标一行。
    ///
    /// 用途是省时间不是防人：别人改过 archetypes.json 之后发来 bug 报告，
    /// 不标出来的话会照着原版代码去查一个不存在的问题。
    /// 正常玩家完全感知不到这个机制。
    /// </summary>
    public static class BuildManifest
    {
        public const string Version = "%VER%";

        public static readonly Dictionary<string, string> Hashes =
            new Dictionary<string, string>
        {
%BODY%
        };
    }
}
'''.replace("%VER%", ver).replace("%BODY%", body))
    print("  -> BuildManifest.cs（v%s，%d 个文件）" % (ver, len(rows)))


if __name__ == "__main__":
    main()
