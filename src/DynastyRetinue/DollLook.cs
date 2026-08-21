using System;
using System.Collections.Generic;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.ResourceLinks;
using Kingmaker.UnitLogic;
using Kingmaker.Visual.CharacterSystem;

namespace DynastyRetinue
{
    /// <summary>
    /// 【实验中】把卫兵的外观改成**捏脸系统合成**，而不是用蓝图自带的整体模型。
    ///
    /// ★为什么要这条路★
    ///   1.0.69 实测 83 个候选单位：11 个没有 Character 组件，其余 72 个有 Character
    ///   但 `BakedCharacter != null`（整套网格提前合批烘死）。而
    ///       UnitEntityView.UpdateBodyEquipmentModel:
    ///           if (CharacterAvatar == null || (bool)CharacterAvatar.BakedCharacter) return;
    ///   —— 烘焙过的连**装备护甲的视觉都不生效**，往上加 EE 更不会渲染。
    ///   我们实际在用的 5 个分型单位 + 10 个精英单位**全部**落在这两类里。
    ///   全游戏唯一确定没被烘焙的 Character，是捏脸用的
    ///   `BlueprintRoot.Instance.CharGenRoot.MaleDoll/FemaleDoll`，
    ///   而 `DollData.CreateUnitView()` 正是拿它做底座重新拼。所以只能走这条。
    ///
    /// ★为什么不写 PartUnitViewSettings.Doll 字段★
    ///   `[JsonProperty] public DollData Doll { get; private set; }` —— 注意特性挂在
    ///   **属性**上，Json.NET 走 getter 序列化，所以连 patch getter 都会把它写进存档；
    ///   而 `GetHash128()` 里还有 `ClassHasher<DollData>.GetHash128(Doll)`，同时进同步哈希。
    ///   写进去就意味着：卸载 mod 后存档里留着我们的 doll，且联机双方配表不同就直接失步。
    ///
    ///   `Instantiate()` 是 Doll 唯一决定视图的地方（其余读它的只有序列化、哈希、预载）。
    ///   所以我们 Prefix 那一个方法、**临时**造一个 DollData 拼出视图返回，字段全程 null：
    ///     · 存档里没有任何痕迹，卸载即回退原版外观
    ///     · 哈希那一项两边永远都是 null —— 联机天然一致，开关可以是纯本地偏好
    ///   和 AppearancePatch 是同一个套路：不是写了再擦，是根本不写。
    ///
    /// ★脸从哪来★
    ///   Kasrkin 那套 KEE 只有护甲/头盔/靴/手套，**没有头和头发**（KEE 目录里根本没有
    ///   KEE_Head*/KEE_Hair*，捏脸的头是另一个来源）。少了头会拼出个无头人。
    ///   原版把完整配方放在 `PregenDollSettings` 组件里（可挂在 BlueprintUnit 上），
    ///   `Entry` 含 RacePreset + Head + Hair + Eyebrows + Beard + Scar + 各种 RampIndex。
    ///   chargen 的 5 个预设角色身上就带着它 —— 直接借用，脸不用猜，还顺带有 5 种长相。
    /// </summary>
    internal static class DollLook
    {
        /// <summary>
        /// 借脸用的 5 个 chargen 预设角色。它们身上挂着 PregenDollSettings。
        /// 按卫兵 UniqueId 稳定挑一个 —— **不能随机**，否则同一个卫兵每次读档换张脸。
        /// </summary>
        private static readonly string[] FaceDonors =
        {
            "89450677ca1d4b93a28c81f7afadf77c", // StartGame_Pregen_Psyker
            "6176bbc9298646d9a371999be2f02e64", // StartGame_Pregen_Fighter
            "478bd2c6192d4867a3145e463bc363d6", // StartGame_Pregen_Soldier
            "2e0da150605f4772bff485952fec3319", // StartGame_Pregen_Adept
            "f9d594a1094247b69f711774dd4d954e", // StartGame_Pregen_Leader
        };

