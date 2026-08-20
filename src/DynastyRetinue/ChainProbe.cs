using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Root;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Enums;
using Kingmaker.UnitLogic.Levelup;
using Kingmaker.UnitLogic.Levelup.Selections.Feature;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.UnitLogic.Progression.Paths;

namespace DynastyRetinue
{
    /// <summary>
    /// v0.0.6 —— 回答 v0.0.5 之后剩下的最后一个未知：career 链能不能串。
    ///
    /// v0.0.5 已证明：
    ///   1. 14 个候选单位对全部 17 条 path 行为完全一致 ⇒ 分型不能靠单位蓝图，
    ///      只能靠我们自己定的 career 链映射。
    ///   2. CanUpgradePath 只是 UI 判定（全代码库仅 2 个调用点，均在 UI）。
    ///      9 条 T2/T3 全部标 [UI否]，但 rank 照样 0→20/20，等级 0→20。
    ///   3. AscensionCareerPath_Fake 连 UI 都放行（Prerequisites 为空）。
    ///
    /// 本轮要验的：同一个单位上依次跑 T1→T2→T3，等级是否累加到 55，
    /// 且血量/天赋是否真涨（而不是只有一个等级数字）。
    /// </summary>
    public static class ChainProbe
    {
        private const string Soldier    = "06f4f78a9c1a472b85cd79a9a142153d"; // T1
        private const string Fighter    = "974496d72fbe4329b438ee15cf004bd2"; // T1
        private const string Adept      = "1529e5a0e7844bf3bb8d0cc0501264d4"; // T1
        private const string Veteran    = "651684417def4c258c72ba91f481b817"; // T2
        private const string Vanguard   = "fec9cd09f11b4615b7a17f441350d2d4"; // T2
        private const string Hunter     = "6f276e8a8e2c4a548504ae39d2a7f22a"; // T2
        private const string Strategist = "a31b390cabe7464fbfd0e1ba53c4112f"; // T2
        private const string Ascension  = "bcefe9c41c7841c9a99b1dbac1793025"; // T3

        public sealed class EliteDef
        {
            /// <summary>
            /// 这个精英专属的 brain（可选）。不填就沿用分型的。
            ///
            /// ★为什么精英需要单独配★
            /// 精英往往是照着某个具体 NPC 复刻的（圣焰·净罪修女的加点方案就是
            /// argenta_soldier_veteran —— Argenta 本人的），那么那个 NPC 的 brain
            /// 通常也最贴它的技能构成。而分型级 brain 是按"普通卫兵那个单位"选的，
            /// 硬套到精英身上不一定合适。
            ///
            /// ★选之前必须验两件事★
            ///   ① m_UseOnlyListedAbilities 必须是 false，或者它列的技能这个单位真的有。
            ///      若为 true 且列的是别人的技能，这个单位会被**锁死**（比现状糟得多）——
            ///      这正是 RetinueTest.cs:233 那条注释描述的坑。
            ///   ② 同名不同物要当心：Inquisitor_Argenta_brain 列的
            ///      HuntDownThePrey_**Hunter**_Ability 和修女的
            ///      DLC3_DL_Sororitas_HBolter_HuntDownThePrey_Ability 是不同 GUID。
            /// </summary>
            public string BrainId;

