// ★自动生成，不要手改★ 由 tools/gen_manifest.py 在每次 bump 时重写。
using System.Collections.Generic;

namespace KgdRetinue
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
        public const string Version = "0.81.0";

        public static readonly Dictionary<string, string> Hashes =
            new Dictionary<string, string>
        {
            { "archetypes.json", "41713dfdf6fd6481b1406d7fdc87d70b4f6feb0778a60de66dd1dc49f13ee9db" },
            { "plans.json", "5d53bacafdb83c69484b1a86c0504195550baf93a4c83fb32ed792eaefa44ee7" },
            { "l10n_en.json", "712f39e616a16d9fe7688178b829d43415a174827bf426f11fc5b28bb06369f0" },
        };
    }
}
