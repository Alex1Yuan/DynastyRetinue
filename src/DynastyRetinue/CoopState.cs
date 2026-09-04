using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Kingmaker.Networking;

namespace DynastyRetinue
{
    /// <summary>
    /// 官方合作模式的只读状态。**不改变任何游戏状态**，只回答"现在是不是在联机、
    /// 是不是房主、双方 mod 一不一致"。
    ///
    /// ★为什么先做这一层★
    ///   实测已证明：在合作里从本 mod 面板招募，会立刻触发不同步提示。
    ///   根因不是随机数（那两处已经消掉了），而是**架构性的**——
    ///   官方合作是 lockstep：两台机器各跑一遍同样的模拟，只同步**指令**。
    ///   而面板点招募是**直接改状态**，只有一台机器执行了，帧末对哈希必然对不上。
    ///
    ///   更糟的是 Uuid：新实体的 UniqueId 来自 `Uuid.Instance`，而它是
    ///   `StatefulRandom` —— 随机状态属于同步状态的一部分。单边生成一个单位
    ///   不只是当场 desync，还会把这台机器的随机流**永久推快一格**，
    ///   之后每一次原版生成的 id 都跟着错位。
    ///   所以红线是：**mod 生成实体必须两台都做，或者两台都不做。**
    ///
    /// ★这一层的用途★
    ///   ① 让面板能如实告诉玩家"你现在在联机，这些操作还不安全"
    ///   ② 接住官方自带的 mod 握手结果（版本不一致时提示）
    ///   ③ 后续走指令通道时，用它判断该走本地执行还是入队广播
    ///
    /// ★为什么每个访问点都单独包一层 NoInlining★
    ///   这些类型来自 Code.dll。万一某个游戏版本改了签名，JIT 在**首次执行到
    ///   包含该引用的方法体**时抛 TypeLoadException —— 写在同一个方法里的
    ///   try/catch 是拦不住自己这个方法的加载失败的。拆成独立的小方法，
    ///   失败就被限制在那一个取值上，其余功能照常。
    /// </summary>
    internal static class CoopState
    {
        /// <summary>
        /// 联机类型在当前版本是否曾成功初始化。读取异常不再永久置 false：网络状态在
        /// 进房/退房/加载边沿可能瞬时不可读，永久熔断会形成“必须同步但无人能发”的死锁。
        /// 每次核心读取都独立 fail-closed，并允许下一次恢复。
        /// </summary>
        public static bool Available { get; private set; } = true;
        private static bool _readFailureLogged;

        /// <summary>是否处于合作会话中（房间已开局）。</summary>
        public static bool InSession { get { return Get(RawIsActive); } }

        /// <summary>可区分“明确 false”和“本轮读取失败”的会话状态读取。</summary>
        public static bool TryGetInSession(out bool value)
        {
            value = false;
            try { value = RawIsActive(); Available = true; return true; }
            catch (Exception e) { Degrade(e); return false; }
        }

        /// <summary>
        /// 可区分“明确不需要联机安全模式”和“本轮网络 API 读取失败”。
        /// 退出清理必须用它，不能只看 IsActive：进房/掉线边沿 PlayersCount 可能仍大于 1。
        /// </summary>
        public static bool TryGetSharedGameplayRequired(out bool value)
        {
            value = true;
            try
            {
                value = RawIsActive() || RawIsMultiplayer();
                Available = true;
                return true;
            }
            catch (Exception e) { Degrade(e); return false; }
        }

        /// <summary>是否真的有别人在（1 个人的房间不算）。</summary>
        public static bool IsMultiplayer { get { return Get(RawIsMultiplayer); } }

        /// <summary>
        /// 影响同步量的玩法补丁该不该按联机安全模式运行。
        ///
        /// ★和 IsMultiplayer 的关键区别：失败时返回 true★
        ///   普通只读诊断取不到状态时可以不报警（IsMultiplayer 的 onFail=false）；
        ///   但玩法闸不能：一台读失败回落本机设置、另一台读成功强制开，伤害落点/AI
        ///   当场分叉。这里 fail-closed：拿不准就按联机处理。代价至多是极端异常下
        ///   单机暂时关不掉近战精英支持；收益是不制造静默不同步。
        /// </summary>
        public static bool SharedGameplayRequired
        {
            get
            {
                try
                {
                    // ★看会话，不看瞬时玩家数★ 加入/掉线过程中 PlayersCount 可能在两端
                    // 不同帧变成 1/2；用 IsMultiplayer 会让同一条同步指令一端放行、一端拦截。
                    // 只要官方合作会话还活着就保持安全模式，房间真正结束后才解除。
                    bool value = RawIsActive() || RawIsMultiplayer();
                    Available = true;
                    return value;
                }
                catch (Exception e) { Degrade(e); return true; }
            }
        }