            /// <summary>精英专用单位蓝图 —— 兼作"是不是这个精英"的持久判据。</summary>
            public string UnitId;
            /// <summary>
            /// UnitId 解析不到时依次尝试的备选蓝图（DLC 缺失兜底）。
            ///
            /// ★精英原来没有这个，是个真空洞★
            ///   分型级的 UnitFallback 只在 `_elite == null` 时才生效（RetinueTest.cs 的生成分支），
            ///   所以精英的 DLC3 蓝图解析不到时会直接掉到面板的全局兜底 ——
            ///   玩家招到的「赏金 · 猎首」其实是个普通甲板卫兵。功能正常，但看着完全不对。
            ///   给精英单独配兜底之后，退化的是外观档次而不是身份。
            ///
            /// ★身份不受影响★ EliteDefOf 优先读 `kgd.e:` 标记（写在 CustomPetName 里），
            ///   蓝图匹配只是旧存档的退路。所以兜底出来的精英仍然是那个精英：
            ///   名字、军衔、职业链、装备、加点方案、brain 覆盖全部照常，
            ///   蓝图只提供模型、基础属性和自带武器 —— 退化的只有外观档次。
            /// </summary>
            public string[] UnitFallback;
            /// <summary>专属名字。旧格式，只在没有 Rank 时用。</summary>
            public string Name;
            /// <summary>英文位阶（archetypes.json 的 "rank_en"）。缺失回落中文。</summary>
            public string RankEn;
            /// <summary>专属位阶（archetypes.json 的 "rank"）。有它就走「位阶·人名」，
            /// 和普通卫兵同一套机制，只是位阶固定不随等级晋升 —— 精英本来就在顶。</summary>
            public string Rank;
            /// <summary>该精英的毕业套装。</summary>
            public string[] Gear;
            /// <summary>可选：覆盖分型的职业链（同一分型下的两个精英可以走不同链）。</summary>
            public string[] Chain;
            /// <summary>可选：覆盖分型的加点方案名。</summary>
            public string PlanName;
            /// <summary>可选：关键天赋 GUID 列表。方案没覆盖到的选择点优先挑这些，
            /// 而不是取第一个可选项 —— 让只有要点、没有完整点法的攻略也能用。</summary>
            public string[] KeyTalents;
            /// <summary>可选：属性优先级，如 ["BallisticSkill","Perception"]。
            /// 属性天赋内部名形如 BallisticSkillStatAdvancement1，按声明顺序匹配。</summary>
            public string[] AttrPriority;
            /// <summary>可选：覆盖种族。PartUnitProgression.SetRace 是实体级的，
            /// 不动蓝图，所以不会污染共用该蓝图的其它单位。
            /// 用途：伊瑞莉特方案里的灵族天赋有种族门控，人类模板拿到灵族起源也解锁不了。</summary>
            public string RaceId;
            /// <summary>可选：升级**之前**额外授予的原版特性，给"方案存不下、但不给就
            /// 整段选项都不出现"的东西用 —— 目前是灵能学派。
            /// Pyromancy_Base_Feature / Biomancy_Base_Feature 各带 9 个 AddFeaturesToLevelUp，
            /// BlueprintSelectionFeature.GetSelectionItems 会把 unit.Facts 上的这些并进候选池；
            /// 不授予的话火系/生物系灵能一条都进不来（实测火杖行刑者 7 条全灭）。</summary>
            public string[] PreGrant;
            /// <summary>可选：普通卫兵按阶位发的三套渐进装备。
            /// 分档依据是物品 Rarity（Common → Pattern → Unique）——
            /// ItemLevel 在本作里 2755/2940 是 0，用不了。
            /// playerGear 有配置时优先用 playerGear（手动压过默认）。</summary>
            public string[] GearT1, GearT2, GearT3;
            /// <summary>可选：按段拼装方案。pathGuid -> { 桶 -> 方案名列表 }，
            /// 桶取值 "FirstCareer" / "SecondCareer" / "FirstOrSecondCareer" / "default"
            ///（只写一个 default 就是整段照抄）。一个桶可以给多个方案名 ——
            /// 候选会合并，PickOne 逐个试，谁真能选上用谁。
            /// FirstOrSecondCareer 那个桶尤其需要多源：单一来源给的往往是它自己 T2 的天赋，
            /// 换条链就点不出来（实测赏金·猎首 3 条 Veteran_* 全空）。
            /// 用于攻略只给要点、但组成它的每一段在别的方案里都有现成数据的路线。
            /// 配了它就不再走 plan/PlanName。</summary>
            public Dictionary<string, Dictionary<string, string[]>> PlanSegments;
            /// <summary>可选：合成时要剔除的条目（比如队友专属天赋，别人点不出来）。</summary>
            public string[] ExcludeFeatures;
        }

