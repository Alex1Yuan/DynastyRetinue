using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.UI.DollRoom;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.View;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 换船模（真·换成巡洋舰/大巡洋舰外观，不是把护卫舰放大）。
    ///
    /// ── 反编译结论（本轮全部一手复核过行号）─────────────────────────────
    ///
    /// 1) API（Kingmaker.UnitLogic.Parts.PartUnitViewSettings）
    ///        public void SetCustomPrefabGuid(string guid)   // :63-66，唯一重载
    ///        { m_CustomPrefabGuid = guid; }                 // ★函数体就这一行★
    ///    **不重建 View、不发事件、不校验 guid**。所以单独调它当场看不到任何变化。
    ///    消费端是 PrefabGuid getter(:32-47)：Doll?.RacePreset != null 返回 null，
    ///    否则优先 m_CustomPrefabGuid，回落 Owner.Blueprint.Prefab.AssetId。
    ///
    /// 2) 当场生效必须自己重建 View。vanilla 自己有两处运行时换模，写法一字不差
    ///    （ChangeAppearance.cs:33-37、CopyAnotherView.cs:42-50）：
    ///        var newView = unit.CreateView();   // ★先建，再拆
    ///        var oldView = unit.View;
    ///        unit.DetachView();                 // Entity.cs:560-580 只解绑，**不销毁 GameObject**
    ///        oldView.DestroyViewObject();       // ★必须补这一刀，否则留孤儿
    ///        unit.AttachView(newView);
    ///
    /// 3) ★缩放：换模和改 Size 会互相打架，解法是 DisableSizeScaling★
    ///    UnitEntityView.GetSizeScale():778-796 算的是**相对值**：
    ///        if (EntityData == null || DisableSizeScaling) return 1f;      // :780
    ///        num2 = State.Size - OriginalSize;  每档 num /= 0.66f
    ///    Size 枚举 Raider_1x1=9 / Frigate_1x2=10 / Cruiser_2x4=11 / GrandCruiser_3x6=12，
    ///    而 OriginalSize = Blueprint.Size（BaseUnitEntity.cs:338-345），
    ///    玩家护卫舰蓝图恒为 Frigate_1x2 且**硬约束禁止新建蓝图 ⇒ 改不动**。
    ///    ⇒ 只要 State.Size 设成 Cruiser_2x4，巡洋舰模型会**再被放大 1.5152 倍**；
    ///       设成 GrandCruiser_3x6 是 **2.2957 倍**。这就是"换完模又变太大"的成因。
    ///    ⇒ 解法照抄 vanilla 变形术（PartUnitViewSettings.cs:81,92 / Polymorph.cs:300）：
    ///           newView.DisableSizeScaling = true;   // ★必须在 AttachView 之前★
    ///
    ///    为什么必须在 AttachView 之前：AttachView → View.AttachToData →
    ///    UnitEntityView.OnDidAttachToData:289 立刻执行
    ///        ViewTransform.localScale = m_OriginalScale * (m_Scale = GetSizeScale());
    ///    之后再设标志，只会让 OnDoLateUpdate:684-691 以 2f*deltaTime 的速率慢慢缩回去，
    ///    玩家看得见一次"先胀后缩"。
    ///
    ///    ★关键：视觉尺寸和格子占位是两条完全独立的路。★
    ///    格子走 SizePathfindingHelper.GetRectForSize（Cruiser_2x4 => IntRect(0,0,1,3)，:28），
    ///    只认 Size，不认 view scale。所以正确姿势是：
    ///        State.Size = 目标档  （吃占位/机制/多打判据）
    ///      + 换目标档的 prefab   （吃外观）
    ///      + DisableSizeScaling  （模型保持 prefab 原生尺寸，不叠第二次放大）
    ///
    /// 4) 存档：m_CustomPrefabGuid 带 [JsonProperty]（:24-26），**会写进存档**。
    ///    真存档核实（savebackup_20260815 全目录）：非 null 实例共 40982 个，
    ///    形如 "m_CustomPrefabGuid":"af29e74b297a9d34c89177e7bd38b82c"，
    ///    **全部是裸 JSON string，被 $type 包裹的数量 = 0**。
    ///    ⇒ 不走 BlueprintConverter、不是类型化标量引用 ⇒ **不碰存档红线**。
    ///    ⇒ 填 vanilla AssetId ⇒ 卸载 mod 后照样反序列化、照样解析得到资源，
    ///      存档正常打开，但船**保持**新外观（单向，不自动还原）。
    ///
    /// 5) ★冷启动缩放 bug（本轮修掉的真 bug）★
    ///    m_CustomPrefabGuid 和 m_Size 都进存档，但 DisableSizeScaling 是 view 上的
    ///    运行时 bool、**不持久化**。读档时 SceneLoader 自己走 AttachToViewOnLoad(null)
    ///    → CreateView → Instantiate，那条路不经过我们。
    ///    如果补缩放的 postfix 用"会话内静态变量"当闸门，冷启动时该变量必为 null
    ///    ⇒ **每次重开游戏读档，船都会被放大 1.5152 倍**。
    ///    所以 Instantiate_Postfix 的判据必须**只读实体自身状态**
    ///    （比对 PrefabGuid 与 Blueprint.Prefab.AssetId），且钩子在 mod 载入时就装。
    /// ────────────────────────────────────────────────────────────────────
    /// </summary>
    public static class StarshipViewTool
    {
        /// <summary>Harmony 钩子是否已装。Install() 幂等。</summary>
        private static bool s_Hooked;

        public static StarshipEntity PlayerShip
        {
            get
            {
                try
                {
                    return Game.Instance != null && Game.Instance.Player != null
                         ? Game.Instance.Player.PlayerShip : null;
                }
                catch { return null; }
            }
        }

        /// <summary>当前生效的自定义 prefab（null = 用蓝图默认）。读实体，不读会话变量。</summary>
        public static string CurrentPrefab
        {
            get
            {
                try
                {
                    var s = PlayerShip;
                    if (s == null) return null;
                    string cur = s.ViewSettings != null ? s.ViewSettings.PrefabGuid : null;
                    string def = BlueprintPrefabOf(s);
                    return (cur == def) ? null : cur;
                }
                catch { return null; }
            }
        }

        private static string BlueprintPrefabOf(BaseUnitEntity u)
        {
            try { return u != null && u.Blueprint != null && u.Blueprint.Prefab != null ? u.Blueprint.Prefab.AssetId : null; }
            catch { return null; }
        }

        // ================================================================
        // 主入口
        // ================================================================

        /// <summary>
        /// 一步到位：换外观 + 同步把 State.Size 设成该 prefab 的原生档位。
        /// 这是推荐用法 —— 模型尺度和格子占位天然自洽。
        /// </summary>
        /// <summary>
        /// 把某个船模用在**指定档位**上（可以跟它的原生档位不同）。
        /// 用途：Gothic 原生是巡洋舰，用它当大巡 —— 分档设 GrandCruiser_3x6、外观还是 Gothic，
        /// 差的那一档由 GetSizeScale_Postfix 等比放大补上（×1.5152）。
        /// </summary>
        public static bool ApplyModelAtTier(ShipModel model, Size tier)
        {
            if (model == null) { Main.LogError("[船模] ApplyModelAtTier: model 为 null"); return false; }
            Main.Log("[船模] 应用 " + model.Hull + " 到 " + tier + "  (prefab=" + model.PrefabAssetId + ")");
            if (StarshipTool.CurrentSize() != tier)
                if (!StarshipTool.SetSize(tier)) return false;
            return Apply(model.PrefabAssetId);
        }

        /// <summary>切到某个档位并用该档位的默认船模。护卫舰档回原版模型。</summary>
        public static bool ApplyTierDefault(Size tier)
        {
            Main.Log("[船模] 点击「切到 " + tier + "」  当前分档=" + StarshipTool.CurrentSize()
                     + "  当前 prefab=" + (string.IsNullOrEmpty(CurrentPrefab) ? "(原版)" : CurrentPrefab));
            var m = ShipModelCatalog.DefaultFor(tier);
            if (m == null)
            {
                if (StarshipTool.CurrentSize() != tier && !StarshipTool.SetSize(tier)) return false;
                return Clear();     // 护卫舰：还原原版外观
            }
            return ApplyModelAtTier(m, tier);
        }

        public static bool ApplyModel(ShipModel model)
        {
            if (model == null) { Main.LogError("[船模] model 为空"); return false; }
            // 先改 Size（可能因"战斗中"被拒），再换模；顺序反了会出现"新模型 + 旧档位"的中间态。
            if (StarshipTool.CurrentSize() != model.Tier)
            {
                if (!StarshipTool.SetSize(model.Tier))
                {
                    Main.LogError("[船模] 分档切换被拒（多半是战斗中），本次不换模，保持原样。");
                    return false;
                }
            }
            return Apply(model.PrefabAssetId);
        }

        /// <summary>
        /// 只换外观，不动 Size。prefabAssetId 必须在 ShipModelCatalog 白名单里。
        /// 传 null 还原成蓝图默认模型。拿不到资源就**什么都不改**，保持原模型。
        /// </summary>
        public static bool Apply(string prefabAssetId)
        {
            var ship = PlayerShip;
            if (ship == null) { Main.LogError("[船模] 拿不到玩家座舰（不在游戏内？）"); return false; }

            Install();   // 幂等；确保读档后补缩放的钩子在位

            // ── 还原 ──────────────────────────────────────────────────────
            if (string.IsNullOrEmpty(prefabAssetId))
            {
                try { ship.ViewSettings.SetCustomPrefabGuid(null); }
                catch (Exception e) { Main.LogError("[船模] 还原失败: " + e.Message); return false; }

                ShipModelBundleHold.RearmVanillaHold(ship);
                ShipModelBundleHold.Cleanup();
                Rebuild(ship, disableSizeScaling: false);
                Main.Log("[船模] 已还原为蓝图默认模型。");
                return true;
            }

            // ── 白名单：只准用核实过的 vanilla AssetId ────────────────────
            // 这不只是洁癖：WhyUnusable 的探针在"bundle 加载成功但资产类型不对"时
            // 会让 extra 的 RequestCount 一增两减（ResourcesLibrary.LoadResource:456-500
            // 的失败分支已经 Unload 过一次，条目却仍留在 s_LoadedResources 里，
            // 下一轮 CleanupLoadedCache 会再 Unload 一次）。白名单把这条路彻底堵死。
            var known = ShipModelCatalog.ByPrefab(prefabAssetId);
            if (known == null)
            {
                Main.LogError("[船模] 拒绝未登记的 prefab: " + prefabAssetId
                            + " —— 只接受 ShipModelCatalog 里核实过的 vanilla AssetId。");
                return false;
            }

            // ── 预检 + hold（优雅降级的关键：探不到就原样不动）────────────
            if (!ShipModelBundleHold.TrySetShipModel(ship, prefabAssetId))
                return false;   // TrySetShipModel 内部已打日志、已保证不写坏字段

            // ── 当场生效 ──────────────────────────────────────────────────
            if (!Rebuild(ship, disableSizeScaling: true))
            {
                // 重建失败：把字段回滚，别留"存档里指着新模型、屏幕上还是旧模型"的半成品
                try { ship.ViewSettings.SetCustomPrefabGuid(null); } catch { }
                ShipModelBundleHold.Cleanup();
                ShipModelBundleHold.RearmVanillaHold(ship);
                return false;
            }

            Main.Log("[船模] 已换成 " + known.Hull + "（" + prefabAssetId + "），缩放已锁定。"
                   + "  ★m_CustomPrefabGuid 会写进存档（裸 string + vanilla guid，"
                   + "卸载 mod 后存档正常打开，但外观保持不还原；要还原请先点【还原原版船模】再存盘）★");
            return true;
        }

        /// <summary>还原成蓝图默认模型（不动 Size）。</summary>
        public static bool Clear() { return Apply(null); }

        /// <summary>
        /// 完全复位：把 Size 设回蓝图原生档 + 还原外观。卸载 mod 前请点这个。
        ///
        /// ★顺序：先 Size 再外观★ 和 ApplyModel 一致，理由也一样但方向相反：
        /// Clear() 内部会 Rebuild view，而 vanilla 在建 view 时按**当时的 Size**
        /// 算缩放（GetSizeScale：每高一档 /0.66）。先 Clear 的话，重建发生在 Size
        /// 还是大巡的时候 ⇒ 新 view 带着 1.515 的缩放出生，随后 SetSize 只改数值、
        /// **不会去重新缩放已经建好的 view**。
        ///
        /// 实测（v0.45.0 日志）：还原之后
        ///     原始 (1.515,1.515,1.515)　分档高于护卫舰 0 档 → ×1.000
        /// 分档读对了、缩放没跟上 ⇒ 改装界面里护卫舰被撑成 1.515 倍，
        /// 表现为"变回原船之后镜头很近"。玩家实测报的就是这个。
        ///
        /// SetSize 可能被拒（战斗中）。被拒就整个放弃 —— 只清 prefab 不改 Size
        /// 会留下"原版外观 + 大巡档位"的中间态，比什么都不做更糟。
        /// </summary>
        public static bool RevertAll()
        {
            try
            {
                var s = PlayerShip;
                if (s != null && StarshipTool.CurrentSize() != s.OriginalSize)
                {
                    if (!StarshipTool.SetSize(s.OriginalSize))
                    {
                        Main.LogError("[船模] 分档复位被拒（多半在战斗中），本次不还原，保持原样。");
                        return false;
                    }
                }
            }
            catch (Exception e) { Main.LogError("[船模] 分档复位失败: " + e.Message); return false; }

            return Clear();   // 此时 Size 已是原生档，重建出来的 view 缩放自然是 1
        }

        // ================================================================
        // View 重建（严格照 vanilla ChangeAppearance.cs:33-37 的顺序）
        // ================================================================
        private static bool Rebuild(StarshipEntity ship, bool disableSizeScaling)
        {
            UnitEntityView oldView = null;
            try { oldView = ship.View; } catch { }

            UnitEntityView newView;
            try { newView = ship.CreateView(); }          // ★先建后拆
            catch (Exception e) { Main.LogError("[船模] CreateView 抛异常，保留旧模型: " + e); return false; }

            if (newView == null)
            {
                Main.LogError("[船模] CreateView 返回 null，保留旧模型不动。"
                            + "（若强行继续，Entity.AttachToViewOnLoad:393-397 会把 IsInGame 置 false，整条船下线。）");
                return false;
            }

            // ★必须在 AttachView 之前★ —— 见类注释 3)
            try { newView.DisableSizeScaling = disableSizeScaling; } catch { }

            try
            {
                if (oldView != null)
                {
                    ship.DetachView();
                    oldView.DestroyViewObject();   // DetachView 只解绑不销毁，孤儿要自己清
                }
                ship.AttachView(newView);
            }
            catch (Exception e) { Main.LogError("[船模] 重建 View 失败: " + e); return false; }
            return true;
        }

        // ================================================================
        // Harmony：读档/换区域后自动补缩放锁定
        // ================================================================

        /// <summary>
        /// 在 mod 载入时调用（Main.Load 里）。**不要**懒装在 Apply 里 ——
        /// m_CustomPrefabGuid 进存档，冷启动读档时根本不会走 Apply，
        /// 懒装 = 每次重开游戏船都被放大 1.5152 倍。
        /// </summary>
        public static void Install()
        {
            if (s_Hooked) return;
            try
            {
                var m = AccessTools.Method(typeof(PartUnitViewSettings), nameof(PartUnitViewSettings.Instantiate));
                if (m == null) { Main.LogError("[船模] 找不到 PartUnitViewSettings.Instantiate，缩放锁定钩子未装。"); return; }

                Main.HarmonyInstance.Patch(
                    original: m,
                    postfix: new HarmonyMethod(typeof(StarshipViewTool), nameof(Instantiate_Postfix)));
                // 等比放大钩子：让低档位船模撑满高档位（比如 Gothic 巡洋舰当大巡用）
                try
                {
                    var gs = AccessTools.Method(typeof(UnitEntityView), "GetSizeScale");
                    if (gs != null)
                        Main.HarmonyInstance.Patch(
                            original: gs,
                            postfix: new HarmonyMethod(typeof(StarshipViewTool), nameof(GetSizeScale_Postfix)));
                    else Main.LogError("[船模] 找不到 UnitEntityView.GetSizeScale，等比放大不可用。");
                }
                catch (Exception e2) { Main.LogError("[船模] 装等比放大钩子失败: " + e2.Message); }

                s_Hooked = true;
                Main.Log("[船模] 缩放锁定钩子已装（读档/换区域后自动补 DisableSizeScaling）。");
            }
            catch (Exception e) { Main.LogError("[船模] 装钩子失败（读档后船可能被放大，需手动重设一次）: " + e); }
        }

        /// <summary>
        /// 等比放大：让低档位的船模撑满高档位。
        ///
        /// 为什么需要：GrandCruiser_3x6 全游戏**只有 2 个船模**（混沌战列巡洋舰、
        /// 帝国质量运输舰），帝国没有战舰造型。想要"帝国 Gothic 级的大巡"只能把
        /// 巡洋舰船模等比放大。
        ///
        /// 倍率怎么算：vanilla 的 GetSizeScale 是**每差一档乘 1/0.66 ≈ 1.5152**，
        /// 但它算的是"相对**蓝图**原始尺寸"（玩家舰蓝图恒为 Frigate_1x2，且硬约束
        /// 禁止新建蓝图所以改不动）。直接放行的话 Gothic 会被按"相对护卫舰两档"
        /// 放大 ×2.2957 —— 而它本身已经是巡洋舰尺度，结果过大。
        /// 所以这里自己算：(1/0.66)^(当前档位 − 该船模的原生档位)。
        /// Gothic(原生 Cruiser_2x4) 放到 GrandCruiser_3x6 = 差一档 = ×1.5152，正好。
        ///
        /// 实现上保留 DisableSizeScaling = true 让 vanilla 的公式提前 return 1f，
        /// 我们在 Postfix 里覆盖返回值 —— 这样 OnDoLateUpdate 的 lerp 目标跟着变，
        /// 缩放是平滑过渡而不是瞬间跳变。
        /// </summary>
        private static void GetSizeScale_Postfix(UnitEntityView __instance, ref float __result)
        {
            try
            {
                if (__instance == null) return;
                if (Main.Settings == null || !Main.Settings.ShipStretchModel) return;

                var ship = __instance.EntityData as StarshipEntity;
                if (ship == null) return;
                var player = PlayerShip;
                if (player == null || !ReferenceEquals(ship, player)) return;

                string prefab = CurrentPrefab;
                if (string.IsNullOrEmpty(prefab)) return;
                var model = ShipModelCatalog.ByPrefab(prefab);
                if (model == null) return;

                // ★ 一律接管返回值，包括 diff == 0 ★
                // 早先这里 diff<=0 就 return，把结果留给 vanilla —— 那是个漏洞：
                // vanilla 算的是"相对**蓝图**原始尺寸"，而玩家舰蓝图恒为 Frigate_1x2，
                // 所以在巡洋舰档它会给 ×1.5152。但 Gothic 的 prefab 本身**已经是巡洋舰尺度**，
                // 再乘一次就大了一圈（实测就是这个症状）。
                // 这条路径只有"换过模的玩家舰"会走，接管它不影响任何别的单位。
                int diff = (int)ship.Size - (int)model.Tier;
                if (diff < 0) diff = 0;          // 船模比当前档位还大：保持原生尺寸，不缩小

                float f = 1f;
                for (int i = 0; i < diff; i++) f /= 0.66f;
                __result = f;

                if (!s_StretchLogged)
                {
                    s_StretchLogged = true;
                    Main.Log("[船模] 缩放接管：" + model.Hull + "（原生 " + model.Tier + "）"
                             + " -> 分档 " + ship.Size + "  实际倍率 ×" + f.ToString("0.###")
                             + (diff == 0 ? "（同档，保持 prefab 原生尺寸）" : "（差 " + diff + " 档，等比放大）")
                             + "。本次会话只报这一条。");
                }
            }
            catch { /* 每帧都会调，出错也不能刷屏 */ }
        }

        private static bool s_StretchLogged;

        /// <summary>
        /// 判据完全自洽：只读实体自身状态，不依赖任何会话内静态变量。
        /// "PrefabGuid != Blueprint.Prefab.AssetId" ⇒ 这条船换过模 ⇒ 一律锁缩放。
        /// </summary>
        private static void Instantiate_Postfix(PartUnitViewSettings __instance, UnitEntityView __result)
        {
            try
            {
                if (__result == null || __instance == null) return;
                var ship = __instance.Owner as StarshipEntity;
                if (ship == null) return;

                string actual = __instance.PrefabGuid;
                if (string.IsNullOrEmpty(actual)) return;

                string bpDefault = BlueprintPrefabOf(ship);
                if (string.Equals(actual, bpDefault, StringComparison.Ordinal)) return;

                __result.DisableSizeScaling = true;
            }
            catch { /* 视觉细节，绝不因此打断 spawn */ }
        }
    }

    // ====================================================================
    // 改装界面兜底
    //
    // ShipDollRoom.CreateSimpleAvatar():84-96 有一行完全无保护的解引用：
    //     GameObject original = view.GetComponentInChildren<StarshipView>().BaseRenderer.gameObject;
    // 而 StarshipView.BaseRenderer（StarshipView.cs:32，public Renderer 字段）
    // **全反编译树里唯一的消费点就是这一行** —— 强烈暗示它只为玩家三条船的
    // 改装界面接过线，NPC 巡洋舰 prefab 上很可能是 null ⇒ 换模后一开改装界面就 NRE。
    //
    // ★这条是推测，不是已确认★：prefab 是二进制 bundle，静态查不到字段有没有赋值。
    // 但成本只有几十行，且失败模式很重（整个改装界面打不开），所以先装上。
    //
    // 缩放这条不用管：我们已经 DisableSizeScaling=true，
    // 所以 view.transform.localScale == m_OriginalScale，
    // :92 的 m_SimpleAvatar.localScale = view.transform.localScale 拿到的就是原始比例。
    // ====================================================================
    [HarmonyPatch]
    internal static class ShipDollRoomGuard
    {
        /// <summary>
        /// 打在 CreateSimpleAvatar 本身而不是 SetupShip：
        /// CreateSimpleAvatar 内部会在 ship.View == null 时自己 CreateView，
        /// 打在外层 + "View 为 null 就 return" 会恰好在唯一需要它的场景里提前退出。
        /// </summary>
        [HarmonyPatch(typeof(ShipDollRoom), "CreateSimpleAvatar")]
        [HarmonyPrefix]
        private static void HealBaseRenderer(BaseUnitEntity ship)
        {
            try
            {
                if (ship == null) return;
                var view = ship.View;
                if (view == null) view = ship.CreateView();   // 与 vanilla 同一条路，让它拿到同一个 view
                if (view == null) return;

                var sv = view.GetComponentInChildren<StarshipView>();
                if (sv == null)
                {
                    Main.LogError("[船模] 当前 prefab 上没有 StarshipView 组件 —— 这个 prefab 不适合当座舰，"
                                + "改装界面 3D 预览会缺失。请换一个 ShipModelCatalog 里的船模。");
                    return;
                }
                if (sv.BaseRenderer != null) return;

                Renderer fallback = sv.GetComponent<Renderer>();
                if (fallback == null) fallback = sv.GetComponentInChildren<Renderer>();
                if (fallback == null)
                {
                    Main.LogError("[船模] prefab 上找不到任何 Renderer，改装界面预览将为空。");
                    return;
                }
                sv.BaseRenderer = fallback;
                Main.Log("[船模] 已为换装后的船体补上 BaseRenderer（原版只给玩家三船接了线）。");
            }
            catch { }
        }

        /// <summary>最后一道：万一还是抛了，吞掉，让改装界面能打开（只是没有 3D 预览）。</summary>
        [HarmonyPatch(typeof(ShipDollRoom), "CreateSimpleAvatar")]
        [HarmonyFinalizer]
        private static Exception SwallowAvatarError(Exception __exception)
        {
            if (__exception != null)
                Main.LogError("[船模] ShipDollRoom.CreateSimpleAvatar 抛异常，已吞掉以免整个改装界面挂掉: "
                            + __exception.Message);
            return null;
        }
    }
}
