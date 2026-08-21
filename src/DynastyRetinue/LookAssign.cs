using System;
using Kingmaker.EntitySystem.Entities;

namespace DynastyRetinue
{
    /// <summary>
    /// 「哪个分型、哪个阶层、用哪套外观」的分配表。
    ///
    /// ★列不是并存的三种卫兵★
    ///   阶位来自 `Archetypes.PlayerTier(队长)` —— 按**玩家等级**推的全局进度，
    ///   不是每个卫兵各自的属性。所以同一时刻全部普通卫兵都在同一列上。
    ///   三列的含义是"随战役推进，这个分型的外观依次变成什么"。
    ///   第四列是精英，它不随阶位变（精英一出场就是顶阶）。
    ///
    /// ★存成字符串而不是数组★
    ///   UMM 的设置是 XML 序列化的，加一个二维数组字段会让老存档的 Settings.xml
    ///   反序列化出半个对象。存一行字符串则天然向后兼容：老配置里没有这一项 =
    ///   空串 = 全部「跟随装备」= 原版行为。
    ///
    /// ★用下标而不是分型名★
    ///   全 mod 认分型都用下标（`RetinueRegistry.TagFor(i)` / `ArchetypeOf(u)`），
    ///   这里跟着用，免得多一套映射。代价是玩家重排 archetypes.json 会让分配表错位 ——
    ///   但装备表、天赋方案本来就是同样的代价，一致比局部更优要紧。
    /// </summary>
    internal static class LookAssign
    {
        public const int Cols = 4;          // T1 / T2 / T3 / 精英
        public const int EliteCol = 3;

        /// <summary>行分隔用 |，格分隔用 ,，空格 = 跟随装备。</summary>
        private static string[][] Parse()
        {
            string raw = Main.Settings != null ? Main.Settings.LookMatrix : null;
            int rows = Archetypes.All != null ? Archetypes.All.Length : 0;
            var m = new string[rows][];
            for (int i = 0; i < rows; i++) m[i] = new string[Cols];
            if (string.IsNullOrEmpty(raw)) return m;

            var lines = raw.Split('|');
            for (int i = 0; i < rows && i < lines.Length; i++)
            {
                var cells = lines[i].Split(',');
                for (int c = 0; c < Cols && c < cells.Length; c++)
                    m[i][c] = (cells[c] ?? string.Empty).Trim();
            }
            return m;
        }

        private static void Save(string[][] m)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < m.Length; i++)
            {
                if (i > 0) sb.Append('|');
                for (int c = 0; c < Cols; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(m[i][c] ?? string.Empty);
                }
            }
            if (Main.Settings != null) Main.Settings.LookMatrix = sb.ToString();
        }

        public static string Get(int archIndex, int col)
        {
            var m = Parse();
            if (archIndex < 0 || archIndex >= m.Length || col < 0 || col >= Cols) return LookCatalog.FollowGear;
            return m[archIndex][col] ?? LookCatalog.FollowGear;
        }

        public static void Set(int archIndex, int col, string lookId)
        {
            var m = Parse();
            if (archIndex < 0 || archIndex >= m.Length || col < 0 || col >= Cols) return;
            m[archIndex][col] = lookId ?? LookCatalog.FollowGear;
            Save(m);
        }

        /// <summary>整行刷成同一个（点行头）。</summary>
        public static void SetRow(int archIndex, string lookId)
        {
            var m = Parse();
            if (archIndex < 0 || archIndex >= m.Length) return;
            for (int c = 0; c < Cols; c++) m[archIndex][c] = lookId ?? LookCatalog.FollowGear;
            Save(m);
        }

        /// <summary>整列刷成同一个（点列头）。</summary>
        public static void SetCol(int col, string lookId)
        {
            var m = Parse();
            if (col < 0 || col >= Cols) return;
            for (int i = 0; i < m.Length; i++) m[i][col] = lookId ?? LookCatalog.FollowGear;
            Save(m);
        }

        public static void SetAll(string lookId)
        {
            var m = Parse();
            for (int i = 0; i < m.Length; i++)
                for (int c = 0; c < Cols; c++) m[i][c] = lookId ?? LookCatalog.FollowGear;
            Save(m);
        }

        /// <summary>这名卫兵此刻该用哪一列。精英恒为第四列。</summary>
        public static int ColumnOf(BaseUnitEntity g, int archIndex)
        {
            try
            {
                var arch = Archetypes.Get(archIndex);
                if (arch != null && GearTool.EliteDefOf(g, arch) != null) return EliteCol;

                int tier = 1;
                var leader = Kingmaker.Game.Instance != null && Kingmaker.Game.Instance.Player != null
                           ? Kingmaker.Game.Instance.Player.MainCharacterEntity : null;
                if (leader != null) tier = Archetypes.PlayerTier(leader);
                if (tier < 1) tier = 1; else if (tier > 3) tier = 3;
                return tier - 1;
            }
            catch { return 0; }
        }

        /// <summary>这名卫兵该用哪套外观。null = 跟随装备（不干预）。</summary>
        public static LookDef LookFor(BaseUnitEntity g)
        {
            if (g == null) return null;
            try
            {
                int ai = RetinueRegistry.ArchetypeOf(g);
                if (ai < 0) return null;
                string id = Get(ai, ColumnOf(g, ai));
                if (string.IsNullOrEmpty(id)) return null;
                var look = LookCatalog.Get(id);
                if (look == null) { Warn(id); return null; }
                return look;
            }
            catch { return null; }
        }

        /// <summary>配表里点名了一套 looks.json 里没有的风格 —— 每个 id 只提醒一次。</summary>
        private static readonly System.Collections.Generic.HashSet<string> _warned =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static void Warn(string id)
        {
            if (_warned.Add(id))
                Main.Log("[外观] 分配表里点名了「" + id + "」，但 looks.json 里没有这套 —— 该格按跟随装备处理。");
        }

        /// <summary>摘要，给折叠标题用。例："卡斯金 ×7　克里格 ×4　跟随装备 ×9"。</summary>
        public static string Summary()
        {
            try
            {
                var m = Parse();
                var count = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < m.Length; i++)
                    for (int c = 0; c < Cols; c++)
                    {
                        string k = m[i][c] ?? string.Empty;
                        int n; count.TryGetValue(k, out n); count[k] = n + 1;
                    }
                var sb = new System.Text.StringBuilder();
                foreach (var kv in count)
                {
                    if (sb.Length > 0) sb.Append("　");
                    var look = LookCatalog.Get(kv.Key);
                    sb.Append(look != null ? look.Display() : L.T("跟随装备")).Append(" ×").Append(kv.Value);
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }
    }
}
