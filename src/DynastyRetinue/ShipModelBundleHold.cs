using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.BundlesLoading;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.ResourceManagement;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.View;

namespace DynastyRetinue
{
    /// <summary>
    /// 换船模时的 bundle 保活。
    ///
    /// ── 为什么需要 ────────────────────────────────────────────────────────────
    /// PartHoldPrefabBundle.TryRequestHandle() 只 hold **Blueprint.Prefab.AssetId**：
    ///     m_Handle = BundledResourceHandle&lt;UnitEntityView&gt;.Request(
    ///                    baseUnitEntity.Blueprint.Prefab.AssetId, hold: true);
    /// 而 CreateView 走的是 PartUnitViewSettings.PrefabGuid（优先 m_CustomPrefabGuid）。
    /// 一旦我们 SetCustomPrefabGuid，vanilla 就在 hold 一个**没人用**的 prefab，
    /// 真正在用的那个 HandleCounter=0。
    ///
    /// ResourcesLibrary.CleanupLoadedCache(UnloadAndCleanRequests)：
    ///     RequestCounter &lt;= 0            -&gt; Unload()（= UnloadBundleForAsset）
    ///     HandleCounter &lt;= 0            -&gt; RequestCounter 强制归 0
    /// Game.LoadAreaStage2 每次切区域调**两次** UnloadUnusedAssetsCoroutine，
    /// 所以 HandleCounter=0 的资源在一次区域切换内就会被丢掉。
    /// BundlesLoadService.UnloadBundle 用的是 Bundle.Unload(unloadAllLoadedObjects: **true**)。
    ///
    /// ── 本作实测的缓刑 ────────────────────────────────────────────────────────
    /// 全部舰船 prefab 都在同一个 bundle "extra" 里（见 Bundles/locationlist.json），
    /// 而 vanilla 正好 hold 着玩家护卫舰的 prefab（也在 extra）
    /// ⇒ extra 的 BundleData.RequestCount 不会归零 ⇒ 船→船换模当前**不会**真的炸。
    /// 但这是巧合，不是契约：目标 prefab 只要落在别的 bundle（*.unit / DLC）立刻成立。
    /// 保活成本 10 行，别赌。
    ///
    /// ── 释放纪律（UnitPortraits 踩过的坑）────────────────────────────────────
    /// 1. 单槽：全局最多持有一个 handle，结构上不可能计数漂移。
    /// 2. **先取后放**：UnloadBundle 是 RequestCount-- 后立刻判 &lt;=0 就 Unload(true)，
    ///    先 Dispose 旧的再 Request 新的，会在两句之间把 extra 打到 0 → 当场全灭。
    /// 3. Main.OnToggle(false) 必须调 Cleanup()。
    /// 4. 回主菜单后 CleanupLoadedCache(UnloadEverything) 会清空全表，
    ///    此时旧 handle 已失去意义，用 Forget() 丢弃而**不要** Dispose（否则计数变负）。
    /// </summary>
    public static class ShipModelBundleHold
    {
        // ── mod 自己的单槽 hold ──────────────────────────────────────────────
        private static BundledResourceHandle<UnitEntityView> _slot;
        private static string _slotGuid;

        public static string HeldGuid { get { return _slotGuid; } }

        /// <summary>拿到目标 prefab 的常驻 hold。先取后放。失败返回 false 且不改变现状。</summary>
        private static bool AcquireSlot(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            if (guid == _slotGuid && _slot != null) return true;

            BundledResourceHandle<UnitEntityView> fresh;
            try { fresh = BundledResourceHandle<UnitEntityView>.Request(guid, true); }
            catch (Exception e) { Main.LogError("[船模] Request 抛异常 " + guid + ": " + e.Message); return false; }

            if (fresh == null || fresh.Object == null)
            {
                // 加载失败（缺 DLC / guid 不是 bundle 资源）。
                // 注意：TryGetResource 即使加载失败也已经登记了 LoadedResource 并 ++ 两个计数，
                // 所以这里必须 Dispose 还回去，否则漏一次 hold。
                try { if (fresh != null) fresh.Dispose(); } catch { }
                return false;
            }

            var old = _slot;                 // ★ 先取后放 ★
            _slot = fresh; _slotGuid = guid;
            try { if (old != null) old.Dispose(); } catch { }
            return true;
        }

        /// <summary>把 hold 还回去。mod 禁用、还原原版船模时调用。</summary>
        public static void Cleanup()
        {
            var h = _slot; _slot = null; _slotGuid = null;
            if (h == null) return;
            try { h.Dispose(); Main.Log("[船模] 已释放 prefab bundle hold"); }
            catch (Exception e) { Main.LogError("[船模] 释放 hold 失败: " + e.Message); }
        }

        /// <summary>回主菜单/换存档后调用：全表已被 UnloadEverything 清空，只丢引用，不 Dispose。</summary>
        public static void Forget() { _slot = null; _slotGuid = null; }

