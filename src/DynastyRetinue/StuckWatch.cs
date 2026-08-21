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
        /// <summary>位移小于这个值算"没动"（单位：米）。</summary>
        private const float MoveEpsilon = 0.35f;
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

        /// <summary>每多少帧才去读一次同步 tick。见 Tick() 里那段说明。</summary>
        private const int FrameSkip = 10;
        private static int _frameSkip;

        private sealed class Row
        {
            public Vector3 Last;
            public int StillTicks;
            public int CooldownLeft;
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
                if (elapsed < ScanTicks) return;
                _lastScanTick = now;

                // ① 战斗中一概不动
                bool inCombat;
                try { inCombat = game.Player.IsInCombat; } catch { return; }
                if (inCombat) { _rows.Clear(); return; }

                var leader = game.Player.MainCharacterEntity;
                if (leader == null) return;

                List<BaseUnitEntity> list;
                try { list = RetinueRegistry.All(false); } catch { return; }
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
                        _rows[id] = new Row { Last = pos, StillTicks = 0, CooldownLeft = 0 };
                        continue;
                    }

                    if (r.CooldownLeft > 0) r.CooldownLeft -= elapsed;

                    if ((pos - r.Last).sqrMagnitude > MoveEpsilon * MoveEpsilon)
                    {
                        r.Last = pos; r.StillTicks = 0; continue;
                    }
                    r.StillTicks += elapsed;
                    if (r.StillTicks < StuckTicks || r.CooldownLeft > 0) continue;

                    // ③ 站着不动但就在旁边 —— 那是正常的，不是卡住
                    float dist;
                    try { dist = Vector3.Distance(pos, leader.Position); } catch { continue; }
                    if (dist < FarDistance) { r.StillTicks = 0; continue; }

                    try
                    {
                        // 跟过图摆位同一套：先停下寻路，再落到队长脚下吸附
                        try { if (g.View != null && g.View.AgentASP != null) g.View.AgentASP.Stop(); } catch { }
                        g.Position = leader.Position;
                        g.SnapToGrid();
                        Main.Log($"[卡住] {NameOf(g)} 静止 {r.StillTicks / TicksPerSecond} 秒且距队长 {dist:F0} 米，已挪回队长身边。");
                    }
                    catch (Exception e) { Main.LogError("[卡住] 传送失败: " + e.Message); }

                    r.Last = g.Position;
                    r.StillTicks = 0;
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
        public static void Reset() { _rows.Clear(); _lastScanTick = 0; _frameSkip = 0; }
    }
}
