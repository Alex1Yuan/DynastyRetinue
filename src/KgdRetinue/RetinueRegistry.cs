using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;   // Entity
using Kingmaker.UnitLogic.Parts;

namespace KgdRetinue
{
    /// <summary>
    /// 卫兵身份层。M2 的第一块 —— **先建删除键，再建创建键**。
    ///
    /// 从 v0.1.0 起卫兵进 Player.CrossSceneState（= 存档里的 party.json），
    /// 实体本体跨区域长期存活。所以在生成第一个持久卫兵之前，必须先有可靠的遣散手段，
    /// 否则测试过程中产生的存档全部无法清理。
    ///
    /// 身份标记用 PartCombatGroup.m_Id：它是 [JsonProperty] 的裸 string
    /// （PartCombatGroup.cs:27-28），进存档、不产生 AssetId、卸载 mod 后
    /// 只是一个陌生字符串，不会让反序列化失败。
    /// 用 StartsWith 而不是 == ，给将来的分型（"kgd.guard.sniper"）留口。
    /// </summary>
    public static class RetinueRegistry
    {
        public const string GuardTag = "kgd.guard";

        // 招募过程中 CombatGroup.Id 还没设上（或被 SetState 覆写），IsGuard 认不出来，
        // 而 RestoreSharedInventory 恰恰在那个窗口里触发。用临时白名单兜住这段。
        private static readonly HashSet<string> Protecting = new HashSet<string>(StringComparer.Ordinal);

        public static void BeginProtect(BaseUnitEntity u)
        {
            try { if (u != null && u.UniqueId != null) Protecting.Add(u.UniqueId); } catch { }
        }

        public static void EndProtect(BaseUnitEntity u)
        {
            try { if (u != null && u.UniqueId != null) Protecting.Remove(u.UniqueId); } catch { }
        }

        /// <summary>已在册的卫兵，或正处于招募/自愈窗口中的单位。</summary>
        public static bool IsProtected(BaseUnitEntity u)
        {
            if (u == null) return false;
            if (IsGuard(u)) return true;
            try { return u.UniqueId != null && Protecting.Contains(u.UniqueId); } catch { return false; }
        }

        /// <summary>
        /// 所有卫兵共用同一个 CombatGroup.Id。
        ///
        /// v0.1.6 曾把分型编号写进 Id（"kgd.guard.2"）来持久化分型，
        /// 结果四个分型 = 四个只有一人的战斗组，而 AI 的敌人列表来自
        /// CombatGroup.Memory.Enemies —— 新建单人组的记忆是空的，
        /// **卫兵找不到敌人，直接结束回合**（v0.3.1 实测：不攻击也不移动）。
        /// 分型改成从它自己的 career path 反推，见 ArchetypeOf。
        /// </summary>
        public static string TagFor(int archetypeIndex)
        {
            return GuardTag;
        }

        /// <summary>
        /// 从卫兵已有的 career path 反推它的分型 —— 不需要额外存储，
        /// 而且 career path 本身就是随存档持久化的。
        /// 匹配规则：分型链的第一段（T1）能在卫兵的 path 列表里找到就算命中；
        /// 多个分型 T1 相同时（比如狙击/连射都是 Soldier）再比第二段。
        /// 认不出返回 -1，调用方回退到面板当前选中的分型。
        /// </summary>
        /// <summary>
        /// 精英身份标记，写在 PartUnitDescription.CustomPetName 里。
        ///
        /// 为什么用这个字段：它是 [JsonProperty] 的裸 string（PartUnitDescription.cs:23），
        /// 进存档、不产生 AssetId、卸载 mod 后只是个陌生字符串。
        /// 而它的三个消费方（SaveManager.cs:1620 / UnitPartPetOwner.cs:159-161）
        /// **全都要求单位拥有宠物** —— 卫兵没有宠物，所以写在这里完全惰性，不影响任何显示。
        ///
        /// 这样精英身份就不再依赖"每个精英一个独占蓝图"，
        /// 七个精英可以共用同一个 1 级创角模板。
        /// </summary>
        public const string EliteTagPrefix = "kgd.e:";