        // ── 可用性预检（DLC 优雅降级）────────────────────────────────────────
        /// <summary>
        /// 每个 guid 只真探一次，结果缓存。
        /// ★为什么必须缓存★：ResourcesLibrary.LoadResource:456-500 的失败路径不对称 ——
        /// 当 bundle 加载成功、但资产不是 UnitEntityView 时，`loaded.AssetId` 已被赋值、
        /// `loaded.Unload()` 已经让 extra.RequestCount 减过一次；而这个失败条目仍然留在
        /// s_LoadedResources 里且 RequestCounter++ 过，我们 FreeResourceRequest 把它打到 0 后，
        /// 下一轮 CleanupLoadedCache 会再 Unload 一次 ⇒ **一增两减**。
        /// 反复探同一个坏 guid 能把 extra 的 RequestCount 推到 0 → Bundle.Unload(true) → 全船隐形。
        /// 缓存 + 上层白名单，双保险。
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> _probeCache
            = new System.Collections.Generic.Dictionary<string, string>();

        /// <summary>
        /// 目标 prefab 现在能不能用。可用返回 null，不可用返回中文原因。
        /// 只做只读判断 + 一次真实加载尝试；任何情况下都不抛。
        /// </summary>
        public static string WhyUnusable(string prefabAssetId)
        {
            if (string.IsNullOrEmpty(prefabAssetId)) return "guid 为空";
            string cached;
            if (_probeCache.TryGetValue(prefabAssetId, out cached)) return cached;
            string r = WhyUnusableUncached(prefabAssetId);
            // 只缓存"确定的"结果；服务没起来时不缓存，等进游戏后重试
            if (r == null || !r.StartsWith("BundlesLoadService")) _probeCache[prefabAssetId] = r;
            return r;
        }

        private static string WhyUnusableUncached(string prefabAssetId)
        {
            try
            {
                var svc = BundlesLoadService.Instance;
                if (svc == null) return "BundlesLoadService 尚未就绪（还没进游戏？）";

                // 第一道：locationlist.json 里根本没有这个 guid ⇒ 它不是可加载资源
                //（蓝图 guid 走 blueprint.assets，不在 locationlist，会在这里被挡下）
                if (!svc.HasLocation(prefabAssetId)) return "不是可加载的 bundle 资源（多半是蓝图 guid，不是 prefab AssetId）";

                // 第二道：真加载一次。缺 DLC 时 locationlist 仍可能有条目，
                // 但 bundle 文件不在盘上 -> BundlesPath 返回 "" -> LoadFromFile 得 null
                // -> TryGetResource 返回 null。**只有这一道能测出缺 DLC。**
                var probe = ResourcesLibrary.TryGetResource<UnitEntityView>(prefabAssetId, true, false);
                if (probe == null)
                {
                    try { ResourcesLibrary.FreeResourceRequest(prefabAssetId, false); } catch { }
                    return "资源加载不出来（对应 DLC 未安装，或 bundle 缺失）";
                }
                // 探针是 hold:false，这里把 RequestCounter 还回去，保持对称
                try { ResourcesLibrary.FreeResourceRequest(prefabAssetId, false); } catch { }
                return null;
            }
            catch (Exception e) { return "预检异常: " + e.Message; }
        }

        // ── 对外主入口 ───────────────────────────────────────────────────────
        /// <summary>
        /// 给船换外观。prefabAssetId 传 null/空 = 还原原版模型。
        /// 拿不到资源就**什么都不改**，保持原模型，绝不写坏 m_CustomPrefabGuid。
        /// 换完后模型不会立刻变：要走一次区域切换/读档让视图重建。
        /// </summary>
        public static bool TrySetShipModel(BaseUnitEntity ship, string prefabAssetId)
        {
            if (ship == null) { Main.LogError("[船模] ship 为空"); return false; }

            // 还原原版
            if (string.IsNullOrEmpty(prefabAssetId))
            {
                try { ship.ViewSettings.SetCustomPrefabGuid(null); }
                catch (Exception e) { Main.LogError("[船模] 还原失败: " + e.Message); return false; }
                RearmVanillaHold(ship);
                Cleanup();
                Main.Log("[船模] 已还原原版船模（下次视图重建生效）");
                return true;
            }

            string why = WhyUnusable(prefabAssetId);
            if (why != null)
            {
                Main.LogError("[船模] 拒绝换模 " + prefabAssetId + " —— " + why + "。保持原模型。");
                return false;
            }

            // 先把 hold 拿到手，再动 m_CustomPrefabGuid：
            // 顺序反了的话，中途失败会留下一个"存档里指向未 hold 资源"的半成品状态。
            if (!AcquireSlot(prefabAssetId))
            {
                Main.LogError("[船模] hold 失败 " + prefabAssetId + "，保持原模型。");
                return false;
            }

            try { ship.ViewSettings.SetCustomPrefabGuid(prefabAssetId); }
            catch (Exception e) { Main.LogError("[船模] 写 m_CustomPrefabGuid 失败: " + e.Message); return false; }

            RearmVanillaHold(ship);
            Main.Log("[船模] 已切到 prefab " + prefabAssetId + "（走一次区域切换后生效）");
            return true;
        }

