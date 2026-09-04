using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace DynastyRetinue
{
    /// <summary>
    /// 把"会影响一次招募结果"的设置打包随指令发走，执行侧临时套用、跑完还原。
    ///
    /// ★为什么必须这么做★
    ///   实机抓到的第一个真实分叉就是这个：两台机器收到**同一条** kgd.recruit，
    ///   生成了**同一个** uid、同一个蓝图、同一个落点、同一个 brain、同一个名字，
    ///   但结果天差地别 ——
    ///
    ///       A（AlignExperience 默认开）  成长 lv0 -> 31   xp=42798   加点 43 个
    ///       B（AlignExperience 被关掉）  成长 lv0 -> 1    xp=0       加点  0 个
    ///
    ///   指令送到了、也一起执行了，可**两边用的规则不一样**，所以做出来的不是同一个人。
    ///   等级、属性、天赋、装备全不同，而这些都进哈希。
    ///
    /// ★为什么不逐个塞成参数★
    ///   招募一条路要读 19 个设置（生成 + 加点 + 发装备三段）。手写参数清单
    ///   一定会漏 —— 今天漏的就是 AlignExperience。而漏一个的后果不是"少个功能"，
    ///   是"看起来一起做了，其实做出两个不同的东西"，比直接报错难查得多。
    ///   用反射按名字批量抓，以后加新开关只要名字进 Keys 就行。
    ///
    /// ★为什么是"临时套用 + 还原"而不是永久同步★
    ///   设置是玩家的个人偏好，不该被联机对端改掉。这里只在**执行那一条指令的
    ///   同步调用期间**借用发起方的值，函数返回前一定还原 —— 玩家面板上看到的
    ///   始终是自己的设置。
    ///
    /// ★为什么这不违反"执行侧不许读本机设置"★
    ///   恰恰相反，这正是那条铁律的实现方式：执行侧读到的**是发起方发来的值**，
    ///   只是借了 Main.Settings 这个容器来传递，免得把 19 个值一路透传到每个函数。
    /// </summary>
    internal static class CoopSettings
    {
        /// <summary>
        /// 一次招募会读到的全部设置字段名。
        ///
        /// 来源是对 RetinueTest / GearTool 里 `Settings.xxx` 的实际扫描，
        /// 外加它们内部那几个组合判据（NoCountCap / NoPfGate / NoLevelCap /
        /// NoEliteCountCap / NoEliteUnlockGate）真正依赖的 Unlock* 原始字段 ——
        /// 组合方法本身不是字段，同步它依赖的输入才有意义。
        /// </summary>
        private static readonly string[] Keys =
        {
            // —— 生成与跟随
            // Language 必须同步的是已经解析后的 L.Current(1/2)，不能发原始 Auto=0；
            // 否则中英文游戏两端仍会在 ApplyName 中写出不同 CustomName。
            "Language", "UnitAssetId", "ArchetypeIndex", "AttachFollow", "IsolateMomentum",
            "GuardsCanShootInMelee",
            // 外观会决定异步创建/重建哪个完整 UnitEntityView（含 AnimationManager），
            // 不是纯颜色；必须随招募命令同步，不能命令返回后再读各自本机矩阵。
            "LookMatrix", "HideGearLook",
            // —— 等级与经验（今天翻车的就在这一组）
            "AlignExperience", "AutoLevelUp", "ScaleGuardXp", "XpRatio",
            "XpCatchUp", "XpCatchUpMax", "XpCatchUpSpan",
            // —— 名额闸门（NoCountCap / NoPfGate / NoLevelCap 的原始输入）
            "UnlockTierLimits", "UnlockPfGate", "UnlockCountCap", "UnlockLevelCap",
            "RecruitUsePfGate", "RecruitPfPerGuard", "RecruitMaxGuards",
            // —— 精英闸门（NoEliteCountCap / NoEliteUnlockGate 的原始输入）
            "UnlockEliteLimit", "EliteIgnoreUnlock", "EliteLimitPerArchetype", "EliteCanBeDowned",
            // —— 装备
            "EquipGraduationGear", "GearTierOverride",
        };

        /// <summary>把当前设置打成 "名=值" 数组，直接当指令参数发走。</summary>
        public static List<string> Capture()
        {
            var outp = new List<string>(Keys.Length);
            try
            {
                var st = Main.Settings;
                if (st == null) return outp;
                var t = st.GetType();
                foreach (string k in Keys)
                {
                    var f = t.GetField(k, BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) continue;                 // 字段改名/删除 —— 跳过，不要崩
                    object v = null;
                    try { v = k == "Language" ? (object)L.Current : f.GetValue(st); } catch { continue; }
                    outp.Add(k + "=" + ToText(v));
                }
            }
            catch (Exception e) { Main.LogError("[合作] 打包设置失败：" + e.Message); }
            return outp;
        }

        /// <summary>
        /// 套用发起方的设置，返回一个"还原器"。
        /// **调用方必须用 try/finally 保证还原**，否则玩家的设置会被联机对端改掉。
        /// </summary>
        public static Dictionary<string, object> Apply(string[] args, int from)
        {
            var saved = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                var st = Main.Settings;
                if (st == null || args == null) return saved;
                var t = st.GetType();
                for (int i = from; i < args.Length; i++)
                {
                    string kv = args[i];
                    if (string.IsNullOrEmpty(kv)) continue;
                    int eq = kv.IndexOf('=');                // 只切第一个 = ，值里可以有 =
                    if (eq <= 0) continue;
                    string k = kv.Substring(0, eq);
                    string v = kv.Substring(eq + 1);
                    if (Array.IndexOf(Keys, k) < 0) continue;   // 只认白名单，别让对端写任意字段
                    var f = t.GetField(k, BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) continue;
                    object cur;
                    try { cur = f.GetValue(st); } catch { continue; }
                    object parsed;
                    if (!FromText(f.FieldType, v, out parsed)) continue;
                    saved[k] = cur;
                    try { f.SetValue(st, parsed); } catch { saved.Remove(k); }
                }
            }
            catch (Exception e) { Main.LogError("[合作] 套用设置失败：" + e.Message); }
            return saved;
        }

        /// <summary>
        /// 两阶段自动恢复专用的严格套用：所有协议字段必须恰好出现一次且成功写入。
        /// 普通 Apply 为兼容旧协议会静默跳过坏字段，不能拿它判断“本机已 ready”。
        /// </summary>
        public static bool TryApplyExact(string[] args, int from,
                                         out Dictionary<string, object> saved, out string failure)
        {
            saved = new Dictionary<string, object>(StringComparer.Ordinal);
            failure = "";
            try
            {
                var st = Main.Settings;
                if (st == null || args == null) { failure = "Settings 或载荷为空"; return false; }
                if (from < 0 || args.Length - from != Keys.Length)
                { failure = "设置字段数量不符"; return false; }

                var t = st.GetType();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = from; i < args.Length; i++)
                {
                    string kv = args[i];
                    int eq = !string.IsNullOrEmpty(kv) ? kv.IndexOf('=') : -1;
                    if (eq <= 0) { failure = "设置项格式无效"; Restore(saved); return false; }
                    string k = kv.Substring(0, eq);
                    string v = kv.Substring(eq + 1);
                    if (Array.IndexOf(Keys, k) < 0 || !seen.Add(k))
                    { failure = "未知或重复设置字段 " + k; Restore(saved); return false; }
                    var f = t.GetField(k, BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) { failure = "本机缺少设置字段 " + k; Restore(saved); return false; }
                    object parsed;
                    if (!FromText(f.FieldType, v, out parsed))
                    { failure = "设置字段解析失败 " + k; Restore(saved); return false; }
                    object cur = f.GetValue(st);
                    saved[k] = cur;
                    try { f.SetValue(st, parsed); }
                    catch (Exception e)
                    { failure = "设置字段写入失败 " + k + "：" + e.Message; Restore(saved); return false; }
                }
                if (seen.Count != Keys.Length)
                { failure = "设置字段不完整"; Restore(saved); return false; }
                return true;
            }
            catch (Exception e)
            {
                failure = "严格套用设置失败：" + e.Message;
                Restore(saved);
                return false;
            }
        }

        /// <summary>还原 Apply 之前的值。任何情况下都要调到。</summary>
        public static void Restore(Dictionary<string, object> saved)
        {
            if (saved == null || saved.Count == 0) return;
            try
            {
                var st = Main.Settings;
                if (st == null) return;
                var t = st.GetType();
                foreach (var kv in saved)
                {
                    var f = t.GetField(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) continue;
                    try { f.SetValue(st, kv.Value); } catch { }
                }
            }
            catch (Exception e) { Main.LogError("[合作] 还原设置失败：" + e.Message); }
        }

        // ==================================================================
        // 跨机器核对设置 —— 把"指纹不一样"变成"具体哪几项不一样"
        //
        // ★为什么光有指纹不够★
        //   船体加成这类**被动规则**是 Harmony 补丁在运行时**持续读设置**算的
        //   （StarshipChargesPatch 里就是 `if (!Settings.ShipExtraShots) return;`
        //   和 `return Settings.ShipCruiserShieldPct;`），
        //   所以随指令发快照救不了它 —— 补丁每次触发都会重新读本机的值。
        //   两边不一致 = 两条船在用不同的护盾/护甲/射击数，海战必然分叉。
        //
        //   指纹能告诉玩家"不一样"，但不能告诉他"哪不一样"。83 个开关靠人肉对
        //   是不现实的。既然指令通道已经实测可用，就用它把双方的设置对发一次，
        //   直接把差异列出来。
        //
        // ★这是只读诊断，不会自动改任何人的设置★
        //   改不改、改哪边，是玩家自己的决定。
        // ==================================================================

        /// <summary>对端发来的设置（字段名 -> 值）。空 = 还没核对过。</summary>
        private static Dictionary<string, string> _remote;
        private static string _remoteWho = "";
        // 收到别人主动发来的 cfg 后，在下一帧回送本机设置。
        // 不能在同步 GameCommand 执行栈里当场 Send（引擎禁止 effect context 嵌套同步命令）。
        private static bool _replyPending;

        /// <summary>本机全部参与比对的设置（口径和指纹完全一致）。</summary>
        public static List<string> CaptureAll()
        {
            var outp = new List<string>();
            try
            {
                var st = Main.Settings;
                if (st == null) return outp;
                foreach (var f in st.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!CoopState.CountsForFingerprint(f.Name)) continue;
                    object v = null;
                    try { v = f.GetValue(st); } catch { continue; }
                    outp.Add(f.Name + "=" + ToText(v));
                }
                outp.Sort(StringComparer.Ordinal);
            }
            catch (Exception e) { Main.LogError("[合作] 打包全部设置失败：" + e.Message); }
            return outp;
        }

        /// <summary>收下对端发来的设置。</summary>
        public static void ReceiveRemote(string who, string[] args, int from)
        {
            bool request = args != null && args.Length > from && args[from] == "@request";
            bool response = args != null && args.Length > from && args[from] == "@response";
            if (request || response) from++;

            // ★自己发的那份要丢掉★ 指令在两台机器上都执行，发起方也会收到自己那份；
            //   拿自己和自己比永远是"完全一致"，真正的差异只有对端看得到。
            string me = CoopState.LocalUserId;
            if (!string.IsNullOrEmpty(me) && string.Equals(who, me, StringComparison.Ordinal))
            {
                Main.Log("[合作] 已发出本机设置，等待对方那边显示差异。");
                return;
            }

            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = from; i < args.Length; i++)
            {
                int eq = args[i].IndexOf('=');
                if (eq > 0) d[args[i].Substring(0, eq)] = args[i].Substring(eq + 1);
            }
            _remote = d; _remoteWho = string.IsNullOrEmpty(who) ? "?" : who;
            if (request) _replyPending = true;     // 响应不再回送，避免无限乒乓
            Main.Log("[合作] 收到对端设置 " + d.Count + " 项，来自 " + _remoteWho + "。" + DiffText());
        }

        /// <summary>由 Main.OnUpdate 调；常态一次 bool 读取，只有收到 request 才发一条 response。</summary>
        public static void TickReply()
        {
            if (!_replyPending) return;
            _replyPending = false;
            try
            {
                var args = new List<string> { CoopState.LocalUserId, "@response" };
                args.AddRange(CaptureAll());
                CoopCommand.Send("cfg", args.ToArray());
            }
            catch (Exception e) { Main.LogError("[合作] 回送设置失败：" + e.Message); }
        }

        /// <summary>本机和对端的差异，给面板和日志用。没核对过返回空串。</summary>
        public static string DiffText()
        {
            if (_remote == null) return "";
            try
            {
                var mine = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in CaptureAll())
                {
                    int eq = kv.IndexOf('=');
                    if (eq > 0) mine[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                }
                var sb = new System.Text.StringBuilder();
                int n = 0;
                var keys = new List<string>(mine.Keys);
                foreach (var k in _remote.Keys) if (!mine.ContainsKey(k)) keys.Add(k);
                keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys)
                {
                    string a, b;
                    mine.TryGetValue(k, out a); _remote.TryGetValue(k, out b);
                    if (a == b) continue;
                    n++;
                    if (n <= 12)
                        sb.Append(Environment.NewLine).Append("    ").Append(SettingLabel(k))
                          .Append("（").Append(k).Append("）")
                          .Append(L.Current == L.ZhCN ? "：你=" : ": You=")
                          .Append(DisplayValue(k, a))
                          .Append(L.Current == L.ZhCN ? "　对方=" : "  Remote=")
                          .Append(DisplayValue(k, b));
                }
                if (n == 0) return L.T("　—— 双方设置完全一致。");
                string more = (n > 12) ? L.F("（另有 {0} 项未列出）", n - 12) : "";
                return L.F("　★{0} 项不一致{1}：", n, more) + sb;
            }
            catch { return ""; }
        }

        private static string SettingLabel(string k)
        {
            bool zh = L.Current == L.ZhCN;
            switch (k)
            {
                case "UnitAssetId": return zh ? "全局兜底单位" : "Fallback unit";
                case "ArchetypeIndex": return zh ? "面板默认招募分型" : "Default recruit archetype";
                case "DialogRecruitEntry": return zh ? "NPC 对话招募入口" : "Recruit option in NPC dialog";
                case "NpcRecruitEntry": return zh ? "点击 NPC 招募入口" : "Clickable NPC recruit entry";
                case "HideGearLook": return zh ? "隐藏卫兵装备外观" : "Hide guard gear visuals";
                case "LookMatrix": return zh ? "卫兵外观分配表" : "Guard appearance matrix";
                case "AttachFollow": return zh ? "跟随队长" : "Follow leader";
                case "AlignExperience": return zh ? "招募时对齐经验" : "Align experience on recruit";
                case "IsolateMomentum": return zh ? "士气隔离" : "Isolate momentum";
                case "UnlockTierLimits": return zh ? "全部解除限制" : "Remove all limits";
                case "UnlockPfGate": return zh ? "解除利润因子限制" : "Ignore profit-factor gate";
                case "UnlockCountCap": return zh ? "解除数量上限" : "Remove guard cap";
                case "UnlockLevelCap": return zh ? "解除等级上限" : "Remove level cap";
                case "MeleeEliteNoEndTurn": return zh ? "森罗刃网不结束回合" : "Blade Shroud does not end turn";
                case "WoundAbilityHpFloor": return zh ? "烧血技能血量下限" : "HP floor for wound-cost abilities";
                case "CommandStallWatch": return zh ? "指令卡顿探针" : "Command stall probe";
                case "StuckRescue": return zh ? "非战斗卡住自动传送" : "Out-of-combat stuck rescue";
                case "GuardAutoHeal": return zh ? "非战斗自动回血" : "Out-of-combat auto-heal";
                case "TraumaMode": return zh ? "卫兵创伤模式" : "Guard trauma mode";
                case "ScaleGuardXp": return zh ? "卫兵经验缩放" : "Scale guard XP";
                case "XpRatio": return zh ? "卫兵经验倍率" : "Guard XP ratio";
                case "XpCatchUp": return zh ? "经验追赶" : "XP catch-up";
                case "XpCatchUpMax": return zh ? "经验追赶倍率上限" : "XP catch-up maximum";
                case "XpCatchUpSpan": return zh ? "经验追赶等级差" : "XP catch-up level span";
                case "SeparateMomentumPool": return zh ? "卫队独立士气池" : "Separate guard momentum pool";
                case "GuardKillFeedsOwnPool": return zh ? "卫兵击杀给卫队池加分" : "Guard kills feed guard pool";
                case "NoCameraFollowGuards": return zh ? "卫兵行动时镜头不跟随" : "Do not follow guards with camera";
                case "GuardPsykerNoVeil": return zh ? "卫兵灵能不推高帷幕" : "Guard psykers do not raise veil";
                case "GuardsCanShootInMelee": return zh ? "卫兵缠斗中可开火" : "Guards can shoot in melee";
                case "RecruitUsePfGate": return zh ? "用利润因子解锁名额" : "Use profit factor for slots";
                case "RecruitPfPerGuard": return zh ? "每名卫兵所需利润因子" : "Profit factor per guard";
                case "RecruitMaxGuards": return zh ? "卫兵硬上限" : "Maximum guards";
                case "AutoLevelUp": return zh ? "过图自动补升级" : "Auto level-up on area load";
                case "EquipGraduationGear": return zh ? "发放配表装备" : "Equip configured gear";
                case "GearTierOverride": return zh ? "装备档位覆盖" : "Gear tier override";
                case "EliteLimitPerArchetype": return zh ? "每种精英数量上限" : "Elite limit per definition";
                case "UnlockEliteLimit": return zh ? "解除精英数量上限" : "Remove elite cap";
                case "EliteIgnoreUnlock": return zh ? "无视精英 T3 解锁" : "Ignore elite T3 unlock";
                case "EliteCanBeDowned": return zh ? "精英可倒地救援" : "Elites can be downed";
                case "ShipArtPreferLance": return zh ? "同挂点优先显示光矛" : "Prefer lance art on shared mount";
                case "ShipDialogEntry": return zh ? "NPC 对话船坞入口" : "Shipyard option in NPC dialog";
                case "ShipDollResnap": return zh ? "改装界面自动重拍船模" : "Refresh ship-doll after refit";
                case "ShipDollScale": return zh ? "改装界面船模缩放" : "Ship-doll scale";
                case "ShipMountFallback": return zh ? "合成缺失舰船武器挂点" : "Synthesize missing ship mounts";
                case "ShipPriceCruiser": return zh ? "巡洋舰改装价格" : "Cruiser refit price";
                case "ShipPriceGrand": return zh ? "大巡洋舰改装价格" : "Grand-cruiser refit price";
                case "ShipProwOffsetPct": return zh ? "舰首挂点前后微调" : "Prow mount longitudinal offset";
                case "ShipProwUpPct": return zh ? "舰首挂点高度微调" : "Prow mount vertical offset";
                case "ShipProwUseLearned": return zh ? "使用学到的舰首挂点" : "Use learned prow mount";
                case "ShipStretchModel": return zh ? "船模按分档等比放大" : "Scale ship model to size tier";
                case "ShipSynthKeel": return zh ? "合成船底挂点" : "Synthesize keel mount";
                case "ShipYardUnlockAll": return zh ? "解除船体更换限制" : "Unlock all ship hulls";
                case "StarMapShipModelSectorMat": return zh ? "扇区图替换船模材质" : "Replace sector-map ship material";
                case "ShipExtraShots": return zh ? "舰船额外开火次数" : "Extra ship shots";
                case "ShipCruiserBroadside": return zh ? "巡洋舰舷炮额外次数" : "Cruiser broadside extra shots";
                case "ShipGrandBroadside": return zh ? "大巡舷炮额外次数" : "Grand-cruiser broadside extra shots";
                case "ShipGrandProw": return zh ? "大巡舰首额外次数" : "Grand-cruiser prow extra shots";
                case "ShipCruiserRange": return zh ? "巡洋舰舰首射程加成" : "Cruiser prow range bonus";
                case "ShipGrandRangeBroadside": return zh ? "大巡舷炮射程加成" : "Grand-cruiser broadside range bonus";
                case "ShipGrandRangeProw": return zh ? "大巡舰首射程加成" : "Grand-cruiser prow range bonus";
                case "ShipCruiserShieldPct": return zh ? "巡洋舰护盾加成" : "Cruiser shield bonus";
                case "ShipGrandShieldPct": return zh ? "大巡护盾加成" : "Grand-cruiser shield bonus";
                case "ShipCruiserArmourPct": return zh ? "巡洋舰装甲加成" : "Cruiser armour bonus";
                case "ShipGrandArmourPct": return zh ? "大巡装甲加成" : "Grand-cruiser armour bonus";
                case "ShipCruiserRamPct": return zh ? "巡洋舰撞角行程加成" : "Cruiser ram range bonus";
                case "ShipGrandRamPct": return zh ? "大巡撞角行程加成" : "Grand-cruiser ram range bonus";
                case "ShipSwitchInCombat": return zh ? "战斗中允许换船档" : "Allow ship refit in combat";
                case "ShipArcFix": return zh ? "舰炮射界按真实占位" : "Use true footprint for firing arcs";
                default: return k;
            }
        }

        private static string DisplayValue(string key, string v)
        {
            if (v == null) return L.Current == L.ZhCN ? "（无）" : "(missing)";
            var f = typeof(Settings).GetField(key, BindingFlags.Public | BindingFlags.Instance);
            if (f != null && f.FieldType == typeof(bool))
                return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                    ? (L.Current == L.ZhCN ? "开" : "On")
                    : (L.Current == L.ZhCN ? "关" : "Off");
            return v;
        }

        // ------------------------------------------------------------------
        // ★文本化一律用 InvariantCulture★
        //   两台机器的系统区域设置可能不同。中文/德文区域下 float 的小数点是逗号，
        //   "0.5" 会解析失败或变成 5 —— 那又是一处只在特定机器上出现的分叉。
        private static string ToText(object v)
        {
            if (v == null) return "";
            if (v is bool)  return ((bool)v) ? "1" : "0";
            if (v is int)   return ((int)v).ToString(CultureInfo.InvariantCulture);
            if (v is float) return ((float)v).ToString("R", CultureInfo.InvariantCulture);
            return v.ToString();
        }

        private static bool FromText(Type ft, string s, out object v)
        {
            v = null;
            try
            {
                if (ft == typeof(bool))   { v = (s == "1" || s == "True" || s == "true"); return true; }
                if (ft == typeof(int))    { int i;   if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return false; v = i; return true; }
                if (ft == typeof(float))  { float f; if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return false; v = f; return true; }
                if (ft == typeof(string)) { v = s; return true; }
            }
            catch { }
            return false;
        }
    }
}
