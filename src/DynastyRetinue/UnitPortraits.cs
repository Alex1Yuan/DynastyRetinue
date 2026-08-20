using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Kingmaker.Blueprints;
using Kingmaker.ResourceLinks;

namespace DynastyRetinue
{
    /// <summary>立绘尺寸。对应 BlueprintPortrait.Data 上的三条 SpriteLink。</summary>
    public enum PortraitSize
    {
        /// <summary>m_PortraitImage —— 185x242（队伍框头像）。列表条目用这个。</summary>
        Small = 0,
        /// <summary>m_HalfLengthImage —— 330x432（半身，角色面板）。</summary>
        HalfLength = 1,
        /// <summary>m_FullLengthImage —— 692x1024（全身，对话/详情）。</summary>
        FullLength = 2,
    }

    /// <summary>
    /// 由 unitId(BlueprintUnit 的 AssetId) 取出可直接画进 UI 的 Sprite。
    ///
    /// 取法链（全部是原版公开 API，只有 m_Portrait 一个私有字段要反射）：
    ///   ResourcesLibrary.TryGetBlueprint&lt;BlueprintUnit&gt;(id)
    ///     -> BlueprintUnit.m_Portrait (private BlueprintPortraitReference)  // 反射
    ///     -> BlueprintPortraitReference.Get() -> BlueprintPortrait
    ///     -> BlueprintPortrait.Data (PortraitData)
    ///     -> Data.m_PortraitImage / m_HalfLengthImage / m_FullLengthImage (public SpriteLink)
    ///     -> SpriteLink.AssetId -> ResourcesLibrary.TryGetResource&lt;Sprite&gt;(assetId, true, hold:true)
    ///
    /// 为什么不直接用 BlueprintUnit.PortraitSafe：
    ///   PortraitSafe 在 m_Portrait 为空时回退到 UIConfig.Portraits.Male/FemalePlaceholderPortrait，
    ///   但那两个占位蓝图（Placeholder_Male_Portrait / Placeholder_Female_Portrait）在
    ///   blueprints-pack.bbp 里三条 SpriteLink 全是空的 —— 它保证 BlueprintPortrait 非 null，
    ///   却不保证 Sprite 非 null。所以这里自己做回退。
    ///
    /// 为什么用 TryGetResource(hold:true) 而不是 Data.SmallPortrait：
    ///   Data.SmallPortrait 内部走 Load(ignorePreloadWarning:true)，hold=false，
    ///   句柄不持有引用，资源可能在之后被卸载，我们缓存的 Sprite 就变成已销毁对象。
    ///   hold:true 会持有，禁用 mod 时用 Cleanup() 一次性还回去。
    /// </summary>
    public static class UnitPortraits
    {
        /// <summary>Empty_Portrait —— 原版"无立绘"蓝图，三张图都真实存在，可当兜底。</summary>
        public const string EmptyPortraitId = "df493e2556e83f347beaa5597ca73abe";

