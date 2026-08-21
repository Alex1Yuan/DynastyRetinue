using System;
using System.Collections.Generic;
using System.Text;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.PubSubSystem.Core.Interfaces;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;

namespace DynastyRetinue
{
    /// <summary>
    /// 战斗行为记录 —— 每个卫兵在战斗里到底做了什么。
    ///
    /// ================= 为什么需要 =================
    /// 作者实机遇到「有几个兵给了额外回合也不动」。而在此之前，日志里
    /// **完全没有逐回合的行为记录** —— 只有生成/发装备/升级/命名/死亡。
    /// 也就是说打完一场，谁动了谁没动、动的时候干了什么，事后一点都查不出来。
    ///
    /// ★光记「动没动」不够★
    /// 「一个技能都不放、只普攻」和「正常放技能」是**两种完全不同的故障**：
    ///   · 只平A  ⇒ brain 的技能列表和这个单位实际会的技能对不上
    ///   · 完全不动 ⇒ 连武器攻击都走不通（没武器 / 够不着 / 被 brain 卡死）
    ///   · 只移动  ⇒ 找不到可打的目标，或者站位逻辑把自己走死了
    /// 从旁边看「只平A」很像正常，所以必须分开记，否则会漏判。
    ///
    /// ================= 挂钩点 =================
    /// IUnitCommandStartHandler.HandleUnitCommandDidStart —— AI 的**每个动作**都是
    /// 一条 AbstractUnitCommand（移动、放技能、攻击都是），这是唯一一个能一网打尽的点。
    /// 比逐个 patch 各种 Ability 执行路径可靠得多。
    ///
    /// 战斗结束靠 OnUpdate 里轮询 IsInCombat 的 true→false 跳变 —— 一帧一个 bool 比较，
    /// 比再挂一个事件接口简单，也不依赖那个接口在这个版本叫什么名字。
    ///
    /// ★只读★ 这个类不改变任何游戏状态，只统计 + 写日志。
    /// </summary>
    public sealed class CombatWatch : IUnitCommandStartHandler
    {
        private sealed class Row
        {
            public string Name;
            public int Weapon;      // 武器攻击（含普攻）
            public int Offensive;   // 攻击性技能（非武器、指向敌人）
            public int Support;     // 辅助性技能（增益/治疗/指向友方或自己）
            public int Item;        // 物品（能力来源是某件装备/消耗品）
            public int Move;        // 移动
            public int Other;
            public readonly List<string> Used = new List<string>();
            public readonly List<string> Others = new List<string>();
            /// <summary>
            /// 每个技能各用了几次。
            /// ★只记名字不记次数是不够的★ 「攻击技 5」+「用过: 击杀猎物/控制射击/快速射击」
            /// 这两栏合起来仍然答不了"它到底开了几枪" —— 那 5 次可能是 4 次标记 + 1 次射击。
            /// CanTargetEnemies 只说明技能**指向敌人**，标记/减益/嘲讽都算，未必是攻击。
            /// 分技能计数之后，看的人（知道每个技能是干嘛的）就能自己判断。
            /// </summary>
            public readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>
            /// 这一场里「轮到它」了几次。
            ///
            /// ★为什么必须单独记★ 只看动作数的话，「轮到了但站着不动」和「压根没轮到」
            /// 长得一模一样（都是 0），而这两者的排查方向完全相反：前者是 AI/装备/可达性的
            /// 真 bug，后者只是战斗结束得太快、它在先攻序里排后面而已。
            /// 2026-08-19 实测吃过一次亏：连着两场报「整场没动」，各是一个不同的分型，
            /// 一度以为是新引入的 bug，最后发现那两场总动作数只有 37/66（正常是 178），
            /// 就是没轮到。没有这个计数，每次都得靠人工翻血量、比总动作数来反推。
            /// </summary>
            public int Turns;

            public int Total { get { return Weapon + Offensive + Support + Item + Move + Other; } }
        }

        private static CombatWatch _instance;
        private static readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        private static bool _wasInCombat;

        public static void Install()
        {
            if (_instance != null) return;
            try
            {
                _instance = new CombatWatch();
                EventBus.Subscribe(_instance);
                Main.LogVerbose("[战斗记录] 已挂载 —— 每场战斗结束后会打一份「谁做了什么」的总账。");
            }
            catch (Exception e) { Main.LogError("[战斗记录] 挂载失败: " + e); _instance = null; }
        }