        public static void SetEliteTag(BaseUnitEntity u, int archIndex, int eliteIndex)
        {
            try
            {
                var d = u.GetOrCreate<PartUnitDescription>();
                d.CustomPetName = EliteTagPrefix + archIndex + ":" + eliteIndex;
            }
            catch { }
        }

        /// <summary>读回 (分型下标, 精英下标)；没有标记返回 (-1,-1)。</summary>
        public static void GetEliteTag(BaseUnitEntity u, out int archIndex, out int eliteIndex)
        {
            archIndex = -1; eliteIndex = -1;
            try
            {
                var d = u.GetOptional<PartUnitDescription>();
                var s = d != null ? d.CustomPetName : null;
                if (string.IsNullOrEmpty(s) || !s.StartsWith(EliteTagPrefix, StringComparison.Ordinal)) return;
                var parts = s.Substring(EliteTagPrefix.Length).Split(':');
                if (parts.Length != 2) return;
                int a, e;
                if (int.TryParse(parts[0], out a) && int.TryParse(parts[1], out e))
                { archIndex = a; eliteIndex = e; }
            }
            catch { }
        }

        public static int ArchetypeOf(BaseUnitEntity u)
        {
            try
            {
                if (u == null || u.Progression == null) return -1;
                var archs = Archetypes.All;

                // ① 精英标记最优先 —— 它是我们显式写进去的，比任何推断都可靠，
                //    而且不要求"每个精英一个独占蓝图"
                int ta, te;
                GetEliteTag(u, out ta, out te);
                if (ta >= 0 && ta < archs.Length) return ta;

                // ② 再按单位蓝图匹配（旧存档里没有标记的卫兵靠这条）
                //    v0.4.2 踩到的坑：灵能的 eliteUnit 用了 FighterPsykerQA_lvl15，
                //    它自带 Fighter 路线，而 Fighter 正是先锋链的第一段 ⇒
                //    路线推断把灵能精英认成了先锋，名字变成「卫兵·先锋」。
                string bpGuid = null;
                try
                {
                    var bp = u.OriginalBlueprint ?? u.Blueprint;
                    if (bp != null) bpGuid = bp.AssetGuid.ToString();
                }
                catch { }
                if (!string.IsNullOrEmpty(bpGuid))
                    for (int i = 0; i < archs.Length; i++)
                    {
                        // ★ 精英列表要先查 ★ v0.5.0 的 bug：多精英重构后 EliteUnitId 已废弃，
                        //   这里却还只查它 ⇒ 精英蓝图匹配不上 ⇒ 回退到路线推断 ⇒
                        //   海因里希预设自带 Fighter，被认成近战分型的普通卫兵。
                        if (archs[i].Elites != null)
                            foreach (var d in archs[i].Elites)
                                if (d != null && string.Equals(d.UnitId, bpGuid, StringComparison.OrdinalIgnoreCase))
                                    return i;
                        if (string.Equals(archs[i].EliteUnitId, bpGuid, StringComparison.OrdinalIgnoreCase)) return i;
                        if (string.Equals(archs[i].UnitId, bpGuid, StringComparison.OrdinalIgnoreCase)) return i;
                    }

                // ② 蓝图认不出（旧存档里的卫兵、或模板改过）才回退到职业路线推断
                var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cp in u.Progression.AllCareerPaths)
                {
                    if (cp.Blueprint == null) continue;
                    owned.Add(cp.Blueprint.AssetGuid.ToString());
                }
                if (owned.Count == 0) return -1;

                int best = -1, bestScore = 0;
                for (int i = 0; i < archs.Length; i++)
                {
                    var chain = archs[i].Chain;
                    if (chain == null || chain.Length == 0) continue;
                    int score = 0;
                    for (int k = 0; k < chain.Length; k++) if (owned.Contains(chain[k])) score++;
                    // 要求至少 T1 命中，且取匹配段数最多的那个
                    if (score > bestScore && owned.Contains(chain[0])) { bestScore = score; best = i; }
                }
                return best;
            }
            catch { return -1; }
        }

