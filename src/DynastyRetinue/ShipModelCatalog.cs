using System;
using System.Collections.Generic;
using Kingmaker.Enums;

namespace DynastyRetinue
{
    /// <summary>
    /// 可选船模清单。**全部是 vanilla Unity 资源 AssetId，零新增 AssetId。**
    ///
    /// 提取与核验方法（可复现，脚本在 D:\RT_RetinueMod\_tmp\ships2.py）：
    ///   1. Bundles/cheatdata.json 按 TypeFullName == Warhammer.SpaceCombat.Blueprints.BlueprintStarship
    ///      枚举出 **123** 条舰船蓝图（不靠关键字，不会混进技能/音效/预设）。
    ///   2. blueprints-pack.bbp 里每条蓝图 blob 的字段序是
    ///         Size(int32) → Color(4×float, a=1.0f) → m_Race → m_Portrait → **Prefab.AssetId** → m_VisualSettings → m_Faction
    ///      用 "<蓝图名>_VisualSettings" 的 guid 作锚点，取它**之前**最后一个
    ///      能在 Bundles/locationlist.json 命中的 32-hex 串 = Prefab.AssetId。
    ///   3. locationlist.json 是 m_Guids[]/m_Bundles[] 两条等长数组（14150 条），
    ///      即"AssetId → bundle"的权威全表（BundlesLoadService.GetBundleNameForAsset 走的就是它）。
    ///
    /// 校准：Sword/Falchion/Firestorm 三条明文 .jbp 的 Prefab.AssetId 与提取结果逐字一致；
    /// 123/123 全部解出 prefab，**没有任何 prefab 跨档位复用**（prefab ↔ Size 干净分层）。
    ///
    /// ★ 全游戏去重后共 39 个舰船船模 ★
    ///     GrandCruiser_3x6   7 条蓝图 →  2 个船模
    ///     Cruiser_2x4       14 条蓝图 →  6 个船模
    ///     Frigate_1x2       37 条蓝图 →  6 个船模
    ///     Raider_1x1        52 条蓝图 → 19 个船模（鱼雷/舰载机/小艇）
    ///     Large             11 条蓝图 →  4 个船模（死灵舰、海盗基地塔等静物）
    ///     Medium             2 条蓝图 →  2 个船模（彗星、星系图图标船）
    ///
    /// ★ 陷阱：名字带 Cruiser 不等于模型是巡洋舰 ★
    ///   DLC2_PirateCruiser_Boss 的 prefab 是 26e3688a…（Falchion **护卫舰**模型），
    ///   OrkCruiser_Port_Unit / OrkCruiser_Starport_Unit 用的是 a6bcda10…（Sword 护卫舰）。
    ///   本表只收去重后确认过 portrait 的真·大船。
    /// </summary>
    public sealed class ShipModel
    {
        /// <summary>Unity 资源 AssetId（不是蓝图 guid）。全部 bundle=extra。</summary>
        public readonly string PrefabAssetId;
        /// <summary>观感（来自原版 portrait 蓝图名，都是 BFG 正典舰级）。</summary>
        public readonly string Hull;
        /// <summary>该 prefab 在原版里配套的 Size 档位。换模时把 State.Size 设成它，格子占位才自洽。</summary>
        public readonly Size Tier;
        public readonly string Faction;
        /// <summary>一条使用该 prefab 的原版蓝图（举证用）。</summary>
        public readonly string SourceBlueprint;
        /// <summary>true = 只有 DLC 蓝图引用它。prefab 本体仍在基础包 extra 里，但优先级放低。</summary>
        public readonly bool DlcOnlyReferenced;

        public ShipModel(string prefabAssetId, string hull, Size tier, string faction,
                         string sourceBlueprint, bool dlcOnlyReferenced = false)
        {
            PrefabAssetId = prefabAssetId; Hull = hull; Tier = tier;
            Faction = faction; SourceBlueprint = sourceBlueprint;
            DlcOnlyReferenced = dlcOnlyReferenced;
        }

        /// <summary>
        /// 给玩家看的船体名（本地化）。
        ///
        /// ★为什么不直接把 Hull 本地化★ Hull 还被当**数据**用：
        /// ShipMountFallback.cs:119 会把它存进 Settings.ProwLearnedFrom、
        /// ShipYardUI.cs:221 拿它拼 GameObject 名、日志里也到处是它。
        /// 存起来的值和对象名不该随界面语言变，否则玩家切一次语言，
        /// 之前学到的挂点记录就对不上了。所以显示和数据分开。
        /// </summary>
        public string HullName { get { return L.T(Hull); } }