        public static void Uninstall()
        {
            if (_instance == null) return;
            try { EventBus.Unsubscribe(_instance); } catch { }
            _instance = null;
        }

        /// <summary>在 Main.OnUpdate 里每帧调一次。只做一个 bool 比较，战斗结束那一帧才干活。</summary>
        public static void Tick()
        {
            bool now;
            try { now = Game.Instance != null && Game.Instance.Player != null && Game.Instance.Player.IsInCombat; }
            catch { return; }

            if (now && !_wasInCombat) { _rows.Clear(); _lastTurnKey = null; }      // 开打：清空上一场
            else if (!now && _wasInCombat)
            {
                Dump("战斗结束");
                // 战斗中阵亡的卫兵只摘了牌、尸体留在地上（见 RetinueRegistry.RemoveOne），
                // 到这里才真正销毁 —— 战斗结束是唯一不会让玩家看到"尸体凭空消失"的时机。
                try { RetinueRegistry.FlushPendingDestroy(); } catch { }
            }
            _wasInCombat = now;

            if (now) TrackTurn();
            AutoEndTurn();
        }

        /// <summary>
        /// 轮询「当前该谁行动」，换人时给那名卫兵的回合数 +1。
        ///
        /// ★为什么是轮询而不是订阅 ITurnStartHandler★
        /// 那个接口是 ISubscriber&lt;IMechanicEntity&gt;（实体级订阅），拿不到"是谁"要额外绕，
        /// 而这里只需要一个 引用比较 —— TurnController.CurrentUnit 是现成属性
        /// （TurnController.cs:206 `public MechanicEntity CurrentUnit => TurnOrder.CurrentUnit;`）。
        /// 每帧一次引用比较的开销可以忽略，且只在战斗中跑。
        /// ★不要在这里做任何遍历或反射★ 这个 mod 之前就是在每帧路径上调 AccessTools.TypeByName 卡死过。
        /// </summary>
        private static string _lastTurnKey;
        private static void TrackTurn()
        {
            try
            {
                var tc = Game.Instance != null ? Game.Instance.TurnController : null;
                if (tc == null) return;
                var cur = tc.CurrentUnit as BaseUnitEntity;
                if (cur == null) { return; }

                string key = null;
                try { key = cur.UniqueId; } catch { }
                if (key == null || key == _lastTurnKey) return;   // 还是同一个人的回合
                _lastTurnKey = key;

                if (!IsOurs(cur)) return;                          // 别人的回合，只更新游标
                RowFor(cur).Turns++;
            }
            catch { }
        }

        /// <summary>
        /// 自动结束玩家回合 —— 纯测试便利：观察卫兵 AI 时不用一直手点结束回合。
        ///
        /// ★只在开发模式 + 显式打开时生效★ 它会让**你自己的角色什么都不做**，
        /// 这在实战里显然是灾难，所以默认关闭、且不出现在玩家区。
        ///
        /// 安全性：CanEndTurn 内含 !AnyUnitIsBusy（TurnController.cs:242-249），
        /// 所以不会在动画/结算中途插进去；RequestEndTurn 只是置一个标志位，幂等。
        /// 仍然加了节流，避免同一回合内每帧刷一次请求。
        /// </summary>
        private static float _lastEnd;
        private static void AutoEndTurn()
        {
            try
            {
                if (!Main.DevMode || Main.Settings == null || !Main.Settings.AutoEndPlayerTurn) return;
                var tc = Game.Instance != null ? Game.Instance.TurnController : null;
                if (tc == null || !tc.TurnBasedModeActive || !tc.InCombat) return;
                if (!tc.IsPlayerTurn || !tc.CanEndTurn) return;

                float t = UnityEngine.Time.realtimeSinceStartup;
                if (t - _lastEnd < 0.4f) return;     // 节流：一回合只请求一次就够
                _lastEnd = t;
                tc.RequestEndTurn();
            }
            catch { }
        }

        // ------------------------------------------------------------ 事件

