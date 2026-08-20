using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker;

namespace DynastyRetinue
{
    /// <summary>
    /// 挂点争用的仲裁：同一个槽位类型被多件武器的美术抢时，**让光矛赢**。
    ///
    /// ================= 为什么必须仲裁 =================
    /// StarshipView.cs:252-259 是**先毁后建**，而且作用于该类型的**全部**挂点：
    ///     var list = ItemSlots.FindAll(x =&gt; x.Type == requiredSlots.SlotType);
    ///     foreach (var item2 in list) if (item2.itemPrefab != null) Destroy(item2.itemPrefab);
    ///     ...
    ///     foreach (var item3 in list) Instantiate(prefab, ...);
    /// ⇒ 两件要求同一类型的武器，**后处理的那件把前一件砸掉**，最终只剩一件。
    /// 补再多同类型挂点也没用 —— 那只会让胜者同时出现好几份（"地板上多一门"就是这么来的）。
    ///
    /// 玩家实测的例子（37 级存档）：
    ///     焚化者光刀    装 Prow    美术要求 Prow
    ///     重型导弹炮台  装 Dorsal  美术要求 **Prow**     ← 和光矛抢同一个
    /// 谁赢完全取决于 hull.HullSlots.WeaponSlots 的遍历顺序，玩家控制不了，
    /// 而舰首那个位置在观感上**就该是光矛**。
    ///
    /// ================= 做法 =================
    /// 在 EquipWeapon 的 Prefix 里：如果这件武器的美术要求的**某个**槽位类型
    /// 正被更高优先级的武器争用，就跳过它（返回 false），让胜者的美术活下来。
    /// 优先级：Lances &gt; Macrobatteries &gt; NovaCannons &gt; 其余。
    ///
    /// 只在**争用发生时**才跳过 —— 没人抢的武器一律照常装配，不改任何默认行为。
    /// 卸装（isEquip=false）永远放行，否则换装会留下清不掉的旧美术。
    ///
    /// ★存档★ 纯视觉，不碰任何 [JsonProperty]。
    /// </summary>
    public static class ShipArtArbiter
    {
        /// <summary>数字越小越优先。表里没有的按 100 处理。</summary>
        private static int Rank(string weaponType)
        {
            switch (weaponType)
            {
                case "Lances":         return 0;   // 舰首那个位置观感上就该是光矛
                case "NovaCannons":    return 1;
                case "Macrobatteries": return 2;
                case "TorpedoTubes":   return 90;  // 本来就没有可见炮塔，垫底
                default:               return 50;
            }
        }

        private static bool _logged;
        public static void ResetLog() { _logged = false; }

        /// <summary>
        /// 扫一遍已装武器，算出每个被争用的槽位类型该由谁拿下。
        /// 返回 槽位类型 → 胜者的武器蓝图对象。没有争用返回空表。
        /// </summary>
        private static Dictionary<string, object> Winners(object shipEntity)
        {
            var byType = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            try
            {
                var hull = Get(shipEntity, "Hull"); if (hull == null) return null;
                var slots = Get(hull, "HullSlots"); if (slots == null) return null;
                var ws = Get(slots, "WeaponSlots") as IEnumerable; if (ws == null) return null;

                foreach (var slot in ws)
                {
                    object item = null;
                    try { item = Get(slot, "Item"); } catch { }
                    if (item == null) continue;
                    var bp = Get(item, "Blueprint"); if (bp == null) continue;
                    foreach (var t in ArtSlotTypes(bp))
                    {
                        List<object> l;
                        if (!byType.TryGetValue(t, out l)) { l = new List<object>(); byType[t] = l; }
                        if (!l.Contains(bp)) l.Add(bp);
                    }
                }
            }
            catch { return null; }

            var win = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in byType)
            {
                if (kv.Value.Count < 2) continue;          // 没争用，不干预
                object best = null; int bestRank = int.MaxValue; string bestName = null;
                foreach (var bp in kv.Value)
                {
                    int r = Rank(WeaponTypeOf(bp));
                    string nm = NameOf(bp);
                    // 同优先级时按名字定序，保证每次结果一致（不随遍历顺序漂）
                    if (r < bestRank || (r == bestRank && string.CompareOrdinal(nm, bestName) < 0))
                    { best = bp; bestRank = r; bestName = nm; }
                }
                if (best != null) win[kv.Key] = best;
            }
            return win;
        }

        private static List<string> ArtSlotTypes(object bp)
        {
            var r = new List<string>();
            try
            {
                var see = Get(bp, "StarshipEE"); if (see == null) return r;
                var descs = Get(see, "EEArtSlotsDescription") as IEnumerable; if (descs == null) return r;
                foreach (var d in descs)
                {
                    if (d == null) continue;
                    var req = Get(d, "RequiredSlots") as IEnumerable; if (req == null) continue;
                    foreach (var q in req)
                    {
                        var t = Get(q, "SlotType");
                        if (t != null && !r.Contains(t.ToString())) r.Add(t.ToString());
                    }
                }
            }
            catch { }
            return r;
        }

        private static string WeaponTypeOf(object bp)
        {
            try { var t = Get(bp, "WeaponType"); return t == null ? "?" : t.ToString(); }
            catch { return "?"; }
        }

        private static string NameOf(object bp)
        {
            try { var f = AccessTools.Field(bp.GetType(), "name"); return f != null ? (f.GetValue(bp) as string ?? "?") : "?"; }
            catch { return "?"; }
        }

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                       | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public
                                          | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p != null && p.CanRead) return p.GetValue(o, null);
            }
            return null;
        }

        [HarmonyPatch]
        public static class EquipWeaponArbitrate
        {
            private static MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("StarshipView");
                return t == null ? null : AccessTools.Method(t, "EquipWeapon");
            }
            private static bool Prepare() { return TargetMethod() != null; }

            /// <summary>返回 false = 跳过这件武器的美术装配。</summary>
            private static bool Prefix(object weaponBP, bool isEquip)
            {
                try
                {
                    if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShipArtPreferLance) return true;
                    if (!isEquip || weaponBP == null) return true;   // 卸装永远放行

                    var ship = Game.Instance != null && Game.Instance.Player != null
                             ? (object)Game.Instance.Player.PlayerShip : null;
                    if (ship == null) return true;

                    var win = Winners(ship);
                    if (win == null || win.Count == 0) return true;   // 没有争用

                    foreach (var t in ArtSlotTypes(weaponBP))
                    {
                        object w;
                        if (!win.TryGetValue(t, out w)) continue;     // 这个类型没争用
                        if (ReferenceEquals(w, weaponBP)) continue;   // 我就是胜者

                        if (!_logged)
                        {
                            _logged = true;
                            Main.Log("[武器美术] 挂点争用仲裁：「" + NameOf(weaponBP) + "」("
                                   + WeaponTypeOf(weaponBP) + ") 与「" + NameOf(w) + "」("
                                   + WeaponTypeOf(w) + ") 都要求 " + t + " 槽位，"
                                   + "让后者显示。\n  原因：vanilla 是先毁后建"
                                   + "（StarshipView.cs:252-259），两件同类型的武器只能活一件，"
                                   + "谁赢本来取决于遍历顺序、玩家控制不了。"
                                   + "这里按 光矛 > 新星炮 > 宏炮 > 鱼雷 定序，"
                                   + "让舰首那个位置稳定显示光矛。本次会话只报这一条。");
                        }
                        return false;   // 跳过，把位置让给胜者
                    }
                }
                catch (Exception e) { Main.LogError("[武器美术] 仲裁失败: " + e.Message); }
                return true;
            }
        }
    }
}
