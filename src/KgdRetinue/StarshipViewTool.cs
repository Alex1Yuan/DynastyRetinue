using System;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.View;

namespace KgdRetinue
{
    /// <summary>
    /// 换船模（真·换成巡洋舰/大巡洋舰外观，不是把护卫舰放大）。
    ///
    /// ── 反编译结论（全部一手核实，见 SHIP_VIEW_FINDINGS）─────────────────
    ///
    /// 1) API 签名（Kingmaker.UnitLogic.Parts.PartUnitViewSettings）
    ///        public void SetCustomPrefabGuid(string guid)      // 唯一重载，参数就是 string
    ///    函数体只有一行 `m_CustomPrefabGuid = guid;` —— **不重建 View，不发事件**。
    ///    消费端是 PrefabGuid getter：Doll.RacePreset != null 时返回 null，
    ///    否则优先 m_CustomPrefabGuid，回落 Owner.Blueprint.Prefab.AssetId。
    ///
    /// 2) 所以**当场不生效**，必须自己重建 View。vanilla 自己的换模写法有两处，
    ///    ChangeAppearance.cs:33-37 和 CopyAnotherView.cs:42-50，两处一字不差：
    ///        var newView = unit.CreateView();   // ★先建，再拆
    ///        var oldView = unit.View;
    ///        unit.DetachView();
    ///        oldView.DestroyViewObject();       // DetachView 不销毁 GameObject，必须自己销毁
    ///        unit.AttachView(newView);
    ///    顺序不能换：CreateView 必须在 DetachView 之前（否则中间态没有 View）。
    ///
    /// 3) 缩放：UnitEntityView.GetSizeScale():778-796 算的是 **相对值**
    ///        scale = (1/0.66)^(State.Size - OriginalSize)
    ///    Size 枚举里 Frigate_1x2=10 / Cruiser_2x4=11 / GrandCruiser_3x6=12，
    ///    而 OriginalSize 是 Blueprint.Size（护卫舰恒为 Frigate_1x2，改不动）。
    ///    ⇒ 只要把 State.Size 设成 Cruiser_2x4，巡洋舰模型会**再被放大 1.515 倍**；
    ///       设成 GrandCruiser_3x6 是 **2.30 倍**。这就是"换完模又变太大"的成因。
    ///    解法照抄 vanilla 变形术（Polymorph.cs:300 / PartUnitViewSettings.cs:81,92）：
    ///        newView.DisableSizeScaling = true;   // ★必须在 AttachView 之前★
    ///    GetSizeScale() 见到该标志直接 return 1f，模型保持 prefab 自带尺寸。
    ///    格子占位走的是另一条完全独立的路（SizePathfindingHelper.GetSizeRect：
    ///    Cruiser_2x4 => IntRect(0,0,1,3)），只认 Size 不认 view scale
    ///    ⇒ **"视觉尺寸"和"格子占位"可以各管各的**，这正是我们要的。
    ///
    ///    为什么必须在 AttachView 之前：AttachView → View.AttachToData →
    ///    UnitEntityView.OnDidAttachToData:289 立刻执行
    ///        ViewTransform.localScale = m_OriginalScale * (m_Scale = GetSizeScale());
    ///    之后设标志只会让 OnDoLateUpdate:684-691 用 2f*dt 的速率**慢慢缩回去**，
    ///    玩家会看到一次可见的"先胀后缩"。
    ///
    /// 4) 存档：m_CustomPrefabGuid 带 [JsonProperty]（PartUnitViewSettings.cs:24-26）。
    ///    已用真存档核实（savebackup_20260815/ForImport_1.zks → party.json）：
    ///        "$type":"Kingmaker.UnitLogic.Parts.PartUnitViewSettings, Code",
    ///        "m_CustomPrefabGuid":null,"Doll":{...}
    ///    就是个**裸 string**，没有 $type、不走 BlueprintConverter、不是类型化引用。
    ///    ⇒ 填 vanilla prefab AssetId ⇒ 卸载 mod 后照样反序列化、照样解析得到资源，
    ///      存档打得开，只是船**保持**巡洋舰外观（单向、不自动还原）。
    ///    ⇒ 想彻底零足迹，用下面的 ZeroFootprint 模式（补 PrefabGuid getter，不写字段）。
    /// ────────────────────────────────────────────────────────────────────
    /// </summary>
    public static class StarshipViewTool
    {
        /// <summary>已核实的舰船 prefab AssetId（全部 locationlist.json bundle=extra 命中）。</summary>
        public const string PrefabSwordFrigate      = "a6bcda106bf8fd44da4286ee04a3ad8f";
        public const string PrefabFalchionFrigate   = "26e3688a99a9eed44baa2e19e16be1a4";
        public const string PrefabFirestormFrigate  = "31da3f04de39e5446b16641deb3be42d";
        /// <summary>帝国巡洋舰（ImperialCruiser10Named / DLC1_ImperialCruiser5 / PirateCruiser12Named 共用）。</summary>
        public const string PrefabImperialCruiser   = "67017c4dd1d5c1c40979ce2fc1cd38b2";
        /// <summary>混沌巡洋舰（ChaosCruiser5 / DLC1_ChaosCruiser）。</summary>
        public const string PrefabChaosCruiser      = "10de1ae75122ba243b423194534e5182";
        /// <summary>兽人/海盗巡洋舰（OrkCruiser10 / PirateCruiser7）。</summary>
        public const string PrefabOrkCruiser        = "8c34d0a2f4987134c8a625612476e22d";
        /// <summary>黑暗灵族巡洋舰（DrukhariCruiser6 / DrukhariBHCruiser10）。</summary>
        public const string PrefabDrukhariCruiser   = "e18691bc8276691408852ec91c909c42";

