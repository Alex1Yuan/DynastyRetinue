using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Blueprints;
using Kingmaker.Controllers.TurnBased;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.UnitLogic.Parts;

namespace DynastyRetinue
{
    /// <summary>
    /// ============ 机械教卫兵：召唤机仆（1.7.75）============
    ///
    /// ★需求★ T1/T2 机械教卫兵每场战斗召唤一个**机仆**；T3/精英召唤**战斗机仆**。
    ///   种类随机，消耗 0，唯一限制是每场一次。
    ///
    /// ═══ 为什么是「借用原版能力 + 重定向召唤物」而不是自建 ═══
    /// 授予单位的 fact（含 BlueprintAbility）**会被序列化进存档**。自建蓝图 ⇒
    /// 卸载 mod 后反序列化失败、存档永久打不开（本项目的头号红线）。
    /// 所以授予的必须是原版已有的 BlueprintAbility，召唤物也必须是原版 BlueprintUnit。
    ///
    /// ★原版没有「召唤机仆」的能力★ 这是全库扫描结论，不是「没搜到」：
    ///   全包只有 26 个蓝图带 ContextActionSpawnMonster，召唤的是恶魔/僵尸/虫群/
    ///   全息投影/伺服颅骨，与 86 个 Servitor 单位求交集为空。
    ///   ⇒ 必须 Harmony 重定向召唤目标，光授予一个现成能力做不到。
    ///
    /// ★donor 选它的理由★ MobTechpriestMagi_VoxSkullSummon_Ability
    ///   · 只有 3 个组件：AbilityEffectRunAction / ContextActionSpawnMonster / SummonPoolUnits
    ///     —— 没有 WarhammerEndTurn、没有 StartCombat、没有冷却组件、没有 locator
    ///   · 反向引用 0：没有任何原版单位持有它 ⇒ 运行时改它的爆炸半径≈0
    ///   · 题材也对得上（技师召唤伺服颅骨 → 我们换成机仆）
    ///   ★代价★ 它**没有名字**（本地化键 aa36c253-… 不在 enGB.json 里），所以显示名必须我们补。
    ///
    /// ★为什么用 RunAction 前后夹住而不是只挂 get_Blueprint★
    ///   同一次 RunAction 里 get_Blueprint 被调**三次**：
    ///     IL_0064 取 Size（算寻路矩形）· IL_0181 取 Prefab（算 Corpulence）· IL_01CA 实际生成
    ///   三次返回不一致会让寻路矩形、模型体积、实际落点互相脱靶。
    ///   所以在 RunAction 的 Prefix 里**一次性抽签并锁定**，Finalizer 清掉 ——
    ///   稳定性由构造保证，不靠"随机数恰好不变"。
    /// </summary>
    internal static class ServitorSummon
    {
        /// <summary>donor：技师召唤伺服颅骨。我们把召唤物换成机仆。</summary>
        internal const string DonorAbility = "7408fdcc13c04a6bb9820e4be96b6d9e";

        /// <summary>T1/T2 用：普通机仆（非 DLC3，安全底盘）。</summary>
        private static readonly string[] ServitorPool =
        {
            "f069ae81d98f4ecba8a5bbc0b6121301",   // ServitorRandomEncounter
            "30ff1bd210ccd914886ec962206dafbc",   // Servitor
        };

        /// <summary>T3/精英用：战斗机仆（非 DLC3）。</summary>
        private static readonly string[] CombatPool =
        {
            "5ba73a4183c24e909d691acabadc1db8",   // CombatServitorMultiMelta
            "3c16f2aeebc54cf1927062857252e95f",   // CombatServitorMultiMelta_Friendly
            "889485dc6e874ccfa535c1f2f59dffa5",   // CombatServitorRandomEncounter
            "454847134b48402792be9798bf92b0ca",   // GuardianCombatServitorColonization_unit
        };

        /// <summary>DLC3 版本（味道更对）。★必须判 Prefab 能不能加载★ —— 无 DLC3 时
        /// RunAction IL_0194-01AC 不会崩，但会走默认 corpulence，结果是一个**隐形单位占着格子**。</summary>
        private static readonly string[] ServitorPoolDlc3 = { "a92cdde1068c4609b437cd5a22f8b3b0" };
        private static readonly string[] CombatPoolDlc3 =
        {
            "d74728f897ef4b0a85978e25f43ea155",   // DLC3_OP_Dogmatic_CombatServitor_Unit
            "2af759a6dba447dca72494973763d3c0",   // DLC3_OP_Xenarite_CombatServitor_Unit
        };

        // 合作能力握手的固定顺序。协议只传 9 位 bitmask；顺序一旦发布不可重排。
        private static readonly string[] CandidateUnits =
        {
            "f069ae81d98f4ecba8a5bbc0b6121301", "30ff1bd210ccd914886ec962206dafbc",
            "5ba73a4183c24e909d691acabadc1db8", "3c16f2aeebc54cf1927062857252e95f",
            "889485dc6e874ccfa535c1f2f59dffa5", "454847134b48402792be9798bf92b0ca",
            "a92cdde1068c4609b437cd5a22f8b3b0", "d74728f897ef4b0a85978e25f43ea155",
            "2af759a6dba447dca72494973763d3c0",
        };

        /// <summary>重定向失败时 donor 原版会召出的伺服颅骨，也必须随战斗收回。</summary>
        private const string DonorFallbackUnit = "9cd810b2af7441daa79476cf3a375fc7";

