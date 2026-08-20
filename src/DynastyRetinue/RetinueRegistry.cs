using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;   // Entity
using Kingmaker.UnitLogic.Parts;

namespace DynastyRetinue
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
        /// <summary>
        /// ★这个字符串永远不要跟着 mod 改名★
        ///
        /// v0.82.0 把 mod 从 KgdRetinue 更名为 DynastyRetinue，命名空间、程序集、
        /// UMM Id、日志文件名全改了，唯独这一族 "kgd.*" 标记原地不动 —— 因为它们
        /// **已经写进了玩家存档**（PartCombatGroup.m_Id 是 [JsonProperty] 的裸 string）。
        /// 改了它，等于让所有既有存档里的卫兵集体失去身份：IsGuard 返回 false，
        /// 名册认不出来、遣散按钮找不到它们、装备保护补丁也不再生效，
        /// 而那些单位仍然实实在在躺在 party.json 里 —— 变成清不掉的幽灵。
        ///
        /// 同族还有 EliteTagPrefix("kgd.e:")、Probe.cs 的 "kgd.probe"、
        /// PlanProbe.cs 的 "kgd.planprobe"。改名时它们一起留下。
        /// 玩家看不见这个字符串，没有任何改的理由。
        /// </summary>
        public const string GuardTag = "kgd.guard";

        /// <summary>
        /// 墓碑前缀。★必须不是 GuardTag 的子前缀★
        ///
        /// v0.83.0 我把它改成过 GuardTag + ".dead."，想让销毁失败的孤儿仍然能被
        /// All()/DismissAll 收走。副作用是致命的：IsGuard 靠 StartsWith(GuardTag) 判断，
        /// 于是阵亡卫兵在「摘牌」到「两帧后销毁」这段窗口里**仍然是卫兵**，
        /// 而全 mod 有 15 处 IsGuard 门控（CameraFollowPatch / VeilPatch / MomentumPatch /
        /// XpPatch / DeathRules / GuardKillCreditPatch …）会继续每帧去碰一个正在销毁的实体。
        /// 实机表现：一名卫兵阵亡后整个游戏进入慢动作，回合再也推不下去，且不产生任何日志。
        ///
        /// 所以前缀退回「与 GuardTag 不同族」—— 摘牌即刻生效，原语义不变。
        /// 孤儿仍然清得掉：All(true) 显式把这一族也扫进来（见下），
        /// 不再依赖「墓碑也算卫兵」这个危险的等价关系。
        /// </summary>
        public const string DeadTag = "kgd.dead.";

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
        public static List<BaseUnitEntity> All() { return All(false); }

        /// <summary>
        /// <paramref name="includeDead"/> = 是否把「已摘牌但还没销毁成功」的墓碑实体也算进来。
        ///
        /// ★为什么要有这个开关★ RemoveOne 是「先摘身份标记、再延迟两帧销毁」。
        /// 摘牌是为了让名额当场释放，也为了让 15 处 IsGuard 门控立刻停止处理这个实体
        /// （不摘的话它们会每帧去碰一个正在销毁的对象 —— v0.83.0 实测会让整个游戏进入慢动作）。
        /// 但销毁那一步是可能失败的（EntityDestroyer.Destroy 抛异常、或 Deferred 的 Runner
        /// 在那两帧里被关掉），失败时只打一行日志、不回滚标记。
        /// 那样的孤儿仍然躺在 party.json 里，却不再是「卫兵」——
        /// 所以这里**显式**把墓碑一族也扫进来，而不是靠让墓碑继续算卫兵。
        /// </summary>
        public static List<BaseUnitEntity> All(bool includeDead)
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
                    if (b == null) continue;
                    if (!IsGuard(b) && !(includeDead && IsTombstoned(b))) continue;
                    string uid;
                    try { uid = b.UniqueId; } catch { continue; }
                    if (uid != null && seen.Add(uid)) result.Add(b);
                }
            }
            return result;
        }

        /// <summary>已摘牌、等待销毁（或销毁失败）的卫兵。</summary>
        public static bool IsTombstoned(BaseUnitEntity u)
        {
            try
            {
                var cg = u != null ? u.CombatGroup : null;
                var id = (cg != null) ? cg.Id : null;
                return id != null && id.StartsWith(DeadTag, StringComparison.Ordinal);
            }
            catch { return false; }
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
                // 先摘掉身份标记 —— 这一步必须**立刻**做：名额当场释放，
                // 而且全 mod 那 15 处 IsGuard 门控（镜头/帷幕/士气/经验/击杀归属…）
                // 从这一刻起不再处理它。摘晚了会每帧去碰一个正在死的实体。
                try { var cg = g.CombatGroup; if (cg != null) cg.Id = DeadTag + Guid.NewGuid().ToString("N").Substring(0, 8); }
                catch { }
                try { g.Remove<UnitPartFollowUnit>(); } catch { }
                try { g.Remove<UnitPartCompanion>(); } catch { }
            }
            catch (Exception e) { Main.LogError("[名册] 拆解失败: " + e.Message); }

            // ★战斗中不销毁，只排队★
            // 两帧后就把实体销毁的话，尸体会当着玩家的面凭空消失 —— 原版所有单位
            // 阵亡后都会把尸体留在地上，唯独卫兵"啪"地不见了，看起来就是个 bug。
            // 摘牌已经把它踢出名册和所有门控了，尸体留到战斗结束毫无代价。
            bool inCombat = false;
            try { inCombat = Game.Instance != null && Game.Instance.Player != null && Game.Instance.Player.IsInCombat; }
            catch { }

            if (inCombat)
            {
                lock (_pending) _pending.Add(g);
                return;
            }
            DestroyNow(g);
        }

        /// <summary>战斗中阵亡、等着战斗结束再销毁的尸体。</summary>
        private static readonly List<BaseUnitEntity> _pending = new List<BaseUnitEntity>();

        private static void DestroyNow(BaseUnitEntity g)
        {
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

        /// <summary>
        /// 战斗结束时把排队的尸体收掉。由 CombatWatch 在检测到「战斗结束」那一帧调用。
        /// 玩家中途退出游戏的话这些尸体会留在存档里 —— 它们带 DeadTag，
        /// All(true) 扫得到，【遣散全部】清得掉，不会变成永久孤儿。
        /// </summary>
        public static void FlushPendingDestroy()
        {
            List<BaseUnitEntity> copy;
            lock (_pending)
            {
                if (_pending.Count == 0) return;
                copy = new List<BaseUnitEntity>(_pending);
                _pending.Clear();
            }
            Main.Log("[名册] 战斗结束，清理 " + copy.Count + " 具阵亡卫兵的遗体。");
            foreach (var g in copy) { try { DestroyNow(g); } catch { } }
        }

        public static int DismissAll()
        {
            // 先把排队的遗体收掉，免得它们既不在名册里、又还没被销毁 ——
            // 玩家点遣散的场景通常就是"准备关 mod 了"，这时候不能留尾巴。
            try { FlushPendingDestroy(); } catch { }
            // ★包含墓碑实体★ 之前销毁失败、只摘了牌的那些也要一起收 ——
            // 它们不在名册里、玩家看不见，但确确实实在 party.json 里。
            // 这里是玩家清理存档的唯一出口，漏掉它们就等于永远清不掉。
            var targets = All(true);
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

            // 复查 —— 不能只靠计数器自报。★用 All(true)★：只查名册的话，
            // 销毁失败但已摘牌的实体会被漏掉，于是打印出「复查在册 0，清理完成」这句
            // 假验收 —— 而 README 正是让玩家拿它当"可以安全关 mod 了"的依据。
            int left = 0;
            try { left = All(true).Count; } catch { }
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
        public static string Describe() { return Describe(All()); }

        /// <summary>
        /// 传入已有快照的版本 —— 面板一帧里要连着用四次名册，
        /// 每次都全量扫一遍所有 State 的实体纯属白费（IMGUI 一帧还触发两轮事件）。
        /// </summary>
        public static string Describe(List<BaseUnitEntity> list)
        {
            if (list == null) list = All();
            // ★这几个串会进玩家面板第一屏★ Main.cs 那行是 L.F("...{1}", ..., Describe())，
            // 模板过了本地化、塞进去的内容没过 —— 于是英文玩家一开面板就看到
            // "lv12 hp40/40 未标记"。这一行在所有折叠块之外，常驻可见。
            if (list.Count == 0) return L.T("无");
            var parts = new List<string>();
            foreach (var u in list)
            {
                string hp = "?";
                try { var h = u.GetHealthOptional(); if (h != null) hp = h.HitPointsLeft + "/" + h.MaxHitPoints; } catch { }
                string st = "?";
                try { var c = u.GetOptional<UnitPartCompanion>(); st = c != null ? c.State.ToString() : L.T("无Companion"); } catch { }
                bool down = false;
                try { down = u.LifeState != null && !u.LifeState.IsConscious; } catch { }
                int ai = ArchetypeOf(u);
                var archs = Archetypes.All;
                string an = (ai >= 0 && ai < archs.Length) ? archs[ai].Name : L.T("未标记");
                string nm = null;
                try { nm = u.CharacterName; } catch { }
                parts.Add((string.IsNullOrEmpty(nm) ? "" : nm + " ")
                          + "lv" + u.Progression.CharacterLevel + " hp" + hp + " " + an + (down ? L.T(" [倒地]") : ""));
            }
            return string.Join(" | ", parts);
        }
    }
}