        public static bool IsGuard(BaseUnitEntity u)
        {
            if (u == null) return false;
            try
            {
                var cg = u.CombatGroup;
                var id = (cg != null) ? cg.Id : null;
                return id != null && id.StartsWith(GuardTag, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>
        /// 同时扫 CrossSceneState 和当前区域的 MainState。
        /// 前者是新方案的落点，后者兜底旧存档里遗留的卫兵（v0.0.x 时代生成的）。
        /// </summary>
        private static IEnumerable<SceneEntitiesState> States()
        {
            var g = Game.Instance;
            if (g == null) yield break;

            SceneEntitiesState cross = null;
            try { cross = g.Player != null ? g.Player.CrossSceneState : null; } catch { }
            if (cross != null) yield return cross;

            SceneEntitiesState main = null;
            try { main = g.State != null && g.State.LoadedAreaState != null ? g.State.LoadedAreaState.MainState : null; } catch { }
            if (main != null && !ReferenceEquals(main, cross)) yield return main;
        }

        /// <summary>返回快照列表 —— 调用方经常要一边遍历一边销毁，不能给惰性序列。</summary>
        public static List<BaseUnitEntity> All()
        {
            var result = new List<BaseUnitEntity>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var st in States())
            {
                List<Entity> snapshot;
                try { snapshot = st.AllEntityData != null ? st.AllEntityData.ToList() : null; }
                catch { continue; }
                if (snapshot == null) continue;

                foreach (var e in snapshot)
                {
                    var b = e as BaseUnitEntity;
                    if (b == null || !IsGuard(b)) continue;
                    string uid;
                    try { uid = b.UniqueId; } catch { continue; }
                    if (uid != null && seen.Add(uid)) result.Add(b);
                }
            }
            return result;
        }

        public static int Count
        {
            get { try { return All().Count; } catch { return 0; } }
        }

        /// <summary>
        /// 遣散全部。这是玩家在「禁用 mod / 禁用 DLC / 存档前清理」时的唯一出口，
        /// 必须比生成逻辑更可靠 —— 每一步单独 try/catch，一个失败不能拖垮其余。
        /// </summary>
        /// <summary>
        /// 遣散全部。
        ///
        /// v0.1.1 修复 blocker：原来的实现**一个都删不掉**，而且会把身份标记一起毁掉。
        /// 根因（EntityDestructionController.cs:128 / :151）：
        ///   PerformDestroy 里 `Faction.IsPlayer && !IsPet && !summon && TryUnrecruit(u)`
        ///   → TryUnrecruit 见到 UnitPartCompanion 就返回 true
        ///   → PerformDestroy 直接 return，RemoveEntityData / Dispose 全部跳过
        ///   → 而 TryUnrecruit 内部还会 SetState(ExCompanion)，把 CombatGroup.Id
        ///     覆写成随机 uuid ⇒ IsGuard() 从此返回 false ⇒ mod 再也找不到这些卫兵
        /// 讽刺的是，正是"让卫兵跨区域存活"的那个 UnitPartCompanion，
        /// 同时让它无法被删除 —— 存活闸门和删除闸门用的是同一个谓词。
        ///
        /// 修法：Destroy 之前先摘掉 UnitPartCompanion，TryUnrecruit 就会返回 false。
        /// 并且结束后**复查**而不是自报成功 —— 原来的日志是会骗人的。
        /// </summary>
        /// <summary>
        /// 把**一名**卫兵移出名册并销毁。给"普通卫兵永久死亡"用。
        ///
        /// 拆解顺序和 DismissAll 一致，两处都不能省：
        ///   UnitPartFollowUnit  —— OnDetach 才会撤销队长侧的 AddIndependentFollower 登记
        ///   UnitPartCompanion   —— 不摘的话 TryUnrecruit 会取消销毁
        /// 销毁**延迟两帧**：死亡事件是在伤害结算途中发出来的，
        /// 当场销毁会打断原版的死亡演出/掉落流水线。
        /// </summary>
        public static void RemoveOne(BaseUnitEntity g)
        {
            if (g == null) return;
            try
            {
                // 先摘掉身份标记 —— 这一步立刻生效，名额当场释放，
                // 不用等销毁完成（销毁是延迟的）。
                try { var cg = g.CombatGroup; if (cg != null) cg.Id = "kgd_dead_" + Guid.NewGuid().ToString("N").Substring(0, 8); }
                catch { }
                try { g.Remove<UnitPartFollowUnit>(); } catch { }
                try { g.Remove<UnitPartCompanion>(); } catch { }
            }
            catch (Exception e) { Main.LogError("[名册] 拆解失败: " + e.Message); }

            Deferred.NextFrames(2, () =>
            {
                try
                {
                    g.IsInGame = false;
                    Game.Instance.EntityDestroyer.Destroy(g);
                    Game.Instance.EntityDestroyer.Tick();
                }
                catch (Exception e) { Main.LogError("[名册] 销毁失败: " + e.Message); }
            });
        }

        public static int DismissAll()
        {
            var targets = All();
            int attempted = targets.Count;
            if (attempted == 0) { Main.Log("没有在册卫兵。"); return 0; }

            foreach (var g in targets)
            {
                try
                {
                    // 先摘跟随：OnDetach 才会撤销队长侧的 AddIndependentFollower 登记
                    try { g.Remove<UnitPartFollowUnit>(); } catch { }
                    // ★ 关键：摘掉 UnitPartCompanion，否则 TryUnrecruit 会取消销毁
                    try { g.Remove<UnitPartCompanion>(); } catch { }
                    try { g.IsInGame = false; } catch { }
                    Game.Instance.EntityDestroyer.Destroy(g);
                }
                catch (Exception ex) { Main.LogError("遣散失败: " + ex.Message); }
            }

            try { Game.Instance.EntityDestroyer.Tick(); }
            catch (Exception ex) { Main.LogError("Destroyer.Tick: " + ex.Message); }

            // 复查 —— 不能只靠计数器自报
            int left = 0;
            try { left = All().Count; } catch { }
            if (left == 0)
            {
                Main.Log("已遣散 " + attempted + " 名卫兵，复查在册 0，清理完成。");
            }
            else
            {
                Main.LogError("遣散不完整：尝试 " + attempted + " 名，仍有 " + left + " 名在册。"
                              + "\n    若游戏日志里出现 \"Cancel unit's destruction\" 或 "
                              + "\"Trying to destroy ... who is a companion\"，说明 UnitPartCompanion 没摘干净。"
                              + "\n    此时请勿存档，先反馈日志。");
            }
            return attempted - left;
        }
        /// <summary>面板/日志用的一行摘要。</summary>
        public static string Describe()
        {
            var list = All();
            if (list.Count == 0) return "无";
            var parts = new List<string>();
            foreach (var u in list)
            {
                string hp = "?";
                try { var h = u.GetHealthOptional(); if (h != null) hp = h.HitPointsLeft + "/" + h.MaxHitPoints; } catch { }
                string st = "?";
                try { var c = u.GetOptional<UnitPartCompanion>(); st = c != null ? c.State.ToString() : "无Companion"; } catch { }
                bool down = false;
                try { down = u.LifeState != null && !u.LifeState.IsConscious; } catch { }
                int ai = ArchetypeOf(u);
                var archs = Archetypes.All;
                string an = (ai >= 0 && ai < archs.Length) ? archs[ai].Name : "未标记";
                string nm = null;
                try { nm = u.CharacterName; } catch { }
                parts.Add((string.IsNullOrEmpty(nm) ? "" : nm + " ")
                          + "lv" + u.Progression.CharacterLevel + " hp" + hp + " " + an + (down ? " [倒地]" : ""));
            }
            return string.Join(" | ", parts);
        }
    }
}