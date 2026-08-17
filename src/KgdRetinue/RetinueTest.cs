using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Root;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Formations;
using Kingmaker.UnitLogic;              // GetMechanicFeature / SnapToGrid
using Kingmaker.UnitLogic.Enums;        // MechanicsFeatureType
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Visual.Critters;   // FollowerSettings

namespace KgdRetinue
{
    /// <summary>
    /// 生成与运行时状态维护。
    ///
    /// v0.1.0（M2）相对 v0.0.x 的三处结构性改动：
    ///   1. 落点从 CrossSceneState 走，卫兵跨区域长期存活（不再每图重生）
    ///   2. 挂 UnitPartCompanion(ExCompanion) 以通过跨区域销毁闸门，
    ///      同时因为 ExCompanion 被 IsDirectlyControllable 的 companion 分支排除，
    ///      仍然是 AI 控制、不进队伍头像栏
    ///   3. 所有"读档即丢"的状态集中到 ApplyRuntimeState，由生命周期钩子重复调用
    /// </summary>
    public static class RetinueTest
    {
        public static int SpawnedCount { get { return RetinueRegistry.Count; } }

        public static void SpawnOne() { SpawnOne(-1, null, false); }

        /// <summary>
        /// 生成一个卫兵。
        /// archOverride &gt;= 0 时用指定分型（否则用面板选中的）；
        /// eliteOverride 非空时强制生成该精英（否则按 NextElite 决定）；
        /// skipCap 跳过数量上限（一键全测用）。
        /// </summary>
        public static BaseUnitEntity SpawnOne(int archOverride, ChainProbe.EliteDef eliteOverride, bool skipCap, bool forceNormal = false)
        {
            try
            {
                var game = Game.Instance;
                var leader = game != null && game.Player != null ? game.Player.MainCharacterEntity : null;
                if (leader == null) { Main.LogError("主角实体为空——请先进入游戏内。"); return null; }

                int archIdx = (archOverride >= 0) ? archOverride : Main.Settings.ArchetypeIndex;

                if (!skipCap)
                {
                    int tier = Archetypes.PlayerTier(leader);
                    // 上限三选一，优先级从高到低：
                    //   解除数量上限 > 利润因子闸（未被单独解除时）> 阶位数量上限
                    int cap;
                    string why;
                    if (Main.Settings.NoCountCap())
                    {
                        cap = 99; why = "（已解除数量上限）";
                    }
                    else if (Main.Settings.RecruitUsePfGate && !Main.Settings.NoPfGate())
                    {
                        cap = ProfitFactorGate.Unlocked();
                        why = "：" + ProfitFactorGate.Summary();
                    }
                    else
                    {
                        cap = Archetypes.GuardCountCap(tier);
                        why = "：玩家 T" + tier + " 最多 " + cap + " 名";
                    }

                    int have = RetinueRegistry.Count;
                    if (have >= cap)
                    {
                        Main.Log("招募上限已满" + why + "（当前 " + have + " 名）。"
                                 + (Main.Settings.RecruitUsePfGate && !Main.Settings.NoPfGate()
                                    ? "提升利润因子即可解锁更多；也可在面板改「每名所需利润因子」或勾选「解除利润因子限制 / 解除数量上限」。"
                                    : "可在面板勾选「解除数量上限」。"));
                        return null;
                    }
                }

                // 分型可以指定自己的单位蓝图 —— brain 跟着蓝图走，
                // 近战 build 必须配近战 brain，否则 AI 还是站着放枪
                var _arch0 = Archetypes.Get(archIdx);
                // 精英：一个分型可以有多个。NextElite 返回第一个还没生成的；
                // 身份靠 CustomPetName 标记，所以多个精英可以共用同一个蓝图。
                // forceNormal：一键全测用。SpawnUnit 是延迟入册的（要到下一次 Tick 才进 state），
                // 同一帧连续生成时 RetinueRegistry.All() 看不到刚生成的精英，
                // NextElite 会一直返回第一个 —— 结果普通卫兵那次又生成了一个精英。
                var _elite = forceNormal ? null : (eliteOverride ?? GearTool.NextElite(archIdx));
                string unitId = (_elite != null) ? _elite.UnitId
                              : ((_arch0 != null && !string.IsNullOrEmpty(_arch0.UnitId))
                                 ? _arch0.UnitId : Main.Settings.UnitAssetId);
                if (string.IsNullOrEmpty(unitId)) unitId = Main.Settings.UnitAssetId;
                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(unitId);
                if (bp == null)
                {
                    Main.LogError("找不到蓝图: " + unitId
                                  + "\n    若这是 DLC 蓝图，请确认对应 DLC 已在 Steam 里启用。");
                    return null;
                }

                // ── Plan A：落点固定为 CrossSceneState（= 存档里的 party.json）
                //    MainState 过图时会被 StashAreaState(dispose:true) 冻存到该区域的 json，
                //    回访时复活成重复卫兵 —— 这正是不能用 MainState 的原因。
                var state = game.Player.CrossSceneState;
                if (state == null) { Main.LogError("CrossSceneState 为 null。"); return null; }

                // 生成在队长脚下，不做任何几何计算 —— 解重叠交给后面的 SnapToGrid。
                // v0.0.9 自己算环形落点失败的根因：槽位弦长 2*1.8*sin15° = 0.93m
                // 小于网格边长 1.35m，被 ForcePlaceAboveGround 量化到同一格。
                var u = game.EntitySpawner.SpawnUnit(bp, leader.Position, Quaternion.identity, state);
                if (u == null) { Main.LogError("SpawnUnit 返回 null。"); return null; }

                // ★★ 顺序经 v0.1.1 代码审查修正 ★★
                // 与 v0.1.0 的区别：CombatGroup.Id 提到 Faction.Set **之前**。
                // 因为 Faction.Set 会同步触发 RestoreSharedInventory，而拦截补丁
                // 靠 CombatGroup.Id 认人 —— 标记设晚了补丁就认不出来。
                RetinueRegistry.BeginProtect(u);   // 双保险：标记之外再加临时白名单
                try
                {
                    // ① SetState 必须最先：UnitPartCompanion.cs:31 —— SetState(ExCompanion)
                    //    会把 CombatGroup.Id 覆写成随机 uuid，之后设的才作数
                    u.GetOrCreate<UnitPartCompanion>().SetState(CompanionState.ExCompanion);

                    // ② 身份标记（必须在 SetState 之后、Faction.Set 之前）
                    // 分型写进标记，跟着卫兵走（存档级持久）
                    u.CombatGroup.Id = RetinueRegistry.TagFor(archIdx);

                    // ②b 精英标记 —— 写进 CustomPetName（惰性字段），
                    //     这样多个精英可以共用同一个单位蓝图，不必一人一个独占蓝图
                    if (_elite != null)
                        RetinueRegistry.SetEliteTag(u, archIdx, GearTool.IndexOfElite(_arch0, _elite));

                    // ③ Faction.Set 必须在 SnapToGrid 之前（占格按阵营区分敌我）。
                    //    它会同步触发 HandleFactionChanged → RestoreSharedInventory，
                    //    由 InventoryPatch 拦下，卫兵装备才不会被倒进玩家仓库。
                    u.Faction.Set(BlueprintRoot.Instance.PlayerFaction);

                    // ④ 阵营变了但移动代理的 traversal provider 还缓存着旧阵营的敌我极性，
                    //    重建一次，否则本区域内卫兵会挤不过队友 / 穿过敌人
                    try { if (u.View != null && u.View.AgentASP != null) u.View.AgentASP.ResetBlocker(); } catch { }

                    // 顺序：先灌经验，再跑 ApplyRuntimeState（里面含按阶位升级）。
                    // v0.1.3 反了 —— 结果是卫兵以 0 经验跑一次升级只到 1 级，纯属白跑。
                    if (Main.Settings.AlignExperience) AlignExperience(u, leader);

                    ApplyRuntimeState(u, leader);
                }
                catch (Exception inner)
                {
                    // 回滚：半成品卫兵会永久留在 party.json 里，既找不到又删不掉
                    Main.LogError("生成中途失败，回滚: " + inner);
                    try
                    {
                        u.Remove<UnitPartFollowUnit>();
                        u.Remove<UnitPartCompanion>();
                        u.IsInGame = false;
                        Game.Instance.EntityDestroyer.Destroy(u);
                        Game.Instance.EntityDestroyer.Tick();
                    }
                    catch (Exception rb) { Main.LogError("回滚也失败了，请勿存档: " + rb.Message); }
                    return null;
                }
                finally { RetinueRegistry.EndProtect(u); }

                var before = u.Position;
                try { u.SnapToGrid(); } catch (Exception e) { Main.LogError("SnapToGrid: " + e.Message); }
                // SpawnUnit 是延迟入册的（EntitySpawnController 只 m_ToSpawn.Add，
                // 要到下一次 Tick 才进 state），所以这里 Count 恒少 1，显式 +1
                Main.Log("已生成 " + bp.name + "  落点 " + before + " -> " + u.Position
                         + "  uid=" + u.UniqueId + "  (在册约 " + (RetinueRegistry.Count + 1) + " 名)");
                DumpUnit(u, "spawn 后");
                return u;
            }
            catch (Exception e) { Main.LogError(e); }
            return null;
        }