        /// <summary>
        /// 没有自带立绘的普通卫兵 -> 借用原版建卡立绘（CharGenRoot.m_Portraits 那 30 张）。
        /// 全是只读引用，只用于画图，不会写进存档，不碰 AssetId 红线。
        /// 想换脸直接改这里的 GUID 即可，候选见 CharGen 立绘池：
        ///   ArbitesMale f0d5da655acb4b47846e52e8e97a5254 / ArbitesFemale bde88426899249eca81c6a6cb5c2dcee
        ///   ArbitesHelmetMale 53f44b5aa25442ed8bedd0015e33d25c / ArbitesHelmetFemale 789d150f4e2b4f7da536980fcabdae31
        ///   AstraMilitarumMale 1b9082909e854f6d97c366358a280102 / AstraMilitarumFemale ba1bdefff9f44351b0b7c139bf5b036f
        ///   ComissarMale 914c9411f0024f02aa4d3f5dd621841e / ComissarFemale 68e23c766b9b42f0bcf09b642b8f5788
        ///   CriminalMale 28e3d61fbcb94305bdbfe3e598ef72c0 / CriminalFemale 12a70d4ed7204766b38e730fb84cd998
        ///   AdeptusMinistorumMale b8c150a212dc43b8ae2a580c0145fa20 / AdeptusMinistorumFemale e5fa9cc788be4459bc0b9c6a74968da6
        ///   ImperialNavyMale d03e6b0de6994d8f8b10a8ad16ebd94e / ImperialNavyFemale 8d19acfeea77464783d579110e4a89e4
        ///   NobilityMale 31b963f3e3054014ad03b1b30d60aa44 / NobilityFemale 222fb5f4775344c495cf14af52a66eec
        ///   PsykerMale 0114a2db302c45a9bc780593d0ec5134 / PsykerFemale 3c1cff3901824c0298ba4abe1801c807
        ///   DecadenceMale db208877d55c43bba8f1b59613a8e857 / DecadenceFemale db248f8c0357439bb49480e13a998e6a
        ///   MercenaryNavigator bd0dee7996ee49de82411584295a20ed / SpaceVendor fed48810d8794e2db1f17b9bc46c4e0a
        ///   NeutralMale 3df3df7a73a544d79455a40e2dc1156a / NeutralFemale 86163d32b05d4b14a7fb674c92c7113d
        /// </summary>
        public static readonly Dictionary<string, string> UnitPortraitOverride =
            new Dictionary<string, string>
            {
                // ================= 精英卫兵立绘 =================
                //
                // ★ v0.27.0 —— 按用户口径 + 工作流字节级普查重排 ★
                //   · 建卡池（CharGenRoot.m_Portraits，实测是 **30** 张不是 32）**可以用**
                //   · **禁止**高频互动 NPC（cue≥20 / 同伴 / 剧情要角）
                //   · **普通卫兵不给立绘** —— 那 6 行 override 已删，落到解析链第 3 步的
                //     Empty_Portrait（原版正规"无立绘"图，三尺寸都在，不是破图）。
                //     顺带解决了两处「精英与自己手下同脸」。
                //
                // ★ 这一版最重要的修正：**单位性别** ★
                // 之前完全没查过性别，结果有两处男脸配女单位。工作串解了 BlueprintUnit 的
                // blob 布局并用 m_Portrait 已知值自校验；我另外独立核实了两个模型名
                //（bundle 里 grep 到 BCT_Eldar_Female_Ranger / BCT_Female_Chorda_Psyker）。
                //
                // 等级：A = cue 0 且非建卡池且无剧情身份 ｜ B = 建卡池 30 张 ｜ C = 低 cue 一次性 NPC
                // 全部 GUID 已逐个在 ref/bbp/catalog.tsv 核到名字；10 张 Small AssetId 互不相同。

                // 铁壁·先锋队长  TreasureWorld_Arbites_ShieldNShotgun（男）-> ArbitratorStein
                //   ★A 档升级★ 原为建卡池 ArbitesHelmetMale。
                //   法务部全覆式暴动盔 + 红色目镜，正对"盾+霰弹枪"的镇暴装。
                //   cue/dlg/bark 全 0，4 个佩戴单位也全 0，非建卡池。
                { "4a02a1bee6f84892b3cb7a3f8c818c69",
                  "942c9ad5ac1146dea04f03d2d35c6bdf|53f44b5aa25442ed8bedd0015e33d25c|f0d5da655acb4b47846e52e8e97a5254" },

                // 磐石·首席战士  VC2_Arbitres_Melee（男）-> ArbitratorBryce
                //   ★A 档升级★ 原为建卡池 ArbitesMale。
                //   这批法务部美术里**唯一不戴全盔**的（红带便帽+呼吸器，露脸），
                //   和铁壁(Stein)、怒火自带的(Clayton)两张盔面拉开层次。cue=0。
                { "30e6364a1d7a425b93d877122c6eed40",
                  "a23953738df04e5b9f1d04a52aab3582|f0d5da655acb4b47846e52e8e97a5254|1b9082909e854f6d97c366358a280102" },

                // 寂静之眼  Quetza_EldarRangerHard（**女性艾达灵族**）-> Iremeryss_Portrait
                //   自带 BCT_Eldar_Male_Guardian2：三条 SpriteLink 指向同一个 AssetId，
                //   而那个 id 不在 locationlist.json 的 14150 条里 —— 图确实没发货，
                //   且连性别都不对（男性 Guardian 配女性 Ranger）。
                //   ★全库唯一「女性 + 灵族 + 三尺寸完整」的脸★（淡蓝灰皮、尖耳、蓝面纹）。
                //   代价：C 档 cue=3（Vect 王座厅那场戏三句台词，dlg=0 bark=0，非反复互动对象）。
                //   备选是唯一的素颜黑暗灵族脸（A 档零引用，但仅 Small 尺寸、脸偏男性）。
                //   ★Craftworld 灵族全库一张路人脸都没有★——367 个 sprite 里只有
                //   Yrliet_* / Solitaire_*，不是"漏打包"，是根本没画。
                { "aca1e823dbf64d6999d2132e3198dd5a",
                  "c0021b321aec4686972310071c458105|d328a891cc7749f49e75b08b636f2ebd|75b9f146f87c4071bae52c06a13eed06" },

                // 赏金·猎首  FootfallAnverSniper_Ranged_Elite（**女**，兜帽遮面）-> CriminalFemale
                //   ★性别修正★ 原为 CriminalMale。短发+疤+义眼，赏金猎人调性一致。
                //   B 档 cue=0，0 个单位佩戴。备选是狙击瞄准型义眼的军队女性。
                { "53281ae602a34756a47c3e23f66c06cd",
                  "12a70d4ed7204766b38e730fb84cd998|ba1bdefff9f44351b0b7c139bf5b036f|86163d32b05d4b14a7fb674c92c7113d" },

                // 圣焰·净罪修女  DLC3_DL_Sororitas_Melta_Unit（女）-> AdeptusMinistorumFemale
                //   金发+额头虔信刻痕的教会系女性。全库唯一非同伴的教会女脸
                //  （SisterArgenta 是阿尔金塔本人，cue=78，禁用）。B 档 cue=0。
                //   备选是法务部女盔（全覆盔，遮脸所以不会和修女头盔打架）。
                { "2cf75c27e6d34681ab623101b0be1135",
                  "e5fa9cc788be4459bc0b9c6a74968da6|789d150f4e2b4f7da536980fcabdae31|222fb5f4775344c495cf14af52a66eec" },

                // 亚空间审判者  Ch05Inquisitor_Psyker_unit（**女**，光头+下颌义体）-> PsykerFemale
                //   ★性别修正★ 原为 AdeptusMinistorumMale。
                //   苍白发青的皮肤、发白光的眼睛、近乎光头 —— 和模型 BCT_Inquisition_Mystic
                //   是同一套设计语言。B 档 cue=0。
                { "d1287134a3e64a4dbdae16b58d21bd8b",
                  "3c1cff3901824c0298ba4abe1801c807|05aca1a00dd4450da436696868650518|ac7139caf2544bc29e19a8f031635e63" },

                // 火杖行刑者  Ch04Chorda_Pyromancer_unit -> DecadenceFemale
                //   ★vanilla 自己的数据打架★：蓝图 Gender=Male，模型却是 BCT_Female_Chorda_Psyker
                //  （我在 bundle 里 grep 到了这个名字，头部贴图也是女性）。
                //   按**模型**为准取女性；苍白贵族女性，同时贴合 Chorda 王朝的宫廷背景。
                //   自带立绘 JungleWorldRebelOfficerMelee 的 Small 指向不在 locationlist 的 id、
                //   Half/Full 才是真 null —— 表现同样是占位符。B 档 cue=0。
                //   备选 PsykerMale 是兜帽+蓝光眼、性别模糊，正好绕开那个数据矛盾。
                { "638ab19bfae74bb99dacc93e7d6fe7f3",
                  "db248f8c0357439bb49480e13a998e6a|0114a2db302c45a9bc780593d0ec5134|db208877d55c43bba8f1b59613a8e857" },

                // 谕令·灵能军官  VC2_Astropath（男）-> ImperialNavyMale
                //   帝国海军军官装，照顾"军官"身份。B 档 cue=0（3 个佩戴单位都是预设）。
                //   备选 AdeptusMinistorumMale：兜帽+金色呼吸面罩，其实更贴"星语者"，
                //   而且模型 BCT_Male_Astropath 是苍白光头、和海军那张黑发青年对不太上。
                { "bc5ca9badb2042b48afb13c1829619b3",
                  "d03e6b0de6994d8f8b10a8ad16ebd94e|b8c150a212dc43b8ae2a580c0145fa20|3df3df7a73a544d79455a40e2dc1156a" },

                // ---- 下面两个**不需要** override：单位自带立绘且图正常 ----
                // 怒火·首席连射 -> 自带 ArbitratorClayton 1704807cd8944603b71331183ff36a1f
                //     A 档 cue=0、三尺寸完好、且是该单位的专属美术 —— 全库最优解，别动。
                // 铁律·政委军官 -> 自带 CommorraghCommissar_Portrait a2699823ca3140eba4a1445871d456c5
                //     与模型 BCT_Human_Male_CommorraghCommissar 同源美术（灰发+眼下蓝纹一致）。
                //     cue=13 但 dlg=0 bark=0，全集中在康莫拉两个杂鱼军官身上；
                //     换成建卡池 ComissarMale 反而会和**男主默认脸**撞图（逐字节同一张）。
                // 解析链第 1 步就命中了，加 override 也不会生效（override 只在自带的取不到时才查）。
            };

        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();
        /// <summary>取不到图的 key —— 不放进 Cache，因为 Cache 的判据是 != null，存 null 等于没存。</summary>
        private static readonly HashSet<string> Misses = new HashSet<string>();
        /// <summary>已 hold 的资源 -> hold 次数。必须计数：缓存自愈会重复 TryGetResource(hold:true)，
        /// 用 HashSet 的话 Cleanup 每个 id 只 Free 一次，计数单向漂移、本会话永远归不了零。</summary>
        private static readonly Dictionary<string, int> Held = new Dictionary<string, int>();
        private static FieldInfo _fPortrait;
        private static bool _fPortraitResolved;

