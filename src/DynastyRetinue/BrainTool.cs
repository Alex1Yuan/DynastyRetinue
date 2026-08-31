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
            // ---- 实际在用、但从未探测过的（补进来才能一键验证）----
            //   这几个是 archetypes.json 里真正被赋给分型/精英的 brain。
            //   候选表原来只收通用基础 brain，于是"我们实际换上去的那个到底
            //   限不限制技能"反而没人验过 —— 战斗修女不行动就出在这里。
            ("ArgentaAlternative",     "c3b4b0e56cec4a99b448bc7102971bd3"),
            ("Ch04_Psyker",            "e257fc0ffe054d569cde2b18723f7a4a"),
            ("Sororitas_HBolter",      "32111e7b22a54ea9842006987571569b"),
            ("Sororitas_Melta",        "8dc8edca88f94951a4c3246c62441d63"),
            ("Sororitas_HFlamer",      "c1a807dc03004eefbe3ddac6b00bd8df"),
            // ---- 支援 / 军官 / 灵能 ----
            ("Officer",               "92118d6f493741e489aabe6e2f8d5fa4"),
            ("RangeNNStation",        "0148827b31a7458c8513d2cedc222af8"),
            ("MedicSpecialistBase",   "0a0c52ef6f654751b511d0d4a72ab96d"),
            ("Psyker_IronArm",        "5c53d46a9f6848cda77f19b3492d4281"),
            ("CombatServitorHeavy",   "77f9ee8cb7354a48998eb45f3531c512"),
            ("JungleWorldEldarRange", "45217eee343c4d23b0aa075d138edec4"),
            // ---- 两个贤者的原生脑（1.7.3 补入）----
            //   加它们是为了给一个具体争议收口：我按「引用了几个具体技能」推断
            //   这两个具名脑会把 AI 锁死在自己的清单里，据此在 1.7.2 换成了通用脑。
            //   但作者 1.7.1 的实战总账推翻了它 —— 高阶贤者顶着原生脑，照样放出了
            //   战术知识 / 暴露弱点 / 死亡低语·副手 / 瞄准破绽 / 完美地点 / 分析敌人，
            //   全是天赋线给的、一个都不在她原生脑的引用列表里。
            //   离线也验不了：blob 里 663 个脑没有一个序列化 UseOnlyListed 字段。
            //   所以只能靠这个按钮读运行时的真值。★引用具体技能 ≠ 白名单★ 多半只是优先级提示。
            ("IronWhisper_ArcShip01", "281aa0b1702244bd80f34ce36d83b599"),
            ("Dementz_ScrapCode",     "dc1e20cc434f4f21a0b3de37cb4d2bb3"),
            // ---- 8 个精英的原生脑（1.7.4 补入）----
            //   ★为什么这批是盲区★ 候选表一直只收「我们主动换上去的」脑，
            //   而**精英全部不覆盖 brain、跑的是各自单位自带的**。于是 10 个精英里
            //   有 8 个的脑从没验过限不限制技能 —— 而受限的表现是「站着不动 / 只用固定几招」，
            //   不报错，光看战斗永远归因不到这里（战斗修女那次就是这么拖了很久）。
            //   顺带纠正本文件开头那句「所有 DLC3_DL_* 的 brain 都是 UseOnlyListed=True」：
            //   1.7.3 实测 Sororitas_Melta（DLC3_DL 系）是 False。那是从少数样本推的通则，不成立。
            ("近战·铁壁守望",          "4478c8239713477d8834916e64f9ed5d"),
            ("近战·磐石首席",          "bb1f320e05ae4ac8b7a4510ef4db6c81"),
            ("狙击·寂静之眼",          "bff100aa45a94b01ae656bf1f39b7ff6"),
            ("狙击·赏金猎人",          "f9213f77f9304d28ad5682e6b84f9057"),
            ("狙击·赏金猎人React",     "47e69b4475ad4866bfa89f6f77264cae"),
            ("压制·怒枪宗师",          "33ac627a59b247f9bbb062b0446fda3c"),
            ("压制·圣焰React",         "01033dd7d3bf4fdfb9d6f892916307c0"),
            ("灵能·亚空间仲裁",        "53d92afa2ce347eeb40094718359b150"),
            ("灵能·亚空间仲裁React",   "f1618b3f8c44429f8fc0b3baa780cdfa"),
            // ---- 两个近战精英的原生脑（1.7.6 补入）----
            //   ★为什么现在才想到★ 它们一直被覆盖成 MeleeFlankerBase，于是「原生的长什么样」
            //   从没人问过。但贤者那次给了启发：UseOnlyListed=False 时，脑里那个技能列表
            //   不是白名单而是**优先级提示**。锈行猎手/电僧的原生脑十有八九点着自己的
            //   Reaper 系技能（死从天降、利刃之舞）—— 而这两个恰恰是实测「解锁了也不放」的。
            //   换成 0 引用的通用脑 = 把优先级提示扔了，AI 只能按通用打分，那两招分不高。
            //   先探测限不限制，不限制就可以试着用回原生脑。
            ("锈行猎手原生",           "95b1ca391e1e4d43abf365c43966a66a"),
            ("电僧原生",               "36dc3364de1641caa458ab02e9e89155"),
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