        public sealed class Archetype
        {
            public readonly string Name;
            public readonly string[] Chain;
            /// <summary>可选：绑定的 RTAutoBuilder 加点方案名（archetypes.json 里的 "plan" 字段）。</summary>
            public string PlanName;
            /// <summary>可选：该分型专用的单位蓝图（决定 brain / 模型 / 自带装备）。
            /// 空则用面板上的全局 UnitAssetId。</summary>
            public string UnitId;
            /// <summary>
            /// 可选：UnitId 解析不到时依次尝试的备选单位蓝图。
            ///
            /// ★为什么需要它★ 五个分型里有四个的主单位是 DLC3 蓝图
            /// （DLC3_DL_Guard_Melee_Ally / _Guard_Sniper / _Sororitas_HBolter / _Inquisitor）。
            /// 没买 DLC3 的玩家，TryGetBlueprint 返回 null，原来的代码直接 return null ——
            /// 玩家的实际观感是「点了招募，什么都没发生」，而且五条线里四条都这样。
            ///
            /// 兜底单位是从**精英表里挑的本体蓝图**：同一个角色定位，且已经在实机里
            /// 验证过能正常生成和穿装备。刻意避开了艾达灵族单位（Quetza_EldarRangerHard），
            /// 那个有异形装备限制，人类装备会大面积装不上。
            /// 每条链末尾都兜到 OfficersDeckGuard（军官分型在用的那个本体蓝图）。
            ///
            /// 代价：没有 DLC3 时普通卫兵会和对应精英长得一样。这是可接受的降级，
            /// 比「招不出人」好得多。README 里有说明。
            /// </summary>
            public string[] UnitFallback;
            /// <summary>可选：覆盖该分型的 AI brain。原版卫兵 brain 多为 UseOnlyListed=True，
            /// 不换的话 career 链练出来的技能 AI 一条都不会用。</summary>
            public string BrainId;
            /// <summary>可选：毕业装备（物品蓝图 GUID 列表，含植入物）。
            /// 凭空生成、不从玩家仓库拿；只发给精英卫兵。解析不到的条目静默跳过。</summary>
            public string[] Gear;
            /// <summary>可选：精英卫兵专用的单位蓝图。设了就用它当"是不是精英"的持久判据 ——
            /// 蓝图本身随实体走，不需要额外存状态，读档后照样认得出来。</summary>
            public string EliteUnitId;
            /// <summary>可选：精英卫兵的专属名字。空则退回「前缀·分型 编号」。</summary>
            public string EliteName;
            /// <summary>可选：玩家在面板里手动装配的装备（覆盖 Gear，发给普通卫兵）。</summary>
            public string[] PlayerGear;
            /// <summary>可选：额外授予的天赋（多为熟练度，如动力甲/重武器/异形武器专精）。
            /// 在发装备**之前**授予 —— 没有对应熟练度的话装备会被 CanBeEquippedBy 拒掉。
            /// 只能填原版 BlueprintFeature 的 GUID，不产生新 AssetId。</summary>
            public string[] GrantFeatures;
            /// <summary>可选：分型级的升级前置（同 EliteDef.PreGrant）。普通卫兵走这条 ——
            /// 它们没有 EliteDef，不给的话分型方案里的学派天赋照样一条都进不来。
            /// 精英自己声明了 preGrant 就用自己的，没声明才回落到这里。</summary>
            public string[] PreGrant;
            /// <summary>可选：普通卫兵按阶位发的三套渐进装备。
            /// 分档依据是物品 Rarity（Common → Pattern → Unique）——
            /// ItemLevel 在本作里 2755/2940 是 0，用不了。
            /// playerGear 有配置时优先用 playerGear（手动压过默认）。</summary>
            public string[] GearT1, GearT2, GearT3;
            /// <summary>可选：普通卫兵按阶位取的三档名字（T1/T2/T3）。
            /// 升阶时会把已有卫兵改名到新档、保留编号。精英不走这里，用 EliteDef.Name。</summary>
            public string[] GuardNames;
            /// <summary>英文军衔。archetypes.json 的 "guardNames_en"。
            /// 缺失时回落中文 —— 宁可一处没译，也不能空白。</summary>
            public string[] GuardNamesEn;
            /// <summary>可选：该分型的多个精英（每个有自己的单位/名字/装备/链）。
            /// 配了这个就忽略上面的 EliteUnitId/EliteName/Gear 单精英字段。</summary>
            public EliteDef[] Elites;
            public Archetype(string name, params string[] chain) { Name = name; Chain = chain; }
        }

        /// <summary>四个分型的 career 链。T1(15) + T2(20) + T3(20) = 55 = XPTable 上限。</summary>
        public static readonly Archetype[] Archetypes =
        {
            new Archetype("先锋 Vanguard", Fighter, Vanguard,   Ascension),
            new Archetype("狙击 Sniper",   Soldier, Hunter,     Ascension),
            new Archetype("连射 Suppress", Soldier, Veteran,    Ascension),
            new Archetype("灵能 Psyker",   Adept,   Strategist, Ascension),
        };

    }
}