        public override string ToString()
        {
            return HullName + "（" + Faction + " / " + Tier + "）";
        }
    }

    public static class ShipModelCatalog
    {
        // ── 大巡洋舰档 GrandCruiser_3x6：全游戏只有这 2 个，没有第 3 个 ──────────
        /// <summary>混沌战列巡洋舰。portrait=chaosbattlecruiser。ChaosGrand10 / ChaosGrand10Miniboss / DLC3_ChaosGrand。</summary>
        public const string Grand_ChaosBattlecruiser = "0da2b98b8cef1b8498dad3ecb12cfb6b";
        /// <summary>帝国 Universe 级质量运输舰（3x6 巨舰，货船轮廓）。portrait=imperial_transport_massconveyor。ImperialTransport3 / DLC2_ImperialTransport_1,2。</summary>
        public const string Grand_ImperialMassConveyor = "0ea91ee80d7b01a44b3cad74efbc8a72";

        // ── 巡洋舰档 Cruiser_2x4：共 6 个 ────────────────────────────────────
        /// <summary>帝国 Gothic 级巡洋舰（哥特飞扶壁 + 光矛，最"帝国巡洋舰"）。ImperialCruiser10Named / PirateCruiser12Named / DLC1_ImperialCruiser5。</summary>
        public const string Cruiser_ImperialGothic = "67017c4dd1d5c1c40979ce2fc1cd38b2";
        /// <summary>帝国 Dictator 级巡洋舰（Lunar 的航母改型）。★注意：使用它的蓝图叫 OrkCruiser10/PirateCruiser7，但 portrait 是 imperial_cruiser_dictator，模型是帝国船。★</summary>
        public const string Cruiser_ImperialDictator = "8c34d0a2f4987134c8a625612476e22d";
        /// <summary>混沌 Carnage 级巡洋舰。ChaosCruiser5 / DLC1_ChaosCruiser。</summary>
        public const string Cruiser_ChaosCarnage = "10de1ae75122ba243b423194534e5182";
        /// <summary>黑暗灵族轻巡洋舰。DrukhariCruiser6 / DrukhariBHCruiser10。</summary>
        public const string Cruiser_Drukhari = "e18691bc8276691408852ec91c909c42";
        /// <summary>灵族巡洋舰。★只被 DLC3_AeldariBoss_var1/var2 引用★。</summary>
        public const string Cruiser_Aeldari = "82d6449f24d12b94eb8d87225f10de32";
        /// <summary>质量运输舰（巡洋尺度，商船轮廓）。★只被 DLC1_ChaosTransport 引用★。</summary>
        public const string Cruiser_MassConveyorSmall = "5ffb23a1b630d1d46b222e70aa56fb8c";

        // ── 护卫舰档（还原基线）────────────────────────────────────────────
        public const string Frigate_Sword = "a6bcda106bf8fd44da4286ee04a3ad8f";
        public const string Frigate_Falchion = "26e3688a99a9eed44baa2e19e16be1a4";
        public const string Frigate_Firestorm = "31da3f04de39e5446b16641deb3be42d";

        public static readonly ShipModel[] All =
        {
            // 大巡 3x6
            new ShipModel(Grand_ChaosBattlecruiser,   "混沌战列巡洋舰",           Size.GrandCruiser_3x6, "Chaos",    "ChaosGrand10"),
            new ShipModel(Grand_ImperialMassConveyor, "帝国 Universe 级质量运输舰", Size.GrandCruiser_3x6, "Imperial", "ImperialTransport3"),

            // 巡洋 2x4
            new ShipModel(Cruiser_ImperialGothic,     "帝国 Gothic 级巡洋舰",     Size.Cruiser_2x4, "Imperial", "ImperialCruiser10Named"),
            new ShipModel(Cruiser_ImperialDictator,   "帝国 Dictator 级巡洋舰",   Size.Cruiser_2x4, "Imperial", "PirateCruiser7"),
            new ShipModel(Cruiser_ChaosCarnage,       "混沌 Carnage 级巡洋舰",    Size.Cruiser_2x4, "Chaos",    "ChaosCruiser5"),
            new ShipModel(Cruiser_Drukhari,           "黑暗灵族轻巡洋舰",         Size.Cruiser_2x4, "Drukhari", "DrukhariCruiser6"),
            new ShipModel(Cruiser_Aeldari,            "灵族巡洋舰",               Size.Cruiser_2x4, "Aeldari",  "DLC3_AeldariBoss_var1", true),
            new ShipModel(Cruiser_MassConveyorSmall,  "质量运输舰（巡洋尺度）",   Size.Cruiser_2x4, "Imperial", "DLC1_ChaosTransport",   true),

            // 护卫 1x2
            new ShipModel(Frigate_Sword,     "Sword 级护卫舰",           Size.Frigate_1x2, "Imperial", "SwordClassFrigatePlayer_Starship"),
            new ShipModel(Frigate_Falchion,  "Falchion 级护卫舰",        Size.Frigate_1x2, "Imperial", "FounderShip_FalchionClassFrigatePlayer_Starship"),
            new ShipModel(Frigate_Firestorm, "Firestorm/Tempest 级护卫舰", Size.Frigate_1x2, "Imperial", "AlternateShip_FirestormClassFrigatePlayer_Starship"),
        };