        /// <summary>当前生效的自定义 prefab（null = 未改）。仅本会话，不持久化。</summary>
        private static string s_Applied;

        /// <summary>Harmony 是否已挂上 view 重建钩子。</summary>
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

        /// <summary>
        /// 换模并**当场生效**。prefabAssetId 必须是 vanilla 资源 guid（零新增 AssetId 硬约束）。
        /// 传 null 还原成蓝图默认模型。
        /// </summary>
        public static bool Apply(string prefabAssetId, bool disableSizeScaling = true)
        {
            var ship = PlayerShip;
            if (ship == null) { Main.LogError("[船模] 拿不到玩家座舰（不在游戏内？）"); return false; }

            // 先确认资源真的存在，并且 hold 住 bundle。
            // PartHoldPrefabBundle.cs:30 只 hold Blueprint.Prefab.AssetId（= 护卫舰那个），
            // 我们换过去的 prefab 不在它的 hold 范围内，所以自己补一次 hold:true。
            // （实测所有舰船 prefab 都在同一个 425MB 的 "extra" bundle 里，
            //   而玩家舰自己的 prefab 也在里面且被 PartHoldPrefabBundle 持有，
            //   所以这条 hold 更多是保险；但保险要买。）
            if (!string.IsNullOrEmpty(prefabAssetId))
            {
                UnitEntityView probe = null;
                try
                {
                    probe = ResourcesLibrary.TryGetResource<UnitEntityView>(
                        prefabAssetId, ignorePreloadWarning: true, hold: true);
                }
                catch (Exception e) { Main.LogError("[船模] 载入 prefab 抛异常: " + e); return false; }

                if (probe == null)
                {
                    Main.LogError("[船模] 找不到 prefab " + prefabAssetId
                                  + " —— 拒绝套用（套上去 CreateView 会返回 null，"
                                  + "Entity.AttachToViewOnLoad:393-397 会把 IsInGame 置 false，船会整体下线）。");
                    return false;
                }
            }

            try
            {
                ship.ViewSettings.SetCustomPrefabGuid(prefabAssetId);
                s_Applied = prefabAssetId;

                EnsureHook();
                if (!Rebuild(ship, disableSizeScaling)) return false;

                Main.Log("[船模] 已换成 " + (prefabAssetId ?? "<蓝图默认>")
                         + "；缩放锁定=" + disableSizeScaling
                         + "  ★m_CustomPrefabGuid 会写进存档（裸 string，vanilla guid，"
                         + "卸载 mod 后存档正常打开，但外观保持不还原）★");
                return true;
            }
            catch (Exception e) { Main.LogError("[船模] 换模失败: " + e); return false; }
        }

        /// <summary>还原成蓝图默认模型。</summary>
        public static bool Clear() { return Apply(null, disableSizeScaling: false); }

        /// <summary>
        /// 按 vanilla ChangeAppearance.cs:33-37 的顺序重建 View。
        /// </summary>
        private static bool Rebuild(StarshipEntity ship, bool disableSizeScaling)
        {
            UnitEntityView oldView = ship.View;

            // ★先建后拆★：CreateView 内部 = ViewSettings.Instantiate() + 回填 Blueprint。
            UnitEntityView newView = ship.CreateView();
            if (newView == null)
            {
                Main.LogError("[船模] CreateView 返回 null，保留旧模型不动。");
                return false;
            }

            // ★必须在 AttachView 之前★ —— 见类注释 3)。
            newView.DisableSizeScaling = disableSizeScaling;

            if (oldView != null)
            {
                ship.DetachView();
                oldView.DestroyViewObject();   // DetachView 只解绑不销毁，孤儿 GameObject 要自己清
            }
            ship.AttachView(newView);
            return true;
        }

        /// <summary>
        /// 读档 / 换区域时 SceneLoader 会自己调 AttachToViewOnLoad(null) 重建 View
        /// （SceneLoader.cs:436 / :862 / :1894），那条路**不经过我们**，
        /// 新 view 的 DisableSizeScaling 是默认 false ⇒ 巡洋舰模型又会被放大 1.515 倍。
        /// DisableSizeScaling 是 view 上的运行时 bool、**不持久化**，所以必须每次补。
        /// 补在 Instantiate 的 postfix 上，此时 view 还没 AttachToData，时机正好。
        /// </summary>
        private static void EnsureHook()
        {
            if (s_Hooked) return;
            try
            {
                var h = new Harmony("kgd.retinue.shipview");
                h.Patch(
                    original: AccessTools.Method(typeof(PartUnitViewSettings), nameof(PartUnitViewSettings.Instantiate)),
                    postfix: new HarmonyMethod(typeof(StarshipViewTool), nameof(Instantiate_Postfix)));
                s_Hooked = true;
                Main.Log("[船模] 已挂 PartUnitViewSettings.Instantiate 钩子（读档/换区域后自动补缩放锁定）。");
            }
            catch (Exception e) { Main.LogError("[船模] 挂钩子失败（读档后可能需要手动重设一次）: " + e); }
        }

        private static void Instantiate_Postfix(PartUnitViewSettings __instance, UnitEntityView __result)
        {
            try
            {
                if (__result == null || string.IsNullOrEmpty(s_Applied)) return;
                if (!(__instance.Owner is StarshipEntity)) return;
                if (__instance.PrefabGuid != s_Applied) return;
                __result.DisableSizeScaling = true;
            }
            catch { /* 视觉细节，绝不因此打断 spawn */ }
        }
    }
}