        /// <summary>
        /// 【实验用写死的一套】Nexus images/29 那身「标准卫队」。
        /// 这些是 **KingmakerEquipmentEntity 蓝图 guid**，不是 EE 资源 AssetId —— 两者
        /// 长得一样但不通用，塞错了什么都不渲染。转换见 KeeAssetIds()。
        /// guid 来源：Bundles/cheatdata.json 按 TypeFullName 过滤（全库 620 条）。
        /// 明细见 ref/kee_standard_retinue.md。
        /// </summary>
        private static readonly string[] Kit =
        {
            "394e4b94f1284fefa4f47f1df0e42161", // KEE_OccupationAstraMilitarumClothes 基础外套
            "a777e0dba135405998b713aaf4bb67a4", // KEE_ArmorKasrkin1                   护甲
            "f344c7545656456aaa52ea4bd85bb214", // KEE_HelmetKasrkin1                  头盔
            "650f2260c8634abeb4d74c28c15c884a", // KEE_BootsKasrkin                    靴
            "7b79c080d9ac4bb7afbe87f2f9c3e1d9", // KEE_GlovesKasrkinArmor              手套
            "193dcc8d662e41a9a7093c01a2381244", // KEE_Randomizer_BeltArmorVoidsmen    腰带
            "9e28bfffe6114efea852b1cdcc2d8f59", // KEE_HellGunEquipmentEntity          背包
        };

        /// <summary>蓝图 guid -> 该 KEE 在某性别/种族下的 EE 资源 id。避免每次生成视图都解析蓝图。</summary>
        private static readonly Dictionary<string, string[]> _keeCache =
            new Dictionary<string, string[]>(StringComparer.Ordinal);

        public static void Invalidate() { _keeCache.Clear(); }

        /// <summary>
        /// 给这名卫兵合成一套外观。返回 null = 造不出来（调用方应当放行原版逻辑）。
        /// </summary>
        public static DollData Build(BaseUnitEntity u, string[] kit)
        {
            if (u == null) return null;
            if (kit == null || kit.Length == 0) kit = Kit;   // 没给就用内置那套（实验期的兜底）
            try
            {
                PregenDollSettings.Entry face = PickFace(u);
                if (face == null) { Main.Log("[外观] 借脸失败：5 个预设角色都没解析到 PregenDollSettings，本次保持原版外观。"); return null; }
                if (face.RacePreset == null) { Main.Log("[外观] 借来的配方没有 RacePreset，本次保持原版外观。"); return null; }

                var d = new DollData { Gender = u.Gender, RacePreset = face.RacePreset };

                // ---- 脸：整套照抄，缺哪件跳哪件 ----
                Add(d, face.Head); Add(d, face.Hair); Add(d, face.Eyebrows);
                Add(d, face.Beard); Add(d, face.Scar);

                // 配色索引跟着一起搬，否则头发/皮肤会用 0 号色，和原配方对不上
                Ramp(d, face.Head, face.SkinRampIndex);
                Ramp(d, face.Head, face.EyesColorRampIndex, secondary: true);
                Ramp(d, face.Hair, face.HairRampIndex);
                Ramp(d, face.Eyebrows, face.EyebrowsColorRampIndex);
                Ramp(d, face.Beard, face.BeardColorRampIndex);
                d.ClothesPrimaryIndex   = face.EquipmentRampIndex;
                d.ClothesSecondaryIndex = face.EquipmentRampIndexSecondary;

                // ---- 衣服：我们的套件 ----
                // ★这里不写 Gender / Race 的类型名★ 原版这两个枚举既有
                //   CharacterStudio 的嵌套版本，又有某个未解包程序集里的顶层同名版本，
                //   写死限定名很容易挑错那个。用 var 让编译器自己推，永远不会挑错。
                var gender = u.Gender;
                var race   = face.RacePreset.RaceId;
                string keyPrefix = gender + "|" + race + "|";

                for (int i = 0; i < kit.Length; i++)
                {
                    string key = keyPrefix + kit[i];
                    string[] ids;
                    if (!_keeCache.TryGetValue(key, out ids))
                    {
                        ids = null;
                        try
                        {
                            var kee = ResourcesLibrary.TryGetBlueprint(kit[i]) as KingmakerEquipmentEntity;
                            if (kee != null)
                            {
                                // GetLinks 自己处理 m_RaceDependent / m_RaceDependentArrays
                                var links = kee.GetLinks(gender, race);
                                if (links != null)
                                {
                                    var list = new List<string>(links.Length);
                                    for (int k = 0; k < links.Length; k++)
                                        if (links[k] != null && !string.IsNullOrEmpty(links[k].AssetId))
                                            list.Add(links[k].AssetId);
                                    ids = list.ToArray();
                                }
                            }
                        }
                        catch { }
                        _keeCache[key] = ids;
                        if (ids == null || ids.Length == 0)
                            Main.Log("[外观] KEE " + kit[i] + " 解析不到部件，这一件跳过。");
                    }
                    if (ids == null) continue;
                    for (int k = 0; k < ids.Length; k++)
                        if (!d.EquipmentEntityIds.Contains(ids[k]))
                            d.EquipmentEntityIds.Add(ids[k]);
                }

                return d;
            }
            catch (Exception e) { Main.LogError("[外观] 合成失败: " + e.Message); return null; }
        }

