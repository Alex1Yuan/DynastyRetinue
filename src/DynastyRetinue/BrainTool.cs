using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.AI.Blueprints;
using Kingmaker.Blueprints;

namespace DynastyRetinue
{
    /// <summary>
    /// brain 探测与替换。
    ///
    /// v0.2.3 实测发现的关键事实：**所有 DLC3_DL_* 卫兵的 brain 都是
    /// UseOnlyListed=True 且列表只有 1-4 条**。BlueprintBrain.GetCustomAbilitySettings 里
    ///     if (m_UseOnlyListedAbilities &amp;&amp; !AbilityPriorityOrder.Order.Any(o =&gt; o.Abilities.Contains(ability)))
    ///         return AbilitySettings.UnplayableSetting;
    /// 意味着 career 链练出来的技能**一条都不会被 AI 考虑**。练到 55 级也白搭。
    ///
    /// 好在 PartUnitBrain.SetBrain(BlueprintBrainBase) 是 public，而且原版自己就在用
    /// （AiOverrideBrain / WarhammerContextActionOverrideBrain）。所以可以运行时换 brain，
    /// 把三件事解耦：
    ///   单位蓝图 → 模型 / 自带装备 / 基础属性
    ///   brain    → AI 行为（可换）
    ///   career 链 → 天赋
    /// </summary>
    public static class BrainTool
    {
        /// <summary>候选 brain。优先收"Base 系"通用脑，它们最可能不限制技能。</summary>
        public static readonly (string Name, string Id)[] Candidates =
        {
            // ---- 近战 ----
            ("MeleeFlankerBase",      "b81b43296c3346bfb6bd4d18d424d4bc"),
            ("ChargerBase",           "ea7d1587cd404d67b47f119ab224a6c3"),
            ("BerserkerMeleeBase",    "c9cafd156d9644fe93876ec2961c8799"),
            ("RushBase",              "07544db9fd314c89ae7f1dcb790b3f07"),
            ("KnivesOutBase",         "c0c46c0cd10d40b6a0736c487481754b"),
            ("MeleeBloodbath",        "649e6de94e754aa6b527bcedb8b62c70"),
            ("QuetzaEldarMelee",      "91b9701d99014d968e781118f4f52af2"),
            ("Sicarian_Base",         "12fde106b2824eb1ac82af5b409e6e23"),
            // ---- 远程 ----
            ("RangeCommonBase",       "16c191c3675a45dd8dd2d27bccb5979d"),
            ("RangedCloseBase",       "4a5beadb76504961ba125efd64c74870"),
            ("RangedPositionalBase",  "adcf80a5d2a344d3814dcf3bb6b127f7"),
            ("RangedRushBase",        "c2f7123cb7e64d9a9e689aa22a286f92"),
            ("RangedFinisherBase",    "cb6ab6c57c1c42a8aa5aea6baf202358"),
            ("RangedGrenadeRushBase", "79a635a2301f4516861d3549044fa3ac"),
            ("BerserkerRangedBase",   "2416f9ee776540ebb507a5875ca5cbf8"),
            // ---- 狙击（实测 UseOnlyListed=False 且列表 0 条）----
            ("RangedSniper",          "4a10f56d2a4e41e0a94a26b0a48aaf5d"),
            ("RangedPositionalSniper","6bfff31e4223456db500de4efe560b6a"),
            ("ProloguePirateSniper",  "b5f8fd374e1948d1b68e301cd3acf13e"),
            // ---- 支援 / 军官 / 灵能 ----
            ("Officer",               "92118d6f493741e489aabe6e2f8d5fa4"),
            ("RangeNNStation",        "0148827b31a7458c8513d2cedc222af8"),
            ("MedicSpecialistBase",   "0a0c52ef6f654751b511d0d4a72ab96d"),
            ("Psyker_IronArm",        "5c53d46a9f6848cda77f19b3492d4281"),
            ("CombatServitorHeavy",   "77f9ee8cb7354a48998eb45f3531c512"),
            ("JungleWorldEldarRange", "45217eee343c4d23b0aa075d138edec4"),
        };

        /// <summary>只读探测：不生成单位，直接读蓝图字段。快。</summary>
        public static void Probe()
        {
            Main.Log("========== brain 探测（" + Candidates.Length + " 个）==========");
            Main.Log("判据: UseOnlyListed=False 且列表条数少 ⇒ AI 会考虑 career 链练出来的技能");
            var good = new List<string>();
            foreach (var c in Candidates)
            {
                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintBrain>(c.Id);
                if (bp == null) { Main.Log("  " + c.Name.PadRight(24) + " 解析不到"); continue; }
                string s = Describe(bp, out bool ok);
                Main.Log("  " + c.Name.PadRight(24) + s);
                if (ok) good.Add(c.Name + " " + c.Id);
            }
            Main.Log("--- 可用（不限制技能）" + good.Count + " 个 ---");
            foreach (var g in good) Main.Log("  " + g);
            Main.Log("========== brain 探测结束 ==========");
        }

        public static string Describe(BlueprintBrain bp, out bool unrestricted)
        {
            unrestricted = false;
            try
            {
                bool onlyListed = false;
                var fi = AccessTools.Field(typeof(BlueprintBrain), "m_UseOnlyListedAbilities");
                if (fi != null) onlyListed = (bool)fi.GetValue(bp);
                unrestricted = !onlyListed;

                int listed = 0;
                try { if (bp.AbilityPriorityOrder.Order != null) listed = bp.AbilityPriorityOrder.Order.Length; } catch { }
                int mv = 0;
                try { if (bp.MovementInfluentAbilities != null) mv = bp.MovementInfluentAbilities.Length; } catch { }
                string melee = "?";
                try { melee = bp.MeleeBrainType.ToString(); } catch { }

                return "UseOnlyListed=" + onlyListed + "  列表" + listed + "  移动" + mv
                       + "  近战型=" + melee + (onlyListed ? "   ★受限" : "   可用");
            }
            catch (Exception e) { return "读取失败: " + e.GetType().Name; }
        }

        /// <summary>给卫兵换 brain。原版 AiOverrideBrain 就是这么干的。</summary>
        public static bool Apply(Kingmaker.EntitySystem.Entities.BaseUnitEntity u, string brainGuid)
        {
            if (u == null || string.IsNullOrEmpty(brainGuid)) return false;
            try
            {
                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintBrainBase>(brainGuid);
                if (bp == null) { Main.LogError("brain 解析不到: " + brainGuid); return false; }
                if (u.Brain == null) { Main.LogError("该单位没有 PartUnitBrain，换不了 brain"); return false; }
                u.Brain.SetBrain(bp);
                return true;
            }
            catch (Exception e) { Main.LogError("换 brain 失败: " + e.Message); return false; }
        }
    }
}