        /// <summary>
        /// 所有「读档 / 过图后会丢失」的状态集中在这里。
        /// 招募时调一次，之后每次 OnAreaDidLoad 再调一次。
        ///
        /// 为什么会丢：这些 Part 全类零 [JsonProperty]，而序列化器是 OptIn
        /// （OptInContractResolver），存档里只剩一个空壳 {"$id":..,"$type":..}。
        /// </summary>
        public static void ApplyRuntimeState(BaseUnitEntity g, BaseUnitEntity leader)
        {
            if (g == null) return;

            // a) IsInGame —— SceneLoader.cs:1490 在区域卸载时无条件重置为
            //    Player.Party.Contains(...)，卫兵不在队伍里 ⇒ 变 false，必须自己置回
            try { g.IsInGame = true; } catch (Exception e) { Main.LogError("IsInGame: " + e.Message); }

            // a2) 死亡规则 —— 精英挂倒地豁免。
            //     必须每次过图重挂：PartMechanicFeatures 整类零 [JsonProperty]，
            //     OnPrePostLoad 强制 Initialize()，Retain 的计数器读档后归零。
            try
            {
                int _ai = RetinueRegistry.ArchetypeOf(g);
                var _arch = Archetypes.Get(_ai >= 0 ? _ai : Main.Settings.ArchetypeIndex);
                DeathRules.ApplyLifeRule(g, GearTool.EliteDefOf(g, _arch) != null);
            }
            catch (Exception e) { Main.LogError("死亡规则: " + e.Message); }

            // b) 自愈 —— ToyBox 的 Party Editor 可能改了 CompanionState。
            //    发现不是 ExCompanion 就重设，并补回被覆写的 CombatGroup.Id。
            try
            {
                var c = g.GetOrCreate<UnitPartCompanion>();
                if (c.State != CompanionState.ExCompanion)
                {
                    Main.Log("  自愈: CompanionState=" + c.State + " -> ExCompanion");
                    int keep = RetinueRegistry.ArchetypeOf(g);
                    c.SetState(CompanionState.ExCompanion);
                    g.CombatGroup.Id = (keep >= 0) ? RetinueRegistry.TagFor(keep) : RetinueRegistry.TagFor(Main.Settings.ArchetypeIndex);
                }
            }
            catch (Exception e) { Main.LogError("CompanionState 自愈: " + e.Message); }

            // c) 机制标志 —— PartMechanicFeatures 整类零 [JsonProperty]，
            //    且 OnPrePostLoad 强制 Initialize() 把每个 flag 换成全新 count=0 实例。
            //    ★ 这是 v0.0.x 一直存在但没测出来的 bug：读档后士气隔离只剩敌方半边。
            if (Main.Settings.IsolateMomentum)
            {
                try { g.GetMechanicFeature(MechanicsFeatureType.DeathAndTraumasDoesNotAffectMomentum).Retain(); }
                catch (Exception e) { Main.LogError("士气隔离: " + e.Message); }
            }
            // 保险：ForceAIControl 的检查早于 companion 分支，双保险防止变可直控
            try { g.GetMechanicFeature(MechanicsFeatureType.ForceAIControl).Retain(); }
            catch (Exception e) { Main.LogError("ForceAIControl: " + e.Message); }

            // c2) 换 brain —— 原版卫兵 brain 多为 UseOnlyListed=True，
            //     不换的话 career 链练出来的技能 AI 一条都不会考虑（v0.2.3 实测）
            try
            {
                int _ai = RetinueRegistry.ArchetypeOf(g);
                var _a = Archetypes.Get(_ai >= 0 ? _ai : Main.Settings.ArchetypeIndex);
                if (_a != null && !string.IsNullOrEmpty(_a.BrainId))
                {
                    var cur = g.Brain != null && g.Brain.Blueprint != null ? g.Brain.Blueprint.AssetGuid.ToString() : null;
                    if (!string.Equals(cur, _a.BrainId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (BrainTool.Apply(g, _a.BrainId))
                            Main.Log("  brain: " + (cur ?? "无") + " -> " + _a.BrainId);
                    }
                }
            }
            catch (Exception e) { Main.LogError("换 brain: " + e.Message); }

            // c3) 改名 —— 分型换了单位蓝图之后，卫兵会顶着蓝图自带的显示名，
            //      灵能分型用的 Inquisitor 蓝图更是直接顶着一个具名角色的名字。
            //      PartUnitDescription.Name 的优先级是
            //          PartPolymorphed ?? CustomName ?? Blueprint.CharacterName ?? ""
            //      （PartUnitDescription.cs:39），所以设了 CustomName 就压得住蓝图名。
            //      CustomName 是 [JsonProperty] 的**裸 string**，进存档但不产生 AssetId ——
            //      卸载 mod 后它只是个陌生字段，不会让反序列化失败。原版自己也走这条路
            //      给宠物改名（SetPetCustomNameGameCommand.cs:55）。
            try { ApplyName(g); } catch (Exception e) { Main.LogError("改名: " + e.Message); }

            // d) 按当前阶位补升级 —— 这是 v0.1.2 的核心改动。
            //    原版 Player.GainPartyExperience（Player.cs:1079-1084）会给 AllCharacters 里
            //    Master==null 且经验低于主角的单位发**全额**队伍经验，卫兵三个条件全中。
            //    但 ApplyChain 原来只在 SpawnOne 调一次 ⇒ 经验一路涨却没人消费，
            //    T1 招的卫兵到 T3 还卡在 15 级。挪到这里，每次区域加载按当前阶位重算上限再升。
            if (Main.Settings.AutoLevelUp && leader != null)
            {
                try
                {
                    int tier      = Archetypes.PlayerTier(leader);
                    bool unlocked = Main.Settings.NoLevelCap();
                    int lvCap     = unlocked ? 55 : Archetypes.GuardLevelCap(tier);
                    int depth     = unlocked ? 3  : Archetypes.ChainDepth(tier);
                    // ★ 用卫兵**自己**的分型，不是面板当前选中的那个。
                    //   v0.1.5 读 Settings.ArchetypeIndex，导致先用先锋生成、再切到灵能时，
                    //   那个先锋卫兵被灌进 Adept 链，攒出第四条 career path。
                    int ai        = RetinueRegistry.ArchetypeOf(g);
                    var arch      = Archetypes.Get(ai >= 0 ? ai : Main.Settings.ArchetypeIndex);
                    int lvBefore  = g.Progression.CharacterLevel;

                    // 精英可能有自己的职业链和加点方案（阿贝拉德先锋 vs 首席战士）
                    var ed = GearTool.EliteDefOf(g, arch);
                    string[] chainOv = (ed != null) ? ed.Chain : null;
                    string planOv    = (ed != null) ? ed.PlanName : null;

                    // 种族覆盖必须在升级之前 —— 种族门控的天赋要靠它才会出现在候选里。
                    // SetRace 是实体级（PartUnitProgression.cs:196），不动蓝图。
                    if (ed != null && !string.IsNullOrEmpty(ed.RaceId))
                    {
                        try
                        {
                            var rb = ResourcesLibrary.TryGetBlueprint<Kingmaker.UnitLogic.Progression.Features.BlueprintRace>(ed.RaceId);
                            if (rb == null) Main.LogError("  种族解析不到: " + ed.RaceId);
                            else if (g.Progression.Race != rb)
                            { g.Progression.SetRace(rb); Main.Log("  设种族: " + (string.IsNullOrEmpty(rb.Name) ? rb.name : rb.Name)); }
                        }
                        catch (Exception e2) { Main.LogError("  设种族失败: " + e2.Message); }
                    }

                    // 精英是毕业形态，生成时就顶到等级上限 —— 普通卫兵才按主角经验×比例起步
                    if (ed != null) Archetypes.GrantXpForLevel(g, lvCap);

                    // ★ 熟练度必须在升级**之前**授予 ★
                    // v0.8.1 实测：TrueSight_Feature（致命精准）的前置就是
                    // AeldariWeaponProficiency_Feature。之前熟练度在 d2) 里、也就是升完级才发，
                    // 于是升级当场前置不满足，方案里那条一直是"出现过但不可选"（B 类）。
                    // 发装备那一步还会再调一次，GrantFeatures 自带幂等，重复调用无副作用。
                    try { GearTool.GrantFeatures(g, arch); } catch (Exception e3) { Main.LogError("  预授熟练度: " + e3.Message); }

                    // 按段合成的方案（攻略只给要点、但各段在别的方案里有现成数据）
                    BuildPlans.Plan composed = null;
                    if (ed != null && ed.PlanSegments != null)
                    {
                        var ch = (chainOv != null && chainOv.Length > 0) ? chainOv : arch.Chain;
                        composed = BuildPlans.Compose(ed.Name, ch, ed.PlanSegments, ed.ExcludeFeatures);
                    }

                    int calls = Archetypes.ApplyChain(g, arch, lvCap, depth, true, false, chainOv, planOv,
                                                      ed != null ? ed.KeyTalents : null,
                                                      ed != null ? ed.AttrPriority : null,
                                                      // ★ 精英不继承分型的 preGrant ★
                                                      // v0.9.2 实战抓到：铁律·政委军官（非灵能的政委）
                                                      // 因为自己没写 preGrant，回落到了分型级的 Biomancy_Base
                                                      //（那是给谕令·灵能军官准备的），战斗日志里它真的在放
                                                      // Biomancy_IronArm。分型级只服务于没有 EliteDef 的普通卫兵。
                                                      ed != null ? ed.PreGrant : arch.PreGrant,
                                                      composed);
                    if (g.Progression.CharacterLevel != lvBefore)
                        Main.Log("  成长: lv" + lvBefore + " -> " + g.Progression.CharacterLevel
                                 + " (阶位T" + tier + " 上限" + lvCap + ", 调用 " + calls + " 次)");
                }
                catch (Exception e) { Main.LogError("补升级: " + e.Message); }
            }

            // d2) 装备 —— 必须在补升级**之后**：装备的 CanBeEquippedBy 可能有等级要求。
            //     精英发毕业套装、普通发玩家自配的那套，由 GearTool 内部判定；
            //     自带幂等判据，重复调用不会叠加。
            try
            {
                int ai2 = RetinueRegistry.ArchetypeOf(g);
                var arch2 = Archetypes.Get(ai2 >= 0 ? ai2 : Main.Settings.ArchetypeIndex);
                // 先授熟练度再发装备 —— 顺序反了的话动力甲/重武器一律装不上
                GearTool.GrantFeatures(g, arch2);
                GearTool.Equip(g, arch2);
            }
            catch (Exception e) { Main.LogError("装备: " + e.Message); }

            // e) 跟随 —— 先摘后挂：OnDetach 才会撤销队长侧的 AddIndependentFollower 登记
            if (leader != null && Main.Settings.AttachFollow)
            {
                try
                {
                    g.Remove<UnitPartFollowUnit>();
                    AttachFollow(g, leader);
                }
                catch (Exception e) { Main.LogError("Follow: " + e.Message); }
            }
        }

        /// <summary>
        /// 给卫兵一个自己的名字，压掉单位蓝图自带的显示名。
        ///
        /// 只在还没有自定义名时赋值 —— ApplyRuntimeState 每次过图/读档都会跑，
        /// 若每次重算编号，卫兵的名字会随遍历顺序来回跳。
        /// 编号取「现有卫兵里已用编号的最大值 + 1」，这样即使中途遣散过也不会撞号。
        /// </summary>
        public static void ApplyName(BaseUnitEntity g)
        {
            var d = g.GetOrCreate<PartUnitDescription>();

            int ai = RetinueRegistry.ArchetypeOf(g);
            var arch = Archetypes.Get(ai >= 0 ? ai : Main.Settings.ArchetypeIndex);

            // 精英：有 rank 就和普通卫兵走同一套「位阶·人名」，只是位阶固定不晋升
            // （精英一出场就是顶端，没有三档可爬）。人名共用同一个池子，所以
            // 精英和普通卫兵之间也不会重名 —— PickPersonName 是全名册去重的。
            // 没配 rank 的旧数据退回专属固定名，保证升级上来的存档不改名。
            var _ed = GearTool.EliteDefOf(g, arch);
            string _eRank = null;
            if (_ed != null)
                _eRank = (L.Current == L.EnGB && !string.IsNullOrEmpty(_ed.RankEn))
                       ? _ed.RankEn : _ed.Rank;
            if (!string.IsNullOrEmpty(_eRank))
            {
                string ecur = d.CustomName;
                if (!string.IsNullOrEmpty(ecur))
                {
                    string er, ep;
                    SplitRankPerson(ecur, out er, out ep);
                    // ★要求人名也在★ 否则「寂静之眼」这种位阶和旧固定名同字的会在这里
                    // 早退，永远补不上人名（旧名整串被当成位阶，person 是空的）
                    if (er == _eRank && !string.IsNullOrEmpty(ep)) return;
                    // 旧的固定专属名（「铁壁 · 先锋队长」）没有人名可继承，直接重发一个
                    if (!string.IsNullOrEmpty(ep) && IsOurRank(arch, er))
                    {
                        // 切语言时把人名也换成对应写法（两个池按下标对齐），
                        // 而不是重新抽一个 —— 那等于换了个人
                        string ren = _eRank + SEP + Archetypes.TranslatePerson(ep);
                        d.SetName(ren);
                        Main.Log("  改名(精英): " + ecur + " -> " + ren);
                        return;
                    }
                    if (ecur != _ed.Name) return;                     // 玩家手改过，不动
                }
                string en = _eRank + SEP + PickPersonName(g);
                d.SetName(en);
                Main.Log("  改名(精英): " + (g.Blueprint != null ? g.Blueprint.CharacterName : "?") + " -> " + en);
                return;
            }
            if (_ed != null && !string.IsNullOrEmpty(_ed.Name))
            {
                if (d.CustomName == _ed.Name) return;
                d.SetName(_ed.Name);
                Main.Log("  改名(精英): " + (g.Blueprint != null ? g.Blueprint.CharacterName : "?") + " -> " + _ed.Name);
                return;
            }

            // ---- 普通卫兵：<军衔>·<人名> ----
            // 军衔按卫兵自己的等级取（archetypes.json 的 guardNames，三档）；
            // 人名招募时从根级 guardNamePool 里挑一个当前没人用的，之后跟他一辈子。
            // 晋升只换军衔 —— 「近卫兵·凯尔顿」升成「近卫长·凯尔顿」，还是同一个人。
            // 死了或遣散了，那个人名重新可用：这个人没了，名字可以有新人继承。
            string rank = TierRank(arch, g);

            string cur = d.CustomName;
            if (!string.IsNullOrEmpty(cur))
            {
                string curRank, person;
                SplitRankPerson(cur, out curRank, out person);

                // 军衔和人名**都**已经是当前语言才早退。
                // 只看军衔的话，"先切英文、名字没跟上、再切回中文"这条路径会卡住：
                // 军衔看起来对，人名却还是另一种语言的写法。
                if (curRank == rank && person == Archetypes.TranslatePerson(person)) return;
                if (IsOurRank(arch, curRank) && !string.IsNullOrEmpty(person))
                {
                    string renamed = rank + SEP + Archetypes.TranslatePerson(person);
                    d.SetName(renamed);
                    Main.Log("  晋升: " + cur + " -> " + renamed);
                    return;
                }
                // 认不出来的名字 —— 可能是玩家手动改的，也可能是旧版本的「前缀·分型 编号」。
                // 旧版格式我们认得出（IsOurRank 会兜住 GuardNamePrefix），认不出的一律不动。
                return;
            }

            string name = rank + SEP + PickPersonName(g);
            d.SetName(name);
            Main.Log("  改名: " + (g.Blueprint != null ? g.Blueprint.CharacterName : "?") + " -> " + name);
        }

        /// <summary>
        /// 卫兵命名的兜底前缀。曾经是可配置项（Settings.GuardNamePrefix），
        /// v0.49.0 删掉了 —— 命名早已成体系（军衔取 archetypes.json 的 guardNames、
        /// 人名取 guardNamePool），这个前缀只在 json 缺 guardNames 时才用得到，
        /// 露在面板上纯属误导。留成常量，是为了让**旧存档里那些老格式的名字**
        /// 仍然能被 IsOurRank 认出来，从而正常晋升，而不是被当成"玩家手改的"不敢动。
        /// </summary>
        private const string LegacyPrefix = "卫兵";

        /// <summary>军衔和人名之间的分隔符。用「·」和精英名（如「铁壁 · 先锋队长」）保持一致观感。</summary>
        private const string SEP = "·";

        /// <summary>
        /// 从池子里挑一个**当前没有别的卫兵在用**的人名。
        /// 池子空或全被占：退回编号式，保证一定有个能区分的名字。
        /// </summary>
        private static string PickPersonName(BaseUnitEntity self)
        {
            var pool = Archetypes.NamePool;

            var used = new System.Collections.Generic.HashSet<string>();
            int maxNum = 0;
            foreach (var other in RetinueRegistry.All())
            {
                if (ReferenceEquals(other, self)) continue;
                string n = null;
                try { var od = other.GetOptional<PartUnitDescription>(); if (od != null) n = od.CustomName; } catch { }
                if (string.IsNullOrEmpty(n)) continue;
                string r, p;
                SplitRankPerson(n, out r, out p);
                if (!string.IsNullOrEmpty(p))
                {
                    used.Add(p);
                    int v;
                    SplitTrailingNumber(p, out v);
                    if (v > maxNum) maxNum = v;
                }
            }

            if (pool != null && pool.Length > 0)
            {
                // 从一个随机起点开始扫，避免每次都从池头拿、名字总是那几个
                int start = 0;
                try { start = UnityEngine.Random.Range(0, pool.Length); } catch { }
                for (int i = 0; i < pool.Length; i++)
                {
                    var cand = pool[(start + i) % pool.Length];
                    if (!string.IsNullOrEmpty(cand) && !used.Contains(cand)) return cand;
                }
            }
            return (maxNum + 1).ToString();      // 池子用光了才退回编号
        }

        /// <summary>拆「军衔·人名」。没有分隔符时整串当军衔、人名为空。</summary>
        private static void SplitRankPerson(string s, out string rank, out string person)
        {
            rank = s; person = null;
            if (string.IsNullOrEmpty(s)) return;
            int i = s.IndexOf(SEP, StringComparison.Ordinal);
            if (i <= 0) return;
            rank = s.Substring(0, i);
            person = s.Substring(i + SEP.Length);
        }

        /// <summary>该卫兵当前阶位对应的军衔。</summary>
        private static string TierRank(ChainProbe.Archetype arch, BaseUnitEntity g)
        {
            try
            {
                // 英文界面用 guardNames_en；没配就回落中文 —— 宁可一处没译，也不能空白
                var names = (L.Current == L.EnGB && arch != null
                             && arch.GuardNamesEn != null && arch.GuardNamesEn.Length > 0)
                          ? arch.GuardNamesEn : (arch != null ? arch.GuardNames : null);
                if (names != null && names.Length > 0)
                {
                    // 阶位按**卫兵自己的等级**推，不是玩家的 —— 军衔该跟着他自己的成长走
                    int lv = g.Progression != null ? g.Progression.CharacterLevel : 1;
                    int t = lv >= 36 ? 3 : (lv >= 16 ? 2 : 1);
                    int idx = t - 1;
                    if (idx >= names.Length) idx = names.Length - 1;
                    var s = names[idx];
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }

            string prefix = LegacyPrefix;
            string an = (arch != null && !string.IsNullOrEmpty(arch.Name)) ? arch.Name : "";
            return string.IsNullOrEmpty(an) ? prefix : prefix + SEP + an;
        }

        /// <summary>这个军衔是不是本分型认得的（三档之一、精英位阶，或旧版的前缀式命名）。</summary>
        private static bool IsOurRank(ChainProbe.Archetype arch, string rank)
        {
            if (string.IsNullOrEmpty(rank)) return false;
            try
            {
                // ★中英两套都要认★ 玩家中途切语言时，卫兵身上还挂着另一种语言的军衔。
                // 认不出来就会被当成"玩家手改的名字"而不敢改，晋升从此卡死。
                if (arch != null && arch.GuardNames != null)
                    foreach (var s in arch.GuardNames) if (s == rank) return true;
                if (arch != null && arch.GuardNamesEn != null)
                    foreach (var s in arch.GuardNamesEn) if (s == rank) return true;
                // 精英位阶也算 —— 否则改了 rank 之后老名字会被当成"玩家手改的"而不敢动
                if (arch != null && arch.Elites != null)
                    foreach (var e in arch.Elites)
                    {
                        if (e == null) continue;
                        if (!string.IsNullOrEmpty(e.Rank)   && e.Rank   == rank) return true;
                        if (!string.IsNullOrEmpty(e.RankEn) && e.RankEn == rank) return true;
                    }
            }
            catch { }
            try
            {
                if (rank.StartsWith(LegacyPrefix, StringComparison.Ordinal)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>拆掉结尾的" 123"，返回基名；num 输出那个数字（没有则 0）。</summary>
        private static string SplitTrailingNumber(string s, out int num)
        {
            num = 0;
            if (string.IsNullOrEmpty(s)) return s;
            int end = s.Length, start = end;
            while (start > 0 && s[start - 1] >= '0' && s[start - 1] <= '9') start--;
            if (start == end) return s;                       // 结尾不是数字
            int v;
            if (!int.TryParse(s.Substring(start, end - start), out v)) return s;
            num = v;
            return s.Substring(0, start).TrimEnd();
        }

        /// <summary>
        /// 原版跟随系统。FollowerSettings / FormationPersonalSettings 都是普通
        /// [Serializable] C# 类，运行时 new 即可，不产生 AssetId ⇒ 零存档足迹。
        /// 代价：读档/过图后必须重挂（原版 MakeUnitFollowUnit 也是这么做的）。
        /// </summary>
        public static void AttachFollow(BaseUnitEntity guard, BaseUnitEntity leader)
        {
            var personal = new FormationPersonalSettings
            {
                m_Offset = new Vector2(2f, -2f),   // X=队长右方, Y=队长前方（负=后方）
                m_RepathDistance = 4f,
                m_LookAngleRandomSpread = 90f,
            };
            guard.GetOrCreate<UnitPartFollowUnit>()
                 .Init(leader, new FollowerSettings(personal));
        }

        /// <summary>
        /// 经验对齐：从主角实时经验派生，mod 不自己记账 ⇒ 不存在第二个真值，
        /// 不可能与 ToyBox 失同步（BodyGuard 的 issue #11 就是自建 XP 账本导致的）。
        /// </summary>
        /// <summary>
        /// 新招募卫兵的经验起点。
        ///
        /// v0.1.4 修正一个双重缩放的 bug：PartUnitProgression.AdvanceExperienceTo 内部是
        ///     int num = targetExp - Experience;  if (num > 0) GainExperience(num, ...);
        /// 而 XpPatch 拦的正是 GainExperience。所以旧版"先自己乘 0.8 算目标值、
        /// 再被补丁对增量乘一次 0.8" ⇒ 实际 0.64 倍（实测 68639 -> 54911）。
        ///
        /// 现在这里**按主角全额**推进，缩放**只由 XpPatch 做一次**，
        /// 净结果仍是 ratio × 主角经验，但不会再叠加。
        /// 升级不在这里做 —— 交给 ApplyRuntimeState，那样过图/读档也会按当前阶位补。
        /// </summary>
        public static void AlignExperience(BaseUnitEntity guard, BaseUnitEntity leader)
        {
            try
            {
                int rtXp = leader.Progression.Experience;
                int before = guard.Progression.Experience;

                guard.Progression.AdvanceExperienceTo(rtXp, false);

                float ratio;
                if (!float.TryParse(Main.Settings.XpRatio, out ratio)) ratio = 0.8f;
                Main.Log("经验起点: 主角 lv" + leader.Progression.CharacterLevel + " xp=" + rtXp
                         + (Main.Settings.ScaleGuardXp ? "  x" + ratio + "(由 XpPatch 缩放)" : "  (未缩放)")
                         + " => 卫兵 xp " + before + " -> " + guard.Progression.Experience);
            }
            catch (Exception e) { Main.LogError("经验起点设定失败: " + e); }
        }
        /// <summary>
        /// 调试：直接给在册卫兵发经验。
        ///
        /// 走的是 PartUnitProgression.GainExperience —— 和原版战斗结算
        /// （Player.GainPartyExperience 内部逐个调的就是它）**完全同一条路径**，
        /// 所以 XpPatch 的缩放会照常生效，能真实验证比例。
        /// 好处是不碰队友的经验，也不需要真打一场。
        /// </summary>
        public static void GrantXp(int amount)
        {
            var list = RetinueRegistry.All();
            if (list.Count == 0) { Main.Log("没有在册卫兵。"); return; }
            if (amount <= 0) { Main.Log("经验数必须为正。"); return; }

            float ratio;
            if (!float.TryParse(Main.Settings.XpRatio, out ratio)) ratio = 0.8f;
            Main.Log("=== 发经验 " + amount + " 点"
                     + (Main.Settings.ScaleGuardXp ? "（预期实收 " + (int)(amount * ratio) + " = x" + ratio + "）" : "（未缩放）")
                     + " ===");

            foreach (var g in list)
            {
                try
                {
                    int before = g.Progression.Experience;
                    int lvBefore = g.Progression.CharacterLevel;
                    g.Progression.GainExperience(amount, false);
                    int got = g.Progression.Experience - before;
                    Main.Log("  " + (g.Blueprint != null ? g.Blueprint.name : "?")
                             + "  xp " + before + " -> " + g.Progression.Experience + " (实收 " + got + ")"
                             + "  lv" + lvBefore + " (等级要点【立即结算成长】才会涨)");
                }
                catch (Exception e) { Main.LogError("发经验失败: " + e.Message); }
            }
        }

        /// <summary>
        /// 调试：立即跑一遍 ApplyRuntimeState —— 等价于"过了一次图"。
        /// 用来在原地验证成长结算，不用真的走出区域。
        /// </summary>
        public static void ForceGrowth()
        {
            var game = Game.Instance;
            var leader = game != null && game.Player != null ? game.Player.MainCharacterEntity : null;
            var list = RetinueRegistry.All();
            if (list.Count == 0) { Main.Log("没有在册卫兵。"); return; }

            Main.Log("=== 手动结算成长（等价于过一次图）===");
            foreach (var g in list)
            {
                try
                {
                    int lvBefore = g.Progression.CharacterLevel;
                    ApplyRuntimeState(g, leader);
                    if (g.Progression.CharacterLevel == lvBefore)
                        Main.Log("  " + (g.Blueprint != null ? g.Blueprint.name : "?")
                                 + " 等级未变（lv" + lvBefore + "，xp=" + g.Progression.Experience
                                 + "），说明经验还没够下一级或已到阶位上限");
                }
                catch (Exception e) { Main.LogError("结算失败: " + e.Message); }
            }
        }

        /// <summary>
        /// 清掉所有卫兵的自定义名再重新编号 —— 改了前缀之后用。
        /// 必须**先全部清空再逐个赋名**：ApplyName 靠扫描其他卫兵的已有编号取最大值，
        /// 边清边赋会读到上一轮的旧名字，编号就接着旧的往上爬了。
        /// </summary>
        public static int RenameAll()
        {
            var list = RetinueRegistry.All();
            if (list.Count == 0) { Main.Log("没有在册卫兵。"); return 0; }

            foreach (var g in list)
            {
                try { g.GetOrCreate<PartUnitDescription>().SetName(null); } catch { }
            }
            Main.Log("=== 重新命名 " + list.Count + " 名卫兵 ===");
            foreach (var g in list)
            {
                try { ApplyName(g); } catch (Exception e) { Main.LogError("改名失败: " + e.Message); }
            }
            return list.Count;
        }

        /// <summary>
        /// 用**游戏原生的**背包界面打开一个卫兵 —— 装配 UI 白拿，不用自己画。
        ///
        /// 原理照抄 ServiceWindowsVM.cs:229-243 的两步：
        ///   ① SelectionCharacter.SetSelected(unit, force:true, forceFullScreenState:true)
        ///      —— 该行**没有队伍成员过滤**（ServiceWindowsVM.cs:247-253），所以能指向卫兵
        ///   ② EventBus 发 HandleOpenInventory()，窗口对"当前选中单位"生效
        /// CharInfoPageType 里没有背包页（只有 Summary/Features/... 六项），
        /// 背包是独立的服务窗口，所以走 HandleOpenInventory 而不是 HandleOpenCharacterInfoPage。
        ///
        /// 渲染得好不好只能实测 —— 头像栏里没有卫兵，某些 VM 可能假定单位在队伍里。
        /// </summary>
        /// <summary>
        /// 测试用：把在册卫兵打到 0 血，走原版的生死判定。
        ///
        /// 为什么要有它：死亡规则（普通永久死亡 / 精英倒地）只有真死一次才验得了，
        /// 而在战斗里精确打死某一个卫兵既慢又不可控。这里直接调原版自己的两步：
        ///     Health.SetHitPointsLeft(0)
        ///     UnitLifeController.ForceTickOnUnit(unit)   ← 它内部就是 CalculateLifeState + SetLifeState
        /// 所以走的是**和真实战斗完全同一条**判定路径，不是模拟。
        ///
        /// which: "normal" 只打普通卫兵，"elite" 只打精英，其它值打列表里第一个。
        /// </summary>
        public static void TestKill(string which)
        {
            try
            {
                var list = RetinueRegistry.All();
                if (list == null || list.Count == 0) { Main.Log("[死亡测试] 没有在册卫兵。"); return; }

                BaseUnitEntity target = null;
                foreach (var g in list)
                {
                    bool isElite = false;
                    try
                    {
                        int ai = RetinueRegistry.ArchetypeOf(g);
                        var arch = Archetypes.Get(ai >= 0 ? ai : 0);
                        isElite = GearTool.EliteDefOf(g, arch) != null;
                    }
                    catch { }
                    if (which == "elite" && !isElite) continue;
                    if (which == "normal" && isElite) continue;
                    target = g; break;
                }
                if (target == null)
                {
                    Main.Log("[死亡测试] 找不到" + (which == "elite" ? "精英" : which == "normal" ? "普通" : "") + "卫兵。");
                    return;
                }

                string name = target.CharacterName;
                try
                {
                    var d = target.GetOptional<PartUnitDescription>();
                    if (d != null && !string.IsNullOrEmpty(d.CustomName)) name = d.CustomName;
                }
                catch { }

                bool downedFlag = false;
                try { downedFlag = target.Features.UnconsciousOnZeroHealth.Value; } catch { }
                int before = RetinueRegistry.Count;

                Main.Log("[死亡测试] 目标 " + name + "　倒地豁免=" + (downedFlag ? "有" : "无")
                         + "　名册 " + before + " 名。开始打到 0 血……");

                target.Health.SetHitPointsLeft(0);
                Kingmaker.Controllers.Units.UnitLifeController.ForceTickOnUnit(target);

                string state = "?";
                try { state = target.LifeState.State.ToString(); } catch { }
                Main.Log("[死亡测试] 结果：生命状态 = <b>" + state + "</b>"
                         + "（Dead=永久死亡  Unconscious=倒地可救）"
                         + "\n    名册人数会在两帧后更新（RemoveOne 是延迟销毁的），"
                         + "再点一次【Dump 状态】看最终值。");
            }
            catch (Exception e) { Main.LogError("[死亡测试] 失败: " + e); }
        }

        public static void OpenNativePanel()
        {
            try
            {
                var list = RetinueRegistry.All();
                if (list.Count == 0) { Main.Log("没有在册卫兵。"); return; }
                var target = list[0];

                Game.Instance.SelectionCharacter.SetSelected(target, force: true, forceFullScreenState: true);
                Kingmaker.PubSubSystem.Core.EventBus.RaiseEvent<Kingmaker.PubSubSystem.INewServiceWindowUIHandler>(
                    h => h.HandleOpenInventory());

                Main.Log("已请求用原生背包界面打开: " + target.CharacterName
                         + "\n    若界面没开、显示的是主角、或一片空白，说明原生界面不接受非队伍单位，"
                         + "回退用 UMM 面板装配。");
            }
            catch (Exception e) { Main.LogError("打开原生面板失败: " + e.Message); }
        }

        public static bool IsGuard(BaseUnitEntity u) { return RetinueRegistry.IsGuard(u); }

        public static string GuardStates() { return RetinueRegistry.Describe(); }

        public static void DespawnAll() { RetinueRegistry.DismissAll(); }

        public static void DumpState()
        {
            try
            {
                var list = RetinueRegistry.All();
                Main.Log("=== 在册卫兵 " + list.Count + " 名 ===");
                foreach (var u in list) DumpUnit(u, "  ");
            }
            catch (Exception e) { Main.LogError(e); }
        }

        /// <summary>列出所有已装备的物品 —— 用来查清"血量为什么比预期低"。</summary>
        private static string DescribeGear(BaseUnitEntity u)
        {
            try
            {
                var body = u.Body;
                if (body == null) return "无 Body";
                var names = new List<string>();
                foreach (var slot in body.EquipmentSlots)
                {
                    if (slot == null) continue;
                    var it = slot.MaybeItem;
                    if (it != null) names.Add(it.Blueprint != null ? it.Blueprint.name : "?");
                }
                return names.Count == 0 ? "空（这就是血量偏低的原因）" : names.Count + " 件: " + string.Join(", ", names);
            }
            catch (Exception e) { return "读取失败:" + e.GetType().Name; }
        }

        /// <summary>属性一览 —— 用来验证加点方案有没有真的落到属性上。</summary>
        public static string StatsLine(BaseUnitEntity u) { return DescribeStats(u); }
        public static string GearLine(BaseUnitEntity u) { return DescribeGear(u); }

        private static string DescribeStats(BaseUnitEntity u)
        {
            try
            {
                var sc = u.Stats;
                if (sc == null) return "无 Stats";
                var parts = new List<string>();
                var want = new[]
                {
                    Kingmaker.EntitySystem.Stats.Base.StatType.WarhammerStrength,
                    Kingmaker.EntitySystem.Stats.Base.StatType.WarhammerAgility,
                    Kingmaker.EntitySystem.Stats.Base.StatType.WarhammerToughness,
                    Kingmaker.EntitySystem.Stats.Base.StatType.WarhammerIntelligence,
                    Kingmaker.EntitySystem.Stats.Base.StatType.WarhammerPerception,
                    Kingmaker.EntitySystem.Stats.Base.StatType.WarhammerWillpower,
                    Kingmaker.EntitySystem.Stats.Base.StatType.WarhammerFellowship,
                    Kingmaker.EntitySystem.Stats.Base.StatType.WarhammerBallisticSkill,
                    Kingmaker.EntitySystem.Stats.Base.StatType.WarhammerWeaponSkill,
                };
                var label = new[] { "力量", "敏捷", "耐力", "智力", "感知", "意志", "魅力", "弹道", "武技" };
                for (int i = 0; i < want.Length; i++)
                {
                    try
                    {
                        var st = sc.GetStat(want[i]);
                        if (st != null) parts.Add(label[i] + st.ModifiedValue);
                    }
                    catch { }
                }
                return parts.Count == 0 ? "读不到" : string.Join(" ", parts.ToArray());
            }
            catch (Exception e) { return "异常:" + e.GetType().Name; }
        }

        private static void DumpUnit(BaseUnitEntity u, string prefix)
        {
            try
            {
                var follow = u.GetOptional<UnitPartFollowUnit>();
                var comp   = u.GetOptional<UnitPartCompanion>();
                // v0.1.0 用 ReferenceEquals(Collection, Player.Inventory) 判独立背包，
                // 那个判据恒为 true（EnsureOwn 之后 Collection 一定是新建的空集合），
                // 是假阴性。改看"卫兵背包里还有几件东西" —— 装备被倒走时这里会变 0。
                int invCount = -1;
                try { invCount = u.Inventory.Collection.Items.Count; } catch { }

                Main.Log(
                    prefix + " name=" + (u.Blueprint != null ? u.Blueprint.name : "?") + " uid=" + u.UniqueId + "\n" +
                    "    faction=" + (u.Faction != null && u.Faction.Blueprint != null ? u.Faction.Blueprint.name : "?") +
                    "  combatGroup=" + (u.CombatGroup != null ? u.CombatGroup.Id : "?") +
                    "  companion=" + (comp != null ? comp.State.ToString() : "无") + "\n" +
                    "    IsDirectlyControllable=" + u.IsDirectlyControllable +
                    "  IsInPlayerParty=" + u.IsInPlayerParty +
                    "  IsInGame=" + u.IsInGame + "\n" +
                    "    背包件数=" + invCount + "  (0 表示装备被倒进玩家仓库了，是 bug)" + "\n" +
                    "    装备=" + DescribeGear(u) + "\n" +
                    "    brain=" + (u.Brain != null && u.Brain.Blueprint != null ? u.Brain.Blueprint.name : "?") +
                    "  follow=" + (follow == null ? "无" : "有") +
                    "  level=" + u.Progression.CharacterLevel + "  exp=" + u.Progression.Experience + "\n" +
                    "    属性=" + DescribeStats(u) + "\n" +
                    "    facts=" + (u.Facts != null ? u.Facts.List.Count : -1));
            }
            catch (Exception e) { Main.LogError("Dump 失败: " + e.Message); }
        }
    }
}