        /// <summary>本机是不是房主。仅用于显示；读取失败时沿用历史行为显示为房主。</summary>
        public static bool IsHost { get { return Get(RawIsGameOwner, true); } }

        /// <summary>
        /// 本机能否发起自动同步动作。与 IsHost 不同，这里必须 fail-closed：
        /// 若联机 API 读取失败，让两端都可能自称房主会重复发送 rescue/placeguards。
        /// 自动动作宁可本轮不触发，也不能有两个发起者。
        /// </summary>
        public static bool IsConfirmedHost
        {
            get
            {
                try { bool value = RawIsGameOwner(); Available = true; return value; }
                catch (Exception e) { Degrade(e); return false; }
            }
        }

        /// <summary>可区分“明确不是房主”和“本轮读取失败”的房主状态读取。</summary>
        public static bool TryGetConfirmedHost(out bool value)
        {
            value = false;
            try { value = RawIsGameOwner(); Available = true; return true; }
            catch (Exception e) { Degrade(e); return false; }
        }

        public static int PlayerCount { get { return GetInt(RawPlayerCount, 1); } }

        // ---- mod 一致性：官方自带的握手结果 ----
        // IsSameMods 内部是 LINQ 全比对，会分配；面板是 IMGUI，一帧至少两轮事件，
        // 所以节流到一秒一次。联机状态本来就不会毫秒级变化。
        private static float _modsCheckedAt = -999f;
        private static bool _modsMatch = true;
        private static bool _modsDumped;

        /// <summary>双方 mod 列表（Id + 版本）是否一致。取不到就当一致，不制造假警报。</summary>
        public static bool ModsMatch
        {
            get
            {
                try
                {
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (now - _modsCheckedAt < 1f) return _modsMatch;
                    _modsCheckedAt = now;
                    bool before = _modsMatch;
                    _modsMatch = Get(RawIsSameMods, true);
                    // 只在**刚变成不一致**的那一刻打一次清单，不是每秒刷屏
                    if (before && !_modsMatch) { _modsDumped = false; }
                    if (!_modsMatch && !_modsDumped) { _modsDumped = true; DumpLocalMods(); }
                }
                catch { }
                return _modsMatch;
            }
        }

        /// <summary>
        /// 一句话现状，给 UMM 面板和日志用。
        /// 不在联机时返回空串 —— 单机玩家不该看见任何联机字样。
        /// </summary>
        public static string Describe()
        {
            try
            {
                if (!InSession) return "";
                string who = IsHost ? L.T("房主") : L.T("加入方");
                string s = L.F("合作模式：{0}　{1} 人　设置指纹 {2}", who, PlayerCount, SettingsFingerprint());
                if (!ModsMatch) s += L.T("　★双方 mod 列表不一致★");
                return s;
            }
            catch { return ""; }
        }

