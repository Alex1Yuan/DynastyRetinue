using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;   // StarshipEntity
using Kingmaker.Pathfinding;          // CustomGridNodeBase 在这儿，不在 Pathfinding 下
using Kingmaker.UI.SurfaceCombatHUD;
using Pathfinding;                    // GraphNode / IntRect / Path 才是 A* 包自己的

namespace DynastyRetinue
{
    /// <summary>
    /// 记住"当前正在给谁画移动区域"。
    ///
    /// ★为什么必须有这一层★
    ///   SetSpaceCombatMovementArea 只收三个节点列表，**不带单位**。
    ///   最初我图省事直接拿 Game.Instance.Player.PlayerShip 的尺寸，结果是：
    ///   鱼雷、飞机这些发射物本身只占 1×1，却被套上了母舰的 3×3 移动区域（玩家实测反馈）。
    ///   而上游的 ShipPathManager.SetPathMarkers(starship, path) 手里就有正确的单位，
    ///   在那儿记一笔即可 —— 它紧接着就会调 SetSpaceCombatMovementArea，中间不隔别的。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.UI.PathRenderer.ShipPathManager), "SetPathMarkers",
                  new Type[] { typeof(StarshipEntity), typeof(Path) })]
    internal static class ShipPathContext
    {
        internal static StarshipEntity Current;

        /// <summary>
        /// marker 世代号。SetPathMarkers 每次都会销毁并重建全部 marker，
        /// 所以这个数一变，任何缓存的 marker 位置都作废。
        /// 用它做缓存失效，免得每帧反射遍历几百个节点（1.4.15 海战变卡就是这么来的）。
        /// </summary>
        internal static int Generation;

        private static void Prefix(StarshipEntity starship)
        {
            Current = starship;
            Generation++;
        }
    }

    /// <summary>
    /// 海战里把绿色可走格按座舰尺寸铺开。
    ///
    /// ================== 病灶 ==================
    /// CombatHUDRenderer 里本来就有 `ExtendMovementAreaByUnitSize(nodes, sizeRect)`，
    /// 地面战斗的大型单位靠它把可走区域铺满。但**海战根本走不到那行**：
    ///
    ///     private void PopulateMovementArea(List&lt;GraphNode&gt; movementNodes)
    ///     {
    ///         if (m_SpaceCombatMovementAreaDisplayEnabled || ...) return;   // ← 海战在这里就返回了
    ///         ...
    ///         ExtendMovementAreaByUnitSize(movementNodes, m_ActiveUnit.SizeRect);
    ///     }
    ///
    /// 海战走的是另一条路 —— ShipPathManager 调 SetSpaceCombatMovementArea 直接塞节点列表，
    /// 整个跳过了扩展。于是不管船多大，绿格永远是一格一个。
    ///
    /// 玩家看到的现象：合法落点之间隔着 2 格（因为巡洋舰移动步长就是 2×2），
    /// 但每个落点只画中心那一格 —— 绿块散成一片，既难看也难点。
    ///
    /// ================== 修法 ==================
    /// 在 SetSpaceCombatMovementArea 进门时把三个 phase 列表各扩展一遍，
    /// **复用游戏自己的 ExtendMovementAreaByUnitSize**，不自己造轮子：
    /// 算法一致，将来官方改了逻辑我们跟着变。
    ///
    /// ★尺寸要先压成正方形★
    ///   直接用 SizeRect（巡洋舰 2×4）会把每格铺成 2×4，那是**占位**不是**步长**。
    ///   ShipPathManager 自己就写了这一行：
    ///       sizeRect.ymax = sizeRect.ymin + sizeRect.Width - 1;
    ///   压完巡洋舰是 2×2、大巡是 3×3、护卫舰是 1×1（所以护卫舰观感不变）。
    ///   这跟"半个身位"的手感完全对上 —— 船长always是宽的两倍。
    ///
    /// ★为什么改传入的 list 是安全的★
    ///   那三个 list 是 ShipPathManager 的成员，每次 SetPathMarkers 开头都会
    ///   ClearMovementAreaNodes() 重建，且只用于喂给 HUD。加进去的节点下一帧就被清掉。
    ///
    /// ★纯视觉★ 只影响绿格画多大，不碰寻路、不碰落点合法性、不进存档、不影响联机。
    /// </summary>
    [HarmonyPatch(typeof(CombatHUDRenderer), "SetSpaceCombatMovementArea")]
    internal static class ShipGridPatch
    {
        /// <summary>游戏那个私有扩展方法。取不到就整个补丁自动失效，绝不自己硬来。</summary>
        private static readonly System.Reflection.MethodInfo Extend =
            AccessTools.Method(typeof(CombatHUDRenderer), "ExtendMovementAreaByUnitSize",
                               new Type[] { typeof(List<GraphNode>), typeof(IntRect) });

        private static bool _warned;
        /// <summary>上一条诊断内容，用来去重 —— 悬停不动时这条路径每帧都会走。</summary>
        private static string _lastLog = "";

        private static void Prefix(CombatHUDRenderer __instance,
                                   List<CustomGridNodeBase> movementAreaPhaseOneNodes,
                                   List<CustomGridNodeBase> movementAreaPhaseTwoNodes,
                                   List<CustomGridNodeBase> movementAreaPhaseThreeNod)
        {
            try
            {
                if (!Main.Enabled) return;
                var s = Main.Settings;
                if (s == null || !s.ShipGridBySize) return;
                if (Extend == null)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Main.LogError("[移动格] 找不到 ExtendMovementAreaByUnitSize，"
                                    + "游戏版本可能变了 —— 移动格保持原样，不影响其它功能。");
                    }
                    return;
                }

                IntRect rect;
                if (!SquareRect(out rect)) return;

                // ★先抄一份原始落点，再动手★
                //   下面会往这三个 list 里加方块和占位高亮，加完就分不清谁是谁了。
                //   落点是这套坐标系里唯一的真相（玩家：绿格是按真实位置算出来的），
                //   判断占位对不对只能拿它当尺子 —— 必须在加工之前留底。
                try
                {
                    var raw = new List<CustomGridNodeBase>();
                    if (movementAreaPhaseOneNodes != null) raw.AddRange(movementAreaPhaseOneNodes);
                    if (movementAreaPhaseTwoNodes != null) raw.AddRange(movementAreaPhaseTwoNodes);
                    if (movementAreaPhaseThreeNod != null) raw.AddRange(movementAreaPhaseThreeNod);
                    ShipTelemetry.SetRawGrid(raw);
                }
                catch { }

                // ★这行是省开销，不是规则★
                //   Width=1 时下面算出来的 shift 是 0、扩展次数也是 0，走完全程结果一模一样。
                //   提前返回只是免掉几十个节点的无谓拷贝和字典操作。
                //   **千万别把它读成"只有大船才铺开"** —— 真正决定铺多大的是单位自己的
                //   SizeRect，任何宽度都自然成立：
                //     1×1 鱼雷/飞机 → 不变      1×2 护卫舰 → 不变
                //     2×4 舰船      → 2×2       3×6 大巡   → 3×3
                //   将来若放出能自动行动的 2×4 僚舰，它自动就是 2×2，这里一行都不用改。
                if (rect.Width <= 1) return;

                // ★1.1.79 的实现 —— 玩家验收过的那版★
                //   偏移集问 GetBlockedNodes，但传的是**压成正方形的 rect**（巡洋 2×2、大巡 3×3）。
                //   问出来的是"一个 W×W 的东西放这格会占哪儿"，正是步长方块该在的位置。
                //   1.1.80 我改成传 unit.SizeRect（整条船 2×4），位置和大小一起变了 ——
                //   之后又去猜方块该往哪边铺，四版全被否。玩家："1.1.79 的绿格才是好的"。
                //
                // ★1.4.9 恢复★ 1.4.7 曾把这里停掉，理由是"原版 marker 已是 W×W，
                //   两套绿色会分家"。那个理由站不住：玩家看到的**常驻**绿色是
                //   CombatHUDRenderer 的 1×1 区域格（原版 marker 默认 SetActive(false)，
                //   只有光标底下那一个会亮）。停掉它 = 整片绿色退回 1×1，这正是
                //   玩家说的"绿格还原成 1×1 了"。
                //
                //   而且这套格子本来就画在**船模位置**上（Footprint 走船体占位、随朝向刚性旋转），
                //   与 ShipMarkerAlign 的 S = −TieBreakOffset 逐格相同 —— 不是两套，是同一处。
                //   现在 marker 和鼠标都被挪到这儿来，三者才第一次真正重合。
                UnityEngine.Vector2Int[] offs = Footprint(rect);
                if (offs != null && offs.Length > 1)
                {
                    // ★把方块整体平移到船首那一块★
                    //   形状仍是 1.1.79 验收过的 W×W（也正是点击吸附等价类的形状），
                    //   只把**位置**挪到船模上。marker 和 m_DecalOffset 加的是同一个量的
                    //   世界坐标版 —— 三者是同一个平移，所以不会分家。
                    //
                    //   1.4.9 只挪了 marker 和光标、漏了这里，玩家实测正是
                    //   "实际落点对齐了模型，绿格还在原地"。
                    UnityEngine.Vector2Int org;
                    bool has = TryGetBlockOrigin(ShipPathContext.Current, out org);
                    if (has && (org.x != 0 || org.y != 0))
                    {
                        var shifted = new UnityEngine.Vector2Int[offs.Length];
                        for (int i = 0; i < offs.Length; i++)
                            shifted[i] = new UnityEngine.Vector2Int(offs[i].x + org.x, offs[i].y + org.y);
                        offs = shifted;
                    }

                    // 每个区块用自己的颜色：占位格跟着它所属的落点走，
                    // 方块整体同色，「本回合可达 / 冲刺可达 / 更远」的层次也保住。
                    // src 和 shade 传同一个 list 是有意的 —— Shade 内部先取 count 快照。
                    Shade(movementAreaPhaseOneNodes, offs, movementAreaPhaseOneNodes);
                    Shade(movementAreaPhaseTwoNodes, offs, movementAreaPhaseTwoNodes);
                    Shade(movementAreaPhaseThreeNod, offs, movementAreaPhaseThreeNod);
                    if (has) CrossCheck(org);
                }

                // ★把两个候选占位同时画出来，让玩家直接指认★（只在诊断开关打开时）
                //
                //   玩家原话："我只能看到模型渲染在哪，没法看到底层数据"。
                //   这正是这问题卡了近百个版本的原因 —— 一边只能看画面、一边只能看数字，
                //   谁也证不了两边说的是同一件事。而我每次只画一个候选，
                //   玩家只能回答"不对"，回答不了"应该是哪个"，于是又轮到我猜。
                //
                // ★把船的**真实逻辑占位**画出来（只在诊断开关打开时）★
                //
                //   这是引擎认定的占位：射界、命中判定、「无法用在自己身上」全都基于它。
                //   ★不加任何本 mod 的偏移★ —— 一旦加了，就变成"拿我挪过的东西
                //   去验证我挪过的东西"，必然吻合、毫无信息量。这个坑已经踩过两次。
                //
                //   判据：**船模应当正好压在这片高亮上**。
                //   若压不上，说明视图层和逻辑层脱节，射界就会离船一个身位。
                if (s.WatchMomentum) ShadeHull(movementAreaPhaseThreeNod, false);

                // 存一份给全量探针。三个 phase 合起来才是玩家看到的整片绿色，
                // 分开存的话读日志时还得自己拼，没意义。
                try
                {
                    var all = new List<CustomGridNodeBase>();
                    if (movementAreaPhaseOneNodes != null) all.AddRange(movementAreaPhaseOneNodes);
                    if (movementAreaPhaseTwoNodes != null) all.AddRange(movementAreaPhaseTwoNodes);
                    if (movementAreaPhaseThreeNod != null) all.AddRange(movementAreaPhaseThreeNod);
                    ShipTelemetry.SetGrid(all);
                }
                catch { }
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[移动格] 扩展失败: " + e.Message); }
            }
        }

        private static string _lastCheck = "";
        private static readonly Dictionary<string, UnityEngine.Vector2Int> _org =
            new Dictionary<string, UnityEngine.Vector2Int>();

        /// <summary>
        /// 落点方块该平移到哪 —— 「船首那 W×W 块」相对锚点的最小角，单位：格。
        ///
        /// ================== 为什么需要它 ==================
        /// Footprint 传的是**压成正方形**的 rect，而 GetBlockedNodes 对正方形 rect
        /// 的结果**不随朝向变**（2×2 绕锚点转还是那 4 格）。所以它恒是锚点上的 2×2 ——
        /// 正好等于点击吸附的等价类，这就是"绿格和鼠标一直对得上"的原因：本就是同一个东西。
        /// 但船体是**刚性旋转**的，于是船一转向，这块就不在船身上了。
        ///
        /// 位移必须从会转的那条路取：传整条船的 SizeRect，把每格投影到航向，
        /// 只留最靠船首的 W 层，取最小角。
        ///
        /// ================== 与玩家实测表的关系 ==================
        /// 玩家逐档指认出的 Δ（那时用来把**船**挪去追绿格）取负后，应当等于本函数的结果。
        /// 已核对的三档（实测船体包围盒来自 1.1.89 的探针日志）：
        ///     0°   船体 x:0~1  z:−2~1  → 船首层 z:0~1  → 最小角 ( 0, 0)   Δ=(0,0)   ✓
        ///     90°  船体 x:−2~1 z:−1~0  → 船首层 x:0~1  → 最小角 ( 0,−1)   Δ=(0,1)   ✓
        ///     180° 船体 x:−1~0 z:−1~2  → 船首层 z:−1~0 → 最小角 (−1,−1)   Δ=(1,1)   ✓
        ///
        /// ★展开成通式★ 最小角 = 0°(0,0)　90°(0,−(W−1))　180°(−(W−1),−(W−1))　270°(−(W−1),0)
        ///     W=1 护卫舰 → 恒 (0,0)，天生不用修（与玩家"护卫舰八向都对"完全吻合）
        ///     W=2 巡洋   → (0,±1)   与实测表逐格相同
        ///     W=3 大巡   → (0,±2)   **整格，不是半格**
        ///   之前我说大巡会出半格，那是沿用 e−Rot·e 那个拟合式的结论；
        ///   该式只在 W=2 上碰巧成立（W−1 恰好 = 2×0.5），换成真几何后半格根本不存在。
        ///
        /// ★1.1.89 被否不是因为算错★
        ///   那版也是切船首 W 层，玩家反馈"方块被挪走了，能点的落点还在原地"——
        ///   问题在于当时**只挪了绿格，没挪光标**。现在 marker 和 m_DecalOffset 加的是
        ///   同一个量的世界坐标版，三者是同一个平移。
        ///
        /// ★取不到就返回 false★ 贴图边、拿不到 BlockManager 时一律不挪；
        ///   此时绿格/标记/光标全部退回原版，仍然自洽。
        /// </summary>
        internal static bool TryGetBlockOrigin(StarshipEntity unit, out UnityEngine.Vector2Int origin)
        {
            origin = new UnityEngine.Vector2Int(0, 0);
            try
            {
                if (unit == null) return false;
                var full = unit.SizeRect;
                int w = full.Width;
                if (w <= 1) return false;                    // 护卫舰：恒 (0,0)，不用算

                var node = unit.CurrentUnwalkableNode as CustomGridNodeBase;
                if (node == null) return false;

                var fwd = unit.Forward;
                int bucket = UnityEngine.Mathf.RoundToInt(
                    UnityEngine.Mathf.Atan2(fwd.x, fwd.z) * UnityEngine.Mathf.Rad2Deg / 45f) & 7;
                string key = full.Width + "x" + full.Height + "|" + bucket;
                if (_org.TryGetValue(key, out origin)) return true;

                if (!_probed)
                {
                    _probed = true;
                    var t0 = AccessTools.TypeByName("Kingmaker.Pathfinding.WarhammerBlockManager");
                    if (t0 != null)
                    {
                        _bmInstProp = AccessTools.Property(t0, "Instance");
                        _bmGet = AccessTools.Method(t0, "GetBlockedNodes", new Type[] {
                            typeof(GraphNode), typeof(IntRect), typeof(UnityEngine.Vector3) });
                    }
                }
                if (_bmGet == null || _bmInstProp == null) return false;
                var inst = _bmInstProp.GetValue(null);
                if (inst == null) return false;

                // ★贴图边时形状是残缺的★ 枚举器会静默跳过越界节点，
                //   残缺的最小角一旦缓存，会平移给每个落点，地图中央也跟着错。
                var graph = node.Graph as CustomGridGraph;
                int pad = UnityEngine.Mathf.Max(full.Width, full.Height);
                int ax = node.XCoordinateInGrid, az = node.ZCoordinateInGrid;
                if (graph != null &&
                    (ax - pad < 0 || az - pad < 0 || ax + pad >= graph.width || az + pad >= graph.depth))
                    return false;

                var cells = new List<UnityEngine.Vector2Int>(32);
                object res = null;
                try
                {
                    res = _bmGet.Invoke(inst, new object[] { node, full, fwd });
                    var en = res as System.Collections.IEnumerable;
                    if (en != null)
                        foreach (var o in en)
                        {
                            var gn = o as CustomGridNodeBase;
                            if (gn == null) continue;
                            cells.Add(new UnityEngine.Vector2Int(
                                gn.XCoordinateInGrid - ax, gn.ZCoordinateInGrid - az));
                        }
                }
                finally
                {
                    var d = res as IDisposable;
                    if (d != null) d.Dispose();
                }
                if (cells.Count == 0) return false;

                // 投影到航向，只留最靠船首的 W 层。
                // 斜向档上这一层不是轴对齐的方块，但我们只取最小角，仍然良定义。
                float fx = fwd.x, fz = fwd.z;
                float mag = UnityEngine.Mathf.Sqrt(fx * fx + fz * fz);
                if (mag < 0.0001f) return false;
                fx /= mag; fz /= mag;

                float best = float.NegativeInfinity;
                for (int i = 0; i < cells.Count; i++)
                {
                    float p = cells[i].x * fx + cells[i].y * fz;
                    if (p > best) best = p;
                }
                float cut = best - w + 0.5f;   // 留半格容差，免得浮点把边界那层切掉

                int ox = int.MaxValue, oz = int.MaxValue;
                for (int i = 0; i < cells.Count; i++)
                {
                    if (cells[i].x * fx + cells[i].y * fz < cut) continue;
                    if (cells[i].x < ox) ox = cells[i].x;
                    if (cells[i].y < oz) oz = cells[i].y;
                }
                if (ox == int.MaxValue) return false;

                origin = new UnityEngine.Vector2Int(ox, oz);
                _org[key] = origin;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 自检：把「实际用的平移量」和「玩家八档实测表」并排打出来。
        ///
        /// ★两个数来源完全独立★
        ///   平移量 ← 引擎 GetBlockedNodes 的逻辑占位，切船首 W 层取最小角
        ///   实测表 ← 玩家逐档肉眼指认**船模**位置拟合出的 TieBreakOffset，取负
        ///   中间没有共用量，所以吻合不是自证 —— 过去两次"验证通过"翻车都是因为
        ///   拿同一个量挪过的两样东西互证，必然吻合且零信息量。
        ///
        /// ★不吻合时以哪个为准★
        ///   以平移量（占位）为准并照打日志。实测表只在 W=2 上验证过，
        ///   且它是拟合式，W≠2 时本就不该外推。差值直接给出，不用再让玩家逐档指认一轮。
        /// </summary>
        private static void CrossCheck(UnityEngine.Vector2Int org)
        {
            try
            {
                var cfg = Main.Settings;
                if (cfg == null || !cfg.WatchMomentum) return;
                var unit = ShipPathContext.Current;
                if (unit == null) return;

                float cell = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                if (cell <= 0.001f) return;

                var fwd = unit.Forward;
                int bucket = UnityEngine.Mathf.RoundToInt(
                    UnityEngine.Mathf.Atan2(fwd.x, fwd.z) * UnityEngine.Mathf.Rad2Deg / 45f) & 7;

                var t = -ShipViewCenterPatch.TieBreakOffset(unit.SizeRect, fwd, cell) / cell;
                string fit = "(" + t.x.ToString("F1") + "," + t.z.ToString("F1") + ")";
                bool same = UnityEngine.Mathf.Abs(t.x - org.x) < 0.01f
                         && UnityEngine.Mathf.Abs(t.z - org.y) < 0.01f;

                string line = "[三件套自检] 朝向档=" + (bucket * 45) + "°"
                            + "　船 " + unit.SizeRect.Width + "×" + unit.SizeRect.Height
                            + "　平移量(占位)=(" + org.x + "," + org.y + ")"
                            + "　实测表(−Δ)=" + fit
                            + (same ? "　✔ 两来源一致"
                                    : "　△ 不同 —— 以占位为准（实测表只在 2×4 上验证过）");
                if (line == _lastCheck) return;
                _lastCheck = line;
                Main.Log(line);
                Main.FlushLog(true);
            }
            catch { }
        }

        /// <summary>
        /// 「船停在某个格子上时会占哪几格」—— 表示成相对那个格子的偏移集。
        ///
        /// ★形状与位置无关★
        ///   GetBlockedNodes(node, rect, dir) 里 node 的唯一作用是提供平移量，
        ///   函数本身是纯的。所以同一 (尺寸, 朝向) 下所有落点的偏移集完全一致，采一次就够。
        ///   旧实现只对船**当前位置**采一次中心偏移，再 RoundToInt 套到所有落点上 ——
        ///   那一次取整就是一格误差的来源。
        ///
        /// ★缓存键用 rect 不用 Size★
        ///   游戏自己的 OffsetsKey 就是 (rect, direction)。跟着它走，
        ///   哪天 SizeRect 被改写也不会串味。键里**不含落点坐标** —— 形状本就平移不变。
        ///
        /// ★边界保护★
        ///   这条路要过 graph.GetNode，船贴着图边时采到的形状是残缺的
        ///   （NodeList 枚举器把 null 静默跳过）。残缺一旦缓存，会平移给每个落点，
        ///   地图中央也跟着少画格。所以四周留不够 Height−1 就放弃这一帧且不写缓存。
        ///
        /// ★Dispose★
        ///   今天 Dispose 是空操作（disposable:false），但 1.1.48 那次
        ///   "借了不还 → ContextData.Check() 每帧抛 Collection was modified → 进存档即崩"
        ///   代价太大，零成本的纪律留着。
        /// </summary>
        /// <summary>
        /// 落点方块：相对落点的偏移集，就是**船首那 W 层**（巡洋 2×2、大巡 3×3）。
        ///
        /// ★为什么不能直接问一个 W×W 的 rect★
        ///   GetBlockedNodes 传正方形 rect 时结果**不随朝向变**（2×2 绕锚点转还是那 4 格），
        ///   传整条船的 SizeRect 时才会刚性旋转。实测七个朝向档：
        ///       方块(2×2)   八档全是 x:0~1 z:0~1     ← 一动不动
        ///       船体(2×4)   0° x:0~1 z:-2~1 → 90° x:-2~1 z:-1~0 → 180° x:-1~0 z:-1~2
        ///   0° 上方块正好落在船体前两排，看着是对的；一转向船转了方块没转，
        ///   180°/225° 两轴同时差一格。这就是"模型和绿格对不上"的真正来源 ——
        ///   模型侧七档全部 `模型 − 真实占位中心 = (0,0)`，船一直画在对的地方。
        ///
        /// ★改从船体切★
        ///   拿会正确旋转的船体，把每格投影到船首方向，只留最靠船首的 W 层：
        ///       proj(c) = c · forward      保留 proj > max(proj) − W
        ///   0° 上这条规则算出来正好是 x:0~1 z:0~1，**与 1.1.79 逐格相同**（玩家验收过的那版），
        ///   其余七档自动跟着转。225° 实测也是干净的 2×2，且贴在船首。
        ///
        /// ★和 1.1.80 不是一回事★
        ///   那版同样传了 SizeRect，但**没有"只留前 W 层"这一步**，
        ///   于是方块变成整条船那么大，之后怎么平移都对不上。
        ///
        /// ★落点自身必然在方块内★
        ///   锚点是"船首后一格"，离船首不超过 1 层，W≥2 时恒被保留 ——
        ///   那一格是唯一能点的，绝不能被切掉。
        ///
        /// ★形状与位置无关，采一次即可★
        ///   GetBlockedNodes 里 node 的唯一作用是提供平移量，函数本身是纯的。
        ///   玩家的移动实验证实了这点：移动前后相对锚点的偏移完全一致
        ///   （0°、巡洋：移动前锚点 (250,250) 占位 z:248~251，移动后锚点 (250,252)
        ///     占位 z:250~253，偏移都是 z:−2~+1）。
        ///
        /// ★边界保护★ 贴着图边时枚举器会静默跳过越界节点，采到的形状是残缺的；
        ///   残缺一旦缓存会平移给每个落点，地图中央也跟着少画格。
        /// ★Dispose★ 今天是空操作，但 1.1.48 那次"借了不还 → 每帧崩"代价太大。
        /// </summary>
        private static UnityEngine.Vector2Int[] Footprint(IntRect rect)
        {
            try
            {
                var unit = ShipPathContext.Current;
                if (unit == null) return null;
                var node = unit.CurrentUnwalkableNode as CustomGridNodeBase;
                if (node == null) return null;

                var fwd = unit.Forward;
                int bucket = UnityEngine.Mathf.RoundToInt(
                    UnityEngine.Mathf.Atan2(fwd.x, fwd.z) * UnityEngine.Mathf.Rad2Deg / 45f) & 7;
                string key = rect.xmin + "," + rect.ymin + "," + rect.xmax + "," + rect.ymax + "|" + bucket;
                UnityEngine.Vector2Int[] hit;
                if (_fp.TryGetValue(key, out hit)) return hit;

                if (!_probed)
                {
                    _probed = true;
                    var t = AccessTools.TypeByName("Kingmaker.Pathfinding.WarhammerBlockManager");
                    if (t != null)
                    {
                        _bmInstProp = AccessTools.Property(t, "Instance");
                        _bmGet = AccessTools.Method(t, "GetBlockedNodes", new Type[] {
                            typeof(GraphNode), typeof(IntRect), typeof(UnityEngine.Vector3) });
                    }
                }
                if (_bmGet == null || _bmInstProp == null) return null;
                var inst = _bmInstProp.GetValue(null);
                if (inst == null) return null;

                var graph = node.Graph as CustomGridGraph;
                int pad = UnityEngine.Mathf.Max(unit.SizeRect.Width, unit.SizeRect.Height);
                int ax = node.XCoordinateInGrid, az = node.ZCoordinateInGrid;
                if (graph != null &&
                    (ax - pad < 0 || az - pad < 0 || ax + pad >= graph.width || az + pad >= graph.depth))
                    return null;

                // ★传压成正方形的 rect★（1.1.79 = 玩家验收过的那版）
                //   1.1.89 我改成"采整条船再切船首 W 层"，理由是船体会转而方块不转。
                //   推理链的起点是"模型画在 GetBlockedNodes 中心 ⇒ 那里就是船的真实位置"，
                //   而那一步是恒等式（模型位置和 GetBlockedNodes 同源），不成立。
                //   玩家实测：方块被挪走了，**能点的落点还在原地** —— 挪的是对的东西的位置。
                object res = null;
                var list = new List<UnityEngine.Vector2Int>(16);
                try
                {
                    res = _bmGet.Invoke(inst, new object[] { node, rect, fwd });
                    var en = res as System.Collections.IEnumerable;
                    if (en != null)
                        foreach (var o in en)
                        {
                            var gn = o as CustomGridNodeBase;
                            if (gn == null) continue;
                            list.Add(new UnityEngine.Vector2Int(
                                gn.XCoordinateInGrid - ax, gn.ZCoordinateInGrid - az));
                        }
                }
                finally
                {
                    var d = res as IDisposable;
                    if (d != null) d.Dispose();
                }

                if (list.Count == 0) return null;
                var arr = list.ToArray();
                _fp[key] = arr;

                // ★三个量摆在一起，才能看出"哪个该转没转"★
                //   实测七档定案：模型侧 `模型 − 真实占位中心 = (0,0)` 全部成立，船画得没问题；
                //   船体跟着朝向刚性旋转，而旧的方块八档纹丝不动 —— 错的是方块。
                //   这几行留着做回归：方块的包围盒现在应当**始终落在船体包围盒之内**，
                //   且格数等于 W×W（斜向可略多）。哪天又有人把它改回去，一眼就能看出来。
                try
                {
                    var st = Main.Settings;
                    if (st != null && st.WatchMomentum)
                    {
                        int bx0 = int.MaxValue, bx1 = int.MinValue, bz0 = int.MaxValue, bz1 = int.MinValue;
                        for (int i2 = 0; i2 < arr.Length; i2++)
                        {
                            if (arr[i2].x < bx0) bx0 = arr[i2].x;
                            if (arr[i2].x > bx1) bx1 = arr[i2].x;
                            if (arr[i2].y < bz0) bz0 = arr[i2].y;
                            if (arr[i2].y > bz1) bz1 = arr[i2].y;
                        }

                        // 船体：单独再问一次，传整条船的 SizeRect（这条路会跟着朝向转）。
                        // 只在诊断开关打开时才多这一次调用，正常游玩不走。
                        int hx0 = int.MaxValue, hx1 = int.MinValue, hz0 = int.MaxValue, hz1 = int.MinValue;
                        int hn = 0;
                        object r2 = null;
                        try
                        {
                            r2 = _bmGet.Invoke(inst, new object[] { node, unit.SizeRect, fwd });
                            var e2 = r2 as System.Collections.IEnumerable;
                            if (e2 != null)
                                foreach (var o2 in e2)
                                {
                                    var g2 = o2 as CustomGridNodeBase;
                                    if (g2 == null) continue;
                                    int dx2 = g2.XCoordinateInGrid - ax, dz2 = g2.ZCoordinateInGrid - az;
                                    hn++;
                                    if (dx2 < hx0) hx0 = dx2;
                                    if (dx2 > hx1) hx1 = dx2;
                                    if (dz2 < hz0) hz0 = dz2;
                                    if (dz2 > hz1) hz1 = dz2;
                                }
                        }
                        catch { }
                        finally { var d2 = r2 as IDisposable; if (d2 != null) d2.Dispose(); }

                        string hullTxt = hn == 0 ? "?"
                            : "x:" + hx0 + "~" + hx1 + " z:" + hz0 + "~" + hz1
                            + " 中心(" + ((hx0 + hx1) * 0.5f).ToString("F2") + ","
                            + ((hz0 + hz1) * 0.5f).ToString("F2") + ")　共" + hn + "格";

                        // 模型相对锚点的偏移。★读数可能是插值中途的★
                        //   绿格在指令完成时就重画，模型的旋转动画未必走完 ——
                        //   1.1.88 那批 −0.44 / −0.52 / 2.41 就是这么来的。
                        //   网格对齐的偏移只可能是 0.5 的整数倍，出现零头即为动画中途，该条作废。
                        string model = "?";
                        try
                        {
                            float cell = GraphParamsMechanicsCache.GridCellSize;
                            if (cell > 0.001f && unit.View != null)
                            {
                                UnityEngine.Vector3 np = (UnityEngine.Vector3)node.position;
                                float ox2 = np.x - ax * cell, oz2 = np.z - az * cell;
                                var mv = unit.View.gameObject.transform.position;
                                model = "(" + ((mv.x - ox2) / cell - ax).ToString("F2") + ","
                                            + ((mv.z - oz2) / cell - az).ToString("F2") + ")";
                            }
                        }
                        catch { }

                        Main.Log("[三量对比] 朝向档=" + (bucket * 45) + "°　Forward=" + fwd.ToString("F2")
                               + "\n    落点方块(船首W层) x:" + bx0 + "~" + bx1 + " z:" + bz0 + "~" + bz1
                               + " 中心(" + ((bx0 + bx1) * 0.5f).ToString("F2") + ","
                               + ((bz0 + bz1) * 0.5f).ToString("F2") + ")　共" + arr.Length + "格"
                               + "\n    船真实占位        " + hullTxt
                               + "\n    模型偏移          " + model
                               + "\n    三行都是相对锚点；方块应落在船体之内且贴船首，"
                               + "模型应等于船体中心（带零头=动画中途，该条作废）");
                        Main.FlushLog(true);
                    }
                }
                catch { }

                return arr;
            }
            catch { return null; }
        }

        private static bool _probed;
        private static System.Reflection.PropertyInfo _bmInstProp;
        private static System.Reflection.MethodInfo _bmGet;
        private static readonly Dictionary<string, UnityEngine.Vector2Int[]> _fp =
            new Dictionary<string, UnityEngine.Vector2Int[]>(StringComparer.Ordinal);
        private static readonly HashSet<long> _seen = new HashSet<long>();

        /// <summary>
        /// 把座舰的逻辑占位铺进 HUD，供肉眼和船模对照。
        ///
        /// ★corrected★
        ///   false = 原样（GetBlockedNodes 给的，也就是原版认为船占的格）
        ///   true  = 加上 Δ = e − Rot(朝向)·e —— 也就是 ShipViewCenterPatch 给模型加的那一份。
        ///           修正生效后，**船应当正好压在这一片上**。两片颜色不同，一眼可辨。
        ///
        /// ★节点引用在 Dispose 之后仍然有效★
        ///   NodeList 内部只有 m_Graph + m_Pattern，节点是遍历时按需从图里取的 ——
        ///   拿到的是图自己的节点对象，不是列表持有的副本。Dispose 只归还列表本身。
        ///   （仍然照常 Dispose：1.1.48 那次"借了不还 → 每帧崩"代价太大。）
        /// </summary>
        private static void ShadeHull(List<CustomGridNodeBase> dst, bool corrected)
        {
            try
            {
                if (dst == null) return;
                var unit = ShipPathContext.Current;
                if (unit == null) return;
                var node = unit.CurrentUnwalkableNode as CustomGridNodeBase;
                if (node == null) return;
                if (_bmGet == null || _bmInstProp == null) return;   // Footprint 还没探到就算了
                var inst = _bmInstProp.GetValue(null);
                if (inst == null) return;
                var graph = node.Graph as CustomGridGraph;

                // Δ 直接问 ShipViewCenterPatch —— 那是唯一的一份实测表，
                // 船和"船该在的高亮格"必须同源，否则两者会各说各话。
                int sx = 0, sz = 0;
                if (corrected)
                {
                    float cell = GraphParamsMechanicsCache.GridCellSize;
                    var d = ShipViewCenterPatch.Delta(unit.SizeRect, unit.Forward, cell);
                    if (cell > 0.001f)
                    {
                        sx = UnityEngine.Mathf.FloorToInt(d.x / cell + 0.5f);
                        sz = UnityEngine.Mathf.FloorToInt(d.z / cell + 0.5f);
                    }
                }

                object res = null;
                try
                {
                    res = _bmGet.Invoke(inst, new object[] { node, unit.SizeRect, unit.Forward });
                    var en = res as System.Collections.IEnumerable;
                    if (en != null)
                        foreach (var o in en)
                        {
                            var gn = o as CustomGridNodeBase;
                            if (gn == null) continue;
                            if (sx == 0 && sz == 0)
                            {
                                if (!dst.Contains(gn)) dst.Add(gn);
                                continue;
                            }
                            if (graph == null) continue;
                            int gx = gn.XCoordinateInGrid + sx, gz2 = gn.ZCoordinateInGrid + sz;
                            if (gx < 0 || gz2 < 0 || gx >= graph.width || gz2 >= graph.depth) continue;
                            var moved = graph.GetNode(gx, gz2) as CustomGridNodeBase;
                            if (moved != null && !dst.Contains(moved)) dst.Add(moved);
                        }
                }
                finally
                {
                    var d2 = res as IDisposable;
                    if (d2 != null) d2.Dispose();
                }
            }
            catch { }
        }

        /// <summary>
        /// 候选 B：锚点 + **旋转后的 SizeRect**，画进 HUD 供和候选 A 对照。
        ///
        /// ★为什么要画两个★
        ///   我每次只画一个候选，玩家只能回答"不对"，回答不了"应该是哪个" ——
        ///   于是又轮到我猜，猜错再来一版。两个一起画、颜色不同，
        ///   玩家指一下就定案，剩下的是纯实现问题。
        ///
        /// A 和 B 在 90° 实测相差整整 (2,1) 格，肉眼不会混。
        /// </summary>
        private static void ShadeSizeRect(List<CustomGridNodeBase> dst)
        {
            try
            {
                if (dst == null) return;
                var unit = ShipPathContext.Current;
                if (unit == null) return;
                var node = unit.CurrentUnwalkableNode as CustomGridNodeBase;
                if (node == null) return;
                var graph = node.Graph as CustomGridGraph;
                if (graph == null) return;

                var sr = unit.SizeRect;
                var fwd = unit.Forward;
                float ang = UnityEngine.Mathf.Atan2(fwd.x, fwd.z) * UnityEngine.Mathf.Rad2Deg;
                float rad = ang * UnityEngine.Mathf.Deg2Rad;
                float cs = UnityEngine.Mathf.Cos(rad), sn = UnityEngine.Mathf.Sin(rad);

                int ax = node.XCoordinateInGrid, az = node.ZCoordinateInGrid;
                for (int dx = sr.xmin; dx <= sr.xmax; dx++)
                    for (int dz = sr.ymin; dz <= sr.ymax; dz++)
                    {
                        // 绕锚点旋转（和 Unity 绕 +Y 转向量一致）
                        float rx = dx * cs + dz * sn;
                        float rz = -dx * sn + dz * cs;
                        int gx = ax + UnityEngine.Mathf.RoundToInt(rx);
                        int gz = az + UnityEngine.Mathf.RoundToInt(rz);
                        if (gx < 0 || gz < 0 || gx >= graph.width || gz >= graph.depth) continue;
                        var gn = graph.GetNode(gx, gz) as CustomGridNodeBase;
                        if (gn != null && !dst.Contains(gn)) dst.Add(gn);
                    }
            }
            catch { }
        }

        /// <summary>
        /// 座舰占位包围盒的中心，格坐标（分量必为整数或半整数）。
        ///
        /// ★为什么要单独开这个口子★
        ///   射界那边原来是拿**模型位置减去 Δ** 反推原版占位中心的。
        ///   Δ 在斜向档上是 0.707 这种无理数，减完中心变成 243.29，
        ///   RoundHalfUp 一取整就翻到隔壁格，指纹永远对不上 ——
        ///   实测日志里 [射界·平移] 一次都没触发，全被挡在 [射界·跳过]。
        ///   这条路直接问 GetBlockedNodes，整数格算出来的，精确且不受转向动画影响。
        /// </summary>
        internal static bool HullCenter(StarshipEntity unit, out float cx, out float cz)
        {
            cx = 0f; cz = 0f;
            try
            {
                if (unit == null) return false;
                var node = unit.CurrentUnwalkableNode as CustomGridNodeBase;
                if (node == null) return false;
                if (_bmGet == null || _bmInstProp == null) return false;
                var inst = _bmInstProp.GetValue(null);
                if (inst == null) return false;

                object res = null;
                int x0 = int.MaxValue, x1 = int.MinValue, z0 = int.MaxValue, z1 = int.MinValue;
                try
                {
                    res = _bmGet.Invoke(inst, new object[] { node, unit.SizeRect, unit.Forward });
                    var en = res as System.Collections.IEnumerable;
                    if (en != null)
                        foreach (var o in en)
                        {
                            var gn = o as CustomGridNodeBase;
                            if (gn == null) continue;
                            int gx = gn.XCoordinateInGrid, gz = gn.ZCoordinateInGrid;
                            if (gx < x0) x0 = gx;
                            if (gx > x1) x1 = gx;
                            if (gz < z0) z0 = gz;
                            if (gz > z1) z1 = gz;
                        }
                }
                finally
                {
                    var d = res as IDisposable;
                    if (d != null) d.Dispose();
                }
                if (x0 > x1) return false;
                cx = (x0 + x1) * 0.5f;
                cz = (z0 + z1) * 0.5f;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把每个落点的方块格画进 shade（落点本身留在原列表保持原色，那才是能点的）。
        /// </summary>
        private static void Shade(List<CustomGridNodeBase> src, UnityEngine.Vector2Int[] offs,
                                  List<CustomGridNodeBase> shade)
        {
            if (src == null || shade == null || offs == null) return;

            _seen.Clear();
            for (int i = 0; i < src.Count; i++)
            {
                var n = src[i];
                if (n == null) continue;
                _seen.Add(((long)n.XCoordinateInGrid << 32) ^ (uint)n.ZCoordinateInGrid);
            }

            // ★先取快照★ src 和 shade 可能是同一个 list，边遍历边追加会无限增长。
            int count = src.Count;
            for (int i = 0; i < count; i++)
            {
                var n = src[i];
                if (n == null) continue;
                for (int k = 0; k < offs.Length; k++)
                {
                    int dx = offs[k].x, dz = offs[k].y;
                    if (dx == 0 && dz == 0) continue;

                    var cur = n;
                    for (int st = 0; st < Math.Abs(dx) && cur != null; st++)
                        cur = cur.GetNeighbourAlongDirection(dx > 0 ? 1 : 3, false);
                    for (int st = 0; st < Math.Abs(dz) && cur != null; st++)
                        cur = cur.GetNeighbourAlongDirection(dz > 0 ? 2 : 0, false);
                    if (cur == null) continue;

                    long h = ((long)cur.XCoordinateInGrid << 32) ^ (uint)cur.ZCoordinateInGrid;
                    if (_seen.Add(h)) shade.Add(cur);
                }
            }
        }

        /// <summary>
        /// 当前单位的占位压成正方形。压法照抄 ShipPathManager.SetPathMarkers 的第一行，
        /// 保证绿格和它画的路径标记是同一个规格。
        ///
        /// ★用的是"正在移动的那个单位"，不是玩家座舰★
        ///   鱼雷、飞机这些发射物是独立单位、只占 1×1，拿母舰的尺寸去铺会把它们的
        ///   移动区域放大成 3×3 —— 那是错的（玩家实测反馈过）。
        ///   改成读当前单位后，规则对任何宽度都自然成立，不需要为船型写任何分支。
        /// </summary>
        private static bool SquareRect(out IntRect rect)
        {
            rect = default(IntRect);
            try
            {
                var unit = ShipPathContext.Current;
                if (unit == null) return false;
                rect = unit.SizeRect;
                rect.ymax = rect.ymin + rect.Width - 1;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 算「往左、往下各挪几格」，让扩展后的方块正好罩住船身。
        ///
        /// 扩展是 `节点 + [0..N-1]²`，也就是以节点为**左下角**；
        /// 而我们要的是以「节点 + 船中心偏移」为**中心**。两者相差：
        ///     平移量 = 船中心偏移 - (N-1)/2
        /// 船中心偏移取自 GetSizePositionOffset（带朝向），换算成格数。
        /// </summary>
        private static bool ShiftFor(IntRect rect, out int sx, out int sz)
        {
            sx = sz = 0;
            try
            {
                var unit = ShipPathContext.Current;
                if (unit == null) return false;

                var off = Kingmaker.Code.Enums.Helper.SizePathfindingHelper.GetSizePositionOffset(unit, true);
                float cell = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                if (cell <= 0.001f) return false;

                float half = (rect.Width - 1) / 2f;
                sx = UnityEngine.Mathf.RoundToInt(half - off.x / cell);
                sz = UnityEngine.Mathf.RoundToInt(half - off.z / cell);
                if (sx < 0) sx = 0;
                if (sz < 0) sz = 0;
                return true;
            }
            catch { return false; }
        }

        /// <summary>把整批节点沿某个方向挪 n 格。走到图边缘拿到 null 就保持原样，宁可不挪也别丢格子。</summary>
        private static void Move(List<CustomGridNodeBase> nodes, int dir, int n)
        {
            if (n <= 0) return;
            for (int i = 0; i < nodes.Count; i++)
            {
                var cur = nodes[i];
                for (int s = 0; s < n && cur != null; s++) cur = cur.GetNeighbourAlongDirection(dir, false);
                if (cur != null) nodes[i] = cur;
            }
        }
    }
}
