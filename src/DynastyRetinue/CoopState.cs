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
        /// <summary>联机相关类型能否正常访问。任何一次取值抛异常就永久降级。</summary>
        public static bool Available { get; private set; } = true;

        /// <summary>是否处于合作会话中（房间已开局）。</summary>
        public static bool InSession { get { return Get(RawIsActive); } }

        /// <summary>是否真的有别人在（1 个人的房间不算）。</summary>
        public static bool IsMultiplayer { get { return Get(RawIsMultiplayer); } }

        /// <summary>本机是不是房主。</summary>
        public static bool IsHost { get { return Get(RawIsGameOwner, true); } }

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
                if (!Available) return "";
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
        public static void DumpLocalMods()
        {
            try
            {
                var t = Type.GetType("Kingmaker.Utility.ModsInfo.UserModsData, Utility.ModsInfo");
                if (t == null) { Main.Log("[合作] 取不到 UserModsData 类型，跳过 mod 清单。"); return; }
                var inst = t.GetProperty("Instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                if (inst == null) return;
                var list = t.GetField("UsedMods")?.GetValue(inst) as System.Collections.IEnumerable;
                if (list == null) return;

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
                Main.Log("[合作] 本机上报给房间的 mod 清单（共 " + n + " 个）——"
                       + "两边各导一份诊断包对比这一段，就知道不一致差在哪：" + sb);
            }
            catch (Exception e) { Main.Log("[合作] 读取 mod 清单失败（不影响功能）：" + e.Message); }
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
                    string line = n + "=" + (v ?? "null");
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
            if (!Available) return onFail;
            try { return f(); }
            catch (Exception e) { Degrade(e); return onFail; }
        }

        private static int GetInt(Func<int> f, int onFail)
        {
            if (!Available) return onFail;
            try { return f(); }
            catch (Exception e) { Degrade(e); return onFail; }
        }

        /// <summary>永久降级。只喊一次，免得每帧刷屏。</summary>
        private static void Degrade(Exception e)
        {
            Available = false;
            Main.LogError("[合作] 读取联机状态失败，本局不再尝试：" + e.Message);
        }
    }
}
