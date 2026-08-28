// ★自动生成，不要手改★ 由 tools/gen_manifest.py 在每次 bump 时重写。
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
        public const string Version = "1.5.80";

        public static readonly Dictionary<string, string> Hashes =
            new Dictionary<string, string>
        {
            { "archetypes.json", "f18313ba9819c24541d387972dddd4fd4afdfe0bdfd7b7f62541c6526df97809" },
            { "plans.json", "278bbdca2c79481f0f4e658e4bdbebf8abf3e1654df5f84f63e77fdbb29477b6" },
            { "l10n_en.json", "83fdecd74f4e0a540a2920e21efd0c42eccbe254c228b4a23f65900761149596" },
        };
    }
}