        // ---------------------------------------------------------------- 主入口

        /// <summary>
        /// 输入 unitId，输出可画的 Sprite；拿不到返回 null。
        /// fallbackToEmpty=true 时，没有自己的立绘会退到 Empty_Portrait 的图。
        /// </summary>
        public static Sprite Get(string unitAssetId, PortraitSize size = PortraitSize.Small,
                                 bool fallbackToEmpty = true)
        {
            if (string.IsNullOrEmpty(unitAssetId)) return null;

            string key = unitAssetId + "#" + ((int)size) + (fallbackToEmpty ? "+f" : "");
            // ★ 负缓存 ★ Cache 里存 null 是没用的：下面的判据是 cached != null，
            // 于是"永远拿不到图"的条目每次重建列表都会重跑一整套蓝图+资源查找。
            // 单独记一份 Misses，命中就直接返回。
            if (Misses.Contains(key)) return null;
            Sprite cached;
            // Unity 的 == null 会同时命中"已销毁对象"，所以缓存失效可以自愈
            if (Cache.TryGetValue(key, out cached) && cached != null) return cached;

            Sprite s = null;
            try
            {
                // 1) 单位蓝图自己配的立绘（精英 pregen 走这条）
                BlueprintPortrait own = GetPortraitBlueprint(unitAssetId, ownOnly: true);
                if (own != null) s = SpriteOf(own, size);

                // 2) 没配 -> 查我们的借脸表（普通卫兵走这条）
                if (s == null)
                {
                    string sub;
                    if (UnitPortraitOverride.TryGetValue(unitAssetId, out sub) && !string.IsNullOrEmpty(sub))
                        s = FromPortraitBlueprint(PickNonClashing(sub), size);
                }
            }
            catch (Exception e) { Main.LogError("[立绘] 解析 " + unitAssetId + " 失败: " + e.Message); }

            // 3) 还是没有 -> Empty_Portrait
            if (s == null && fallbackToEmpty)
            {
                try { s = FromPortraitBlueprint(EmptyPortraitId, size); }
                catch (Exception e) { Main.LogError("[立绘] Empty_Portrait 兜底失败: " + e.Message); }
            }

            if (s != null) Cache[key] = s; else Misses.Add(key);
            return s;
        }