        /// <summary>
        /// 按 UniqueId 稳定挑一张脸。同一个卫兵每次读档必须是同一张。
        ///
        /// ★必须按性别筛★
        ///   身体性别用的是卫兵自己的 `u.Gender`（和它的名字、语音一致），
        ///   但 Head/Hair/Beard 是从捐脸单位抄的，而 `PregenDollSettings.Entry`
        ///   **本身不带性别** —— 性别在捐脸单位的 `BlueprintUnit.Gender` 上。
        ///   不筛就会出现男脸配女身。所以先只挑同性别的；一个同性别的都解析不到
        ///   （缺 DLC / 配表改动）才退而求其次，并且**明确记一笔**，
        ///   免得以后看到性别错乱还以为是别的原因。
        /// </summary>
        private static PregenDollSettings.Entry PickFace(BaseUnitEntity u)
        {
            string uid = u.UniqueId ?? string.Empty;
            int h = 0;
            for (int i = 0; i < uid.Length; i++) h = unchecked(h * 31 + uid[i]);
            if (h < 0) h = -h;

            PregenDollSettings.Entry fallback = null;

            // 从选中的那个开始往后找，先要同性别的
            for (int step = 0; step < FaceDonors.Length; step++)
            {
                string g = FaceDonors[(h + step) % FaceDonors.Length];
                try
                {
                    var bp = ResourcesLibrary.TryGetBlueprint(g) as BlueprintUnit;
                    var comp = bp != null ? bp.GetComponent<PregenDollSettings>() : null;
                    if (comp == null || comp.Default == null) continue;
                    if (bp.Gender == u.Gender) return comp.Default;
                    if (fallback == null) fallback = comp.Default;
                }
                catch { }
            }

            if (fallback != null)
                Main.Log("[外观] 没有和该卫兵同性别（" + u.Gender + "）的捐脸单位可用，退而用了异性的脸 —— 长相会和性别/语音对不上。");
            return fallback;
        }


        private static void Add(DollData d, EquipmentEntityLink link)
        {
            if (link == null || string.IsNullOrEmpty(link.AssetId)) return;
            if (!d.EquipmentEntityIds.Contains(link.AssetId)) d.EquipmentEntityIds.Add(link.AssetId);
        }

        private static void Ramp(DollData d, EquipmentEntityLink link, int index, bool secondary = false)
        {
            if (index < 0 || link == null || string.IsNullOrEmpty(link.AssetId)) return;
            if (secondary) d.EntitySecondaryRampIdices[link.AssetId] = index;
            else           d.EntityRampIdices[link.AssetId] = index;
        }
    }
}
