using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities.Components.Patterns;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 舰炮射界：把 applicationNode 的半尺寸偏移**跟着朝向旋转**。
    ///
    /// ================== 病灶（实测确认）==================
    ///   朝向    applicationNode   direction      占位中心        node−占位中心
    ///   0°      (251,251)         (0,0,1)        (250.5,249.5)   (+0.50, +1.50)
    ///   315°    (248,259)         (-.71,0,.71)   (248.5,257.5)   (-0.50, +1.50)
    ///   270°    (244,261)         (-1,0,0)       (244.5,260.5)   (-0.50, +0.50)
    ///
    /// direction 恒等于船的 Forward —— 旋转量没问题，排除。
    /// 而 0° 那组偏移 (0.5, 1.5) 正是 ((W−1)/2, (H−1)/2)，即巡洋的半宽、半长。
    /// 结论：**applicationNode = 占位中心 + SizeRect 半尺寸，而这个偏移从不随朝向旋转**。
    /// 0° 时船恰好朝北、未旋转即正确；一转向就偏。
    ///
    /// 验算 270°（fwd=(-1,0,0)、right=(0,0,1)）：
    ///     正确 = 占位中心 + right×0.5 + fwd×1.5 = (243, 261)
    ///     实际 = (244, 261)                        差 (1, 0) —— 正好一格
    ///
    /// 这也解释了"按 1×2 护卫舰算"：护卫舰半宽 0、半长 0.5，两者只差半格，
    /// 被圆角吃掉看不出来；巡洋 2×4 差一整格，立刻暴露。
    ///
    /// ================== 修法 ==================
    /// 把偏移换成 right×(W−1)/2 + fwd×(H−1)/2，一处修完所有炮 ——
    /// 不需要按槽位分支，因为各槽位共用同一个基准点，槽位差异在 pattern 模板里。
    ///
    /// ★这一层是所有 AoE 技能的公共出口，必须严格限流★
    ///   GetOriented(node, direction) **不带 caster 参数**，无法直接知道这次调用是给谁的。
    ///   所以用两个强条件把范围收死：
    ///     · direction 必须与玩家座舰的 Forward 几乎相同（同朝向）
    ///     · node 必须落在座舰占位中心附近（一条船长以内）
    ///   两条同时成立才动手。地面单位、敌舰、非本船技能一律原样放过。
    ///
    /// ★显示和判定一起改★
    ///   pattern 同时喂给显示和命中判定，改这里两边一起变 ——
    ///   正是玩家要求的"显示和判定都要改"。也因此开关**不进** CoopState.LocalOnly，
    ///   必须参与设置指纹，保证联机两端一致。
    /// </summary>
    [HarmonyPatch]
    internal static class ShipArcAlign
    {
        /// <summary>
        /// 只挂 **GetOriented 的九参重载** —— 它是所有射界的共同出口。
        ///
        /// ★两个重载的关系（离线读 IL 确认，见 tools/il_calls.ps1）★
        ///       GetOriented(applicationNode, direction)              两参
        ///           └─ 内部调用 →  GetOriented(checkLosFromNode, applicationNode, direction, 6个bool)
        ///
        ///   所以：
        ///     · 只挂两参 → 直接调九参的炮（舷炮）碰不到  ← 1.3.7 之前"舷炮没修好"的真因
        ///     · 两个都挂 → 走两参那条路的炮（艏炮）被改**两遍**，位移叠加
        ///                  （1.3.8 实测：入参已是修正值 +0.50，最终被推到 +1.50）
        ///     · 只挂九参 → 两条路都经过它，且每次只改一遍　✓
        ///
        /// ★为什么按参数名而不是参数个数挑★
        ///   九参那个的**第一个**参数也是 CustomGridNodeBase（checkLosFromNode），
        ///   按位置绑定会改错对象。Harmony 的 Prefix 按名字绑定，
        ///   所以这里也用名字来认，两边口径一致。
        /// </summary>
        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var list = new System.Collections.Generic.List<MethodBase>();
            var t = AccessTools.TypeByName("Kingmaker.UnitLogic.Abilities.Components.Patterns.AoEPattern");
            if (t == null) return list;

            MethodBase best = null;
            int bestParams = -1;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name != "GetOriented") continue;
                bool hasNode = false, hasDir = false, hasLos = false;
                foreach (var p in m.GetParameters())
                {
                    if (p.Name == "applicationNode" && p.ParameterType == typeof(CustomGridNodeBase)) hasNode = true;
                    if (p.Name == "direction" && p.ParameterType == typeof(Vector3)) hasDir = true;
                    if (p.Name == "checkLosFromNode") hasLos = true;
                }
                if (!hasNode || !hasDir) continue;
                // 认 checkLosFromNode 这个特征参数；万一将来改名，退回"参数最多的那个"，
                // 因为共同出口必然是签名最全的那一个。
                int np = m.GetParameters().Length;
                if (hasLos) { best = m; break; }
                if (np > bestParams) { bestParams = np; best = m; }
            }
            if (best != null) list.Add(best);
            return list;
        }

        private static bool Prepare()
        {
            MethodBase m = null;
            foreach (var x in TargetMethods()) { m = x; break; }
            Main.Log("[射界·对齐] 补丁挂载 "
                   + (m != null ? "成功 → GetOriented(" + m.GetParameters().Length + " 参，共同出口)"
                                : "失败：找不到带 applicationNode/direction 的 GetOriented"));
            return m != null;
        }

        private static bool _warned;
        private static readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 每一条提前返回的原因，各报一次。
        ///
        /// ★为什么非加不可★
        ///   实测日志里舷炮**一次修正记录都没有**，从头到尾只有 Prow。
        ///   按代码算 0° 的 Starboard 该从 (251,251) 挪到 (250,250)，必然留痕 ——
        ///   没有，说明它在前面某个 return 就退出了。而那些 return 全是静默的，
        ///   我分不清是上下文为空、槽位读不到、还是别的。
        ///   "什么都没发生"必须能自己说出是哪一步没发生，否则每一轮都得让玩家重测。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _why =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static void Why(string reason)
        {
            try
            {
                var s = Main.Settings;
                if (s == null || !s.WatchMomentum) return;
                if (!_why.Add(reason)) return;
                Main.Log("[射界·跳过] " + reason);
                Main.FlushLog(true);
            }
            catch { }
        }

        /// <summary>
        /// ★按玩家给的规格重定基准点★
        ///
        ///     舷炮（Port / Starboard）  起点 = 本舷中点，偶数宽取外侧两格的中点
        ///     船首炮（Prow）            起点 = 船首格
        ///     船脊 / 背炮（Keel/Dorsal）起点 = 船正中心
        ///
        ///   起点落在**船体边缘**而不是船心，射程就从边缘起算 ——
        ///   船身自己占的格子不消耗射程，这正是"船身宽度不计入射程、额外增加这部分"。
        ///
        /// ★为什么不是一个公式套所有炮★
        ///   上一版假设"所有炮共用一个基准点"，日志立刻打脸：
        ///   0° 下一门炮 node=(250,251)、另一门=(251,251)。
        ///   炮装在船的不同位置，基准本来就该不同 —— 必须按槽位分支。
        ///
        /// ★为什么用具名 ref 参数★
        ///   上一版用 object[] __args 写回，日志显示改了、画面纹丝不动 ——
        ///   这个 Harmony 版本不把 __args 的修改回写给原方法。
        ///   具名参数类型精确匹配（CustomGridNodeBase applicationNode），ref 才真正生效。
        /// </summary>
        private static void Prefix(ref CustomGridNodeBase applicationNode, Vector3 direction)
        {
            try
            {
                // ★★ 已停用 —— 这个补丁打在了错误的地方 ★★
                //
                //   九参 AoEPattern.GetOriented 不是舰炮射界的生成点。它是
                //   RestrictedFiringAreaComponent.GetRestrictedArea 用的**角度限制蒙版**
                //   （日志里那个恒为 93/87 格的东西）。真正的射界是：
                //       WeaponSlot 按 槽位类型 + OffsetFromProw + BatteryWidth + SizeRect + 朝向
                //       算出炮组源格 → FiringArcHelper.TraverseGraph 展开射程
                //       → 与角度蒙版求交 → 多源格 UnionWith → 减去船体自身占格
                //   在这里挪蒙版只会把射界扭曲成别的形状，治不了根。
                //
                //   另外：我一直当判据的「最终 pattern 原点」是
                //   restrictedFiringArcNodes.FirstOrDefault() —— HashSet 的第一个元素，
                //   不是炮口位置。基于它做的"原点随朝向翻面"分析全部作废。
                //
                //   正确的下手点是 WeaponSlot.GetFiringArcSourceNodesOffsets(...)，
                //   它同时被显示、CanTargetFromNode、UnitUseAbilityParams.IsDirectionCorrect 三条路径经过。
                //   保留本文件只为记住：**改之前先确认这个方法真的产出你要改的那个东西**。
                return;

#pragma warning disable 162
                var s = Main.Settings;
                if (s == null || !s.ShipArcFix) return;
                if (applicationNode == null) { Why("applicationNode 为空"); return; }

                var game = Kingmaker.Game.Instance;
                var ship = game != null && game.Player != null ? game.Player.PlayerShip : null;
                if (ship == null || ship.View == null || ship.View.gameObject == null)
                { Why("拿不到座舰或它的 View"); return; }

                var rect = ship.SizeRect;
                // ★护卫舰不碰★ 1×2 各朝向原版都正确（玩家实测确认过），
                //   而这套偏移是按大船的半尺寸算的，套上去只会把对的改坏。
                if (rect.Width <= 1) { Why("船宽=1（护卫舰/劫掠舰），按原版不碰"); return; }

                // 这个方法不带 caster，只能靠朝向一致来限流：地面单位、敌舰一律放过
                Vector3 fwd = ship.Forward;
                if (fwd.sqrMagnitude < 0.001f || direction.sqrMagnitude < 0.001f)
                { Why("朝向向量为零"); return; }
                if (Vector3.Dot(fwd.normalized, direction.normalized) < 0.99f)
                { Why("direction 与座舰朝向不一致（多半是别的单位的技能）"); return; }

                // ★不再要求"必须拿到技能"★
                //   旧版靠 ShipArcContext / CursorController.SelectedAbility 来限流。
                //   问题是这两条**只在点击那条路上有值**：玩家把鼠标悬停在炮上预览时，
                //   日志里是 `[射界·跳过] 上下文和光标都拿不到技能` —— 补丁直接放行，
                //   于是悬停显示原版、点击显示补丁版，同一门炮两个射界。
                //   玩家原话："点击是一个射程，鼠标移动到炮上预览是另一个"。
                //   限流条件必须是两条路都拿得到的量，否则修好一边照样对不上。
                //   槽位仍然读，但只用于日志，读不到也照改。
                string slot = SlotOf(ShipArcContext.Current
                                     ?? (game.CursorController != null ? game.CursorController.SelectedAbility : null),
                                     ship);

                float cell = GraphParamsMechanicsCache.GridCellSize;
                if (cell <= 0.001f) return;

                // ★占位中心直接问 GetBlockedNodes★
                //   原来是拿模型位置减去 Δ 反推的，而 Δ 在斜向上是 0.707 这种无理数，
                //   减完中心变成 243.29，RoundHalfUp 一取整就翻到隔壁格 ——
                //   实测日志里 [射界·平移] 一次都没触发，全被指纹挡掉。
                //   这条路是整数格算出来的，分量必为整数或半整数，且不受转向动画影响。
                float cx, cz;
                if (!ShipGridPatch.HullCenter(ship, out cx, out cz))
                { Why("拿不到占位中心（GetBlockedNodes 未就绪）"); return; }

                Vector3 dv = ShipViewCenterPatch.Delta(rect, fwd, cell);
                float dcx = dv.x / cell, dcz = dv.z / cell;

                Vector3 f = fwd.normalized;

                // ★限流：node 必须落在本舰船体附近★
                //
                //   原来要求 node **精确等于** RoundHalfUp(占位中心)+step，
                //   假设"全船的炮共用一个原点"。实测打脸 —— 每门炮的 applicationNode 不同：
                //       node=(250,251) ≠ 期望(251,251)     ← 本舰的炮，被当成别人挡掉了
                //       node=(259,261) ≠ 期望(259,260)
                //       node=(253,258) ≠ 期望(253,259)
                //   炮装在船体不同位置，原点本来就该不同。于是只有一门炮被修，
                //   其余全被自己的指纹拦在门外 —— 玩家看到的正是"光矛改了、舷炮没改"。
                //
                //   改成范围判定：本舰所有炮的原点都在船体那一小片里。
                //   半径取 max(W,H)/2 + 1：巡洋 = 3 格，斜向档包围盒最大也只有 4×4，够用。
                //   配合上面的 direction 一致性检查，别的单位要误伤，得同时满足
                //   "朝向完全相同"且"贴着我们的船" —— 而且后果只是显示位移一格。
                float rad = UnityEngine.Mathf.Max(rect.Width, rect.Height) * 0.5f + 1f;
                int nx = applicationNode.XCoordinateInGrid, nz = applicationNode.ZCoordinateInGrid;
                if (Mathf.Abs(nx - cx) > rad || Mathf.Abs(nz - cz) > rad)
                {
                    Why("node=(" + nx + "," + nz + ") 离本舰占位中心 ("
                      + cx.ToString("F1") + "," + cz.ToString("F1") + ") 超过 "
                      + rad.ToString("F0") + " 格，判定为别的单位");
                    return;
                }

                // ★原版射界原点本来就是对的 —— 这里只观测，不动画面★
                //
                //   用 0°（唯一确认正确的朝向）定标：射界原点相对**修正后**的船中心
                //   恒为「船首向 (H−1)/2、右舷向 (W−1)/2」。拿原版 node 反推三个朝向：
                //
                //     朝向   原版 node    修正后船中心      原点在船体坐标
                //     90°    (259,260)    (257.5, 260.5)    船首 1.5、右舷 0.5
                //     180°   (268,250)    (268.5, 251.5)    船首 1.5、右舷 0.5
                //     270°   (260,243)    (261.5, 242.5)    船首 1.5、右舷 0.5
                //
                //   三档完全一致 —— **射界一直站在"船该在的位置"上，错的自始至终只有模型**。
                //   1.2.0/1.2.1 那版给它补了一份 Δ，等于把对的东西推歪了
                //   （玩家实测："右转180 没问题，唯一就是射界还不对"）。
                //
                //   反过来这也独立验证了那张实测表：拿原版 node 反解出的船中心，
                //   减去 GetBlockedNodes 中心，90°/180°/270° 得 (0,1)/(1,1)/(1,0)，
                //   与玩家肉眼指认的「左舷1」「左舷1+船尾1」「船尾1」逐个吻合。
                //
                //   保留指纹：它是唯一正确的下手点。
                //
                // ★把射界原点挪回它相对船体应有的位置★
                //
                //   原版的病：占位中心落在格线上（巡洋宽 2 格、长 4 格都是偶数），
                //   原点必须在相邻两格里二选一，而引擎恒定朝世界 +x/+z 挑、不随朝向转。
                //   把原点换算到船体坐标就一目了然（用真实船中心，八档实测占位）：
                //
                //       0°    船首 1.5    右舷 +0.5      45°   船首 2.121  右舷 0
                //       90°   船首 1.5    右舷 −0.5      135°  船首 1.414  右舷 −0.707
                //       180°  船首 0.5    右舷 −0.5      225°  船首 0.707  右舷 0
                //       270°  船首 0.5    右舷 +0.5      315°  船首 1.414  右舷 +0.707
                //
                //   同一门炮，横向在 0°/90° 之间**翻了个面** —— 这就是玩家一直说的
                //   "原版射界也是错的"。0° 和 45° 是玩家确认正确的基准档。
                //
                //   补到基准所需的位移 = −(平局残差)，换算成世界坐标**恰好全是整格**：
                //       0°/45° (0,0)   90°/135° (0,−1)   180°/225° (−1,−1)   270°/315° (−1,0)
                //
                // ★为什么改这里，不改船★
                //   曾经反过来做：把**船**挪 +残差。射界相对船确实对上了，
                //   但船离开了它逻辑上占的格子 —— 引擎立刻用「无法用在自己身上」
                //   点破（那格落在渲染出的船体之外）。占位、落点、命中判定都读逻辑位置，
                //   挪画面治标不治本。改 applicationNode 则只动 pattern，
                //   船、占位、落点、判定一格不动，而显示与命中共用同一个 pattern，两边一起对。
                //
                // ★0° 上是恒等变换★
                //   玩家唯一从头到尾确认正确的朝向上一格不改 ——
                //   任何在 0° 上会动的方案都可以直接否掉（上一版就是这么被否的）。
                Vector3 tb = ShipViewCenterPatch.TieBreakOffset(rect, fwd, cell);
                int dx = -Mathf.FloorToInt(tb.x / cell + 0.5f);
                int dz = -Mathf.FloorToInt(tb.z / cell + 0.5f);

                // ★测量现场要在提前返回**之前**记★
                //   原来放在函数末尾，于是 0°/45° 走"无需修正"直接 return，
                //   基准档一条测量都留不下 —— 而基准正是算其它档修正量的唯一依据。
                //   仪器本身漏采数据，比数据不对更难发现。
                _mValid = true;
                _mSlot = slot ?? "?";
                _mNx = nx; _mNz = nz;
                _mBucket = Mathf.RoundToInt(ship.Orientation / 45f) & 7;
                _mCx = cx; _mCz = cz;
                _mF = f;
                _mR = Quaternion.AngleAxis(90f, Vector3.up) * f;
                _mW = rect.Width; _mH = rect.Height;

                if (dx == 0 && dz == 0) { Why("本朝向无需修正（0°/45° 恒等）"); return; }

                var target = Walk(applicationNode, dx, dz);
                if (target == null) { Why("走到图边界，放弃修正"); return; }

                if (s.WatchMomentum)
                {
                    int bk = _mBucket;
                    if (_seen.Add("arc|" + bk + "|" + (slot ?? "?") + "|" + nx + "," + nz))
                    {
                        Main.Log("[射界·修正] 朝向档=" + (bk * 45) + "°　" + (slot ?? "?")
                               + "　原点 (" + nx + "," + nz + ") → (" + (nx + dx) + "," + (nz + dz) + ")"
                               + "　位移 " + dx + "," + dz + " 格"
                               + "　（= −平局残差；船与占位一格未动）");
                        Main.FlushLog(true);
                    }
                }

                applicationNode = target;
                return;
            }
#pragma warning restore 162
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[射界·基准] 失败（射界保持原样）: " + e.Message); }
            }
        }

        // Prefix 记下的现场，供 Postfix 把覆盖换算到船体坐标
        private static bool _mValid;
        private static string _mSlot;
        private static int _mBucket, _mW, _mH, _mNx, _mNz;
        private static float _mCx, _mCz;
        private static Vector3 _mF, _mR;
        private static readonly System.Collections.Generic.HashSet<string> _measured =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 把射界覆盖换算到**船体坐标**（相对修正后的船中心）并打出来。
        ///
        /// ★为什么必须换算★
        ///   世界坐标下每个朝向的数字都不一样，没法互相比。换算到船体坐标之后，
        ///   同一门炮在**所有朝向上都该给出同一组数**（前后各够多远、左右到哪、离船多近）。
        ///   0° 和 45° 是玩家确认正确的档 —— 拿它们当基准，
        ///   其余档的差值直接就是要补的修正量，不需要任何推测。
        ///
        /// ★这是替代"猜位移量"的做法★
        ///   1.2.2（不平移）和 1.2.3（平移 Δ）都被玩家实测否掉，
        ///   说明偏差不是简单平移。再猜下去只会继续消耗玩家的测试轮次。
        /// </summary>
        private static void Postfix(OrientedPatternData __result)
        {
            try
            {
                if (!_mValid) return;
                _mValid = false;

                var s = Main.Settings;
                if (s == null || !s.WatchMomentum) return;
                // ★按原点去重，不按槽位★ 槽位经常读成 "?"，
                // 第一条 "?" 记完之后舷炮就被去重吃掉了 —— 手里只剩光矛的数据。
                // 原点唯一标识一门炮，不依赖读不读得到槽位。

                int n = 0;
                float fMin = float.MaxValue, fMax = float.MinValue;
                float rMin = float.MaxValue, rMax = float.MinValue;
                foreach (CustomGridNodeBase nd in __result.Nodes)
                {
                    if (nd == null) continue;
                    n++;
                    float dx = nd.XCoordinateInGrid - _mCx;
                    float dz = nd.ZCoordinateInGrid - _mCz;
                    float af = dx * _mF.x + dz * _mF.z;      // 船首向
                    float ar = dx * _mR.x + dz * _mR.z;      // 右舷向
                    if (af < fMin) fMin = af;
                    if (af > fMax) fMax = af;
                    if (ar < rMin) rMin = ar;
                    if (ar > rMax) rMax = ar;
                }
                if (n == 0) return;

                // ★去重键必须含格数★
                //   只用原点的话，舷炮和艏炮共用同一个 node（90° 都是 (259,260)/(259,261)），
                //   舷炮的 pattern 会被当成重复丢掉 —— 手里就永远只有艏炮的数据。
                //   格数能区分模板（艏炮 93/87、舷炮 80/44），所以放进键里。
                string key = _mBucket + "|" + _mNx + "," + _mNz + "|" + n;
                if (!_measured.Add(key)) return;

                float halfH = (_mH - 1) * 0.5f;   // 船首在 +halfH
                float halfW = (_mW - 1) * 0.5f;   // 右舷边在 +halfW

                // ★原点本身的船体坐标 —— 这才是能跨朝向直接比的量★
                //   同一门炮的炮口装在船体固定位置，所以这两个数**应当与朝向无关**。
                //   0°/45° 是玩家确认正确的基准档；其余档与基准的差，就是这门炮要补的量。
                //   覆盖范围会被地图边缘和船体自身裁剪，格数不稳定，不适合当判据；原点稳定。
                float odx = _mNx - _mCx, odz = _mNz - _mCz;
                float oF = odx * _mF.x + odz * _mF.z;
                float oR = odx * _mR.x + odz * _mR.z;

                // ★最终 pattern 原点也量一遍★
                //   applicationNode 只是入参；pattern 模板自带一份偏移，
                //   而那份偏移**每门炮、每个朝向档各不相同**（烘焙出来的）。
                //   实测：同一门 MacroPlasma，最终原点在 0° 是「船首向 1.5」、
                //   90° 变成 2.5 —— 而 applicationNode 两档已经一致了。
                //   所以真正还没对齐的是这一步。两个原点并排打出来，
                //   差在哪一步一眼可见，不用再靠推。
                string fin = "读不到";
                try
                {
                    var app = __result.ApplicationNode;
                    if (app != null)
                    {
                        float fdx = app.XCoordinateInGrid - _mCx, fdz = app.ZCoordinateInGrid - _mCz;
                        fin = "(" + app.XCoordinateInGrid + "," + app.ZCoordinateInGrid + ")"
                            + "　船首向 " + (fdx * _mF.x + fdz * _mF.z).ToString("F2")
                            + "　右舷向 " + (fdx * _mR.x + fdz * _mR.z).ToString("F2");
                    }
                }
                catch { }

                Main.Log("[射界·船体坐标] " + _mSlot + "　朝向档=" + (_mBucket * 45) + "°"
                       + "　原点=(" + _mNx + "," + _mNz + ")　共" + n + "格"
                       + "\n    入参原点(原版) 船首向 " + oF.ToString("F2")
                       + "　右舷向 " + oR.ToString("F2")
                       + "\n    ★最终pattern原点 " + fin
                       + "\n    （同一门炮各朝向这两行都应相同；0°/45° 为基准）"
                       + "\n    船首向 " + fMin.ToString("F2") + " ~ " + fMax.ToString("F2")
                       + "　（船首在 +" + halfH.ToString("F1") + "，最远端超出船首 "
                       + (fMax - halfH).ToString("F2") + " 格）"
                       + "\n    右舷向 " + rMin.ToString("F2") + " ~ " + rMax.ToString("F2")
                       + "　（舷边在 ±" + halfW.ToString("F1") + "，覆盖中心 "
                       + ((rMin + rMax) * 0.5f).ToString("F2") + "）"
                       + "\n    ★同一门炮各朝向这几个数应当相同；0°/45° 为基准档");
                Main.FlushLog(true);
            }
            catch { }
        }

        /// <summary>
        /// AoEPattern 的结构，只打一次。
        ///
        /// ★为了回答"射程能不能补一格"★
        ///   玩家要求起点后退一格，但**外缘不许跟着退** —— 净效果是覆盖变宽、够得一样远。
        ///   起点一退，锥形整体后移，最远端必然少一格，所以必须把半径加回来一格。
        ///   形状来自烘焙好的 pattern 模板（8 个朝向档预先算好），
        ///   能不能加取决于这个类型有没有暴露"半径/射程"这类可读写的量。
        ///   在没看清结构之前不许动手改 —— 直接写字段是共享蓝图状态，
        ///   会波及别的单位、还会带进存档和联机。
        /// </summary>
        private static bool _dumpedPattern;

        private static void DumpPatternType()
        {
            try
            {
                if (_dumpedPattern) return;
                var s = Main.Settings;
                if (s == null || !s.WatchMomentum) return;
                _dumpedPattern = true;

                var t = AccessTools.TypeByName("Kingmaker.UnitLogic.Abilities.Components.Patterns.AoEPattern");
                if (t == null) { Main.Log("[射界·模板] 找不到 AoEPattern 类型"); return; }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("========== AoEPattern 结构（查半径能不能加一格）==========");
                sb.AppendLine("类型 " + t.FullName + "　值类型=" + t.IsValueType);
                foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                             | BindingFlags.Instance | BindingFlags.Static))
                    sb.AppendLine("  字段 " + (fi.IsStatic ? "static " : "") + fi.Name + " : " + fi.FieldType.Name
                                + (fi.IsInitOnly ? "（只读）" : ""));
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic
                                                | BindingFlags.Instance | BindingFlags.Static))
                    sb.AppendLine("  属性 " + p.Name + " : " + p.PropertyType.Name + "　可写=" + p.CanWrite);
                sb.AppendLine("静态工厂（返回 AoEPattern 的方法 = 也许能直接要一个大一号的）：");
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.ReturnType != t) continue;
                    var ps = m.GetParameters();
                    var line = new System.Text.StringBuilder("  " + m.Name + "(");
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i > 0) line.Append(", ");
                        line.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name);
                    }
                    sb.AppendLine(line.Append(")").ToString());
                }
                Main.Log(sb.ToString());
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError("[射界·模板] 探测失败: " + e.Message); }
        }

        /// <summary>沿网格逐格走到目标偏移；碰到图边界返回 null（宁可不改，也别指向图外）。</summary>
        private static CustomGridNodeBase Walk(CustomGridNodeBase from, int dx, int dz)
        {
            var cur = from;
            for (int i = 0; i < Mathf.Abs(dx) && cur != null; i++)
                cur = cur.GetNeighbourAlongDirection(dx > 0 ? 1 : 3, false);
            for (int i = 0; i < Mathf.Abs(dz) && cur != null; i++)
                cur = cur.GetNeighbourAlongDirection(dz > 0 ? 2 : 0, false);
            return cur;
        }

        /// <summary>槽位名（Port/Starboard/Prow/Keel/Dorsal）；不是舰船武器返回 null。</summary>
        private static string SlotOf(Kingmaker.UnitLogic.Abilities.AbilityData ab, object ship)
        {
            try
            {
                if (ab == null) return null;
                var src = ab.SourceItem;
                if (src == null) return null;
                var t = src.GetType();
                object ws = null;
                foreach (var n in new string[] { "WeaponSlot", "m_WeaponSlot" })
                {
                    try { var p = AccessTools.Property(t, n); if (p != null) { ws = p.GetValue(src); if (ws != null) break; } } catch { }
                    try { var fi = AccessTools.Field(t, n); if (fi != null) { ws = fi.GetValue(src); if (ws != null) break; } } catch { }
                }
                if (ws == null) return null;
                foreach (var n in new string[] { "Type", "m_Type" })
                {
                    try { var p = AccessTools.Property(ws.GetType(), n); if (p != null) { var v = p.GetValue(ws); if (v != null) return v.ToString(); } } catch { }
                    try { var fi = AccessTools.Field(ws.GetType(), n); if (fi != null) { var v = fi.GetValue(ws); if (v != null) return v.ToString(); } } catch { }
                }
                return null;
            }
            catch { return null; }
        }
    }
}
