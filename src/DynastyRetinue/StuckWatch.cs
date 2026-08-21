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
        /// <summary>连续没动多少秒算卡住。</summary>
        private const float StuckSeconds = 6f;
        /// <summary>离队长多远才认为"该跟上却没跟上"。</summary>
        private const float FarDistance = 12f;
        /// <summary>两次传送之间的最小间隔，防止在某个死角反复瞬移。</summary>
        private const float CooldownSeconds = 8f;

        /// <summary>
        /// ★多久真正检查一次★ 绝不能每帧跑。
        ///
        /// RetinueRegistry.All() 内部对每个场景状态做 AllEntityData.ToList() ——
        /// 那是把**区域里所有实体**复制一份。实测一个普通区域有 60 个单位，
        /// 每帧跑就是每秒六十次全量拷贝加分配，纯粹给 GC 添堵。
        /// 而"卡住"这件事本身以秒计（阈值 6 秒），1 秒一次的精度绰绰有余。
        /// </summary>
        private const float ScanInterval = 1.0f;
        private static float _sinceScan;

        private sealed class Row
        {
            public Vector3 Last;
            public float StillFor;
            public float CooldownLeft;
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

                // 节流放在最前面：不到间隔就什么都不做，连 Game.Instance 都不碰
                _sinceScan += dt;
                if (_sinceScan < ScanInterval) return;
                dt = _sinceScan;          // 用真实经过的时间计时，不是单帧的 dt
                _sinceScan = 0f;

                var game = Game.Instance;
                if (game == null || game.Player == null) return;

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
                        _rows[id] = new Row { Last = pos, StillFor = 0f, CooldownLeft = 0f };
                        continue;
                    }

                    if (r.CooldownLeft > 0f) r.CooldownLeft -= dt;

                    if ((pos - r.Last).sqrMagnitude > MoveEpsilon * MoveEpsilon)
                    {
                        r.Last = pos; r.StillFor = 0f; continue;
                    }
                    r.StillFor += dt;
                    if (r.StillFor < StuckSeconds || r.CooldownLeft > 0f) continue;

                    // ③ 站着不动但就在旁边 —— 那是正常的，不是卡住
                    float dist;
                    try { dist = Vector3.Distance(pos, leader.Position); } catch { continue; }
                    if (dist < FarDistance) { r.StillFor = 0f; continue; }

                    try
                    {
                        // 跟过图摆位同一套：先停下寻路，再落到队长脚下吸附
                        try { if (g.View != null && g.View.AgentASP != null) g.View.AgentASP.Stop(); } catch { }
                        g.Position = leader.Position;
                        g.SnapToGrid();
                        Main.Log($"[卡住] {NameOf(g)} 静止 {r.StillFor:F0} 秒且距队长 {dist:F0} 米，已挪回队长身边。");
                    }
                    catch (Exception e) { Main.LogError("[卡住] 传送失败: " + e.Message); }

                    r.Last = g.Position;
                    r.StillFor = 0f;
                    r.CooldownLeft = CooldownSeconds;
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
        public static void Reset() { _rows.Clear(); }
    }
}