        /// <summary>
        /// 读档/切区域后自愈：存档里存着的 m_CustomPrefabGuid 现在可能已经失效
        /// （玩家退了 DLC、或换了台机器）。失效就退回原版模型 —— 否则
        /// Instantiate 返回 null → CreateView 返回 null → AttachToViewOnLoad
        /// 把 IsInGame = false，整条船下线。
        /// 建议挂在 RetinueLifecycle.OnAreaDidLoad 里调一次。
        /// </summary>
        public static void ValidateAndRearm(BaseUnitEntity ship)
        {
            if (ship == null) return;
            string guid = null;
            try { guid = ship.ViewSettings != null ? ship.ViewSettings.PrefabGuid : null; } catch { }
            if (string.IsNullOrEmpty(guid)) return;

            string blueprintGuid = null;
            try { blueprintGuid = ship.Blueprint.Prefab.AssetId; } catch { }
            if (guid == blueprintGuid) { Cleanup(); return; }   // 没在换模状态

            string why = WhyUnusable(guid);
            if (why != null)
            {
                Main.LogError("[船模] 存档里的自定义船模 " + guid + " 现在不可用（" + why
                            + "），已退回原版模型以免整船下线。");
                try { ship.ViewSettings.SetCustomPrefabGuid(null); } catch { }
                Cleanup();
                RearmVanillaHold(ship);
                return;
            }
            AcquireSlot(guid);
            RearmVanillaHold(ship);
        }

        // ── 让 vanilla 那个 Part 也 hold 对的东西 ────────────────────────────
        private static readonly FieldInfo FHandle =
            AccessTools.Field(typeof(PartHoldPrefabBundle), "m_Handle");

        /// <summary>
        /// PartHoldPrefabBundle 的 handle 是在视图挂载那一刻建的，
        /// 我们事后改了 PrefabGuid，它手里还攥着旧的。这里换掉（先取后放）。
        /// </summary>
        public static void RearmVanillaHold(BaseUnitEntity ship)
        {
            if (FHandle == null || ship == null) return;
            try
            {
                var part = ship.GetOptional<PartHoldPrefabBundle>();
                if (part == null) return;

                string guid = null;
                try { guid = ship.ViewSettings != null ? ship.ViewSettings.PrefabGuid : null; } catch { }
                if (string.IsNullOrEmpty(guid)) { try { guid = ship.Blueprint.Prefab.AssetId; } catch { } }
                if (string.IsNullOrEmpty(guid)) return;

                var cur = FHandle.GetValue(part) as BundledResourceHandle<UnitEntityView>;
                if (cur != null && cur.AssetId == guid) return;      // 已经对了

                var fresh = BundledResourceHandle<UnitEntityView>.Request(guid, true);  // ★ 先取
                FHandle.SetValue(part, fresh);
                if (cur != null) cur.Dispose();                                          // ★ 后放
            }
            catch (Exception e) { Main.LogError("[船模] RearmVanillaHold 失败: " + e.Message); }
        }

        /// <summary>补丁开关：万一和别的 mod 抢 PartHoldPrefabBundle，关掉它，只靠本 mod 的单槽 hold。</summary>
        public static bool PatchEnabled = true;
    }

    /// <summary>
    /// 让 PartHoldPrefabBundle 去 hold "实际在用的" prefab，而不是蓝图上写死的那个。
    /// 对 vanilla 是恒等变换：没设 m_CustomPrefabGuid 时 PrefabGuid == Blueprint.Prefab.AssetId；
    /// 用 Doll 的单位 PrefabGuid 返回 null，我们回落到蓝图值，与原版完全一致。
    /// </summary>
    [HarmonyPatch(typeof(PartHoldPrefabBundle))]
    internal static class PartHoldPrefabBundle_UseEffectivePrefab
    {
        private static readonly FieldInfo FHandle =
            AccessTools.Field(typeof(PartHoldPrefabBundle), "m_Handle");

        [HarmonyPrefix]
        [HarmonyPatch("TryRequestHandle")]
        private static bool Prefix(PartHoldPrefabBundle __instance)
        {
            if (!ShipModelBundleHold.PatchEnabled || FHandle == null) return true; // 交还原版
            try
            {
                var u = __instance.Owner as BaseUnitEntity;
                if (u == null) return false;                       // 与原版同 guard
                if (FHandle.GetValue(__instance) != null) return false;

                string guid = null;
                try { guid = u.ViewSettings != null ? u.ViewSettings.PrefabGuid : null; } catch { }
                if (string.IsNullOrEmpty(guid)) { try { guid = u.Blueprint.Prefab.AssetId; } catch { } }
                if (string.IsNullOrEmpty(guid)) return false;

                FHandle.SetValue(__instance, BundledResourceHandle<UnitEntityView>.Request(guid, true));
                return false;
            }
            catch
            {
                return true;                                        // 出任何岔子都跑原版
            }
        }
    }
}