        /// <summary>
        /// 把**本机**上报给房间的 mod 清单打进日志。
        ///
        /// ★为什么值得单独打★
        ///   「双方 mod 列表不一致」是游戏自带的握手结论（ModsNetManager.IsSameMods），
        ///   它比对的是 UserModsData.Instance.UsedMods 里每个 mod 的 **Id + 版本号**，
        ///   涵盖玩家装的**所有** mod，不只是本 mod。玩家看到这句话时最自然的反应是
        ///   "我这个 mod 明明是一样的" —— 而真正差的往往是 ToyBox 之类的别人。
        ///   两边各导一份诊断包，把这段一对比就知道差在哪，不用猜。
        ///
        ///   顺带澄清一个常见误解：**DLC 不走这条**。DLC 有独立的 DlcNetManager，
        ///   DLC 不同会由那边报，不会让 mod 列表判定不一致。
        ///
        /// ★为什么用反射★
        ///   UserModsData 在 Utility.ModsInfo.dll 里，本 mod 没引用那个程序集。
        ///   为一行诊断多加一个引用不划算，而且引用越多、游戏版本一变越容易整体加载失败。
        ///   反射失败就静默跳过 —— 这只是诊断信息，不该影响任何功能。
        /// </summary>
        public static string LocalModsText()
        {
            try
            {
                var t = Type.GetType("Kingmaker.Utility.ModsInfo.UserModsData, Utility.ModsInfo");
                if (t == null) return "（取不到 UserModsData 类型）";
                var inst = t.GetProperty("Instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                if (inst == null) return "（UserModsData.Instance 为空）";
                var list = t.GetField("UsedMods")?.GetValue(inst) as System.Collections.IEnumerable;
                if (list == null) return "（UsedMods 为空）";

                var sb = new System.Text.StringBuilder();
                int n = 0;
                foreach (var m in list)
                {
                    if (m == null) continue;
                    var mt = m.GetType();
                    string id  = mt.GetField("Id")?.GetValue(m) as string
                              ?? mt.GetProperty("Id")?.GetValue(m) as string ?? "?";
                    object v   = mt.GetField("Version")?.GetValue(m)
                              ?? mt.GetProperty("Version")?.GetValue(m);
                    sb.Append(Environment.NewLine).Append("    ").Append(++n).Append(") ")
                      .Append(id).Append("  ").Append(v);
                }
                return "本机上报给房间的 mod 清单（共 " + n + " 个）：" + sb;
            }
            catch (Exception e) { return "（读取 mod 清单失败：" + e.Message + "）"; }
        }

        public static void DumpLocalMods()
        {
            Main.Log("[合作] " + LocalModsText()
                   + "\n    两边各导一份诊断包对比这一段，就知道不一致差在哪。");
        }

        /// <summary>
        /// 影响玩法的设置的指纹（8 位十六进制）。
        ///
        /// ★为什么必须有这个★
        ///   指令通道只能同步**离散动作**（招募、换船、改名……）。但本 mod 还有一大类
        ///   **被动规则** —— 士气隔离、卫兵经验缩放、灵能不推高亚空间威胁、
        ///   舰船多打一发 / 护盾护甲加成 / 射程加成、近战中可开火……
        ///   它们是 Harmony 补丁在战斗中**持续**读 Main.Settings 算出来的。
        ///
        ///   两个玩家的开关不一样，就等于两台机器在用**不同的规则**跑同一场战斗：
        ///   伤害、命中、士气、护盾值全都会分叉，而这些都进哈希。
        ///   陆战和海战都躲不掉，而且**游戏自带的握手查不到** ——
        ///   它只比对 mod 的 Id 和版本号，配置根本不参与。
        ///
        /// ★为什么用反射而不是手写字段清单★
        ///   手写的清单一定会漂：以后加个新开关，没人记得回来补一行，
        ///   指纹就变成"看起来一致、其实不一致"，比没有还危险。
        ///   反射把**所有**公开字段都算进去，只排除明确与玩法无关的几个。
        ///
        /// ★为什么排除那几个★
        ///   Panel* 是面板折叠状态、Language 是界面语言、InspectFilter / ItemQuery /
        ///   DebugXpAmount 是诊断输入框、FontOverride 已废弃 —— 都不参与任何玩法计算，
        ///   算进去只会让两个玩家因为"我展开了舰船那一栏"而收到假警报。
        /// </summary>
        /// <summary>
        /// 不参与玩法、因而不进指纹的字段。
        ///
        /// ★宁可漏排，不可错排★
        ///   排错一个（把真正影响玩法的排掉了），指纹就变成"看起来一致、其实不一致"——
        ///   比没有指纹更危险，因为它会让人放心。所以这个清单只收
        ///   **能一眼确认与任何数值计算无关**的：界面语言、诊断输入框、
        ///   日志开关、快捷键名、以及界面上次选了什么的记忆值。
        ///
        ///   反过来，像 StuckRescue（会挪动卫兵位置）、PreviewAsPlayer、
        ///   EliteCanBeDowned 这些即使看着像"测试项"也一律**保留在指纹里** ——
        ///   只要它可能改变任何进哈希的量，就得算。
        /// </summary>
        /// <summary>
        /// 这个字段算不算进指纹。
        /// 抽成公开判据是为了让「跨机器核对设置」用**完全同一套口径** ——
        /// 两处各写一遍排除逻辑，早晚会漂成"指纹说一致、逐项核对说不一致"。
        /// </summary>
        public static bool CountsForFingerprint(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("Panel", StringComparison.Ordinal)) return false;  // 面板折叠状态
            return Array.IndexOf(LocalOnly, name) < 0;
        }

