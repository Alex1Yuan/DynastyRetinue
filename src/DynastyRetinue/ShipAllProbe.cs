using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Kingmaker.Pathfinding;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 各处补丁顺手存下来的最近一次现场数据，供全量探针一次性打印。
    ///
    /// ★为什么要这么个中转站★
    ///   绿格、射界、船位这三份数据分别产生在三个互不相干的回调里，
    ///   谁也拿不到别人的。以前的做法是各打各的日志，玩家要在几十行里
    ///   自己对时间戳拼上下文 —— 而船在两条日志之间可能已经动过，拼出来的结论不可信。
    ///   存一份最近值，等选中武器时一次性打全，所有数字来自同一时刻，才能直接相减。
    /// </summary>
    internal static class ShipTelemetry
    {
        /// <summary>绿色可达格（已按船宽铺开后的最终结果）。</summary>
        internal static List<Vector2Int> Grid = new List<Vector2Int>();
        /// <summary>射界覆盖格。</summary>
        internal static List<Vector2Int> Pattern = new List<Vector2Int>();
        /// <summary>射界原点（pattern.ApplicationNode）。</summary>
        internal static Vector2Int PatternOrigin;
        internal static bool HasPattern;

        internal static void SetGrid(IEnumerable<CustomGridNodeBase> nodes)
        {
            try
            {
                Grid.Clear();
                if (nodes == null) return;
                foreach (var n in nodes)
                {
                    if (n == null) continue;
                    Grid.Add(new Vector2Int(n.XCoordinateInGrid, n.ZCoordinateInGrid));
                }
            }
            catch { }
        }

        /// <summary>
        /// **未经本 mod 加工**的原始落点 —— 游戏自己算出来、玩家真正点得到的那些格。
        ///
        /// ★为什么必须单独存一份★
        ///   Grid 是在铺完 W×W 方块、画完占位高亮之后才存的，三样东西混在一起，
        ///   读日志时根本分不出哪些是"能点的"、哪些是我加的。
        ///   而落点是这套坐标系里**唯一的真相**（玩家原话：绿格是根据真实位置算出来的），
        ///   要判断占位算得对不对，只能拿它当尺子 —— 必须在动手之前抄一份。
        /// </summary>
        internal static List<Vector2Int> RawGrid = new List<Vector2Int>();

        internal static void SetRawGrid(IEnumerable<CustomGridNodeBase> nodes)
        {
            try
            {
                RawGrid.Clear();
                if (nodes == null) return;
                foreach (var n in nodes)
                {
                    if (n == null) continue;
                    RawGrid.Add(new Vector2Int(n.XCoordinateInGrid, n.ZCoordinateInGrid));
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 【诊断】选中一门炮就自动打一份**全量坐标**，所有量统一换算成格坐标。
    ///
    /// ★为什么改成自动触发★
    ///   原来要玩家先选炮、再去面板点【海战坐标全量】，一门炮一趟。
    ///   炮有五门、朝向有八个，来回四十趟，且中途手一抖船就动了、数据作废。
    ///   玩家的原话是"把点击每门炮就直接打出来，不要每次还要点坐标全量"——完全正确，
    ///   探测成本本来就该压到零，人只负责点炮。
    ///
    /// ★为什么全部换算成格★
    ///   世界坐标（16.2, 0, 10.8）和格坐标（12, 8）混在一起时，
    ///   "差了多少"要心算除以 1.35，一走神就错。全部化成格之后，
    ///   「模型 − 底座 = (0.96, 0)」这种行可以直接读成"差了将近一格"，不需要任何换算。
    ///   我自己看图判断偏移已经错过好几轮，数字不会骗人。
    ///
    /// ★只读★ 不改任何状态，挂在「详细日志」开关后面。
    /// </summary>
    internal static class ShipAllProbe
    {
        /// <summary>已打印过的现场（技能 × 锚点 × 朝向档）。换炮、移动、转向都会产生新记录。</summary>
        private static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        public static void Reset() { _seen.Clear(); }

        public static void Tick()
        {
            try
            {
                var s = Main.Settings;
                if (s == null || !s.WatchMomentum) return;

                var game = Kingmaker.Game.Instance;
                if (game == null || game.Player == null) return;
                var ship = game.Player.PlayerShip;
                if (ship == null || ship.View == null) return;

                var cc = game.CursorController;
                var ab = cc != null ? cc.SelectedAbility : null;
                if (ab == null) return;                       // 没选炮就不打，免得刷屏

                var node = ship.CurrentUnwalkableNode as CustomGridNodeBase;
                if (node == null) return;

                string abName = ab.Blueprint != null ? ab.Blueprint.name : "?";
                int bucket = Mathf.RoundToInt(ship.Orientation / 45f) % 8;
                if (bucket < 0) bucket += 8;
                string key = abName + "|" + node.XCoordinateInGrid + "," + node.ZCoordinateInGrid + "|" + bucket;
                if (!_seen.Add(key)) return;

                Dump(ship, node, ab, abName, bucket);
            }
            catch { }
        }

        private static void Dump(Kingmaker.EntitySystem.Entities.StarshipEntity ship,
                                 CustomGridNodeBase node,
                                 Kingmaker.UnitLogic.Abilities.AbilityData ab,
                                 string abName, int bucket)
        {
            var sb = new StringBuilder();
            float cell = GraphParamsMechanicsCache.GridCellSize;
            if (cell <= 0.001f) cell = 1f;

            // 世界→格的基准：拿锚点当尺子。node.position 是它的世界坐标、
            // XCoordinateInGrid 是它的格坐标，两者一减就得到网格原点。
            Vector3 np = (Vector3)node.position;
            float ox = np.x - node.XCoordinateInGrid * cell;
            float oz = np.z - node.ZCoordinateInGrid * cell;

            sb.AppendLine("========== 全量坐标　" + abName + "　" + SlotOf(ab, ship) + " ==========");
            sb.AppendLine("格边长=" + cell.ToString("F3") + "　朝向档=" + (bucket * 45) + "°");

            // ---------- 船：逻辑 ----------
            var r = ship.SizeRect;
            sb.AppendLine("[逻辑] 锚点格=(" + node.XCoordinateInGrid + "," + node.ZCoordinateInGrid + ")"
                        + "　朝向=" + ship.Orientation.ToString("F1") + "°"
                        + "　Size=" + ship.Size + "　SizeRect=[x:" + r.xmin + "~" + r.xmax
                        + " y:" + r.ymin + "~" + r.ymax + "] W=" + r.Width + " H=" + r.Height);
            sb.AppendLine("[逻辑] Position=" + Cell(ship.Position, ox, oz, cell)
                        + "　占位(SizeRect未旋转)= x:" + (node.XCoordinateInGrid + r.xmin)
                        + "~" + (node.XCoordinateInGrid + r.xmax)
                        + " z:" + (node.ZCoordinateInGrid + r.ymin)
                        + "~" + (node.ZCoordinateInGrid + r.ymax));
            try { sb.AppendLine("[逻辑] Forward=" + ship.Forward.ToString("F3")); } catch { }

            // ---------- 船：模型 ----------
            var t = ship.View.gameObject.transform;
            sb.AppendLine("[模型] pos=" + Cell(t.position, ox, oz, cell)
                        + "　eulerY=" + t.eulerAngles.y.ToString("F1") + "°"
                        + "　scale=" + t.lossyScale.x.ToString("F2"));
            sb.AppendLine("[差值] ★模型 − 逻辑 = " + Delta(t.position - ship.Position, cell));

            // ---------- 船：实际渲染范围（唯一不靠公式的量）----------
            //
            // ★为什么非要这一条★
            //   之前所有"模型对不对"的判据都是恒等式：
            //       模型位置 = Position + GetSizePositionOffset(SizeRect, Forward)
            //       真实占位 = GetBlockedNodes(node, SizeRect, Forward)
            //   两个函数同源，拿它们互证必然得 (0,0)，对"船画在哪几格"零信息量。
            //   我拿这个 (0,0) 三次得出"原版没问题"，玩家三次回答"有问题的是模型"。
            //
            // ★必须在船体坐标系里量★
            //   1.1.90 那版直接用 Renderer.bounds（**世界**轴对齐盒），读出 5.00×4.44 格。
            //   世界 AABB 对斜着放的物体会膨胀（45° 时能胀到 √2 倍），
            //   量一条转过来的船必然失真 —— 那组数没有意义。
            //   这里改用 mesh 自己的 bounds，经 (船的 worldToLocal × 网格的 localToWorld)
            //   变换到**船体坐标系**再取包围盒：X 就是舰宽、Z 就是舰长，与朝向无关，
            //   可以直接和 SizeRect 的 2×4 比。
            //
            // ★逐个列出来★
            //   护盾球、选中面片、尾焰这类东西会把包围盒撑大，混在一起看不出谁是谁。
            //   把每个网格单独打一行，最大的那个才是船体，异常的一眼就能挑出来。
            try
            {
                var shipT = ship.View.gameObject.transform;
                Matrix4x4 w2l = shipT.worldToLocalMatrix;
                bool any = false;
                float lx0 = 0, lx1 = 0, lz0 = 0, lz1 = 0;
                var lines = new List<string>();

                foreach (var mf in ship.View.gameObject.GetComponentsInChildren<MeshFilter>(false))
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled) continue;

                    Bounds mb = mf.sharedMesh.bounds;
                    Matrix4x4 m = w2l * mf.transform.localToWorldMatrix;

                    float ax0 = 0, ax1 = 0, az0 = 0, az1 = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        var corner = new Vector3(
                            (c & 1) == 0 ? mb.min.x : mb.max.x,
                            (c & 2) == 0 ? mb.min.y : mb.max.y,
                            (c & 4) == 0 ? mb.min.z : mb.max.z);
                        var p = m.MultiplyPoint3x4(corner);
                        if (c == 0) { ax0 = ax1 = p.x; az0 = az1 = p.z; }
                        else
                        {
                            if (p.x < ax0) ax0 = p.x; if (p.x > ax1) ax1 = p.x;
                            if (p.z < az0) az0 = p.z; if (p.z > az1) az1 = p.z;
                        }
                    }

                    if (!any) { lx0 = ax0; lx1 = ax1; lz0 = az0; lz1 = az1; any = true; }
                    else
                    {
                        if (ax0 < lx0) lx0 = ax0; if (ax1 > lx1) lx1 = ax1;
                        if (az0 < lz0) lz0 = az0; if (az1 > lz1) lz1 = az1;
                    }

                    lines.Add("    " + mf.name
                            + "　宽=" + ((ax1 - ax0) / cell).ToString("F2")
                            + " 长=" + ((az1 - az0) / cell).ToString("F2") + " 格"
                            + "　中心=(" + ((ax0 + ax1) * 0.5f / cell).ToString("F2") + ","
                            + ((az0 + az1) * 0.5f / cell).ToString("F2") + ")");
                }

                if (!any) sb.AppendLine("[渲染] 没找到可用的 MeshFilter");
                else
                {
                    float bw = (lx1 - lx0) / cell, bl = (lz1 - lz0) / cell;
                    float ccx = (lx0 + lx1) * 0.5f / cell, ccz = (lz0 + lz1) * 0.5f / cell;
                    sb.AppendLine("[渲染] 船体坐标系合并包围盒　宽(右舷向)=" + bw.ToString("F2")
                                + " 长(船首向)=" + bl.ToString("F2") + " 格"
                                + "　对比 SizeRect " + ship.SizeRect.Width + "×" + ship.SizeRect.Height);
                    sb.AppendLine("[判据★渲染] ★网格中心相对 pivot = 右舷向 " + ccx.ToString("F2")
                                + " 格，船首向 " + ccz.ToString("F2") + " 格"
                                + "　（不为 0 = 模型几何中心没落在自己的 pivot 上，"
                                + "transform 再准画面也是偏的）");
                    sb.AppendLine("[渲染] 逐个网格（" + lines.Count + " 个）：");
                    for (int i = 0; i < lines.Count && i < 24; i++) sb.AppendLine(lines[i]);
                    if (lines.Count > 24) sb.AppendLine("    …还有 " + (lines.Count - 24) + " 个");
                }
            }
            catch (Exception er) { sb.AppendLine("[渲染] 读取失败: " + er.Message); }

            // ★真正的判据：模型中心 vs 占位中心★
            //   「模型 − 底座」没用 —— 底座跟着本补丁一起动，实测永远是 (0,0)。
            //   而「绿格」画的是**落点方块**（游戏自己压成 W×W 的移动步长），
            //   不是船的占位，拿它对齐一条 2×4 的船必然差半个身位 ——
            //   玩家在各朝向调出来的那些 ±0.75，其实都是在补这个参照物的偏差。
            //
            //   占位本身是能算准的：落点节点和船锚点用同一套坐标，
            //   而落点间隔恰好等于 Width（大巡 3、巡洋 2），移动一步正好前进半个身位，
            //   反推出占位 = 锚点 + [0, 宽−1] × [0, 高−1]，锚点在西南角，
            //   90°/270° 时宽高互换。两条实测都对得上：
            //       巡洋 90°  占位 x:238~241 z:242~243 → 中心 锚点+(1.5, 0.5)
            //       大巡 0°   占位 x:250~252 z:250~255 → 中心 锚点+(1.0, 2.5)
            //   所以下面这一行为 (0,0) 就是船画对了，不需要任何人用眼睛判断。
            try
            {
                float fang = Mathf.Atan2(ship.Forward.x, ship.Forward.z) * Mathf.Rad2Deg;
                int fq = Mathf.RoundToInt(fang / 90f) % 4;
                if (fq < 0) fq += 4;
                bool fswap = (fq == 1 || fq == 3);
                int gw = fswap ? r.Height : r.Width;
                int gh = fswap ? r.Width : r.Height;
                int ax = node.XCoordinateInGrid, az = node.ZCoordinateInGrid;
                float ccx = ax + (gw - 1) * 0.5f;
                float ccz = az + (gh - 1) * 0.5f;
                float mx = (t.position.x - ox) / cell;
                float mz = (t.position.z - oz) / cell;
                sb.AppendLine("[占位] 旋转后 " + gw + "宽×" + gh + "高　x:" + ax + "~" + (ax + gw - 1)
                            + " z:" + az + "~" + (az + gh - 1)
                            + "　中心=(" + ccx.ToString("F2") + ", " + ccz.ToString("F2") + ")");
                sb.AppendLine("[判据] ★模型 − 占位中心 = 格(" + (mx - ccx).ToString("F2")
                            + ", " + (mz - ccz).ToString("F2") + ")　（这一行是 0 就说明船画对了）");
            }
            catch { }
            try
            {
                var off = Kingmaker.Code.Enums.Helper.SizePathfindingHelper.GetSizePositionOffset(ship, true);
                sb.AppendLine("[差值] GetSizePositionOffset(含本mod补偿) = " + Delta(off, cell));
                ShipViewCenterPatch.Bypass = true;
                var raw = Kingmaker.Code.Enums.Helper.SizePathfindingHelper.GetSizePositionOffset(ship, true);
                ShipViewCenterPatch.Bypass = false;
                sb.AppendLine("[差值] GetSizePositionOffset(原版) = " + Delta(raw, cell)
                            + "　→ 本mod补了 " + Delta(off - raw, cell));
            }
            catch { ShipViewCenterPatch.Bypass = false; }

            // ---------- 底座 ----------
            // ★底座是目前最可信的"游戏认为船该在哪"★
            //   它不走本 mod 的补偿（船挪了、底座没跟着挪，截图里看得很清楚），
            //   而且随朝向旋转 —— 所以「模型 − 底座」这一行为 0 就说明对齐了。
            try
            {
                GameObject mark = null;
                float best = float.MaxValue;
                foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
                {
                    if (go == null) continue;
                    if (go.name.IndexOf("StarshipUnitMark", StringComparison.Ordinal) < 0) continue;
                    float d = (go.transform.position - ship.Position).sqrMagnitude;
                    if (d < best) { best = d; mark = go; }   // 场上有多条船，取离本船最近的那个
                }
                if (mark != null)
                {
                    var mt = mark.transform;
                    sb.AppendLine("[底座] pos=" + Cell(mt.position, ox, oz, cell)
                                + "　eulerY=" + mt.eulerAngles.y.ToString("F1") + "°"
                                + "　scale=(" + mt.lossyScale.x.ToString("F2")
                                + "," + mt.lossyScale.z.ToString("F2") + ")");
                    sb.AppendLine("[差值] 底座 − 逻辑 = " + Delta(mt.position - ship.Position, cell));
                    sb.AppendLine("[差值] ★模型 − 底座 = " + Delta(t.position - mt.position, cell)
                                + "　（这一行是 0 就说明船画对了）");
                }
                else sb.AppendLine("[底座] 没找到 StarshipUnitMark");
            }
            catch { }

            // ★真实占位：暂时停用★
            //
            //   1.1.48 试过直接调 WarhammerBlockManager.GetBlockedNodes(node, rect, forward)，
            //   拿到的确实是真值，但它返回的 NodeList 是**池化对象** —— 借了必须还。
            //   我只遍历不归还，池子的记账被打乱，于是 ContextData.Check() 在
            //   Game.Tick 里遍历自己那份清单时撞上并发修改：
            //       System.InvalidOperationException: Collection was modified
            //       at ContextData.Check() → Game.Tick() → Runner.Update()
            //   因为挂在 Tick 上，每帧都炸，进存档即不可玩。
            //
            //   教训：诊断代码同样会改变被观测的系统。查询式 API 看着无害，
            //   一旦涉及对象池，"只读"就不再等于"无副作用"。
            //   下一步改用 NodeContains 逐格问 —— 传入格子和单位、返回 bool，
            //   不分配、不借还，天然没有这个问题。先把签名打出来。
            try
            {
                var bmType = AccessTools.TypeByName("Kingmaker.Pathfinding.WarhammerBlockManager");
                if (bmType != null && !_sigDumped)
                {
                    _sigDumped = true;
                    foreach (var m in bmType.GetMethods())
                        if (m.Name == "NodeContains" || m.Name == "GetBlockedNodes")
                            sb.AppendLine("[占位API] " + Sig(m));

                    // ★零调用探测★
                    //   上一版直接调 GetBlockedNodes，拿到真值却把游戏搞崩了 ——
                    //   返回的 NodeList 是池化对象，借了不还，ContextData.Check() 每帧抛异常。
                    //   这次只读**类型信息**：反射拿方法的 ReturnType，列出它的成员，
                    //   看归还的口子叫什么（Dispose / Release / Return / Free…）。
                    //   全程不触发任何游戏逻辑，也就不可能有副作用 ——
                    //   诊断代码自己把被观测的系统搞坏，这个错误犯一次就够了。
                    var gm2 = AccessTools.Method(bmType, "GetBlockedNodes",
                        new Type[] { typeof(Pathfinding.GraphNode), typeof(Pathfinding.IntRect), typeof(Vector3) });
                    if (gm2 != null)
                    {
                        var rt = gm2.ReturnType;
                        sb.AppendLine("[占位API] 返回类型 " + rt.FullName
                                    + "　IDisposable=" + typeof(IDisposable).IsAssignableFrom(rt));
                        var names = new List<string>();
                        foreach (var m in rt.GetMethods())
                        {
                            if (m.DeclaringType != rt) continue;
                            names.Add(m.Name);
                        }
                        names.Sort();
                        sb.AppendLine("[占位API] NodeList 成员: " + string.Join("　", names.ToArray()));
                    }

                    // 船身上的 blocker —— NodeContains 要的是它，不是单位本身
                    try
                    {
                        var bl = new List<string>();
                        foreach (var p in ship.GetType().GetProperties())
                            if (p.Name.IndexOf("Block", StringComparison.OrdinalIgnoreCase) >= 0)
                                bl.Add("实体." + p.Name + ":" + p.PropertyType.Name);
                        if (ship.View != null)
                            foreach (var p in ship.View.GetType().GetProperties())
                                if (p.Name.IndexOf("Block", StringComparison.OrdinalIgnoreCase) >= 0)
                                    bl.Add("视图." + p.Name + ":" + p.PropertyType.Name);
                        sb.AppendLine("[占位API] blocker 候选: " + (bl.Count == 0 ? "无" : string.Join("　", bl.ToArray())));
                    }
                    catch { }
                }

                // ★真实占位：调 GetBlockedNodes，用完必须归还★
                //
                //   1.1.48 崩游戏的原因已经查清：返回的 NodeList 是 **IDisposable** 的池化对象，
                //   借了不还，池子记账错乱，ContextData.Check() 每帧在 Game.Tick 里抛
                //   "Collection was modified"。零调用探测确认了它带 Dispose，
                //   所以套上 try/finally 就能安全使用 —— 病根是漏还，不是这个 API 本身。
                //
                //   为什么非用它不可：阻挡、寻路、点击判定全从这儿取占位。
                //   我之前那套「锚点在西南角 + 尺寸一半」是从落点间隔反推的，
                //   而落点方块（W×W 步长）和船占位（W×H）本来就是两回事，
                //   正交能对上纯属巧合 —— 玩家实测"正交也不太站得住"正是这个结果。
                //   真值只有一个来源，就是这里。
                //
                //   ★只在诊断路径调用★ 一次点炮调一次，频率极低。
                //   将来若要拿它驱动渲染，必须先加缓存，绝不能进每帧路径。
                var gm3 = bmType == null ? null : AccessTools.Method(bmType, "GetBlockedNodes",
                    new Type[] { typeof(Pathfinding.GraphNode), typeof(Pathfinding.IntRect), typeof(Vector3) });
                object inst3 = bmType == null ? null : AccessTools.Property(bmType, "Instance")?.GetValue(null);
                if (gm3 != null && inst3 != null)
                {
                    object res = null;
                    var cells = new List<Vector2Int>();
                    try
                    {
                        res = gm3.Invoke(inst3, new object[] { node, ship.SizeRect, ship.Forward });
                        var en = res as System.Collections.IEnumerable;
                        if (en != null)
                            foreach (var o in en)
                            {
                                var gn = o as CustomGridNodeBase;
                                if (gn != null) cells.Add(new Vector2Int(gn.XCoordinateInGrid, gn.ZCoordinateInGrid));
                            }
                    }
                    finally
                    {
                        var d = res as IDisposable;
                        if (d != null) d.Dispose();   // ← 漏了这一行就是上次那个每帧崩
                    }

                    if (cells.Count == 0) sb.AppendLine("[真实占位] 返回空");
                    else
                    {
                        Rows("[真实占位]", cells, sb);
                        int rminX = int.MaxValue, rmaxX = int.MinValue, rminZ = int.MaxValue, rmaxZ = int.MinValue;
                        foreach (var c in cells)
                        {
                            if (c.x < rminX) rminX = c.x;
                            if (c.x > rmaxX) rmaxX = c.x;
                            if (c.y < rminZ) rminZ = c.y;
                            if (c.y > rmaxZ) rmaxZ = c.y;
                        }
                        float tcx = (rminX + rmaxX) * 0.5f, tcz = (rminZ + rmaxZ) * 0.5f;
                        float mx2 = (t.position.x - ox) / cell;
                        float mz2 = (t.position.z - oz) / cell;
                        sb.AppendLine("[真实占位] " + (rmaxX - rminX + 1) + "宽×" + (rmaxZ - rminZ + 1)
                                    + "高　中心=(" + tcx.ToString("F2") + ", " + tcz.ToString("F2")
                                    + ")　锚点偏移=(" + (tcx - node.XCoordinateInGrid).ToString("F2")
                                    + ", " + (tcz - node.ZCoordinateInGrid).ToString("F2") + ")");
                        sb.AppendLine("[判据★真] ★模型 − 真实占位中心 = 格(" + (mx2 - tcx).ToString("F2")
                                    + ", " + (mz2 - tcz).ToString("F2")
                                    + ")　（这一行才是真判据，0 = 船画对了）");
                    }
                }
            }
            catch { }

            // ---------- 绿色可达 ----------
            Rows("[绿格]", ShipTelemetry.Grid, sb);

            // ---------- 落点 vs 占位：直接摆在一起 ----------
            //
            // 玩家的问题："高亮格的位置计算就是有问题的？还是坐标对但渲染位置不对？"
            //   渲染这一步可以排除 —— 高亮和绿格走同一条渲染通道，
            //   而绿格能点、点了船就去那儿，说明「格坐标 → 屏幕位置」是准的。
            //   所以只可能是坐标本身。下面把三样东西的坐标并排列出来，
            //   不需要任何推理就能看出谁跟谁对不上。
            try
            {
                var raw = ShipTelemetry.RawGrid;
                if (raw != null && raw.Count > 0)
                {
                    int rx0 = int.MaxValue, rx1 = int.MinValue, rz0 = int.MaxValue, rz1 = int.MinValue;
                    foreach (var c in raw)
                    {
                        if (c.x < rx0) rx0 = c.x; if (c.x > rx1) rx1 = c.x;
                        if (c.y < rz0) rz0 = c.y; if (c.y > rz1) rz1 = c.y;
                    }
                    sb.AppendLine("[原始落点] 共" + raw.Count + "格（游戏自己给的，未经本 mod 加工）"
                                + "　包围盒 x:" + rx0 + "~" + rx1 + " z:" + rz0 + "~" + rz1);

                    // 落点相对锚点的**步长格局**：锚点自己就是一个落点，
                    // 所以合法落点应当和锚点同奇偶。列出最近的几个，看步长和方向。
                    int ax = node.XCoordinateInGrid, az = node.ZCoordinateInGrid;
                    var near = new List<Vector2Int>();
                    foreach (var c in raw)
                    {
                        int d = Mathf.Max(Mathf.Abs(c.x - ax), Mathf.Abs(c.y - az));
                        if (d > 0 && d <= 4) near.Add(new Vector2Int(c.x - ax, c.y - az));
                    }
                    near.Sort((p, q) =>
                    {
                        int dp = Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y));
                        int dq = Mathf.Max(Mathf.Abs(q.x), Mathf.Abs(q.y));
                        return dp != dq ? dp - dq : (p.x != q.x ? p.x - q.x : p.y - q.y);
                    });
                    var line = new StringBuilder("[落点·相对锚点] ");
                    for (int i = 0; i < near.Count && i < 20; i++)
                        line.Append("(").Append(near[i].x).Append(",").Append(near[i].y).Append(") ");
                    sb.AppendLine(line.ToString());
                    sb.AppendLine("[落点·奇偶] 锚点=(" + ax + "," + az + ")"
                                + "　落点与锚点同奇偶 = 步长整齐；若出现异奇偶，说明落点不是以锚点为基准的");
                }
                else sb.AppendLine("[原始落点] 空 —— 说明这次调用时移动区还没算出来");
            }
            catch (Exception e2) { sb.AppendLine("[原始落点] 读取失败: " + e2.Message); }


            // ---------- 射界 ----------
            if (ShipTelemetry.HasPattern)
            {
                sb.AppendLine("[射界] 原点=(" + ShipTelemetry.PatternOrigin.x + ","
                            + ShipTelemetry.PatternOrigin.y + ")");
                Rows("[射界]", ShipTelemetry.Pattern, sb);
            }
            else sb.AppendLine("[射界] 本次没抓到 pattern（把鼠标移到战场上让射界画出来）");

            if (!_apiDumped) { _apiDumped = true; DumpApi(ship, sb); }

            Main.Log(sb.ToString());
            Main.FlushLog(true);
        }

        /// <summary>整个会话只打一次的 API 清单 —— 找"谁掌握真正的占位"。</summary>
        private static bool _apiDumped;

        /// <summary>占位 API 签名也只打一次。</summary>
        private static bool _sigDumped;

        /// <summary>
        /// 把可能持有**真实占位**的接口列出来。
        ///
        /// ★为什么必须直接问游戏★
        ///   到目前为止我用的占位是**反推**的：落点间隔恰好等于 Width，
        ///   由此倒推出「锚点 + [0,宽−1] × [0,高−1]」。它自洽、也和两条实测对得上，
        ///   但自洽不等于是真的 —— 判定、点击、阻挡一定有个权威来源，
        ///   拿反推的结果当尺子，等于用一个猜测去量另一个猜测。
        ///   玩家一句话点破了这件事：模型可能偏、射界起点可能错，
        ///   但"这条船真实占哪几格"必然是唯一确定的，那才是核心参照物。
        ///
        ///   ref 目录里没有游戏本体的反编译源，静态搜不到，所以在运行时反射列一遍：
        ///   名字里带 Occup / Node / Block / Rect / Size 的成员，那个真值多半就在其中。
        /// </summary>
        private static void DumpApi(Kingmaker.EntitySystem.Entities.StarshipEntity ship, StringBuilder sb)
        {
            try
            {
                sb.AppendLine();
                sb.AppendLine("-------- 占位 API 探测（整个会话只打一次）--------");

                var t = ship.GetType();
                var hits = new List<string>();
                foreach (var m in t.GetMethods())
                {
                    string n = m.Name;
                    if (n.IndexOf("Occup", StringComparison.OrdinalIgnoreCase) < 0
                     && n.IndexOf("Block", StringComparison.OrdinalIgnoreCase) < 0
                     && n.IndexOf("Node", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var ps = m.GetParameters();
                    var sig = new StringBuilder(n).Append('(');
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i > 0) sig.Append(", ");
                        sig.Append(ps[i].ParameterType.Name);
                    }
                    sig.Append(')');
                    hits.Add(sig.ToString());
                }
                hits.Sort();
                sb.AppendLine("  实体方法: " + (hits.Count == 0 ? "无" : string.Join("　", hits.ToArray())));

                var props = new List<string>();
                foreach (var p in t.GetProperties())
                {
                    string n = p.Name;
                    if (n.IndexOf("Occup", StringComparison.OrdinalIgnoreCase) < 0
                     && n.IndexOf("Block", StringComparison.OrdinalIgnoreCase) < 0
                     && n.IndexOf("Node", StringComparison.OrdinalIgnoreCase) < 0
                     && n.IndexOf("Rect", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    props.Add(n + ":" + p.PropertyType.Name);
                }
                props.Sort();
                sb.AppendLine("  实体属性: " + (props.Count == 0 ? "无" : string.Join("　", props.ToArray())));

                // 静态工具类：占位相关的算法多半是静态的
                var helper = typeof(Kingmaker.Code.Enums.Helper.SizePathfindingHelper);
                var hs = new List<string>();
                foreach (var m in helper.GetMethods())
                {
                    var ps = m.GetParameters();
                    var sig = new StringBuilder(m.Name).Append('(');
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i > 0) sig.Append(", ");
                        sig.Append(ps[i].ParameterType.Name);
                    }
                    sig.Append(')');
                    hs.Add(sig.ToString());
                }
                hs.Sort();
                sb.AppendLine("  SizePathfindingHelper: " + string.Join("　", hs.ToArray()));

                // 全局阻挡管理器 —— Owlcat 一贯把"哪个格子被谁占了"放在这种单例里
                foreach (string tn in new string[] {
                    "Kingmaker.Pathfinding.WarhammerBlockManager",
                    "Kingmaker.Pathfinding.UnitBlockManager",
                    "Kingmaker.Pathfinding.NodeBlockManager" })
                {
                    var bt = AccessTools.TypeByName(tn);
                    if (bt == null) continue;
                    var bm = new List<string>();
                    foreach (var m in bt.GetMethods())
                    {
                        if (m.DeclaringType != bt) continue;
                        bm.Add(m.Name);
                    }
                    bm.Sort();
                    sb.AppendLine("  " + bt.Name + ": " + string.Join("　", bm.ToArray()));
                }
            }
            catch (Exception e) { sb.AppendLine("  API 探测失败: " + e.Message); }
        }

        /// <summary>
        /// 把一堆格子压成「每行一个 z、列出 x 区间」。
        /// 直接列几十上百个 (x,z) 没人读得下去，压成区间之后
        /// 「哪一行少了一格」一眼就能看出来 —— 而那正是要找的东西。
        /// </summary>
        private static void Rows(string tag, List<Vector2Int> cells, StringBuilder sb)
        {
            if (cells == null || cells.Count == 0) { sb.AppendLine(tag + " 空"); return; }

            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            var byRow = new Dictionary<int, List<int>>();
            foreach (var c in cells)
            {
                if (c.x < minX) minX = c.x;
                if (c.x > maxX) maxX = c.x;
                if (c.y < minZ) minZ = c.y;
                if (c.y > maxZ) maxZ = c.y;
                List<int> row;
                if (!byRow.TryGetValue(c.y, out row)) { row = new List<int>(); byRow[c.y] = row; }
                row.Add(c.x);
            }
            sb.AppendLine(tag + " 共" + cells.Count + "格　包围盒 x:" + minX + "~" + maxX
                        + " z:" + minZ + "~" + maxZ);

            var zs = new List<int>(byRow.Keys);
            zs.Sort();
            zs.Reverse();                       // z 大的在上，跟屏幕上看到的顺序一致
            foreach (int z in zs)
            {
                var xs = byRow[z];
                xs.Sort();
                var line = new StringBuilder();
                int start = xs[0], prev = xs[0];
                for (int i = 1; i <= xs.Count; i++)
                {
                    if (i < xs.Count && (xs[i] == prev || xs[i] == prev + 1)) { prev = xs[i]; continue; }
                    if (line.Length > 0) line.Append(", ");
                    line.Append(start == prev ? start.ToString() : (start + "~" + prev));
                    if (i < xs.Count) { start = xs[i]; prev = xs[i]; }
                }
                sb.AppendLine("    z=" + z + "　x " + line);
            }
        }

        /// <summary>方法签名，只用于探测失败时把线索留在日志里。</summary>
        private static string Sig(System.Reflection.MethodInfo m)
        {
            var sb = new StringBuilder(m.Name).Append('(');
            var ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ps[i].ParameterType.Name);
            }
            return sb.Append(") -> ").Append(m.ReturnType.Name).ToString();
        }

        private static string Cell(Vector3 w, float ox, float oz, float cell)        {
            return "格(" + ((w.x - ox) / cell).ToString("F2") + ", " + ((w.z - oz) / cell).ToString("F2") + ")";
        }

        private static string Delta(Vector3 d, float cell)
        {
            return "格(" + (d.x / cell).ToString("F2") + ", " + (d.z / cell).ToString("F2") + ")";
        }

        /// <summary>
        /// 这门炮装在哪个槽（Prow / Port / Starboard / Keel / Dorsal）。
        ///
        /// ★两条路，因为第一条实测读不到★
        ///   原来只走 SourceItem.WeaponSlot.Type，日志里一直是 "槽位=?"。
        ///   而射界的规格全部按槽位分支（舷炮走对侧骷髅头、船首炮走船首格、船脊炮走船中心），
        ///   读不出槽位，规格里没有一条能验证 —— 所以这里必须给出退路：
        ///   遍历船体的武器槽，找装着这件武器的那个，槽自己知道自己的类型。
        ///
        /// ★失败时把类型名带出来★
        ///   两条路都不通就返回物品的实际类型，下一版照着它改，
        ///   而不是让玩家再点一轮只为了看见另一个 "?"。
        /// </summary>
        private static string SlotOf(Kingmaker.UnitLogic.Abilities.AbilityData ab,
                                     Kingmaker.EntitySystem.Entities.StarshipEntity ship)
        {
            object src = null;
            try
            {
                src = ab.SourceItem;
                if (src == null) return "（无来源物品）";

                // ★属性和字段都要试★
                //   前两版只用 AccessTools.Property，一直返回 "槽位=?"。
                //   类型名探出来是 ItemEntityStarshipWeapon —— 对象没找错，
                //   是 WeaponSlot 多半根本不是属性。Owlcat 的实体类大量用公开字段，
                //   只查属性等于自断一条路。
                var t = src.GetType();
                object ws = Member(src, t, "WeaponSlot", "m_WeaponSlot", "Slot", "m_Slot");
                if (ws != null)
                {
                    var ty = Member(ws, ws.GetType(), "Type", "m_Type", "SlotType");
                    if (ty != null) return "槽位=" + ty;
                }
            }
            catch { }

            // 退路：从船体的槽位表反查
            try
            {
                var hull = Member(ship, ship.GetType(), "Hull", "m_Hull");
                if (hull != null)
                {
                    var slots = Member(hull, hull.GetType(), "WeaponSlots", "m_WeaponSlots", "Weapons")
                                as System.Collections.IEnumerable;
                    if (slots != null)
                        foreach (var slot in slots)
                        {
                            if (slot == null) continue;
                            var item = Member(slot, slot.GetType(), "MaybeItem", "Item", "m_Item");
                            if (!ReferenceEquals(item, src)) continue;
                            var ty = Member(slot, slot.GetType(), "Type", "m_Type", "SlotType");
                            if (ty != null) return "槽位=" + ty + "（反查）";
                        }
                }
            }
            catch { }

            // 还是不行就把成员名列出来，下一版照着改，不必再让人点一轮
            try
            {
                var t = src.GetType();
                var names = new List<string>();
                foreach (var p in t.GetProperties())
                    if (p.Name.IndexOf("Slot", StringComparison.OrdinalIgnoreCase) >= 0)
                        names.Add("属性." + p.Name + ":" + p.PropertyType.Name);
                foreach (var f in t.GetFields())
                    if (f.Name.IndexOf("Slot", StringComparison.OrdinalIgnoreCase) >= 0)
                        names.Add("字段." + f.Name + ":" + f.FieldType.Name);
                return "槽位=? item=" + t.Name + " 含Slot成员["
                     + (names.Count == 0 ? "无" : string.Join(" ", names.ToArray())) + "]";
            }
            catch { return "槽位=?"; }
        }

        /// <summary>按名字依次试属性和字段 —— 哪个先命中用哪个。</summary>
        private static object Member(object obj, Type t, params string[] names)
        {
            foreach (var n in names)
            {
                try
                {
                    var p = AccessTools.Property(t, n);
                    if (p != null) { var v = p.GetValue(obj); if (v != null) return v; }
                }
                catch { }
                try
                {
                    var f = AccessTools.Field(t, n);
                    if (f != null) { var v = f.GetValue(obj); if (v != null) return v; }
                }
                catch { }
            }
            return null;
        }
    }
}