        public void HandleUnitCommandDidStart(AbstractUnitCommand command)
        {
            try
            {
                if (command == null) return;
                var u = command.Executor as BaseUnitEntity;
                if (u == null || !IsOurs(u)) return;

                var row = RowFor(u);
                var ua = command as UnitUseAbility;
                if (ua == null)
                {
                    // 非技能类指令：移动占绝大多数，其余归 Other。
                    // ★"其它"要记类型名★ 只记个计数的话，看到「其它 5」完全不知道它在干嘛，
                    // 而那 5 次恰恰可能是"为什么这个兵不打人"的答案。
                    string tn = command.GetType().Name;
                    if (tn.IndexOf("Move", StringComparison.OrdinalIgnoreCase) >= 0) row.Move++;
                    else
                    {
                        row.Other++;
                        if (row.Others.Count < 12 && !row.Others.Contains(tn)) row.Others.Add(tn);
                    }
                    return;
                }

                var ab = ua.Ability;
                if (ab == null) { row.Other++; return; }

                string nm = NameOf(ab);
                if (row.Used.Count < 24 && !row.Used.Contains(nm)) row.Used.Add(nm);
                int c; row.Counts.TryGetValue(nm, out c); row.Counts[nm] = c + 1;

                bool fromItem = false;
                try { fromItem = ab.SourceItem != null; } catch { }
                bool fromWeapon = false;
                try { fromWeapon = ab.Weapon != null; } catch { }

                if (fromWeapon) row.Weapon++;
                else if (fromItem) row.Item++;
                else if (TargetsEnemies(ab)) row.Offensive++;
                else row.Support++;
            }
            catch { }   // 记录器绝不能把游戏搞崩 —— 它只是个旁观者
        }

        // ------------------------------------------------------------ 工具

        /// <summary>
        /// ★必须是 O(1)★ 这个方法挂在 HandleUnitCommandDidStart 上 ——
        /// 战斗中**每个单位的每条指令**都会调它，包括所有敌人的。
        ///
        /// 原来的写法是 `foreach (var g in RetinueRegistry.All())` 做引用比对，
        /// 而 All() 内部对每个场景状态做 AllEntityData.ToList()，
        /// 那是把区域里所有实体（实测一个普通区域 60 个）拷一份。
        /// 于是一场战斗里每条指令都触发一次全量拷贝，纯给 GC 添堵。
        ///
        /// IsGuard 只读一次 CombatGroup.Id 做前缀比较，语义完全等价 ——
        /// All() 本来就是靠 IsGuard 筛出来的。
        /// </summary>
        private static bool IsOurs(BaseUnitEntity u)
        {
            try { return RetinueRegistry.IsGuard(u); }
            catch { }
            return false;
        }

        private static Row RowFor(BaseUnitEntity u)
        {
            string key = null;
            try { key = u.UniqueId; } catch { }
            if (string.IsNullOrEmpty(key)) key = u.GetHashCode().ToString();
            Row r;
            if (!_rows.TryGetValue(key, out r))
            {
                r = new Row { Name = DisplayName(u) };
                _rows[key] = r;
            }
            return r;
        }

        private static string DisplayName(BaseUnitEntity u)
        {
            try
            {
                var d = u.GetOptional<Kingmaker.UnitLogic.Parts.PartUnitDescription>();
                if (d != null && !string.IsNullOrEmpty(d.CustomName)) return d.CustomName;
            }
            catch { }
            try { return u.Blueprint != null ? u.Blueprint.name : "?"; } catch { return "?"; }
        }

        private static string NameOf(Kingmaker.UnitLogic.Abilities.AbilityData ab)
        {
            // ★不要用反射★ 第一版写的是 GetProperty("Name")，两个坑同时踩：
            //   ① 这个 codebase 的已知陷阱 —— 基类和派生类都声明同名成员时，
            //      不加 DeclaredOnly 的 GetProperty 会抛 AmbiguousMatchException，
            //      被 catch 吞掉，结果整列显示 "?"。
            //   ② AbilityData 本来就有 public string Name（AbilityData.cs:699），
            //      根本不需要反射。
            try { var s = ab.Name; if (!string.IsNullOrEmpty(s)) return s; } catch { }
            try { return ab.Blueprint != null ? ab.Blueprint.name : "?"; } catch { }
            return "?";
        }

