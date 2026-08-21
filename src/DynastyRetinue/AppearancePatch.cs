using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Parts;

namespace DynastyRetinue
{
    /// <summary>
    /// 让卫兵**穿别的单位的模型**，而不改它的属性。
    ///
    /// ★为什么需要★
    ///   传奇层想要「血鸦战团的阿斯塔特」，但实测 `Blood_Raven` 蓝图只有 192 血，
    ///   而通用星际战士（`Spacemarine_melta` 613 / `_bolter` 565）够强却是别的配色。
    ///   要"血鸦的样子 + 强单位的底子"，就得把外观和数值拆开。
    ///
    /// ★为什么不直接写 m_CustomPrefabGuid★
    ///   那个字段是 `[JsonProperty]`**进存档**，而且 `GetHash128()` 里
    ///   `result.Append(m_CustomPrefabGuid)` —— **进哈希**。写它有两个代价：
    ///     · 卸载 mod 之后模型换不回来（存档里那个值还在，没人清）
    ///     · 联机时两台机器只要这个字段不同，哈希就对不上
    ///   现有的船模替换正是走的这条路，所以 README 里才必须写"卸载前先点还原"。
    ///
    /// ★改成 patch getter★
    ///   PrefabGuid 的实现是：
    ///       if (Doll?.RacePreset != null) return null;
    ///       if (!IsNullOrEmpty(m_CustomPrefabGuid)) return m_CustomPrefabGuid;
    ///       return Owner.Blueprint.Prefab.AssetId;
    ///   消费方（Instantiate / PreloadResources）全都走这个属性。
    ///   所以 Postfix 改返回值就够了，而**字段始终保持 null**：
    ///     · 存档里不留任何痕迹 —— 卸载 mod 即自动回退原版外观，玩家不用做任何事
    ///     · 哈希里那一项两台机器永远都是 null —— 联机天然一致
    ///   这就是"错误卸载也能回退"的正确形态：不是写了再擦，是**根本不写**。
    ///
    /// ★Doll 那一支必须尊重★
    ///   原版在有 RacePreset 时返回 null（走捏脸/装扮系统渲染）。
    ///   我们只在原版返回了一个**非空**的 prefab id 时才替换 —— 硬塞会让
    ///   本来该走 doll 的单位渲染错乱。
    ///
    /// ★开销★
    ///   getter 只在实例化视图和预加载资源时被调，不是每帧路径。
    ///   即便如此，第一道闸是一个静态 bool（配表里根本没人用外观覆盖时直接返回），
    ///   解析出来的 assetId 也按蓝图 GUID 缓存，不重复查表。
    /// </summary>
    [HarmonyPatch(typeof(PartUnitViewSettings), "get_PrefabGuid")]
    internal static class AppearancePatch
    {
        /// <summary>配表里有没有人用外观覆盖。没有就整条路径零开销。</summary>
        private static bool _anyConfigured;
        private static bool _scanned;

        /// <summary>外观蓝图 GUID -> 它的 prefab assetId。避免每次都解析蓝图。</summary>
        private static readonly Dictionary<string, string> _cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>配表重载后调用，让下次访问重新扫描。</summary>
        public static void Invalidate() { _scanned = false; _cache.Clear(); }

        private static void Postfix(PartUnitViewSettings __instance, ref string __result)
        {
            // 原版返回 null = 该走 doll 渲染，别碰
            if (string.IsNullOrEmpty(__result)) return;
            try
            {
                if (!Main.Enabled) return;
                // 第一道闸：配表里没人用 appearanceUnit **且** 分配表是空的，整条路径零开销
                if (!AnyConfigured() &&
                    (Main.Settings == null || string.IsNullOrEmpty(Main.Settings.LookMatrix))) return;

                var u = __instance != null ? __instance.Owner as BaseUnitEntity : null;
                if (u == null || !RetinueRegistry.IsGuard(u)) return;

                // ★玩家的分配表优先于配表里的 appearanceUnit★
                //   appearanceUnit 是内容配置（"这个精英该长什么样"），
                //   分配表是玩家的明确选择。玩家选了就听玩家的；
                //   选「跟随装备」（分配表为空）时才回落到内容配置 —— 那样最不意外。
                string want = null;
                var look = LookAssign.LookFor(u);
                if (look != null && look.IsBorrow)
                {
                    int ai = RetinueRegistry.ArchetypeOf(u);
                    var arch = ai >= 0 ? Archetypes.Get(ai) : null;
                    want = look.UnitFor(arch != null ? arch.Name : null);
                }
                if (string.IsNullOrEmpty(want)) want = AppearanceUnitOf(u);
                if (string.IsNullOrEmpty(want)) return;

                string prefab = PrefabOf(want);
                if (!string.IsNullOrEmpty(prefab)) __result = prefab;
            }
            catch { /* 出岔子就保持原版外观 */ }
        }

        /// <summary>这名卫兵要借哪个蓝图的模型。只有精英能配。</summary>
        private static string AppearanceUnitOf(BaseUnitEntity u)
        {
            try
            {
                int ai = RetinueRegistry.ArchetypeOf(u);
                if (ai < 0) return null;
                var a = Archetypes.Get(ai);
                if (a == null) return null;
                var ed = GearTool.EliteDefOf(u, a);
                return ed != null ? ed.AppearanceUnitId : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 取某个单位蓝图的 prefab assetId（带缓存）。
        ///
        /// ★支持 a|b|c 回退链★ 和 gear 用同一套语法：从左往右试，第一个能解析出
        /// prefab 的就用。理由是借模型的首选常常来自 DLC —— 没装 DLC 的玩家
        /// 不该直接掉回"完全不搭的原版模型"，而应该退到次选。
        /// </summary>
        private static string PrefabOf(string unitGuid)
        {
            string hit;
            if (_cache.TryGetValue(unitGuid, out hit)) return hit;

            string val = null;
            string[] chain = unitGuid.Split('|');
            for (int i = 0; i < chain.Length && string.IsNullOrEmpty(val); i++)
            {
                string one = chain[i].Trim();
                if (one.Length == 0) continue;
                try
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(one);
                    if (bp != null && bp.Prefab != null) val = bp.Prefab.AssetId;
                }
                catch { }
                if (string.IsNullOrEmpty(val) && chain.Length > 1)
                    Main.Log("[外观] 借模型候选 " + one + " 解析不到，试下一个。");
            }

            // 整条链都解析不到（缺 DLC）就缓存空串 —— 下次不用再查，外观退回原版
            _cache[unitGuid] = val;
            if (string.IsNullOrEmpty(val))
                Main.Log("[外观] 借用模型的蓝图 " + unitGuid + " 整条链都解析不到，该卫兵保持原版外观。");
            return val;
        }

        private static bool AnyConfigured()
        {
            if (_scanned) return _anyConfigured;
            _scanned = true;
            _anyConfigured = false;
            try
            {
                var all = Archetypes.All;
                if (all == null) return false;
                for (int i = 0; i < all.Length && !_anyConfigured; i++)
                {
                    var es = all[i] != null ? all[i].Elites : null;
                    if (es == null) continue;
                    for (int k = 0; k < es.Length; k++)
                        if (es[k] != null && !string.IsNullOrEmpty(es[k].AppearanceUnitId))
                        { _anyConfigured = true; break; }
                }
            }
            catch { }
            return _anyConfigured;
        }
    }
}