        private static readonly System.Collections.Generic.HashSet<string> AllServitorUnits = BuildTrackedSummons();

        private static System.Collections.Generic.HashSet<string> BuildTrackedSummons()
        {
            var result = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pool in new[] { ServitorPool, CombatPool, ServitorPoolDlc3, CombatPoolDlc3 })
                foreach (string id in pool) if (!string.IsNullOrEmpty(id)) result.Add(id);
            result.Add(DonorFallbackUnit);
            return result;
        }

        // ★硬排除（不要往池子里加回去）★ 自爆/腐化/剧情/幽灵/伺服颅骨：
        //   RE_MadCombatServitor 03d80aa2 · _Melee 1b6b3452 · _wBuff 33abf95b ·
        //   ServitorChaosSelfDestruct 9513a791 · _ForgeHeart 511c60c0 · ServitorFrozen 16203cb3 ·
        //   CombatServitor_BoardedShip 9acbca8f · ServoSkullScrapCode 9cd810b2（是颅骨不是机仆）

        /// <summary>本次 RunAction 抽中的召唤物。★三次 get_Blueprint 必须拿到同一个★</summary>
        [ThreadStatic] private static BlueprintUnit _locked;
        [ThreadStatic] private static bool _cancel;

        // ★这两个成员在 Code.dll 里不是 public，只能反射★
        //   ContextAction.Context            —— protected
        //   RuleCalculateCooldown 的 set_CooldownComponent —— 非公开
        // 句柄一次性缓存；取不到就**明说**，不静默退化成"这个功能没生效"。
        private const System.Reflection.BindingFlags Any =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance;

        private static System.Reflection.PropertyInfo _pCtx, _pCdComp;
        private static bool _looked, _warned;

        private static void EnsureMembers()
        {
            if (_looked) return;
            _looked = true;
            try
            {
                for (var t = typeof(ContextActionSpawnMonster); t != null && _pCtx == null; t = t.BaseType)
                    _pCtx = t.GetProperty("Context", Any);
                _pCdComp = typeof(Kingmaker.RuleSystem.Rules.RuleCalculateCooldown)
                           .GetProperty("CooldownComponent", Any);
            }
            catch { }
            if ((_pCtx == null || _pCdComp == null) && !_warned)
            {
                _warned = true;
                Main.Log("[召唤机仆] ★挂不上★ 反射取不到："
                       + (_pCtx == null ? " ContextAction.Context" : "")
                       + (_pCdComp == null ? " RuleCalculateCooldown.CooldownComponent" : "")
                       + "　—— 成员名或可访问性变了。★这一行的存在本身就是防线："
                       + "静默失效会让功能像「AI 从来不选这个技能」，能查很久。★");
            }
        }

        /// <summary>取这次 spawn 动作的施法者。取不到一律返回 null（放行原版）。</summary>
        private static BaseUnitEntity CasterOf(ContextActionSpawnMonster act)
        {
            try
            {
                EnsureMembers();
                if (_pCtx == null || act == null) return null;
                var ctx = _pCtx.GetValue(act, null)
                          as Kingmaker.UnitLogic.Mechanics.MechanicsContext;
                return ctx != null ? ctx.MaybeCaster as BaseUnitEntity : null;
            }
            catch { return null; }
        }