        private static readonly string[] LocalOnly =
        {
            "Language",          // 界面语言
            "InspectFilter",     // 探测用关键词框
            "ItemQuery",         // 物品查询框
            "DebugXpAmount",     // 调试给经验的输入框
            "FontOverride",      // 已废弃
            "WatchMomentum",     // 「详细日志」开关，只影响日志量
            "LastAugmentTier",   // 界面记住的上次选择
            "SpawnKeyName",      // 快捷键绑定
            "DespawnKeyName",    // 快捷键绑定
            "RecruitNpcKeys",    // 对话入口匹配的 NPC 关键词，只影响入口出现在哪
            // 合作会话中 Appearance/Doll 禁止完整 View/AttachView 重建；LookMatrix 仅保留为
            // 单机外观偏好。HideGearLook 只过滤渲染层 EquipmentEntity，不写实体状态。
            "LookMatrix", "HideGearLook",

            // 纯开发测试：代码另有 Main.DevMode 硬闸，普通发布环境即使旧设置残留 true 也不生效。
            // 算进指纹只会让作者开发机和普通玩家永久假红。
            "AutoEndPlayerTurn",

            // —— 已隐藏/废弃的近战精英旧开关 ——
            // 运行时已经全部归并到 MeleeEliteSupport；这些字段仅为旧 Settings.xml
            // 反序列化兼容而保留。两边历史值不同不会改变玩法，算进指纹/逐项对比只会假红。
            "SwordClassGate", "DeathFromAboveGate", "ReaperUltimateGate",
            "BladeDanceGate", "DeathWaltzAoeFix", "AnimClipFallback",

            // —— 近战精英主开关：联机时**生效值被 Main 强制为 true** ——
            //   字段里保存的只是「退出联机后，本机想不想继续开」这个个人偏好。
            //   两边字段不同不会让规则分叉；算进指纹只会制造假警报。
            //   ★这里只能排主开关本身★ WoundAbilityHpFloor / MeleeEliteNoEndTurn
            //   仍然直接影响生命消耗闸和回合结束，必须继续进指纹。
            "MeleeEliteSupport",

            // —— 海战相机：**纯本地视觉**，改的是场景里 CameraZoom 的运行时字段 ——
            //   不进存档、不影响任何判定，联机双方各看各的镜头本来就天经地义。
            //   （硬要同步反而错：两个人的显示器和偏好不一样。）
            // 移动格铺开也是纯视觉：只影响绿格画多大，不碰寻路、不碰落点合法性。
            "ShipGridBySize",
            "ShipHologramFix",
            "StarMapShipModel",
            "ShipRangeFocusFix",
            "ShipViewCenterFix",
            "GridShiftX",
            "GridShiftZ",
            // 相机后推：每帧重算的纯视觉量，不进任何判定。
            "CamPushEnabled",
            "CamPushCruiser",
            "CamPushGrand",

            // —— 舰船挂点：**纯视觉**，且本来就不是"偏好" ——
            //   ProwDropRatio / ProwZBackRatio 在 ShipMountFallback 里只用来算
            //   舰首武器模型的挂载坐标（`broadsideY - ProwDropRatio * span` 之类），
            //   算出来的是一个 Vector3 摆放位置，进不了任何被哈希的量。
            //   ProwLearned / ProwLearnedFrom 是"有没有学到挂点数据"的标记。
            //
            //   ★为什么必须排掉★ 这几个值是各人在自己船上实测学出来的，
            //   两台机器几乎**必然**不同（实机差异：0.7728229 vs 0.784）。
            //   留在指纹里就是常驻假警报 —— 玩家每次核对都看到"不一致"，
            //   真正要紧的那几项反而被淹没。
            //
            //   ★有先例可循★ ResetSettingsToDefault 的 keep 清单里正好也是这四个
            //   加 PreviewAsPlayer，理由写的是"那是实测学到的挂点数据，不是偏好"。
            //   同一个判据：不是偏好 ⇒ 不该参与"双方设置是否一致"的比对。
            "ProwLearned", "ProwLearnedFrom", "ProwDropRatio", "ProwZBackRatio",
            "ProwRamClearance",   // 只参与武器模型 localPosition；不参与命中/伤害/射程
            "PreviewAsPlayer",
        };

