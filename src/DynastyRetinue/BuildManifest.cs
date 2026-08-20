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
        public const string Version = "1.0.0";

        public static readonly Dictionary<string, string> Hashes =
            new Dictionary<string, string>
        {
            { "archetypes.json", "1926c8772c920740608043ccda869f7245e5dd971f11c4c36d361101d572a7c7" },
            { "plans.json", "4853798d25d6d5eddf0f2a0f26220a1138e4357c9f64165481ec43dcb4d16f65" },
            { "l10n_en.json", "9bc9da2f54dd72012161df6a8876f2d5044bcd642f708c1037726ac4e155bda1" },
        };
    }
}