        /// <summary>
        /// 攻击性 / 辅助性。
        ///
        /// ★CanTargetEnemies 是 public 字段不是属性★（BlueprintAbility.cs:109）
        /// 第一版用 GetProperty 去拿，永远返回 null ⇒ 一律判成辅助 ⇒
        /// 打完一场「攻击技」全是 0、「辅助技」高达 42，数据整列失真。
        /// 直接读字段，不绕反射。
        /// </summary>
        private static bool TargetsEnemies(Kingmaker.UnitLogic.Abilities.AbilityData ab)
        {
            try
            {
                var bp = ab.Blueprint;
                if (bp == null) return false;
                return bp.CanTargetEnemies;      // 能打敌人就算攻击向，兼能打友方也按攻击记
            }
            catch { return false; }
        }

        // ------------------------------------------------------------ 总账

        /// <summary>手动导出（开发区按钮）或战斗结束自动调用。</summary>
        public static void Dump(string why)
        {
            try
            {
                int n = 0;
                try { n = RetinueRegistry.Count; } catch { }
                if (n == 0) { Main.Log("[战斗记录] " + why + "：在册 0 名，无可记录。"); return; }

                var sb = new StringBuilder();
                sb.AppendLine("======== 战斗行为总账（" + why + "）========");
                sb.AppendLine("  ★「武器」只统计挂着武器实体的攻击；很多单位的射击是**技能式武器攻击**"
                            + "（如 Sororitas_HBolter_RapidFire_Ability），它们计入「攻击技」。"
                            + "判断有没有在打人要看 **攻击合计 = 武器 + 攻击技**。★");
                sb.AppendLine("  卫兵                          回合  武器  攻击技  辅助技  物品  移动  其它   合计");

                int idle = 0, noAttack = 0, noTurn = 0;
                foreach (var g in RetinueRegistry.All())
                {
                    string key = null;
                    try { key = g.UniqueId; } catch { }
                    Row r;
                    if (key == null || !_rows.TryGetValue(key, out r))
                        r = new Row { Name = DisplayName(g) };

                    int atk = r.Weapon + r.Offensive;
                    sb.AppendLine(string.Format("  {0,-28} {1,4} {2,5} {3,6} {4,6} {5,5} {6,5} {7,5} {8,6}{9}",
                        Trim(r.Name, 28), r.Turns, r.Weapon, r.Offensive, r.Support, r.Item, r.Move, r.Other, r.Total,
                        r.Turns == 0 ? "   （没轮到，战斗提前结束）"
                        : r.Total == 0 ? "   ★轮到了却一次都没动★"
                        : atk == 0 ? "   ★有行动但一次都没攻击★"
                        : ""));

                    if (r.Turns == 0) noTurn++;
                    else if (r.Total == 0) idle++;
                    else if (atk == 0) noAttack++;

                    if (r.Counts.Count > 0)
                    {
                        var parts = new List<string>();
                        foreach (var kv in r.Counts) parts.Add(kv.Key + "×" + kv.Value);
                        sb.AppendLine("      用过: " + string.Join(" / ", parts.ToArray()));
                    }
                    if (r.Others.Count > 0)
                        sb.AppendLine("      其它指令: " + string.Join(" / ", r.Others.ToArray()));
                }

                sb.AppendLine("  ---");
                if (idle == 0 && noAttack == 0)
                    sb.AppendLine("  轮到过的卫兵全都行动且攻击过了。"
                                + (noTurn > 0 ? "（另有 " + noTurn + " 名没轮到就打完了，不是问题）" : ""));
                else
                {
                    if (idle > 0)
                        sb.AppendLine("  ★" + idle + " 名轮到了却一次都没动★ 这是真问题，查：有没有武器、"
                                    + "够不够得着、行动力是不是 0。");
                    if (noAttack > 0)
                        sb.AppendLine("  ★" + noAttack + " 名有行动但没攻击★ 多半是走位/够不着，"
                                    + "看它的「其它指令」和「用过」两栏。");
                    if (noTurn > 0)
                        sb.AppendLine("  （另有 " + noTurn + " 名整场没轮到，先攻序靠后 + 战斗结束得早，不用管）");
                }
                Main.Log(sb.ToString());
            }
            catch (Exception e) { Main.LogError("[战斗记录] 导出失败: " + e); }
        }

        private static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }
    }
}