        /// <summary>这个单位是不是我们要给召唤能力的机械教卫兵。</summary>
        internal static bool Applies(BaseUnitEntity u)
        {
            try
            {
                if (u == null || !Main.Enabled) return false;
                if (!RetinueRegistry.IsGuard(u)) return false;
                int ai = RetinueRegistry.ArchetypeOf(u);
                if (ai < 0) return false;
                var arch = Archetypes.Get(ai);
                // ★按名字里的 Mechanicus 判，不写死下标★ archetypes.json 的顺序可能变
                return arch != null && arch.Name != null
                    && arch.Name.IndexOf("Mechanicus", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 给这个卫兵装上召唤能力（幂等）。
        /// ★存档安全★ 加进 Abilities 的是**原版** BlueprintAbility 的 AssetGuid，
        ///   与「原版技师持有该能力时写进存档的东西」逐字节同构。卸载 mod 后
        ///   反序列化照常，能力变回它原本的样子（一个没人用的召唤伺服颅骨技能）。
        /// </summary>
        internal static void EnsureGranted(BaseUnitEntity guard)
        {
            try
            {
                if (!Applies(guard)) return;
                var bp = ResourcesLibrary.TryGetBlueprint<Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility>(DonorAbility);
                if (bp == null)
                {
                    if (!_donorWarned)
                    {
                        _donorWarned = true;
                        Main.Log("[召唤机仆] ★取不到 donor 蓝图 " + DonorAbility + "★ 功能不会生效。");
                    }
                    return;
                }
                EnsureDonorTuned(bp);

                foreach (var a in guard.Abilities)
                    if (a != null && a.Blueprint == bp) return;      // 已经有了
                guard.Abilities.Add(bp);
                Main.Log("[召唤机仆] 已授予 " + (guard.CharacterName ?? "?")
                       + "　★若整场没有「召唤 →」那一行，说明能力在但 AI 没选它★");
            }
            catch (Exception e) { Main.LogError("[召唤机仆] 授予失败: " + e); }
        }

        private static bool _donorWarned, _donorTuned;

        // ---------- 合作候选能力握手 ----------
        private static readonly Dictionary<string, int> CapabilityReports =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly HashSet<string> GenerationMembers =
            new HashSet<string>(StringComparer.Ordinal);
        private static bool _wasCoopSession, _capsSent, _poolPublishPending;
        private static bool _sharedUseDlc3, _hostPauseSent;
        private static int _sharedCapabilityMask = -1;
        private static int _localCapabilityMask = -1;
        private static int _sharedReporterCount;
        private static int _capabilityFrameSkip;
        private static int _sessionFalseSamples;
        private static int _capabilityEpochSequence;
        private static string _generation = "";
        private static string _capabilityEpoch = "";
        private static string _reportsEpoch = "";
        private static string _hostPendingEpoch = "";
        private static string[] _hostObservedMembers = new string[0];
        private const int CapabilityCheckFrames = 60;
        private static readonly BlueprintUnit[] CapabilityBlueprints = new BlueprintUnit[CandidateUnits.Length];

        internal static void TickCapabilities()
        {
            // Main.OnUpdate 会调用这里，但绝不能每帧扫描网络成员或资源。
            // 平时只有一个 int 自增和比较；每 60 帧才读一次最多 6 个 UserId。
            if (++_capabilityFrameSkip < CapabilityCheckFrames) return;
            _capabilityFrameSkip = 0;

            bool session;
            // 只有 IsActive 与 IsMultiplayer 都明确为 false 才清池；边沿读取失败保留旧状态。
            if (!CoopState.TryGetSharedGameplayRequired(out session)) session = _wasCoopSession;
            if (!session)
            {
                if (!_wasCoopSession) return;
                if (++_sessionFalseSamples < 3) return;
                ClearCapabilitySession();
                _wasCoopSession = false;
                _sessionFalseSamples = 0;
                return;
            }
            _sessionFalseSamples = 0;
            _wasCoopSession = true;

            // 只有房主周期读取最多 6 个 UserId 并观察成员变化；加入方完全不扫描成员表，
            // 只消费房主通过同步 GameCommand 发布的 epoch / generation。
            if (CoopState.IsConfirmedHost)
            {
                bool unchanged;
                if (!CoopState.TryActivePlayersMatch(_hostObservedMembers, out unchanged))
                {
                    RequestCapabilityPause();
                    return;
                }
                if (!unchanged)
                {
                    // 同人数换人也必须先同步暂停旧池；一个 epoch 只绑定一个 generation。
                    if (!_hostPauseSent) { RequestCapabilityPause(); return; }
                    if (string.Equals(_reportsEpoch, _capabilityEpoch, StringComparison.Ordinal))
                    {
                        _hostPauseSent = false;
                        _hostPendingEpoch = "";
                        RequestCapabilityPause();
                        return;
                    }
                    string observedGeneration;
                    string[] observedMembers;
                    if (!CoopState.TryGetActivePlayerGeneration(out observedGeneration, out observedMembers)) return;
                    var payload = new string[3 + observedMembers.Length];
                    payload[0] = _capabilityEpoch;
                    payload[1] = observedGeneration;
                    payload[2] = observedMembers.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Array.Copy(observedMembers, 0, payload, 3, observedMembers.Length);
                    CoopCommand.Send("servitorreset", payload);
                    return;
                }
                if (_hostPauseSent && !string.Equals(_reportsEpoch, _capabilityEpoch, StringComparison.Ordinal))
                {
                    string observedGeneration;
                    string[] observedMembers;
                    if (!CoopState.TryGetActivePlayerGeneration(out observedGeneration, out observedMembers)) return;
                    var payload = new string[3 + observedMembers.Length];
                    payload[0] = _capabilityEpoch;
                    payload[1] = observedGeneration;
                    payload[2] = observedMembers.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Array.Copy(observedMembers, 0, payload, 3, observedMembers.Length);
                    CoopCommand.Send("servitorreset", payload);
                    return;
                }
                if (_hostPauseSent && ReportsComplete()) _poolPublishPending = true;
            }

            // 当前 epoch 的 reset 尚未执行时先不报告。
            if (string.IsNullOrEmpty(_capabilityEpoch) || string.IsNullOrEmpty(_generation)
                || !string.Equals(_reportsEpoch, _capabilityEpoch, StringComparison.Ordinal)) return;

            if (!_capsSent)
            {
                string who = CoopState.LocalUserId;
                if (!string.IsNullOrEmpty(who) && GenerationMembers.Contains(who))
                {
                    // 仅每个 generation 探测一次 9 个固定蓝图；若入队失败只重发缓存掩码。
                    if (_localCapabilityMask < 0) _localCapabilityMask = LocalCapabilityMask();
                    CoopCommand.Send("servitorcaps", _capabilityEpoch, _generation, who,
                        _localCapabilityMask.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            if (_poolPublishPending && CoopState.IsConfirmedHost)
            {
                if (!ReportsComplete()) return;
                int mask = (1 << CandidateUnits.Length) - 1;
                foreach (string member in GenerationMembers) mask &= CapabilityReports[member];
                bool useDlc3 = CoopSessionHasDlc3();
                // pending 保留到 ReceiveSharedPool 真执行；命令丢失只重发短载荷。
                CoopCommand.Send("servitorpool", _capabilityEpoch, _generation,
                    GenerationMembers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    useDlc3 ? "1" : "0",
                    mask.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        private static void RequestCapabilityPause()
        {
            if (_hostPauseSent) return;
            if (string.IsNullOrEmpty(_hostPendingEpoch))
            {
                string host = CoopState.LocalUserId;
                int tick = 0;
                try { tick = Kingmaker.Game.Instance.RealTimeController.CurrentNetworkTick; } catch { }
                _hostPendingEpoch = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(host ?? ""))
                    + ":" + tick.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ":" + (++_capabilityEpochSequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            CoopCommand.Send("servitorpause", _hostPendingEpoch);
        }

        private static void ClearCapabilitySession()
        {
            CapabilityReports.Clear();
            GenerationMembers.Clear();
            _capsSent = false;
            _poolPublishPending = false;
            _sharedCapabilityMask = -1;
            _localCapabilityMask = -1;
            _sharedReporterCount = 0;
            _sharedUseDlc3 = false;
            _hostPauseSent = false;
            _generation = "";
            _capabilityEpoch = "";
            _reportsEpoch = "";
            _hostPendingEpoch = "";
            _hostObservedMembers = new string[0];
            Array.Clear(CapabilityBlueprints, 0, CapabilityBlueprints.Length);
        }

        private static void ResetToGeneration(string generation, string[] members)
        {
            CapabilityReports.Clear();
            GenerationMembers.Clear();
            foreach (string member in members) GenerationMembers.Add(member);
            _capsSent = false;
            _poolPublishPending = false;
            _sharedCapabilityMask = -1;
            _localCapabilityMask = -1;
            _sharedReporterCount = 0;
            _sharedUseDlc3 = false;
            _generation = generation;
            _reportsEpoch = _capabilityEpoch;
            Array.Clear(CapabilityBlueprints, 0, CapabilityBlueprints.Length);
        }

        private static int LocalCapabilityMask()
        {
            int mask = 0;
            for (int i = 0; i < CandidateUnits.Length; i++)
            {
                try
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(CandidateUnits[i]);
                    CapabilityBlueprints[i] = bp != null && PrefabLoads(bp) ? bp : null;
                    if (CapabilityBlueprints[i] != null) mask |= 1 << i;
                }
                catch { CapabilityBlueprints[i] = null; }
            }
            return mask;
        }

        /// <summary>成员表处于加入/离开过渡期时，各端同步暂停使用旧共同池。</summary>
        internal static void ReceivePause(string[] args)
        {
            if (args == null || args.Length < 1 || string.IsNullOrEmpty(args[0])) return;
            string epoch = args[0];
            _capabilityEpoch = epoch;
            // 重复 pause 也保持暂停；正常命令顺序由原版 GameCommandQueue 保证。
            _sharedCapabilityMask = -1;
            _sharedReporterCount = 0;
            _poolPublishPending = false;
            if (CoopState.IsConfirmedHost)
            {
                _hostPauseSent = true;
                _hostPendingEpoch = epoch;
            }
            Main.Log("[召唤机仆] 合作成员正在变化，已同步暂停召唤，等待共同池重新冻结。");
        }

        /// <summary>房主同步发布当前 epoch 的成员 generation；同人数换人也会换代。</summary>
        internal static void ReceiveGeneration(string[] args)
        {
            if (args == null || args.Length < 4 || string.IsNullOrEmpty(args[0])
                || !string.Equals(args[0], _capabilityEpoch, StringComparison.Ordinal)
                || string.IsNullOrEmpty(args[1])) return;
            int count;
            if (!int.TryParse(args[2], System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out count)
                || count < 1 || count > 6 || args.Length != count + 3) return;

            var members = new string[count];
            Array.Copy(args, 3, members, 0, count);
            for (int i = 0; i < members.Length; i++)
            {
                if (string.IsNullOrEmpty(members[i])) return;
                if (i > 0 && string.CompareOrdinal(members[i - 1], members[i]) >= 0) return;
            }
            string rebuilt = CoopState.BuildPlayerGeneration(members);
            if (!string.Equals(args[1], rebuilt, StringComparison.Ordinal))
            {
                Main.LogError("[召唤机仆] 拒绝成员 generation 与名单不一致的 reset。");
                return;
            }
            bool sameReset = string.Equals(_reportsEpoch, _capabilityEpoch, StringComparison.Ordinal)
                          && string.Equals(_generation, rebuilt, StringComparison.Ordinal)
                          && GenerationMembers.Count == members.Length;
            if (sameReset)
                foreach (string member in members)
                    if (!GenerationMembers.Contains(member)) { sameReset = false; break; }
            // 同 epoch 重复 reset 幂等忽略；新 epoch 才重新收集能力。
            if (!sameReset) ResetToGeneration(rebuilt, members);
            if (CoopState.IsConfirmedHost)
            {
                _hostObservedMembers = (string[])members.Clone();
                _hostPendingEpoch = "";
            }
            Main.Log("[召唤机仆] 合作能力代已同步：" + count + " 人；等待新报告。");
        }

        internal static void ReceiveCapabilities(string[] args)
        {
            if (args == null || args.Length < 4) return;
            string epoch = args[0], generation = args[1], who = args[2];
            int mask;
            if (!string.Equals(epoch, _capabilityEpoch, StringComparison.Ordinal)
                || !string.Equals(epoch, _reportsEpoch, StringComparison.Ordinal)
                || !string.Equals(generation, _generation, StringComparison.Ordinal)
                || !GenerationMembers.Contains(who)
                || !int.TryParse(args[3], System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out mask)) return;

            int allowed = (1 << CandidateUnits.Length) - 1;
            int normalized = mask & allowed;
            int previous;
            bool changed = !CapabilityReports.TryGetValue(who, out previous) || previous != normalized;
            CapabilityReports[who] = normalized;
            if (string.Equals(who, CoopState.LocalUserId, StringComparison.Ordinal)) _capsSent = true;
            if (changed) _sharedCapabilityMask = -1;
            if (CoopState.IsConfirmedHost && ReportsComplete()) _poolPublishPending = true;
        }

        internal static void ReceiveSharedPool(string[] args)
        {
            if (args == null || args.Length < 5) return;
            int reporters, useDlc3, mask;
            if (!string.Equals(args[0], _capabilityEpoch, StringComparison.Ordinal)
                || !string.Equals(args[0], _reportsEpoch, StringComparison.Ordinal)
                || !string.Equals(args[1], _generation, StringComparison.Ordinal)
                || !int.TryParse(args[2], System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out reporters)
                || !int.TryParse(args[3], System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out useDlc3)
                || !int.TryParse(args[4], System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out mask)
                || (useDlc3 != 0 && useDlc3 != 1)
                || reporters != GenerationMembers.Count
                || !ReportsComplete())
            {
                Main.Log("[召唤机仆] 忽略过期或不完整的共同池。");
                return;
            }

            int expectedMask = (1 << CandidateUnits.Length) - 1;
            foreach (string member in GenerationMembers) expectedMask &= CapabilityReports[member];
            int allowed = (1 << CandidateUnits.Length) - 1;
            if ((mask & allowed) != expectedMask)
            {
                Main.LogError("[召唤机仆] 共同池掩码与已同步报告不一致，已拒绝。");
                return;
            }

            _sharedCapabilityMask = expectedMask;
            _sharedReporterCount = reporters;
            _sharedUseDlc3 = useDlc3 != 0;
            _poolPublishPending = false;
            if (CoopState.IsConfirmedHost) _hostPauseSent = false;
            Main.Log("[召唤机仆] 合作共同候选池已冻结：报告 " + reporters
                   + " 份，会话池=" + (_sharedUseDlc3 ? "DLC3" : "本体")
                   + "，双方共同可用 " + CountBits(_sharedCapabilityMask) + "/" + CandidateUnits.Length + " 种。");
        }

        private static bool ReportsComplete()
        {
            if (GenerationMembers.Count == 0 || CapabilityReports.Count != GenerationMembers.Count) return false;
            foreach (string member in GenerationMembers)
                if (!CapabilityReports.ContainsKey(member)) return false;
            return true;
        }

        internal static string CapabilityStatus()
        {
            if (!CoopState.SharedGameplayRequired) return "单机（不需要共同池）";
            if (_sharedCapabilityMask < 0)
                return "尚未冻结，generation=" + (string.IsNullOrEmpty(_generation) ? "无" : "已同步")
                     + "，已收到 " + CapabilityReports.Count + "/" + GenerationMembers.Count + " 份报告";
            return "已冻结，池=" + (_sharedUseDlc3 ? "DLC3" : "本体")
                 + "，共同可用 " + CountBits(_sharedCapabilityMask) + "/" + CandidateUnits.Length
                 + " 种，报告 " + _sharedReporterCount + " 份";
        }

        private static int CountBits(int value)
        {
            int count = 0;
            while (value != 0) { count += value & 1; value >>= 1; }
            return count;
        }

        private static string[] SharedEligible(string[] source)
        {
            if (_sharedCapabilityMask < 0 || string.IsNullOrEmpty(_generation)) return new string[0];
            var result = new List<string>();
            foreach (string id in source)
            {
                int index = Array.IndexOf(CandidateUnits, id);
                if (index >= 0 && (_sharedCapabilityMask & (1 << index)) != 0) result.Add(id);
            }
            return result.ToArray();
        }

        /// <summary>DLC3（The Infinite Museion）的 BlueprintDlc GUID，不是 DLC3Reward。</summary>
        private const string Dlc3Blueprint = "30938411c3c64d77b415fbe6d23bbaa0";
        private static bool _coopDlcWarned;

        /// <summary>
        /// 官方合作会话最终启用的 DLC 集是否含 DLC3。
        /// PhotonManager.DLC.DLCsInGame 由房主 CreateDlcInGameList 后随 CloseRoom/SaveMeta
        /// 冻结并下发，两端读取同一份会话值；不能改看本机 Steam/DLC/Prefab 状态。
        /// </summary>
        private static bool CoopSessionHasDlc3()
        {
            try
            {
                var dlcs = Kingmaker.Networking.PhotonManager.DLC.DLCsInGame;
                foreach (string id in dlcs)
                    if (string.Equals(id, Dlc3Blueprint, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception e)
            {
                if (!_coopDlcWarned)
                {
                    _coopDlcWarned = true;
                    Main.LogError("[召唤机仆] 读取官方会话 DLC 列表失败，联机本局回落本体池：" + e.Message);
                }
            }
            return false;
        }

        /// <summary>
        /// 运行时把 donor 调成「0 消耗」。
        /// ★为什么改蓝图是安全的★ 蓝图**从不被序列化**，存档只存 AssetGuid；
        ///   而这个 donor 的反向引用是 0（没有任何原版单位持有它），爆炸半径≈0。
        /// ★为什么不用 Params.FreeAction★ AbstractUnitCommand.IsFreeAction 是非虚 getter、
        ///   所有指令共用，补它等于站在全局路径上。改蓝图字段只影响这一个技能。
        /// </summary>
        private static void EnsureDonorTuned(Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility bp)
        {
            if (_donorTuned) return;
            _donorTuned = true;
            try
            {
                bp.IsFreeAction = true;      // AbilityData.CalculateActionPointCost: IsFreeAction ? 0 : rule
                bp.CooldownRounds = 0;       // 每场一次由 RuleCalculateCooldown 那条补丁负责
                Main.Log("[召唤机仆] donor 已调为 0 消耗（IsFreeAction=true）。"
                       + "★注意★ 该 donor 原版**没有显示名**（本地化键不在 enGB.json 里），"
                       + "技能栏上会是空白 —— 显示名与图标尚未补，见发布前待办。");
            }
            catch (Exception e) { Main.LogError("[召唤机仆] 调 donor 失败: " + e); }
        }

        /// <summary>该给普通机仆还是战斗机仆。true = 战斗机仆（T3 / 精英）。</summary>
        private static bool WantsCombatServitor(BaseUnitEntity u)
        {
            try
            {
                int ai = RetinueRegistry.ArchetypeOf(u);
                var arch = ai >= 0 ? Archetypes.Get(ai) : null;
                if (arch != null && GearTool.IsElite(u, arch)) return true;
                int lv = u.Progression != null ? u.Progression.CharacterLevel : 1;
                return lv >= 36;                        // 与 TierRank 同一套断点：36=T3
            }
            catch { return false; }
        }

        /// <summary>抽一个召唤物。DLC3 池优先，Prefab 加载不到就回落非 DLC3。</summary>
        private static BlueprintUnit Pick(BaseUnitEntity caster)
        {
            try
            {
                bool combat = WantsCombatServitor(caster);
                var dlc3 = combat ? CombatPoolDlc3 : ServitorPoolDlc3;
                var safe = combat ? CombatPool : ServitorPool;

                // ═══ 1.7.84：种子必须是**同步量** ═══
                //
                // ★原来这里是 `seed ^ Environment.TickCount` —— 联机直接炸★
                //   TickCount 是本机时钟，两台机器会抽到不同的机仆、生成不同的单位。
                //   而仓库里本来就写着这条纪律（CombatWatch.cs:262）：
                //     「★realtimeSinceStartup 只能用来记日志★ 真实时间不是同步量」
                //   我写的时候没查就用了。★凡是会影响游戏状态的随机，种子只能取同步量。★
                //
                // 现在用「施法者 UniqueId + 当前战斗回合」，两者在两台机器上一致。
                // ★不用 string.GetHashCode()★ 它在不同运行/不同运行时下不保证一致，
                //   自己算一个稳定哈希（FNV-1a），结果只取决于字符串内容。
                int seed = StableHash(caster.UniqueId);
                try
                {
                    var game = Kingmaker.Game.Instance;
                    var tc = game != null ? game.TurnController : null;
                    if (tc != null) seed = seed * 31 + tc.CombatRound;
                    // ★跨战斗熵必须也是同步量★ 只有 UniqueId+CombatRound 时，同一个卫兵
                    //   每场第 1 回合都会拿到同一种机仆。CurrentNetworkTick 派生自 Player.RealTime，
                    //   进入状态哈希、联机两端一致；每次施放所在 tick 又不同。
                    if (game != null && game.RealTimeController != null)
                        seed = seed * 31 + game.RealTimeController.CurrentNetworkTick;
                }
                catch { }
                var rnd = new Random(seed);

                // ★联机只用本体安全池，并且一次抽中后不按本机资源状态向后跳★
                //   旧写法固定「DLC3 段优先」，然后 TryGetBlueprint/PrefabLoads 失败就继续：
                //   A 有 DLC3 ⇒ 召 DLC3；B 没 DLC3 ⇒ 跳到本体，两个 BlueprintUnit 直接分叉。
                //   本体安全池属于同一游戏版本的共同资源；索引只由同步种子决定。
                //   这里也故意不做 PrefabLoads 分支 —— 本机加载成功与否不能参与同步选择。
                if (CoopState.SharedGameplayRequired)
                {
                    // 用户要求：官方会话启用 DLC3 时联机也应能召 DLC3 机仆。
                    // DLCsInGame 是会话共同值，两端会选择同一个 pool；随后相同 seed
                    // 抽同一 index。禁止按本机 Prefab 成败向后跳，否则会重新分叉。
                    // 施放时不再根据本机 TryGetBlueprint/PrefabLoads 决定候选；
                    // 只从会话开始时双方上报、房主发布的共同交集中抽取。
                    var pool = SharedEligible(_sharedUseDlc3 ? dlc3 : safe);
                    // 会话启用了 DLC3，但双方共同交集中恰好没有对应 DLC3 候选时，
                    // 统一回落到双方共同可用的本体池；仍只读冻结 bitmask，不查本机资源。
                    if (pool.Length == 0 && _sharedUseDlc3) pool = SharedEligible(safe);
                    if (pool.Length == 0) return null;
                    string selected = pool[rnd.Next(pool.Length)];
                    int selectedIndex = Array.IndexOf(CandidateUnits, selected);
                    return selectedIndex >= 0 ? CapabilityBlueprints[selectedIndex] : null;
                }

                var order = new System.Collections.Generic.List<string>();
                for (int i = 0; i < dlc3.Length; i++) order.Add(dlc3[i]);
                for (int i = 0; i < safe.Length; i++) order.Add(safe[i]);
                // 单机：洗牌 DLC3 段和安全段各自内部，但保持 DLC3 优先；
                // 本机缺 DLC/Prefab 时允许继续回落，不涉及跨机器分叉。
                Shuffle(order, 0, dlc3.Length, rnd);
                Shuffle(order, dlc3.Length, safe.Length, rnd);

                for (int i = 0; i < order.Count; i++)
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(order[i]);
                    if (bp == null) continue;
                    if (!PrefabLoads(bp)) continue;    // ★单机隐形单位占格的唯一防线★
                    return bp;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 稳定哈希（FNV-1a 32 位）。★不要用 string.GetHashCode()★ ——
        /// 它不保证跨运行/跨运行时一致，拿它当随机种子在联机里就是两台机器各抽各的。
        /// </summary>
        private static int StableHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                return (int)h;
            }
        }

        private static void Shuffle(System.Collections.Generic.List<string> l, int from, int len, Random r)
        {
            for (int i = len - 1; i > 0; i--)
            {
                int j = r.Next(i + 1);
                var t = l[from + i]; l[from + i] = l[from + j]; l[from + j] = t;
            }
        }

        /// <summary>Prefab 能不能真的加载出来。加载不到 = 无 DLC3 或资源缺失，绝不能用。</summary>
        private static bool PrefabLoads(BlueprintUnit bp)
        {
            try
            {
                var pf = bp.Prefab;
                if (pf == null) return false;
                return pf.Load(false, false) != null;
            }
            catch { return false; }
        }

        // ═══ 在 RunAction 前后夹住，锁定本次抽签结果 ═══
        [HarmonyPatch(typeof(ContextActionSpawnMonster), "RunAction")]
        internal static class LockPick
        {
            private static bool Prefix(ContextActionSpawnMonster __instance)
            {
                try
                {
                    _locked = null;
                    _cancel = false;
                    if (!Main.Enabled) return true;
                    // Context 为 null = 编辑器 / GetCaption 路径，别碰
                    var caster = CasterOf(__instance);
                    if (caster == null || !Applies(caster)) return true;
                    _locked = Pick(caster);
                    _cancel = _locked == null;
                    // ★这条必须无条件打★ 沉默 ≠ 成功：
                    //   看不到机仆时，要能分清「没授予」「授予了但 AI 没选」「选了但重定向失败」。
                    //   前两种在这里根本不会出现日志，所以这一行的**有无**本身就是判据。
                    if (_locked == null)
                        Main.Log("[召唤机仆] " + (caster.CharacterName ?? "?")
                               + " 抽不到双方共同可用的召唤物，本次同步取消；不会改召伺服颅骨。");
                    else
                        Main.Log("[召唤机仆] " + (caster.CharacterName ?? "?")
                               + " 召唤 → " + _locked.name
                               + "（" + (WantsCombatServitor(caster) ? "战斗机仆" : "机仆") + "）");
                    return !_cancel;
                }
                catch { _locked = null; _cancel = true; return false; }
            }

            /// <summary>★用 Finalizer 不用 Postfix★ 原方法抛异常时也要清，否则会污染下一次。</summary>
            private static void Finalizer() { _locked = null; _cancel = false; }
        }

        [HarmonyPatch(typeof(ContextActionSpawnMonster), "get_Blueprint")]
        internal static class Redirect
        {
            private static void Postfix(ref BlueprintUnit __result)
            {
                try { if (_locked != null) __result = _locked; }
                catch { }
            }
        }

        // ═══ 战斗结束 / 召唤者倒下时清理 ═══

        private static bool IsOurServitor(BaseUnitEntity unit)
        {
            try
            {
                if (unit == null || unit.GetOptional<UnitPartSummonedMonster>() == null) return false;
                var bp = unit.OriginalBlueprint ?? unit.Blueprint;
                if (bp == null || !AllServitorUnits.Contains(bp.AssetGuid.ToString())) return false;

                // RulePerformSummonUnit 会复制召唤者的 CombatGroup.Id。即使普通卫兵随后死亡、
                // 被 RetinueRegistry 摘牌成 kgd.dead，已经召出的机仆仍保留 kgd.guard；
                // 因而用召唤物自己的组标记比重新问已死亡的召唤者更稳。
                var group = unit.CombatGroup;
                var id = group != null ? group.Id : null;
                return id != null && id.StartsWith(RetinueRegistry.GuardTag, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static bool SummonerGone(BaseUnitEntity unit)
        {
            try
            {
                var part = unit != null ? unit.GetOptional<UnitPartSummonedMonster>() : null;
                var summoner = part != null ? part.Summoner as BaseUnitEntity : null;
                return summoner == null || summoner.IsDeadOrUnconscious;
            }
            catch { return true; }
        }

        private static void QueueDestroy(BaseUnitEntity unit, string why)
        {
            try
            {
                if (unit == null || unit.IsDisposed || unit.WillBeDestroyed) return;
                Kingmaker.Game.Instance.EntityDestroyer.Destroy(unit);
                Main.Log("[召唤机仆] " + why + "，已收回 "
                       + (unit.Blueprint != null ? unit.Blueprint.name : "?"));
            }
            catch (Exception e) { Main.LogError("[召唤机仆] 战后清理失败: " + e.Message); }
        }

        /// <summary>
        /// 读档/过图冷路径兜底：1.7.97 以前召出的机仆可能已经在战斗外存活，
        /// 它们不会再触发一次 LeaveCombat。同时扫 CrossSceneState 与当前区域，不进每帧路径。
        /// </summary>
        internal static void CleanupOrphansOutsideCombat()
        {
            try
            {
                var game = Kingmaker.Game.Instance;
                var tc = game != null ? game.TurnController : null;
                if (game == null || tc == null || tc.InCombat || tc.TurnBasedModeActive) return;
                var states = new System.Collections.Generic.List<Kingmaker.EntitySystem.SceneEntitiesState>();
                var cross = game.Player != null ? game.Player.CrossSceneState : null;
                var main = game.State != null && game.State.LoadedAreaState != null
                    ? game.State.LoadedAreaState.MainState : null;
                if (cross != null) states.Add(cross);
                if (main != null && !ReferenceEquals(main, cross)) states.Add(main);

                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var state in states)
                {
                    if (state.AllEntityData == null) continue;
                    var snapshot = new System.Collections.Generic.List<Kingmaker.EntitySystem.Entities.Base.Entity>(state.AllEntityData);
                    foreach (var entity in snapshot)
                    {
                        var unit = entity as BaseUnitEntity;
                        if (!IsOurServitor(unit)) continue;
                        string uid = unit.UniqueId;
                        if (!string.IsNullOrEmpty(uid) && !seen.Add(uid)) continue;
                        QueueDestroy(unit, SummonerGone(unit) ? "召唤者已死亡/不存在" : "战斗已结束");
                    }
                }
            }
            catch (Exception e) { Main.LogError("[召唤机仆] 战外遗留清理失败: " + e.Message); }
        }

        /// <summary>
        /// 全局战斗退出后清理。不能挂单个单位的 LeaveCombat：原版当时仍按索引遍历
        /// UnitGroup，修改成员会跳过后续单位；而且单个机仆可能早于整场战斗离场。
        /// ExitTb Postfix 执行时 Data.InCombat 已 false，先攻/士气组也已清完，是安全边界。
        /// </summary>
        [HarmonyPatch(typeof(TurnController), "ExitTb")]
        internal static class CleanupAfterCombat
        {
            private static void Postfix()
            {
                try { RetinueRegistry.FlushPendingDestroy(); } catch { }
                try { CleanupOrphansOutsideCombat(); } catch { }
            }
        }

        // ═══ 每场战斗一次 ═══
        //
        // ★为什么挂 RuleCalculateCooldown 而不是 AbilityData.IsAvailable★
        //   IsAvailable 有 18 个调用点（含 AI 的 DecisionContext.IsUsableAbility 和 5 个 UI 槽位类），
        //   是 UI 刷新级热路径；而 RuleCalculateCooldown 全局只有一个触发点
        //   （PartAbilityCooldowns.StartCooldown），后者又只有一个调用点（UnitUseAbility.OnAction）
        //   —— 即「技能真正打出去」才跑一次。量级完全不同。
        //
        // ★WarhammerCooldown 不进存档★ 它是 BlueprintComponent 子类但**不是 SimpleBlueprint**
        //   （没有 AssetGuid），运行时 new 一个只是普通 C# 对象。
        //   存档里落的是 CooldownData 的 int+bool，key 是原版 BlueprintAbility ⇒ 不碰红线。
        //
        // ★已知边界（不是 bug，是引擎语义）★
        //   StartCooldown 开头就 `if (!Owner.IsInCombat) return;`
        //   ⇒ **战斗外施放不记冷却**，「每场一次」只在战斗内成立。
        [HarmonyPatch(typeof(Kingmaker.RuleSystem.Rules.RuleCalculateCooldown), "OnTrigger")]
        internal static class OncePerCombat
        {
            private static readonly Kingmaker.UnitLogic.FactLogic.WarhammerCooldown _cd =
                new Kingmaker.UnitLogic.FactLogic.WarhammerCooldown
                { CooldownInRounds = 0, UntilEndOfCombat = true };

            private static void Postfix(Kingmaker.RuleSystem.Rules.RuleCalculateCooldown __instance)
            {
                try
                {
                    if (!Main.Enabled || __instance == null) return;
                    var ab = __instance.Ability;
                    var bp = ab != null ? ab.Blueprint : null;
                    if (bp == null) return;
                    string g = null;
                    try { g = bp.AssetGuid.ToString(); } catch { }
                    if (!string.Equals(g, DonorAbility, StringComparison.OrdinalIgnoreCase)) return;

                    var caster = ab.Caster as BaseUnitEntity;
                    if (caster == null || !Applies(caster)) return;

                    EnsureMembers();
                    if (_pCdComp == null) return;            // 取不到就别硬来，上面已经报过了
                    __instance.Result = 0;
                    _pCdComp.SetValue(__instance, _cd, null); // 0 回合 + 直到战斗结束 = 每场一次
                }
                catch { }
            }
        }
    }
}
