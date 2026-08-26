using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;          // SnapToGrid 扩展方法
using Kingmaker.UnitLogic.Parts;    // PartUnitDescription
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 卡住检测：卫兵长时间原地不动且离队长很远时，把它挪回队长脚下。
    ///
    /// ★为什么需要★
    ///   传奇档要收恶魔引擎，而 Helbrute / Defiler 是 **Gargantuan**、
    ///   ForgeFiend 是 **Huge** —— 比玩家能操控的任何东西都大两档。
    ///   走廊、门框、狭窄楼梯很可能过不去。这个机制不是为了掩盖问题，
    ///   而是让"过不去"从"卫兵永远留在上一个房间"降级成"晚几秒自己跟上"。
    ///
    /// ★积木是现成的★
    ///   过图/读档后的摆位（RetinueLifecycle.TickPending）已经在用
    ///   `Position = leader.Position; SnapToGrid();`，这里复用同一套。
    ///   区别只是触发条件：那边是"区域刚加载"，这边是"卡了一段时间"。
    ///
    /// ★三个条件必须同时成立才传送★
    ///   ① 不在战斗中 —— 战斗里位移本身就是战术资源，瞬移是作弊；
    ///      而且回合制下把单位挪走会打乱行动顺序和攻击范围判定。
    ///   ② 连续 StuckSeconds 秒位移小于 MoveEpsilon。
    ///   ③ 离队长超过 FarDistance。原地不动但就站在你旁边是**正常**的 ——
    ///      卫兵没有巡逻行为，跟到位就会停下。少了这一条会变成
    ///      "站着不动就被瞬移"，比卡住还烦人。
    /// </summary>
    public static class StuckWatch
    {
        /// <summary>
        /// 「有没有进展」的半径（米）。窗口内没走出这个圈就算卡住。
        ///
        /// ★为什么不是「两次采样之间动没动」★（1.5.1 改）
        ///   原来的判据是 `位移 < 0.35 米就算没动`，一旦超过就把计时清零。
        ///   可卡住的单位**常常不是纹丝不动，而是在原地抖** —— 寻路反复失败、
        ///   贴着几何体来回蹭。每秒抖过 35 厘米，计时就永远攒不到 6 秒，
        ///   于是「看起来明明卡死了，却不传送」。玩家两种都实测遇到过。
        ///
        ///   改成对**窗口起点**量距离：走不出 1.5 米就是没进展，抖多厉害都一样。
        ///   正常跟随的卫兵一步就出圈，不会误判。
        /// </summary>
        private const float ProgressRadius = 1.5f;
        /// <summary>离队长多远才认为"该跟上却没跟上"。</summary>
        private const float FarDistance = 12f;

        // ★计时一律用同步的网络 tick，不用真实时间★
        //
        //   原来是拿 Main.OnUpdate 传进来的 dt 累加。那在单机没问题，
        //   但**真实时间不是同步量** —— 两台机器的帧率、加载耗时、后台掉帧都不同，
        //   "连续静止 6 秒"必然在不同时刻成立。一台把卫兵瞬移了、另一台还没，
        //   位置当场分叉，而位置是进哈希的。这是官方合作里一个必然触发的不同步源。
        //
        //   RealTimeController.CurrentNetworkTick 派生自 Game.Instance.Player.RealTime
        //   —— 那是**游戏状态**，跟着存档和同步走，两台机器一致。
        //   NetworkStepMs = 50，也就是每秒 20 tick。
        //   换成它之后，两台机器会在**同一个 tick** 得出同一个结论，
        //   要传送就一起传送，不需要为了联机把这个功能关掉。
        private const int TicksPerSecond = 20;
        /// <summary>连续没动多少 tick 算卡住（6 秒）。</summary>
        private const int StuckTicks = 6 * TicksPerSecond;
        /// <summary>两次传送之间的最小间隔（8 秒），防止在某个死角反复瞬移。</summary>
        private const int CooldownTicks = 8 * TicksPerSecond;

        /// <summary>
        /// ★多久真正检查一次★ 绝不能每帧跑。
        ///
        /// RetinueRegistry.All() 内部对每个场景状态做 AllEntityData.ToList() ——
        /// 那是把**区域里所有实体**复制一份。实测一个普通区域有 60 个单位，
        /// 每帧跑就是每秒六十次全量拷贝加分配，纯粹给 GC 添堵。
        /// 而"卡住"这件事本身以秒计（阈值 6 秒），1 秒一次的精度绰绰有余。
        /// </summary>
        private const int ScanTicks = 1 * TicksPerSecond;
        private static int _lastScanTick;

        /// <summary>
        /// 两次扫描之间最多允许隔多久（60 秒）。超过就当作「中间读过档或过了图」，
        /// 重新对基准并清账，而不是拿一个跨越加载的 elapsed 去累加静止时间。
        /// </summary>
        private const int MaxSaneElapsed = 60 * TicksPerSecond;

        /// <summary>每多少帧才去读一次同步 tick。见 Tick() 里那段说明。</summary>
        private const int FrameSkip = 10;
        private static int _frameSkip;

        private sealed class Row
        {
            /// <summary>本次「没有进展」窗口的起点。走出 ProgressRadius 就重设。</summary>
            public Vector3 Anchor;
            public int StillTicks;
            public int CooldownLeft;
            /// <summary>「攒够时间了但离队长太近」这条只报一次，免得每秒刷屏。</summary>
            public bool NearReported;
        }

        private static readonly Dictionary<string, Row> _rows =
            new Dictionary<string, Row>(StringComparer.Ordinal);

        /// <summary>
        /// 由 Main.OnUpdate 每帧调用，但**每秒才真正扫一次**（见 ScanInterval）。
        /// 帧上的开销只有一次浮点累加和一次比较。
        /// </summary>
        public static void Tick(float dt)
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.StuckRescue) return;

                // ★便宜的帧闸放在最前面★
                //   本文件原本用 float 累加 dt，节流写在第一句，注释明确写着
                //   "不到间隔就什么都不做，连 Game.Instance 都不碰"。
                //   改成同步 tick 计时之后，读 tick 本身就得先拿到 Game.Instance
                //   （CurrentNetworkTick 内部还要对 Player.RealTime 做 TimeSpan 换算）——
                //   于是那条承诺被我自己破坏了，变成每帧都走一遍。
                //
                //   加一个纯 int 的帧计数挡在前面：每 10 帧才去读一次 tick。
                //   扫描间隔是 20 tick（1 秒），10 帧的粒度绰绰有余，
                //   而平时每帧的代价回到"一次自增 + 一次比较"。
                if (++_frameSkip < FrameSkip) return;
                _frameSkip = 0;

                var game = Game.Instance;
                if (game == null || game.Player == null) return;

                // 节流 + 计时都用同步 tick（见上面 TicksPerSecond 那段注释）
                int now;
                try { now = game.RealTimeController.CurrentNetworkTick; } catch { return; }
                int elapsed = now - _lastScanTick;

                // ★★tick 会倒退，倒退一次这个功能就整场作废★★
                //   CurrentNetworkTick 派生自 Player.RealTime —— 那是**存档里的游戏状态**，
                //   而 UMM 的 mod 在读档之间是一直活着的。读一个更早的存档，now 就比
                //   _lastScanTick 小一大截，elapsed 变成负数；而下面那句 `if (elapsed <
                //   ScanTicks) return;` 又不更新 _lastScanTick —— 于是**永远**回不来，
                //   直到玩家重启游戏。玩家实测反馈的「传送不是一直生效」就是这个。
                //
                //   本来该由 Reset() 兜底，可 Reset() **一个调用点都没有**（注释写着
                //   「遣散/读档后清账」，但从没挂上去）。现在两头都补：这里自愈，
                //   RetinueLifecycle.OnAreaLoadingComplete 也调 Reset()。
                //
                //   跳变过大同样要重新对基准：那意味着中间隔了读档或长时间加载，
                //   _rows 里记的坐标已经没有意义了。
                if (elapsed < 0 || elapsed > MaxSaneElapsed)
                {
                    _lastScanTick = now;
                    _rows.Clear();
                    return;
                }

                if (elapsed < ScanTicks) return;
                _lastScanTick = now;

                // ① 战斗中一概不动
                bool inCombat;
                try { inCombat = game.Player.IsInCombat; } catch { return; }
                if (inCombat) { _rows.Clear(); return; }

                var leader = game.Player.MainCharacterEntity;
                if (leader == null) return;

                List<BaseUnitEntity> list = Guards(now);
                if (list == null || list.Count == 0) { if (_rows.Count > 0) _rows.Clear(); return; }

                foreach (var g in list)
                {
                    if (g == null) continue;
                    string id;
                    try { id = g.UniqueId; } catch { continue; }
                    if (string.IsNullOrEmpty(id)) continue;

                    Vector3 pos;
                    try { pos = g.Position; } catch { continue; }

                    Row r;
                    if (!_rows.TryGetValue(id, out r))
                    {
                        _rows[id] = new Row { Anchor = pos, StillTicks = 0, CooldownLeft = 0 };
                        continue;
                    }

                    if (r.CooldownLeft > 0) r.CooldownLeft -= elapsed;

                    // ★量的是「离窗口起点多远」，不是「这一秒动了多少」★ 见 ProgressRadius 注释
                    if ((pos - r.Anchor).sqrMagnitude > ProgressRadius * ProgressRadius)
                    {
                        r.Anchor = pos; r.StillTicks = 0; r.NearReported = false; continue;
                    }
                    r.StillTicks += elapsed;
                    if (r.StillTicks < StuckTicks || r.CooldownLeft > 0) continue;

                    // ③ 站着不动但就在旁边 —— 那是正常的，不是卡住
                    float dist;
                    try { dist = Vector3.Distance(pos, leader.Position); } catch { continue; }
                    if (dist < FarDistance)
                    {
                        // ★这条要能查★ 距离用的是**直线**距离，不是路径距离。
                        //   卡在一道门后面、直线才 8 米但绕路要 40 米的卫兵，会被
                        //   这一条判成「就在旁边」而不传送。真遇到时，日志里这行
                        //   就是唯一能把它和「压根没扫到」区分开的证据。
                        if (Main.Settings.WatchMomentum && !r.NearReported)
                        {
                            r.NearReported = true;
                            Main.Log($"[卡住] {NameOf(g)} 已 {r.StillTicks / TicksPerSecond} 秒没走出 "
                                   + $"{ProgressRadius:F1} 米，但离队长直线只有 {dist:F0} 米"
                                   + $"（阈值 {FarDistance:F0}）—— 按「就在旁边、属正常」处理，不传送。");
                        }
                        r.StillTicks = 0; continue;
                    }

                    try
                    {
                        // 跟过图摆位同一套：先停下寻路，再落到队长脚下吸附
                        try { if (g.View != null && g.View.AgentASP != null) g.View.AgentASP.Stop(); } catch { }
                        g.Position = leader.Position;
                        g.SnapToGrid();
                        Main.Log($"[卡住] {NameOf(g)} 静止 {r.StillTicks / TicksPerSecond} 秒且距队长 {dist:F0} 米，已挪回队长身边。");
                    }
                    catch (Exception e) { Main.LogError("[卡住] 传送失败: " + e.Message); }

                    r.Anchor = g.Position;
                    r.StillTicks = 0;
                    r.NearReported = false;
                    r.CooldownLeft = CooldownTicks;
                }
            }
            catch (Exception e) { Main.LogError("[卡住] Tick: " + e.Message); }
        }

        /// <summary>日志用的显示名。取不到就退回蓝图名，不让日志里出现空串。</summary>
        private static string NameOf(BaseUnitEntity u)
        {
            try
            {
                var d = u.GetOptional<PartUnitDescription>();
                if (d != null && !string.IsNullOrEmpty(d.CustomName)) return d.CustomName;
            }
            catch { }
            try { return u.Blueprint != null ? u.Blueprint.name : "?"; } catch { return "?"; }
        }

        /// <summary>遣散/读档后清账，免得旧 id 一直留在表里。</summary>
        public static void Reset() { _rows.Clear(); _lastScanTick = 0; _frameSkip = 0; _guards = null; _guardsAt = int.MinValue; }

        /// <summary>
        /// 卫兵名单，带缓存。
        ///
        /// ★为什么必须缓存 —— 玩家实测的掉帧就是这儿来的★
        ///   RetinueRegistry.All() 每次都要把 CrossSceneState 和当前区域的
        ///   **全部实体**各拷一份（AllEntityData.ToList()）—— 那不只是单位，
        ///   还有道具、交互物、灯光，大区域上千个。
        ///
        ///   而原来的写法是**每秒**调一次，且调用点在「有没有卫兵」的判断**之前** ——
        ///   于是一个还没招募过任何卫兵的玩家，在非战斗状态下也一直在每秒拷两份大表。
        ///   Main.cs 那行「无卫兵时几乎零开销」的注释是错的。
        ///   周期性大块分配 ⇒ 周期性 GC 尖峰 ⇒ 玩家报的「走几步整个画面顿一下」。
        ///   而且它只在非战斗时跑，正好对上「在地图上走路时」。
        ///
        /// ★为什么 10 秒够★ 卡住检测本身的阈值是 6 秒静止，名单晚十秒更新
        ///   最多让一名刚招募的卫兵晚一轮被看护，代价可以忽略；
        ///   而过图和遣散都会走 Reset()，那两个才是名单真正会变的时刻。
        /// </summary>
        private const int GuardsRefreshTicks = 10 * TicksPerSecond;
        private static List<BaseUnitEntity> _guards;
        private static int _guardsAt = int.MinValue;

        private static List<BaseUnitEntity> Guards(int now)
        {
            if (_guards != null && now - _guardsAt >= 0 && now - _guardsAt < GuardsRefreshTicks)
                return _guards;
            try { _guards = RetinueRegistry.All(false); }
            catch { _guards = null; }
            _guardsAt = now;
            return _guards;
        }
    }
}