        /// <summary>该单位蓝图自己配了立绘吗（不算占位回退）。</summary>
        public static bool HasOwnPortrait(string unitAssetId)
        {
            return GetPortraitBlueprint(unitAssetId, ownOnly: true) != null;
        }

        /// <summary>
        /// 从候选链里挑第一个**不和主控角色撞脸**的立绘。
        ///
        /// 为什么需要：借脸表用的是原版建卡池那 30 张，玩家捏主角时也从同一批里挑 ——
        /// 恰好选中同一张，队伍框里就会出现两张一模一样的脸。
        /// 链里的候选是按"越靠前越合适"排的，所以只在真撞了才往后退。
        ///
        /// ★ 必须比对**图的 AssetId**，不能比对蓝图 GUID ★
        /// 全库 174 张可用立绘只对应 150 张不同的图，有 21 组是别名
        ///（*Navigator 系列 / RTArbitres* / RogueTraderMale|Female* 等指向同一张图）。
        /// 比 GUID 会漏掉"不同蓝图、同一张脸"这种撞法。
        ///
        /// 链里全部撞光时返回第一个 —— 与其显示空白，不如撞脸。
        /// </summary>
        private static string PickNonClashing(string chain)
        {
            if (string.IsNullOrEmpty(chain)) return chain;
            if (chain.IndexOf('|') < 0) return chain;      // 单件，没得挑

            var parts = chain.Split('|');
            string mine = MainCharacterPortraitImageId();
            if (string.IsNullOrEmpty(mine)) return parts[0].Trim();

            for (int i = 0; i < parts.Length; i++)
            {
                string id = parts[i].Trim();
                if (id.Length == 0) continue;
                string img = ImageIdOf(id);
                if (string.IsNullOrEmpty(img) || img != mine)
                {
                    if (i > 0) Main.Log("[立绘] " + parts[0].Trim().Substring(0, 8)
                                        + " 与主控角色撞脸，改用候选 #" + (i + 1) + " " + id.Substring(0, 8));
                    return id;
                }
            }
            return parts[0].Trim();
        }