        public static ShipModel ByPrefab(string prefabAssetId)
        {
            if (string.IsNullOrEmpty(prefabAssetId)) return null;
            for (int i = 0; i < All.Length; i++)
                if (All[i].PrefabAssetId == prefabAssetId) return All[i];
            return null;
        }

        /// <summary>
        /// 每个档位的**默认**船模。
        ///
        /// 设计（用户拍板，v0.23 修订）：**大巡改用放大的帝国 Dictator 级**。
        ///
        /// 改的理由是实测挂点数据，不是审美：
        ///     Gothic 巡洋    9 个挂点  Port×4 Starboard×4 Dorsal×1                    ← 无 Prow
        ///     Universe 运输 23 个挂点  Dorsal×1 Port×11 Starboard×11                  ← 无 Prow
        ///     混沌战列巡洋  27 个挂点  Dorsal×1 NoType×1 AugurArray×1 Port×12 Starboard×12  ← 无 Prow
        ///     Dictator 巡洋 20 个挂点  AugurArray Dorsal Keel LandingBays PlasmaDrive×7 Port×4 Prow×1 Starboard×4
        /// 光矛装在 **Prow** 槽位，武器美术是挂到船体 prefab 上同类型的 StarshipItemSlot 下面的
        /// （StarshipView: ItemSlots.FindAll(x => x.Type == requiredSlots.SlotType)）。
        /// 匹配不到 ⇒ 美术挂不上去 ⇒ 开火点退回原点，表现为「光矛从虚空里开火」——
        /// 玩家实测确认了这一点：换 Gothic 后宏炮正常、光矛在虚空。
        ///
        /// **Dictator 是四个里唯一 Prow/Keel 齐全的**，而两个原生 GrandCruiser 反而都缺 Prow。
        /// 所以「大巡 = 放大的 Dictator」既解决光矛，又仍然是帝国战舰造型。
        ///
        /// 巡洋舰档保持 Gothic（用户原选择，造型最"帝国巡洋舰"）；
        /// 它缺 Prow 的问题由 StarshipView 的挂点兜底补丁处理，
        /// 想要开箱即用无兜底的话菜单里手动选 Dictator。
        /// </summary>
        public static ShipModel DefaultFor(Size tier)
        {
            switch (tier)
            {
                case Size.Cruiser_2x4:
                    return ByPrefab(Cruiser_ImperialGothic);
                case Size.GrandCruiser_3x6:
                    return ByPrefab(Cruiser_ImperialDictator);   // 原生巡洋，放大 ×1.5152 当大巡
                default:
                    return null;      // 护卫舰用原版模型，不换
            }
        }

        public static List<ShipModel> ForTier(Size tier)
        {
            var r = new List<ShipModel>();
            for (int i = 0; i < All.Length; i++)
                if (All[i].Tier == tier) r.Add(All[i]);
            return r;
        }

        /// <summary>该档位的推荐默认船模。没有就返回 null。</summary>
        public static ShipModel Recommended(Size tier)
        {
            switch (tier)
            {
                case Size.GrandCruiser_3x6: return ByPrefab(Cruiser_ImperialDictator);
                case Size.Cruiser_2x4:      return ByPrefab(Cruiser_ImperialGothic);
                case Size.Frigate_1x2:      return ByPrefab(Frigate_Sword);
                default:                    return null;
            }
        }

        /// <summary>是否在白名单里。换模前必须过这一关 —— 只准用本表里核实过的 vanilla AssetId。</summary>
        public static bool IsKnown(string prefabAssetId) { return ByPrefab(prefabAssetId) != null; }
    }
}