        private static float _fpAt = -999f;
        private static string _fpCache = "--------";

        public static string SettingsFingerprint()
        {
            // ★必须缓存★
            //   这个函数要反射 83 个字段、逐个 ToString、再拼串哈希。
            //   而 Describe() 是在 UMM 的 IMGUI 里调的 —— IMGUI 一帧至少跑
            //   Layout 和 Repaint 两轮事件，等于面板开着时每帧反射 166 次。
            //   设置只有玩家动手时才变，一秒一次绰绰有余。
            try
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _fpAt < 1f) return _fpCache;
                _fpAt = now;
            }
            catch { }
            _fpCache = ComputeFingerprint();
            return _fpCache;
        }

        private static string ComputeFingerprint()
        {
            try
            {
                var st = Main.Settings;
                if (st == null) return "--------";
                var fields = st.GetType().GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var names = new List<string>();
                foreach (var f in fields)
                {
                    string n = f.Name;
                    if (!CountsForFingerprint(n)) continue;
                    names.Add(n);
                }
                names.Sort(StringComparer.Ordinal);

                // FNV-1a：够稳定、够短，不需要密码学强度 —— 只是让两个人肉眼比对
                uint h = 2166136261u;
                foreach (var n in names)
                {
                    object v = null;
                    try { v = st.GetType().GetField(n).GetValue(st); } catch { }
                    // ★必须与 CoopSettings 的逐项对比同一口径★ object.ToString() 受系统区域
                    // 设置影响：同一个 0.5 在中文/德文机器上会变成 0.5 / 0,5，指纹假红，
                    // 但展开逐项对比又说完全一致。数字统一 InvariantCulture。
                    string text;
                    if (v == null)       text = "null";
                    else if (v is bool)  text = ((bool)v) ? "1" : "0";
                    else if (v is int)   text = ((int)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    else if (v is float) text = ((float)v).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    else                 text = v.ToString();
                    string line = n + "=" + text;
                    foreach (char c in line) { h ^= c; h *= 16777619u; }
                }
                return h.ToString("x8");
            }
            catch { return "--------"; }
        }

        // ------------------------------------------------------------------
        // 原始取值。每个都独立、NoInlining，见类注释。
        // ------------------------------------------------------------------
        /// <summary>
        /// 本机在房间里的唯一 id。用来识别"这条指令是我自己发的"。
        ///
        /// ★为什么需要★
        ///   指令会在**两台机器上都执行**（那正是它的用途）。于是点【核对双方设置】
        ///   的那一台也会收到自己发的那份，拿自己的数据和自己比 —— 永远显示"完全一致"，
        ///   而对端才看到真正的差异。实机截图里房主显示一致、加入方显示 6 项不一致，
        ///   就是这么来的。
        ///   用 id 而不是"房主/加入方"标签：标签在两人局里够用，但三人局里
        ///   两个加入方会互相误判。
        /// </summary>
        public static string LocalUserId
        {
            get
            {
                try { return RawLocalUserId() ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>
        /// 常态零分配地核对当前 lockstep 成员是否仍等于一份已排序快照。
        /// 最多比较 6×6 个短字符串；不构造 List/数组/哈希/字符串。
        /// </summary>
        public static bool TryActivePlayersMatch(string[] sortedExpected, out bool matches)
        {
            matches = false;
            try
            {
                bool ok = RawActivePlayersMatch(sortedExpected, out matches);
                Available = true;
                return ok;
            }
            catch (Exception e) { Degrade(e); return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RawActivePlayersMatch(string[] sortedExpected, out bool matches)
        {
            matches = false;
            if (sortedExpected == null || !NetworkingManager.IsActive
                || !PhotonManager.Initialized || PhotonManager.Instance == null) return false;
            var active = PhotonManager.Instance.ActivePlayers;
            int expectedCount = NetworkingManager.PlayersCount;
            if (expectedCount < 1 || active.Count != expectedCount) return false;
            if (active.Count != sortedExpected.Length) { matches = false; return true; }

            for (int i = 0; i < active.Count; i++)
            {
                string id = active[i].UserId;
                if (string.IsNullOrEmpty(id)) return false;
                bool found = false;
                for (int j = 0; j < sortedExpected.Length; j++)
                    if (string.Equals(id, sortedExpected[j], StringComparison.Ordinal))
                    { found = true; break; }
                if (!found) { matches = false; return true; }
            }
            // 反向再查一次：防御异常的重复 UserId，例如 active=[A,A]、expected=[A,B]。
            for (int i = 0; i < sortedExpected.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < active.Count; j++)
                    if (string.Equals(sortedExpected[i], active[j].UserId, StringComparison.Ordinal))
                    { found = true; break; }
                if (!found) { matches = false; return true; }
            }
            matches = true;
            return true;
        }

        /// <summary>
        /// 只在成员确实变化时读取当前玩家并生成 generation。
        /// 代号是「排序后的 UserId 长度前缀串」的 UTF-8 Base64；同人数换人也一定变化。
        /// 读取失败、UserId 为空/重复、ActivePlayers 尚未追上 PlayerCount 时返回 false。
        /// </summary>
        public static bool TryGetActivePlayerGeneration(out string generation, out string[] userIds)
        {
            generation = "";
            userIds = new string[0];
            try
            {
                bool ok = RawActivePlayerGeneration(out generation, out userIds);
                Available = true;
                return ok;
            }
            catch (Exception e)
            {
                Degrade(e);
                generation = "";
                userIds = new string[0];
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RawActivePlayerGeneration(out string generation, out string[] userIds)
        {
            generation = "";
            userIds = new string[0];
            if (!NetworkingManager.IsActive || !PhotonManager.Initialized || PhotonManager.Instance == null)
                return false;

            var active = PhotonManager.Instance.ActivePlayers;
            int expected = NetworkingManager.PlayersCount;
            if (expected < 1 || active.Count != expected) return false;

            var ids = new List<string>(active.Count);
            for (int i = 0; i < active.Count; i++)
            {
                string id = active[i].UserId;
                if (string.IsNullOrEmpty(id)) return false;
                ids.Add(id);
            }
            ids.Sort(StringComparer.Ordinal);
            for (int i = 1; i < ids.Count; i++)
                if (string.Equals(ids[i - 1], ids[i], StringComparison.Ordinal)) return false;

            userIds = ids.ToArray();
            generation = BuildPlayerGeneration(userIds);
            return !string.IsNullOrEmpty(generation);
        }

        /// <summary>为已排序且非空的 UserId 列表生成精确 generation；供同步协议验包。</summary>
        internal static string BuildPlayerGeneration(string[] sortedUserIds)
        {
            if (sortedUserIds == null || sortedUserIds.Length == 0) return "";
            var raw = new System.Text.StringBuilder();
            raw.Append(sortedUserIds.Length).Append('|');
            foreach (string id in sortedUserIds)
            {
                if (string.IsNullOrEmpty(id)) return "";
                raw.Append(id.Length).Append(':').Append(id);
            }
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw.ToString()));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string RawLocalUserId() { return PhotonManager.Instance.LocalPlayerUserId; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RawIsActive() { return NetworkingManager.IsActive; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RawIsMultiplayer() { return NetworkingManager.IsMultiplayer; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RawIsGameOwner() { return NetworkingManager.IsGameOwner; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RawPlayerCount() { return NetworkingManager.PlayersCount; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RawIsSameMods() { return PhotonManager.Mods.IsSameMods; }

        private static bool Get(Func<bool> f, bool onFail = false)
        {
            try { bool v = f(); Available = true; return v; }
            catch (Exception e) { NoteReadFailure(e); return onFail; }
        }

        private static int GetInt(Func<int> f, int onFail)
        {
            try { int v = f(); Available = true; return v; }
            catch (Exception e) { NoteReadFailure(e); return onFail; }
        }

        /// <summary>只记录第一次瞬时失败；不永久熔断，后续读取继续尝试。</summary>
        private static void Degrade(Exception e) { NoteReadFailure(e); }

        private static void NoteReadFailure(Exception e)
        {
            Available = false;
            if (_readFailureLogged) return;
            _readFailureLogged = true;
            Main.LogError("[合作] 读取联机状态暂时失败；本轮按安全模式处理，后续会重试：" + e.Message);
        }
    }
}