        /// <summary>主控角色立绘的**图** AssetId（Small）。取不到返回 null。</summary>
        private static string MainCharacterPortraitImageId()
        {
            try
            {
                var g = Kingmaker.Game.Instance;
                var pl = g != null ? g.Player : null;
                var mc = pl != null ? pl.MainCharacterEntity : null;
                if (mc == null) return null;
                var pd = mc.Portrait;                 // PortraitData
                if (pd == null) return null;
                var link = pd.m_PortraitImage;
                return link != null ? link.AssetId : null;
            }
            catch { return null; }
        }

        /// <summary>立绘蓝图 -> 它 Small 那张图的 AssetId。</summary>
        private static string ImageIdOf(string portraitAssetId)
        {
            try
            {
                BlueprintPortrait p;
                try { p = ResourcesLibrary.TryGetBlueprint<BlueprintPortrait>(portraitAssetId); }
                catch { return null; }
                if (p == null || p.Data == null) return null;
                var link = p.Data.m_PortraitImage;
                return link != null ? link.AssetId : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 取单位的 BlueprintPortrait。
        /// ownOnly=true：只认蓝图上真配的，没配返回 null。
        /// ownOnly=false：没配就走 PortraitSafe（可能拿到空图的占位蓝图）。
        /// </summary>
        public static BlueprintPortrait GetPortraitBlueprint(string unitAssetId, bool ownOnly)
        {
            if (string.IsNullOrEmpty(unitAssetId)) return null;
            // ResourcesLibrary.TryGetBlueprint<T> 内部是**硬转换** (T)TryGetBlueprint(assetId)，
            // 不是 as —— GUID 指向的不是 BlueprintUnit 时会抛 InvalidCastException。
            // 这是 public 方法，不能把异常甩给 UI 层（一帧抛一次会刷爆日志）。
            BlueprintUnit bp;
            try { bp = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(unitAssetId); }
            catch (Exception e)
            {
                Main.LogError("[立绘] 蓝图 " + unitAssetId + " 取用失败: " + e.Message);
                return null;
            }
            if (bp == null) return null;

            if (!_fPortraitResolved)
            {
                _fPortraitResolved = true;
                _fPortrait = typeof(BlueprintUnit).GetField(
                    "m_Portrait", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_fPortrait == null)
                    Main.LogError("[立绘] BlueprintUnit.m_Portrait 字段没找到（游戏更新过？）");
            }

            if (_fPortrait != null)
            {
                BlueprintPortraitReference r = _fPortrait.GetValue(bp) as BlueprintPortraitReference;
                if (r != null && !r.IsEmpty())
                {
                    BlueprintPortrait own = r.Get();
                    if (own != null) return own;
                }
            }

            if (ownOnly) return null;
            try { return bp.PortraitSafe; } catch { return null; }
        }

        /// <summary>直接按 BlueprintPortrait 的 AssetId 取图（占位/兜底/自选立绘都走这里）。</summary>
        public static Sprite FromPortraitBlueprint(string portraitAssetId, PortraitSize size)
        {
            if (string.IsNullOrEmpty(portraitAssetId)) return null;
            BlueprintPortrait p = ResourcesLibrary.TryGetBlueprint<BlueprintPortrait>(portraitAssetId);
            return p == null ? null : SpriteOf(p, size);
        }

        // ---------------------------------------------------------------- 内部

        private static Sprite SpriteOf(BlueprintPortrait p, PortraitSize size)
        {
            PortraitData d = p.Data;
            if (d == null) return null;

            // 首选：拿 AssetId 自己 hold 住，避免缓存的 Sprite 之后被卸载
            SpriteLink link = LinkOf(d, size);
            if (link != null && !string.IsNullOrEmpty(link.AssetId))
            {
                Sprite s = ResourcesLibrary.TryGetResource<Sprite>(link.AssetId, true, true);
                if (s != null)
                {
                    // 计数而不是 Add：同一个 AssetId 可能被 hold 多次（缓存自愈会重来一遍）
                    int n; Held.TryGetValue(link.AssetId, out n); Held[link.AssetId] = n + 1;
                    return s;
                }
            }

            // 次选：自定义立绘（AssetId 为空，图从玩家 Portraits 目录读）只能走属性
            switch (size)
            {
                case PortraitSize.FullLength: return d.FullLengthPortrait;
                case PortraitSize.HalfLength: return d.HalfLengthPortrait;
                default:                      return d.SmallPortrait;
            }
        }

        private static SpriteLink LinkOf(PortraitData d, PortraitSize size)
        {
            switch (size)
            {
                case PortraitSize.FullLength: return d.m_FullLengthImage;
                case PortraitSize.HalfLength: return d.m_HalfLengthImage;
                default:                      return d.m_PortraitImage;
            }
        }

        // ---------------------------------------------------------------- 清理

        /// <summary>mod 被禁用/卸载时调用：把 hold 住的资源全部还回去，清缓存。</summary>
        public static void Cleanup()
        {
            // hold 了几次就 Free 几次 —— 计数不对称会让 HandleCounter 单向漂移、永不归零
            foreach (var kv in Held)
            {
                for (int i = 0; i < kv.Value; i++)
                {
                    try { ResourcesLibrary.FreeResourceRequest(kv.Key, true); }
                    catch { /* 卸载期出错不能再抛 */ }
                }
            }
            Held.Clear();
            Cache.Clear();
            Misses.Clear();
            _fPortrait = null;
            _fPortraitResolved = false;
        }

        // ---------------------------------------------------------------- IMGUI 画图

        /// <summary>
        /// 在 IMGUI 里画一个 Sprite（图集里的 Sprite 不能直接 GUI.DrawTexture，
        /// 必须按 textureRect 换算 UV）。返回是否真的画了。
        /// </summary>
        public static bool DrawSprite(Rect rect, Sprite s)
        {
            if (s == null) return false;
            Texture2D tex = s.texture;
            if (tex == null) return false;

            Rect tr = s.textureRect;
            Rect uv = new Rect(tr.x / tex.width, tr.y / tex.height,
                               tr.width / tex.width, tr.height / tex.height);
            GUI.DrawTextureWithTexCoords(rect, tex, uv);
            return true;
        }

        /// <summary>按原图宽高比缩放到 box 内并居中，再画。</summary>
        public static bool DrawSpriteFit(Rect box, Sprite s)
        {
            if (s == null) return false;
            Rect tr = s.textureRect;
            if (tr.width <= 0f || tr.height <= 0f) return false;

            float scale = Mathf.Min(box.width / tr.width, box.height / tr.height);
            float w = tr.width * scale, h = tr.height * scale;
            Rect fit = new Rect(box.x + (box.width - w) * 0.5f,
                                box.y + (box.height - h) * 0.5f, w, h);
            return DrawSprite(fit, s);
        }

        // ---------------------------------------------------------------- 诊断

        /// <summary>
        /// 逐个单位打日志：有没有自己的立绘、立绘蓝图名、Sprite 实际像素。
        /// 用来把"哪些单位真有立绘"这张表在运行时坐实。
        /// </summary>
        public static void DumpTable(IEnumerable<string> unitAssetIds)
        {
            foreach (string id in unitAssetIds)
            {
                string line = id + "  ";
                try
                {
                    BlueprintUnit u = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(id);
                    if (u == null) { Main.Log(line + "BlueprintUnit 解析失败"); continue; }
                    line += "unit=" + u.name + "  ";

                    BlueprintPortrait own = GetPortraitBlueprint(id, ownOnly: true);
                    line += own != null ? ("有立绘 portrait=" + own.name) : "无立绘";

                    Sprite s = Get(id, PortraitSize.Small, fallbackToEmpty: false);
                    if (s != null)
                        line += "  small=" + (int)s.textureRect.width + "x" + (int)s.textureRect.height;
                    else
                        line += "  small=null";
                }
                catch (Exception e) { line += "EXC " + e.Message; }
                Main.Log("[立绘表] " + line);
            }
        }
    }
}
