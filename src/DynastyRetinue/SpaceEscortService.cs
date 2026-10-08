using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Code.Enums.Helper;
using Kingmaker.Controllers.TurnBased;
using Kingmaker.EntitySystem;
using Kingmaker.ElementsSystem.ContextData;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.Pathfinding;
using Kingmaker.Items;
using ItemSlot = Kingmaker.Items.Slots.ItemSlot;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.SpaceCombat.StarshipLogic.Parts;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Enums;
using Kingmaker.UnitLogic.Parts;
using Pathfinding;
using UnityEngine;
using Warhammer.SpaceCombat.AI;
using Warhammer.SpaceCombat.Blueprints;
using Warhammer.SpaceCombat.Blueprints.Slots;
using Warhammer.SpaceCombat.StarshipLogic.Weapon;

namespace DynastyRetinue
{
    /// <summary>
    /// 海战舰队：按存档名册在每场太空战临时部署多艘原版 AI 舰。
    /// 名册只保存原版 GUID；战斗实体只进当前 MainState，战后统一回收。
    /// </summary>
    public sealed class SpaceEscortService : IAreaHandler, IAreaActivationHandler,
        ITurnBasedModeHandler, IEndSpaceCombatHandler, IExitSpaceCombatHandler,
        IEntityJoinTBCombat
    {
        private const string MarkerPrefix = "kgd.spaceescort:";
        private const string ChildMarkerPrefix = "kgd.spacecraft:1:";
        private const string Schema = "3";
        private const string SpawnOp = "spawn";
        private const string CleanupOp = "cleanup";
        internal const int FleetCapacity = 6;
        private const int MinRadius = 3;
        private const int MaxRadius = 16;
        private const int MaxShips = 6;

        private enum Phase { Idle, SpawnPending, Active, CleanupPending }

        private sealed class PlannedShip
        {
            public string EntryId;
            public string Name;
            public SpaceFleetHullDef Spec;
            public BlueprintStarship Blueprint;
            public CustomGridNodeBase Anchor;
            public int AnchorX;
            public int AnchorZ;
            public int Direction;
            public Vector3 SpawnPosition;
            public Quaternion Rotation;
            public SpaceFleetRuntimeProfile Profile;
            public readonly List<SpaceFleetLoadoutChoice> Loadout = new List<SpaceFleetLoadoutChoice>();
            public readonly List<CustomGridNodeBase> Cells = new List<CustomGridNodeBase>();
        }

        private sealed class FleetPlan
        {
            public string Op;
            public string Encounter;
            public string Area;
            public string PlayerUid;
            public StarshipEntity Player;
            public int PlayerX;
            public int PlayerZ;
            public int Direction;
            public BlueprintFaction PlayerFaction;
            public SceneEntitiesState State;
            public readonly List<PlannedShip> Ships = new List<PlannedShip>();
            public readonly List<StarshipEntity> Escorts = new List<StarshipEntity>();
            public readonly List<string> EscortUids = new List<string>();
        }

        private static SpaceEscortService _instance;
        private static Phase _phase;
        private static string _area = "";
        private static string _encounter = "";
        private static string _preparedHostSpawnEncounter = "";
        private static readonly List<StarshipEntity> PendingSpawned = new List<StarshipEntity>();
        private static readonly ConditionalWeakTable<StarshipEntity, object> KnownEscortSources
            = new ConditionalWeakTable<StarshipEntity, object>();
        private static readonly ConditionalWeakTable<StarshipEntity, object> KnownChildSources
            = new ConditionalWeakTable<StarshipEntity, object>();
        private static readonly ConditionalWeakTable<SpaceFleetRosterState, object> NormalizedRosters
            = new ConditionalWeakTable<SpaceFleetRosterState, object>();
        private static readonly ConditionalWeakTable<StarshipEntity, SpaceFleetRuntimeProfile> RuntimeProfiles
            = new ConditionalWeakTable<StarshipEntity, SpaceFleetRuntimeProfile>();
        private static readonly object WeakTableMarker = new object();
        private static bool _workScheduled;
        private static bool _spawnCancelled;
        private static int _cleanupRetryCount;
        private static int _profileRestoreFailures;
        private static int _generation;
        private static readonly FieldInfo NavigationTraversalProviderField =
            typeof(PartStarshipNavigation).GetField("traversalProvider",
                BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Subscribe()
        {
            if (_instance != null) return;
            try
            {
                _instance = new SpaceEscortService();
                EventBus.Subscribe(_instance);
                RestoreCurrentArea();
                ArmIfNeeded();
                ScheduleWork();
            }
            catch (Exception e)
            {
                try { if (_instance != null) EventBus.Unsubscribe(_instance); } catch { }
                _instance = null;
                Main.LogError("[海战舰队] 订阅失败: " + e);
            }
        }

        public static void Unsubscribe()
        {
            if (_instance != null)
            {
                try { EventBus.Unsubscribe(_instance); } catch { }
            }
            _instance = null;
            ResetRuntime();
        }

        internal static void CleanupForDisable()
        {
            bool shared;
            if (!CoopState.TryGetSharedGameplayRequired(out shared) || shared)
            {
                Main.Log("[海战舰队] 合作会话中请先退出房间再停用 mod；本次未单边清理舰队。");
                return;
            }
            DestroyMarkedImmediately("mod 停用");
        }

        public void OnAreaDidLoad()
        {
            SpaceFleetDisposedTargetPatch.ResetCount();
            RestoreCurrentArea();
            SpaceFleetItemLedger.SeedCurrentPlayer();
        }

        public void OnAreaActivated()
        {
            RestoreCurrentArea();
            ArmIfNeeded();
            if (_phase == Phase.Active && InCurrentSpaceCombat())
            {
                foreach (var ship in MarkedEscorts(false))
                {
                    ApplyRuntimeControl(ship);
                    SeedEnemyMemory(ship);
                }
                RestoreNativeChildren(true);
            }
            ScheduleWork();
        }

        public void OnAreaBeginUnloading()
        {
            DestroyMarkedImmediately("区域卸载");
            ResetRuntime();
        }

        public void HandleTurnBasedModeSwitched(bool enabled)
        {
            _preparedHostSpawnEncounter = "";
            if (enabled)
            {
                SpaceFleetDisposedTargetPatch.ResetCount();
                _spawnCancelled = false; ArmIfNeeded(); ScheduleWork();
            }
            else { _spawnCancelled = true; RequestCleanup(); }
        }

        public void HandleEndSpaceCombat()
        { _preparedHostSpawnEncounter = ""; _spawnCancelled = true; RequestCleanup(); }
        public void HandleExitSpaceCombat()
        { _preparedHostSpawnEncounter = ""; _spawnCancelled = true; RequestCleanup(); }
        internal static void NotifyTurnBasedExit()
        { _preparedHostSpawnEncounter = ""; _spawnCancelled = true; RequestCleanup(); }

        public void HandleEntityJoinTBCombat()
        {
            var ship = EventInvokerExtensions.MechanicEntity as StarshipEntity;
            if (!IsExpectedEscort(ship)) return;
            PendingSpawned.Remove(ship);
            SeedEnemyMemory(ship);
            if (Main.Settings != null && Main.Settings.WatchMomentum)
                DiagnoseJoinedPlacement(ship);
        }

        internal static void RearmPending() { ScheduleWork(); }

        internal static void EnableCatchUp()
        {
            RestoreCurrentArea();
            ArmIfNeeded();
            ScheduleWork();
        }

        // ---------------------------------------------------------------- 名册

        private static string CurrentGameId()
        {
            try { return Game.Instance != null && Game.Instance.Player != null
                ? Game.Instance.Player.GameId ?? "" : ""; }
            catch { return ""; }
        }

        internal static SpaceFleetRosterState CurrentRoster(bool create)
        {
            string gameId = CurrentGameId();
            var game = Game.Instance;
            var settings = game != null && game.State != null ? game.State.InGameSettings : null;
            var roster = SpaceFleetSaveStore.Get(settings, gameId, create);
            if (roster == null) return null;
            object marker;
            if (!NormalizedRosters.TryGetValue(roster, out marker))
            {
                bool changed = NormalizeRoster(roster);
                if (!changed || SpaceFleetSaveStore.TryWrite(settings, roster))
                    NormalizedRosters.GetValue(roster, delegate { return WeakTableMarker; });
            }
            return roster;
        }

        private static bool NormalizeRoster(SpaceFleetRosterState roster)
        {
            if (roster == null || roster.DataVersion > SpaceFleetCatalog.RosterDataVersion)
                return false;
            bool changed = false;
            bool migrateBonuses = roster.DataVersion < SpaceFleetCatalog.RosterDataVersion;
            if (roster.Entries == null) { roster.Entries = new List<SpaceFleetEntry>(); changed = true; }
            if (roster.KnownShipItemGuids == null)
            { roster.KnownShipItemGuids = new List<string>(); changed = true; }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            int next = Math.Max(1, roster.NextId);
            for (int i = 0; i < roster.Entries.Count; i++)
            {
                var entry = roster.Entries[i];
                if (entry == null)
                {
                    roster.Entries.RemoveAt(i--);
                    changed = true;
                    continue;
                }
                if (entry.Loadout == null)
                { entry.Loadout = new List<SpaceFleetLoadoutChoice>(); changed = true; }
                if (string.IsNullOrEmpty(entry.Id) || !ids.Add(entry.Id))
                {
                    do { entry.Id = "fleet-" + next++.ToString(CultureInfo.InvariantCulture); }
                    while (!ids.Add(entry.Id));
                    changed = true;
                }
                int parsed;
                if (entry.Id.StartsWith("fleet-", StringComparison.Ordinal)
                    && int.TryParse(entry.Id.Substring(6), NumberStyles.None,
                        CultureInfo.InvariantCulture, out parsed))
                    next = Math.Max(next, parsed + 1);

                if (migrateBonuses)
                {
                    SnapshotLegacyBonuses(entry);
                    changed = true;
                }
            }
            if (roster.NextId != next) { roster.NextId = next; changed = true; }
            if (!roster.Initialized) { roster.Initialized = true; changed = true; }
            if (roster.DataVersion != SpaceFleetCatalog.RosterDataVersion)
            { roster.DataVersion = SpaceFleetCatalog.RosterDataVersion; changed = true; }
            return changed;
        }

        private static void SnapshotLegacyBonuses(SpaceFleetEntry entry)
        {
            var settings = Main.Settings;
            var hull = entry != null ? SpaceFleetCatalog.Find(entry.BlueprintGuid) : null;
            if (settings == null || hull == null || !settings.ShipExtraShots) return;
            if (hull.Class == 1)
            {
                entry.BroadsideExtraShots = FleetBudget.ClampBonus(settings.ShipCruiserBroadside, 4);
                entry.NonBroadsideExtraRange = FleetBudget.ClampBonus(settings.ShipCruiserRange, 8);
            }
            else if (hull.Class == 2)
            {
                entry.BroadsideExtraShots = FleetBudget.ClampBonus(settings.ShipGrandBroadside, 4);
                entry.NonBroadsideExtraShots = FleetBudget.ClampBonus(settings.ShipGrandProw, 4);
                entry.BroadsideExtraRange = FleetBudget.ClampBonus(settings.ShipGrandRangeBroadside, 8);
                entry.NonBroadsideExtraRange = FleetBudget.ClampBonus(settings.ShipGrandRangeProw, 8);
            }
        }

        internal static List<SpaceFleetEntry> RosterEntries()
        {
            var roster = CurrentRoster(true);
            return roster != null
                && roster.DataVersion <= SpaceFleetCatalog.RosterDataVersion
                && roster.Entries != null
                ? new List<SpaceFleetEntry>(roster.Entries) : new List<SpaceFleetEntry>();
        }

        internal static int CapacityUsed()
        {
            int used = 0;
            foreach (var entry in RosterEntries())
            {
                var hull = entry != null ? SpaceFleetCatalog.Find(entry.BlueprintGuid) : null;
                if (hull != null) used += hull.Capacity;
            }
            return used;
        }

        internal static bool HullAvailable(string blueprintGuid, out string reason)
        {
            reason = "";
            var spec = SpaceFleetCatalog.Find(blueprintGuid);
            if (spec == null) { reason = L.T("未知舰型"); return false; }
            var bp = ResourcesLibrary.TryGetBlueprint<BlueprintStarship>(spec.BlueprintGuid);
            if (bp == null) { reason = L.T("舰船蓝图不可用"); return false; }
            return ValidateBlueprint(bp, spec, out reason);
        }

        private static bool CanEditRoster(out string reason)
        {
            reason = "";
            if (InCurrentSpaceCombat())
            { reason = L.T("海战中不能改舰队名册"); return false; }
            bool shared;
            if (!CoopState.TryGetSharedGameplayRequired(out shared))
            { reason = L.T("合作状态暂时不可读取；为避免双方名册分叉，本次未修改"); return false; }
            if (shared && !CoopState.IsConfirmedHost)
            { reason = L.T("合作模式只有房主可以编辑舰队"); return false; }
            var roster = CurrentRoster(false);
            if (roster != null && roster.DataVersion > SpaceFleetCatalog.RosterDataVersion)
            { reason = L.T("舰队名册来自更新版本，当前版本不会修改"); return false; }
            return true;
        }

        internal static bool Recruit(string blueprintGuid, out string result)
        {
            result = "";
            if (!CanEditRoster(out result)) return false;
            var spec = SpaceFleetCatalog.Find(blueprintGuid);
            if (spec == null) { result = L.T("未知舰型"); return false; }
            string reason;
            if (!HullAvailable(spec.BlueprintGuid, out reason))
            { result = L.F("{0}不可用：{1}", SpaceFleetCatalog.DisplayName(spec), reason); return false; }
            var hull = spec;
            int used = CapacityUsed();
            if (used + hull.Capacity > FleetCapacity)
            { result = L.F("舰队容量不足（{0}/{1}）", used, FleetCapacity); return false; }
            var roster = CurrentRoster(true);
            if (roster == null) { result = L.T("尚未载入游戏存档"); return false; }
            if (roster.Entries == null) roster.Entries = new List<SpaceFleetEntry>();
            int id = Math.Max(1, roster.NextId);
            var entry = new SpaceFleetEntry
            {
                Id = "fleet-" + id.ToString(CultureInfo.InvariantCulture),
                BlueprintGuid = spec.BlueprintGuid,
                Name = SpaceFleetCatalog.DisplayName(spec) + " " + id.ToString(CultureInfo.InvariantCulture)
            };
            long currentPf = FleetBudget.Used(roster.Entries);
            var candidate = new List<SpaceFleetEntry>(roster.Entries) { entry };
            long candidatePf = FleetBudget.Used(candidate);
            string budgetReason;
            if (!FleetBudget.CanApply(currentPf, candidatePf, out budgetReason))
            { result = budgetReason; return false; }

            int oldNextId = roster.NextId;
            roster.NextId = id + 1;
            roster.Entries.Add(entry);
            if (!SaveRoster())
            {
                roster.Entries.Remove(entry);
                roster.NextId = oldNextId;
                result = L.T("更新舰队存档数据失败，招募已回滚");
                return false;
            }
            result = L.F("已招募{0}；容量 {1}/{2}；PF 占用 {3}/{4}",
                SpaceFleetCatalog.DisplayName(spec), used + hull.Capacity, FleetCapacity,
                candidatePf, FleetBudget.Limit());
            return true;
        }

        internal static bool Dismiss(string entryId, out string result)
        {
            result = "";
            if (!CanEditRoster(out result)) return false;
            var roster = CurrentRoster(false);
            if (roster == null || roster.Entries == null) { result = L.T("舰队名册为空"); return false; }
            for (int i = 0; i < roster.Entries.Count; i++)
            {
                var entry = roster.Entries[i];
                if (entry == null || !string.Equals(entry.Id, entryId, StringComparison.Ordinal)) continue;
                roster.Entries.RemoveAt(i);
                if (!SaveRoster())
                {
                    roster.Entries.Insert(i, entry);
                    result = L.T("更新舰队存档数据失败，遣散已回滚");
                    return false;
                }
                result = L.F("已遣散 {0}", entry.Name ?? entry.Id);
                return true;
            }
            result = L.F("找不到舰队条目 {0}", entryId);
            return false;
        }

        internal static string SelectedItemGuid(SpaceFleetEntry entry,
            SpaceFleetSlotDef slot)
        {
            if (entry != null && entry.Loadout != null && slot != null)
                foreach (var choice in entry.Loadout)
                    if (choice != null && string.Equals(choice.SlotKey, slot.Key,
                        StringComparison.Ordinal)) return choice.ItemGuid ?? "";
            return slot != null ? slot.OriginalItemGuid ?? "" : "";
        }

        internal static string ItemDisplayName(string itemGuid, string unnamedLabel = null)
        {
            if (string.IsNullOrEmpty(itemGuid)) return L.T("空槽");
            try
            {
                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintStarshipItem>(itemGuid);
                string name = bp != null ? bp.Name : null;
                Guid identifier;
                if (!string.IsNullOrWhiteSpace(name)
                    && !string.Equals(name, itemGuid, StringComparison.OrdinalIgnoreCase)
                    && !Guid.TryParse(name, out identifier)) return name;
                // 某些 NPC 原装组件有有效蓝图但没有本地化名称，用调用方的组件类别标识。
                return bp != null && !string.IsNullOrWhiteSpace(unnamedLabel)
                    ? unnamedLabel : L.T("未知装备");
            }
            catch { return L.T("未知装备"); }
        }

        internal static string SlotDisplayName(SpaceFleetSlotDef slot)
        {
            if (slot == null) return L.T("未知槽位");
            if (!slot.IsWeapon)
            {
                switch (slot.Key)
                {
                    case "component:PlasmaDrives": return L.T("等离子驱动器");
                    case "component:VoidShieldGenerator": return L.T("虚空盾发生器");
                    case "component:AugerArray": return L.T("探测阵列");
                    case "component:ArmorPlating": return L.T("装甲板");
                    default: return L.T("未知槽位");
                }
            }
            string name;
            switch (slot.WeaponType)
            {
                case WeaponSlotType.Port: name = L.T("左舷"); break;
                case WeaponSlotType.Starboard: name = L.T("右舷"); break;
                case WeaponSlotType.Prow: name = L.T("舰首"); break;
                case WeaponSlotType.Dorsal: name = L.T("船脊"); break;
                default: return L.T("未知槽位");
            }
            return L.F("{0} #{1}", name, slot.WeaponIndex + 1);
        }

        internal static List<string> ItemOptions(SpaceFleetEntry entry, SpaceFleetSlotDef slot)
        {
            var hull = entry != null ? SpaceFleetCatalog.Find(entry.BlueprintGuid) : null;
            var roster = CurrentRoster(false);
            return ItemOptions(hull, slot != null ? slot.Key : null,
                roster != null ? roster.KnownShipItemGuids : null);
        }

        internal static List<string> ItemOptions(SpaceFleetHullDef hull, string slotKey,
            IEnumerable<string> known)
        {
            var result = new List<string>();
            var slot = SpaceFleetCatalog.FindSlot(hull, slotKey);
            if (!SpaceFleetCatalog.IsOpenSlot(slot)) return result;
            result.Add(slot.OriginalItemGuid ?? "");
            // 只查账本中已获得的 GUID，不扫描资源库或玩家库存。
            var candidates = new SortedSet<string>(known ?? new string[0],
                StringComparer.OrdinalIgnoreCase);
            foreach (string guid in candidates)
            {
                BlueprintStarshipItem item;
                SpaceFleetWeaponDef weapon;
                if (string.IsNullOrEmpty(guid) || string.Equals(guid, result[0],
                    StringComparison.OrdinalIgnoreCase)
                    || !SpaceFleetCatalog.TryResolveItem(hull, slotKey, guid, out item, out weapon)
                    || item == null) continue;
                result.Add(item.AssetGuid.ToString());
            }
            return result;
        }

        internal static List<string> WeaponOptions(SpaceFleetEntry entry, SpaceFleetSlotDef slot)
        { return ItemOptions(entry, slot); }

        internal static bool SelectWeapon(string entryId, string slotKey,
            string itemGuid, out string result)
        { return SelectItem(entryId, slotKey, itemGuid, out result); }

        internal static bool SelectItem(string entryId, string slotKey,
            string itemGuid, out string result)
        {
            result = "";
            if (!CanEditRoster(out result)) return false;
            var roster = CurrentRoster(false);
            if (roster == null || roster.Entries == null)
            { result = L.T("舰队名册为空"); return false; }
            SpaceFleetEntry entry = null;
            foreach (var candidate in roster.Entries)
                if (candidate != null && string.Equals(candidate.Id, entryId,
                    StringComparison.Ordinal)) { entry = candidate; break; }
            var hull = entry != null ? SpaceFleetCatalog.Find(entry.BlueprintGuid) : null;
            var slot = SpaceFleetCatalog.FindSlot(hull, slotKey);
            if (entry == null || !SpaceFleetCatalog.IsOpenSlot(slot))
            { result = L.T("舰船或舰装槽无效"); return false; }

            var options = ItemOptions(entry, slot);
            string selected = options.Find(x => string.Equals(x, itemGuid ?? "",
                StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            { result = L.T("所选舰装尚未获得或不兼容此槽位"); return false; }
            string current = SelectedItemGuid(entry, slot);
            bool stale = !options.Exists(x => string.Equals(x, current,
                StringComparison.OrdinalIgnoreCase));
            if (!stale && string.Equals(current, selected, StringComparison.OrdinalIgnoreCase))
            { result = L.T("这个槽位已经使用所选舰装"); return false; }

            // 先在候选副本计算整支舰队的 delta；预算通过前不改 live loadout。
            var proposed = entry.Loadout != null
                ? new List<SpaceFleetLoadoutChoice>(entry.Loadout)
                : new List<SpaceFleetLoadoutChoice>();
            proposed.RemoveAll(x => x != null
                && string.Equals(x.SlotKey, slotKey, StringComparison.Ordinal));
            if (!string.Equals(selected, slot.OriginalItemGuid ?? "", StringComparison.OrdinalIgnoreCase))
                proposed.Add(new SpaceFleetLoadoutChoice { SlotKey = slotKey, ItemGuid = selected });
            proposed.Sort((a, b) => string.CompareOrdinal(a != null ? a.SlotKey : "",
                b != null ? b.SlotKey : ""));

            long beforeFleet = FleetBudget.Used(roster.Entries);
            long afterFleet = 0;
            var replacement = CloneForCost(entry, proposed);
            foreach (var row in roster.Entries)
                afterFleet = FleetBudget.Add(afterFleet,
                    FleetBudget.Cost(ReferenceEquals(row, entry) ? replacement : row).Total);
            string reason;
            if (!FleetBudget.CanApply(beforeFleet, afterFleet, out reason))
            { result = reason; return false; }
            if (!SaveLoadoutChange(entry, proposed, SaveRoster))
            { result = L.T("更新舰队存档数据失败，换装已回滚"); return false; }
            result = stale && string.Equals(selected, slot.OriginalItemGuid ?? "",
                StringComparison.OrdinalIgnoreCase)
                ? L.F("已将失效舰装恢复为原装；PF 占用 {0}/{1}", afterFleet, FleetBudget.Limit())
                : L.F("已为{0}装配{1}；PF 占用 {2}/{3}",
                    SlotDisplayName(slot), ItemDisplayName(selected), afterFleet, FleetBudget.Limit());
            return true;
        }

        internal static bool SaveLoadoutChange(SpaceFleetEntry entry,
            List<SpaceFleetLoadoutChoice> proposed, Func<bool> save)
        {
            var previous = entry.Loadout;
            bool saved = false;
            try
            {
                entry.Loadout = proposed;
                saved = save();
                return saved;
            }
            finally
            {
                // 保存返回 false 或抛异常都恢复原始列表引用（包括原先的 null）。
                if (!saved) entry.Loadout = previous;
            }
        }

        private static SpaceFleetEntry CloneForCost(SpaceFleetEntry source,
            List<SpaceFleetLoadoutChoice> loadout)
        {
            return new SpaceFleetEntry
            {
                Id = source.Id, BlueprintGuid = source.BlueprintGuid, Name = source.Name,
                BroadsideExtraShots = source.BroadsideExtraShots,
                NonBroadsideExtraShots = source.NonBroadsideExtraShots,
                BroadsideExtraRange = source.BroadsideExtraRange,
                NonBroadsideExtraRange = source.NonBroadsideExtraRange,
                Loadout = loadout ?? new List<SpaceFleetLoadoutChoice>()
            };
        }

        internal static bool AdjustBonus(string entryId, string field, int delta,
            out string result)
        {
            result = "";
            if (!CanEditRoster(out result)) return false;
            var roster = CurrentRoster(false);
            if (roster == null || roster.Entries == null)
            { result = L.T("舰队名册为空"); return false; }
            SpaceFleetEntry entry = null;
            foreach (var candidate in roster.Entries)
                if (candidate != null && string.Equals(candidate.Id, entryId, StringComparison.Ordinal))
                { entry = candidate; break; }
            if (entry == null) { result = L.F("找不到舰队条目 {0}", entryId); return false; }
            var hull = SpaceFleetCatalog.Find(entry.BlueprintGuid);
            if (hull == null) { result = L.T("舰型不受支持"); return false; }

            int current;
            int limit;
            if (field == "bs")
            { current = entry.BroadsideExtraShots; limit = BonusLimit(hull, field); }
            else if (field == "ns")
            { current = entry.NonBroadsideExtraShots; limit = BonusLimit(hull, field); }
            else if (field == "br")
            { current = entry.BroadsideExtraRange; limit = BonusLimit(hull, field); }
            else if (field == "nr")
            { current = entry.NonBroadsideExtraRange; limit = BonusLimit(hull, field); }
            else { result = L.T("未知强化字段"); return false; }

            int next = delta > 0
                ? Math.Min(limit, current + delta)
                : Math.Max(0, current + delta);
            if (next == current)
            {
                result = delta > 0 ? L.F("已到当前舰型上限 {0}", limit) : L.T("已经是 0");
                return false;
            }
            long before = FleetBudget.Used(roster.Entries);
            SetBonus(entry, field, next);
            long after = FleetBudget.Used(roster.Entries);
            string reason;
            if (!FleetBudget.CanApply(before, after, out reason))
            {
                SetBonus(entry, field, current);
                result = reason;
                return false;
            }
            if (!SaveRoster())
            {
                SetBonus(entry, field, current);
                result = L.T("更新舰队存档数据失败，强化已回滚");
                return false;
            }
            result = L.F("已调整 {0}：{1} → {2}；PF 占用 {3}/{4}",
                BonusName(field), current, next, after, FleetBudget.Limit());
            return true;
        }

        internal static int BonusLimit(SpaceFleetHullDef hull, string field)
        {
            if (hull == null || Main.Settings == null) return 0;
            if (hull.Class == 1)
            {
                if (field == "bs") return FleetBudget.ClampBonus(Main.Settings.ShipCruiserBroadside,
                    SpaceFleetCatalog.MaxExtraShots);
                if (field == "nr") return FleetBudget.ClampBonus(Main.Settings.ShipCruiserRange,
                    SpaceFleetCatalog.MaxExtraRange);
                return 0;
            }
            if (hull.Class == 2)
            {
                if (field == "bs") return FleetBudget.ClampBonus(Main.Settings.ShipGrandBroadside,
                    SpaceFleetCatalog.MaxExtraShots);
                if (field == "ns") return FleetBudget.ClampBonus(Main.Settings.ShipGrandProw,
                    SpaceFleetCatalog.MaxExtraShots);
                if (field == "br") return FleetBudget.ClampBonus(Main.Settings.ShipGrandRangeBroadside,
                    SpaceFleetCatalog.MaxExtraRange);
                if (field == "nr") return FleetBudget.ClampBonus(Main.Settings.ShipGrandRangeProw,
                    SpaceFleetCatalog.MaxExtraRange);
            }
            return 0;
        }

        private static string BonusName(string field)
        {
            if (field == "bs") return L.T("舷炮额外开火");
            if (field == "ns") return L.T("非舷炮额外开火");
            if (field == "br") return L.T("舷炮额外射程");
            if (field == "nr") return L.T("非舷炮额外射程");
            return field ?? "";
        }

        private static void SetBonus(SpaceFleetEntry entry, string field, int value)
        {
            if (field == "bs") entry.BroadsideExtraShots = value;
            else if (field == "ns") entry.NonBroadsideExtraShots = value;
            else if (field == "br") entry.BroadsideExtraRange = value;
            else if (field == "nr") entry.NonBroadsideExtraRange = value;
        }

        internal static bool Rename(string entryId, string newName, out string result)
        {
            result = "";
            if (!CanEditRoster(out result)) return false;
            string name = (newName ?? "").Trim();
            if (name.Length == 0) { result = L.T("舰名不能为空"); return false; }
            if (name.Length > 64)
            {
                name = name.Substring(0, 64);
                if (name.Length > 0 && char.IsHighSurrogate(name[name.Length - 1]))
                    name = name.Substring(0, name.Length - 1);
            }
            for (int i = 0; i < name.Length; i++)
            {
                if (char.IsHighSurrogate(name[i]))
                {
                    if (i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1]))
                    { result = L.T("舰名含无效 Unicode 字符"); return false; }
                    i++;
                }
                else if (char.IsLowSurrogate(name[i]))
                { result = L.T("舰名含无效 Unicode 字符"); return false; }
            }
            var roster = CurrentRoster(false);
            if (roster == null || roster.Entries == null) { result = L.T("舰队名册为空"); return false; }
            foreach (var entry in roster.Entries)
            {
                if (entry == null || !string.Equals(entry.Id, entryId, StringComparison.Ordinal)) continue;
                string oldName = entry.Name;
                entry.Name = name;
                if (!SaveRoster())
                {
                    entry.Name = oldName;
                    result = L.T("更新舰队存档数据失败，改名已回滚");
                    return false;
                }
                result = L.F("已改名为 {0}", name);
                return true;
            }
            result = L.F("找不到舰队条目 {0}", entryId);
            return false;
        }

        internal static bool SaveRoster()
        {
            var game = Game.Instance;
            var settings = game != null && game.State != null ? game.State.InGameSettings : null;
            return SpaceFleetSaveStore.TryWrite(settings, CurrentRoster(false));
        }

        // ---------------------------------------------------------------- 生命周期

        private static void RestoreCurrentArea()
        {
            _generation++;
            _workScheduled = false;
            _cleanupRetryCount = 0;
            _profileRestoreFailures = 0;
            _preparedHostSpawnEncounter = "";
            PendingSpawned.Clear();
            _area = RetinueLifecycle.CurrentAreaId();
            _encounter = "";
            var deployed = MarkedEscorts(false);
            foreach (var ship in deployed)
            {
                KnownEscortSources.GetValue(ship, delegate { return WeakTableMarker; });
                SpaceFleetRuntimeProfile profile;
                RuntimeProfiles.Remove(ship);
                string failure = "marker 无效";
                if (TryReadMarkerProfile(ship, out profile)
                    && TryRestoreProfileLoadout(ship, profile, out failure))
                    RegisterRuntimeProfile(ship, profile);
                else if ((ship.Description.CustomPetName ?? "").StartsWith(
                    MarkerPrefix + "3:", StringComparison.Ordinal))
                {
                    _profileRestoreFailures++;
                    Main.LogError("[海战舰队] 冷读档舰装/profile 恢复失败 " + ship.UniqueId + "：" + failure);
                }
            }
            if (deployed.Count != 0) RestoreNativeChildren(false);
            if (deployed.Count == 0) _phase = Phase.Idle;
            else if (InCurrentSpaceCombat())
            {
                _phase = Phase.Active;
                Main.Log("[海战舰队] 战中读档已复用 " + deployed.Count + " 艘舰，不重复生成。");
            }
            else
            {
                _phase = Phase.CleanupPending;
                Main.Log("[海战舰队] 读档发现战外遗留 " + deployed.Count + " 艘舰，已排队清理。");
            }
        }

        private static void ArmIfNeeded()
        {
            if (!Main.Enabled || !InCurrentSpaceCombat() || _phase == Phase.Active) return;
            if (MarkedEscorts(false).Count > 0) { _phase = Phase.Active; return; }
            if (RosterEntries().Count == 0) { _phase = Phase.Idle; return; }
            _area = RetinueLifecycle.CurrentAreaId();
            _encounter = NewEncounterKey(_area, PlayerShip());
            if (!string.IsNullOrEmpty(_encounter)) _phase = Phase.SpawnPending;
        }

        private static void RequestCleanup()
        {
            if (MarkedEscorts(false).Count == 0 && PendingSpawned.Count == 0)
            { _phase = Phase.Idle; return; }
            if (_phase == Phase.CleanupPending)
            {
                if (!_workScheduled)
                {
                    _cleanupRetryCount = 0;
                    ScheduleCleanupRetry("收到新的清理请求");
                }
                return;
            }
            _phase = Phase.CleanupPending;
            _cleanupRetryCount = 0;
            ScheduleWork();
        }

        private static void ScheduleWork(int frames = 1)
        {
            if (_workScheduled || (_phase != Phase.SpawnPending && _phase != Phase.CleanupPending)) return;
            _workScheduled = true;
            int generation = _generation;
            Deferred.NextFrames(Math.Max(1, frames), delegate
            {
                if (generation != _generation) return;
                _workScheduled = false;
                ProcessPending();
            });
        }

        private static void ProcessPending()
        {
            if (_phase == Phase.CleanupPending) { ProcessCleanup(); return; }
            if (_phase != Phase.SpawnPending || !InCurrentSpaceCombat()) return;

            bool shared;
            if (!CoopState.TryGetSharedGameplayRequired(out shared))
            { ScheduleWork(60); return; }
            if (shared && !CoopState.IsConfirmedHost) return;
            if (Main.Settings == null || !Main.Settings.SpaceEscortEnabled)
            { _phase = Phase.Idle; return; }

            string[] payload;
            string failure;
            if (!TryBuildSpawnPayload(out payload, out failure))
            { Main.Log("[海战舰队] 本场未部署：" + failure); _phase = Phase.Idle; return; }
            if (shared)
            {
                if (!CoopAutoTransaction.Begin("spaceescort", payload))
                    CoopAutoTransaction.DelayBegin("spaceescort");
                return;
            }

            object plan;
            string signature;
            if (!TryPrepareSynchronizedEscort(payload, out plan, out signature, out failure)
                || !CanCommitSynchronizedEscort(plan, out failure))
            { Main.Log("[海战舰队] 单机预检未通过：" + failure); _phase = Phase.Idle; return; }
            CommitSynchronizedEscort(plan);
        }

        private static void ProcessCleanup()
        {
            bool shared;
            if (!CoopState.TryGetSharedGameplayRequired(out shared))
            { ScheduleCleanupRetry("合作状态暂时不可读取"); return; }

            var escorts = MarkedEscorts(false);
            foreach (var pending in PendingSpawned)
                if (pending != null && !pending.IsDisposed && !pending.WillBeDestroyed && !escorts.Contains(pending))
                    escorts.Add(pending);
            if (escorts.Count == 0) { FinishCleanup(); return; }

            string[] payload;
            string failure;
            if (!TryBuildCleanupPayload(escorts, out payload, out failure))
            { ScheduleCleanupRetry("无法构造清理计划: " + failure); return; }
            if (shared)
            {
                if (!CoopState.IsConfirmedHost) return;
                if (!CoopAutoTransaction.Begin("spaceescort", payload))
                    CoopAutoTransaction.DelayBegin("spaceescort");
                return;
            }

            object plan;
            string signature;
            if (!TryPrepareSynchronizedEscort(payload, out plan, out signature, out failure)
                || !CanCommitSynchronizedEscort(plan, out failure))
            { ScheduleCleanupRetry("单机清理预检未通过: " + failure); return; }
            CommitSynchronizedEscort(plan);
        }

        private static void ScheduleCleanupRetry(string reason)
        {
            if (_phase != Phase.CleanupPending) return;
            int[] waits = { 60, 180, 600 };
            if (_cleanupRetryCount >= waits.Length)
            {
                Main.LogError("[海战舰队] 清理重试已停止（仍可由下一次区域/战斗事件唤醒）: " + reason);
                return;
            }
            int frames = waits[_cleanupRetryCount++];
            Main.Log("[海战舰队] 清理将在 " + frames + " 帧后重试: " + reason);
            ScheduleWork(frames);
        }

        // ---------------------------------------------------------------- 事务 payload

        private static bool TryResolveProfile(SpaceFleetEntry entry, SpaceFleetHullDef hull,
            out SpaceFleetRuntimeProfile profile, out string failure)
        {
            profile = null;
            failure = "";
            if (entry == null || hull == null || string.IsNullOrEmpty(entry.Id))
            { failure = "舰队条目或舰型无效"; return false; }
            int bs = FleetBudget.ClampBonus(entry.BroadsideExtraShots, SpaceFleetCatalog.MaxExtraShots);
            int ns = FleetBudget.ClampBonus(entry.NonBroadsideExtraShots, SpaceFleetCatalog.MaxExtraShots);
            int br = FleetBudget.ClampBonus(entry.BroadsideExtraRange, SpaceFleetCatalog.MaxExtraRange);
            int nr = FleetBudget.ClampBonus(entry.NonBroadsideExtraRange, SpaceFleetCatalog.MaxExtraRange);
            if (bs != entry.BroadsideExtraShots || ns != entry.NonBroadsideExtraShots
                || br != entry.BroadsideExtraRange || nr != entry.NonBroadsideExtraRange)
            { failure = "逐舰强化超出协议硬上限"; return false; }
            profile = new SpaceFleetRuntimeProfile
            {
                EntryId = entry.Id,
                HullGuid = hull.BlueprintGuid,
                BroadsideExtraShots = bs,
                NonBroadsideExtraShots = ns,
                BroadsideExtraRange = br,
                NonBroadsideExtraRange = nr,
                ShieldPct = hull.Class == 2 ? Math.Max(0, Main.Settings.ShipGrandShieldPct)
                    : hull.Class == 1 ? Math.Max(0, Main.Settings.ShipCruiserShieldPct) : 0,
                ArmourPct = hull.Class == 2 ? Math.Max(0, Main.Settings.ShipGrandArmourPct)
                    : hull.Class == 1 ? Math.Max(0, Main.Settings.ShipCruiserArmourPct) : 0,
                RamPct = hull.Class == 2 ? Math.Max(0, Main.Settings.ShipGrandRamPct)
                    : hull.Class == 1 ? Math.Max(0, Main.Settings.ShipCruiserRamPct) : 0
            };
            if (profile.ShieldPct > 1000 || profile.ArmourPct > 1000 || profile.RamPct > 1000)
            { failure = "舰型防御/撞角加成超出协议硬上限"; profile = null; return false; }
            return true;
        }

        private static bool AppendCanonicalLoadout(SpaceFleetEntry entry, SpaceFleetHullDef hull,
            List<string> rows, out string failure)
        {
            var roster = CurrentRoster(false);
            return AppendCanonicalLoadout(entry, hull, rows,
                roster != null ? roster.KnownShipItemGuids : null, out failure);
        }

        internal static bool AppendCanonicalLoadout(SpaceFleetEntry entry, SpaceFleetHullDef hull,
            List<string> rows, IEnumerable<string> acquired, out string failure)
        {
            failure = "";
            var known = new HashSet<string>(acquired ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var choices = entry.Loadout != null
                ? new List<SpaceFleetLoadoutChoice>(entry.Loadout)
                : new List<SpaceFleetLoadoutChoice>();
            choices.Sort((a, b) => string.CompareOrdinal(a != null ? a.SlotKey : "",
                b != null ? b.SlotKey : ""));
            var canonical = new List<SpaceFleetLoadoutChoice>();
            string previous = null;
            foreach (var choice in choices)
            {
                if (choice == null || string.IsNullOrEmpty(choice.SlotKey))
                { failure = "loadout 含空选择"; return false; }
                if (string.Equals(previous, choice.SlotKey, StringComparison.Ordinal))
                { failure = "loadout 含重复 SlotKey"; return false; }
                previous = choice.SlotKey;
                var slot = SpaceFleetCatalog.FindSlot(hull, choice.SlotKey);
                if (slot == null) { failure = "loadout 含未知 SlotKey"; return false; }
                string item = choice.ItemGuid ?? "";
                if (string.Equals(item, slot.OriginalItemGuid ?? "", StringComparison.OrdinalIgnoreCase))
                    continue;
                BlueprintStarshipItem blueprint;
                SpaceFleetWeaponDef weapon;
                if (!known.Contains(item)
                    || !SpaceFleetCatalog.TryResolveItem(hull, choice.SlotKey, item,
                        out blueprint, out weapon) || blueprint == null)
                { failure = "槽位 " + choice.SlotKey + " 的舰装尚未获得或不适配"; return false; }
                canonical.Add(new SpaceFleetLoadoutChoice
                { SlotKey = choice.SlotKey, ItemGuid = blueprint.AssetGuid.ToString() });
            }
            rows.Add(canonical.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var choice in canonical)
            { rows.Add(choice.SlotKey); rows.Add(choice.ItemGuid ?? ""); }
            return true;
        }

        internal static bool TryParseLoadout(SpaceFleetHullDef hull, string[] fields,
            int offset, int count, out List<SpaceFleetLoadoutChoice> loadout, out string failure)
        {
            loadout = new List<SpaceFleetLoadoutChoice>();
            failure = "";
            if (hull == null || fields == null || offset < 0 || offset > fields.Length
                || count < 0 || count > hull.Slots.Length || count > (fields.Length - offset) / 2)
            { failure = "loadout 长度无效"; return false; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int k = 0; k < count; k++)
            {
                string key = fields[offset + k * 2];
                string guid = fields[offset + k * 2 + 1];
                var slot = SpaceFleetCatalog.FindSlot(hull, key);
                BlueprintStarshipItem item;
                SpaceFleetWeaponDef weapon;
                if (slot == null || !seen.Add(key)
                    || !SpaceFleetCatalog.TryResolveItem(hull, key, guid, out item, out weapon))
                { failure = "loadout 舰装或 SlotKey 未通过类型适配"; return false; }
                // 房主在构建 payload 时验证获得记录；接收端不依赖本地 Settings 账本。
                if (!string.Equals(guid ?? "", slot.OriginalItemGuid ?? "", StringComparison.OrdinalIgnoreCase))
                    loadout.Add(new SpaceFleetLoadoutChoice
                    { SlotKey = key, ItemGuid = item.AssetGuid.ToString() });
            }
            return true;
        }

        private static bool TryBuildSpawnPayload(out string[] payload, out string failure)
        {
            payload = null;
            failure = "";
            var player = PlayerShip();
            var state = MainState();
            var entries = RosterEntries();
            entries.Sort((a, b) => string.CompareOrdinal(a != null ? a.Id : "", b != null ? b.Id : ""));
            if (player == null || state == null || entries.Count < 1 || entries.Count > MaxShips)
            { failure = "玩家舰、MainState 或舰队名册无效"; return false; }
            if (CapacityOf(entries) > FleetCapacity) { failure = "舰队名册超过容量"; return false; }

            var origin = GridAreaHelper.GetNearestNodeXZUnwalkable(player.Position);
            if (origin == null || !(origin.Graph is CustomGridGraph))
            { failure = "玩家舰不在海战网格上"; return false; }
            int direction;
            try { direction = CustomGraphHelper.GuessDirection(player.Forward); }
            catch { failure = "玩家舰方向无效"; return false; }

            var graph = origin.Graph as CustomGridGraph;
            HashSet<long> occupied;
            if (!TrySnapshotOccupiedStarshipCells(graph, state, null, out occupied, out failure)) return false;
            ReservePlayerForwardPassage(player, origin, direction, occupied);
            var rows = new List<string>();
            var reserved = new HashSet<long>();
            var reservedStarts = new HashSet<long>();
            foreach (var entry in entries)
            {
                var spec = entry != null ? SpaceFleetCatalog.Find(entry.BlueprintGuid) : null;
                var bp = spec != null ? ResourcesLibrary.TryGetBlueprint<BlueprintStarship>(spec.BlueprintGuid) : null;
                if (entry == null || string.IsNullOrEmpty(entry.Id) || spec == null || bp == null
                    || !ValidateBlueprint(bp, spec, out failure)) return false;
                CustomGridNodeBase anchor;
                List<CustomGridNodeBase> cells, passage;
                if (!TryFindAnchor(origin, direction, bp, occupied, reserved, reservedStarts,
                    out anchor, out cells, out passage, out failure)) return false;
                ReserveCells(reserved, cells);
                ReserveCells(reservedStarts, cells);
                ReserveCells(reserved, passage);
                SpaceFleetRuntimeProfile profile;
                if (!TryResolveProfile(entry, spec, out profile, out failure)) return false;
                rows.Add(entry.Id);
                rows.Add(spec.BlueprintGuid);
                rows.Add(entry.Name ?? spec.Name);
                rows.Add(anchor.XCoordinateInGrid.ToString(CultureInfo.InvariantCulture));
                rows.Add(anchor.ZCoordinateInGrid.ToString(CultureInfo.InvariantCulture));
                rows.Add(direction.ToString(CultureInfo.InvariantCulture));
                rows.Add(profile.BroadsideExtraShots.ToString(CultureInfo.InvariantCulture));
                rows.Add(profile.NonBroadsideExtraShots.ToString(CultureInfo.InvariantCulture));
                rows.Add(profile.BroadsideExtraRange.ToString(CultureInfo.InvariantCulture));
                rows.Add(profile.NonBroadsideExtraRange.ToString(CultureInfo.InvariantCulture));
                rows.Add(profile.ShieldPct.ToString(CultureInfo.InvariantCulture));
                rows.Add(profile.ArmourPct.ToString(CultureInfo.InvariantCulture));
                rows.Add(profile.RamPct.ToString(CultureInfo.InvariantCulture));
                if (!AppendCanonicalLoadout(entry, spec, rows, out failure)) return false;
            }

            string area = RetinueLifecycle.CurrentAreaId();
            var playerBp = player.OriginalBlueprint ?? player.Blueprint;
            if (string.IsNullOrEmpty(area) || string.IsNullOrEmpty(_encounter) || playerBp == null)
            { failure = "区域或玩家舰身份无效"; return false; }
            var header = new[]
            {
                Schema, SpawnOp, _encounter, area, player.UniqueId, playerBp.AssetGuid.ToString(),
                origin.XCoordinateInGrid.ToString(CultureInfo.InvariantCulture),
                origin.ZCoordinateInGrid.ToString(CultureInfo.InvariantCulture),
                direction.ToString(CultureInfo.InvariantCulture),
                entries.Count.ToString(CultureInfo.InvariantCulture)
            };
            payload = new string[header.Length + rows.Count];
            Array.Copy(header, payload, header.Length);
            rows.CopyTo(payload, header.Length);
            return true;
        }

        private static bool TryBuildCleanupPayload(List<StarshipEntity> escorts,
            out string[] payload, out string failure)
        {
            payload = null;
            failure = "";
            string area = RetinueLifecycle.CurrentAreaId();
            string encounter = !string.IsNullOrEmpty(_encounter) ? _encounter : NewEncounterKey(area, PlayerShip());
            escorts.Sort((a, b) => string.CompareOrdinal(a != null ? a.UniqueId : "", b != null ? b.UniqueId : ""));
            if (string.IsNullOrEmpty(area) || string.IsNullOrEmpty(encounter)
                || escorts.Count < 1 || escorts.Count > MaxShips)
            { failure = "cleanup 区域、encounter 或数量无效"; return false; }
            payload = new string[6 + escorts.Count];
            payload[0] = Schema;
            payload[1] = CleanupOp;
            payload[2] = encounter;
            payload[3] = area;
            payload[4] = PlayerShip() != null ? PlayerShip().UniqueId ?? "" : "";
            payload[5] = escorts.Count.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < escorts.Count; i++)
            {
                if (escorts[i] == null || string.IsNullOrEmpty(escorts[i].UniqueId))
                { failure = "cleanup 含空舰或空 UID"; return false; }
                payload[6 + i] = escorts[i].UniqueId;
            }
            return true;
        }

        internal static bool ValidTransactionHeader(string[] payload)
        {
            return payload != null && payload.Length >= 3 && payload[0] == Schema
                && (payload[1] == SpawnOp || payload[1] == CleanupOp)
                && !string.IsNullOrEmpty(payload[2]);
        }

        internal static bool TryPrepareSynchronizedEscort(string[] payload,
            out object opaquePlan, out string signature, out string failure)
        {
            opaquePlan = null;
            signature = "";
            failure = "";
            if (!ValidTransactionHeader(payload) || payload.Length < 6)
            { failure = "spaceescort schema 或参数无效"; return false; }
            if (payload[1] == SpawnOp) return TryPrepareSpawn(payload, out opaquePlan, out signature, out failure);
            if (payload[1] == CleanupOp) return TryPrepareCleanup(payload, out opaquePlan, out signature, out failure);
            failure = "spaceescort op 无效";
            return false;
        }

        private static bool TryPrepareSpawn(string[] payload,
            out object opaquePlan, out string signature, out string failure)
        {
            opaquePlan = null;
            signature = "";
            failure = "";
            int count, playerX, playerZ, playerDir;
            if (payload.Length < 10 || !TryInt(payload[9], out count) || count < 1 || count > MaxShips
                || !TryInt(payload[6], out playerX) || !TryInt(payload[7], out playerZ)
                || !TryDirection(payload[8], out playerDir))
            { failure = "fleet spawn 参数无效"; return false; }
            if (!string.Equals(payload[3], RetinueLifecycle.CurrentAreaId(), StringComparison.Ordinal)
                || !InCurrentSpaceCombat())
            { failure = "本端区域或海战状态未就绪"; return false; }
            if (MarkedEscorts(false).Count != 0 || PendingSpawned.Count != 0)
            { failure = "本场已经有已部署舰队"; return false; }

            var player = PlayerShip();
            if (player == null || !string.Equals(player.UniqueId, payload[4], StringComparison.Ordinal))
            { failure = "玩家舰 UID 不一致"; return false; }
            var playerBp = player.OriginalBlueprint ?? player.Blueprint;
            var playerNode = GridAreaHelper.GetNearestNodeXZUnwalkable(player.Position);
            if (playerBp == null || !string.Equals(playerBp.AssetGuid.ToString(), payload[5], StringComparison.Ordinal)
                || playerNode == null || playerNode.XCoordinateInGrid != playerX
                || playerNode.ZCoordinateInGrid != playerZ
                || CustomGraphHelper.GuessDirection(player.Forward) != playerDir)
            { failure = "玩家舰蓝图、位置或方向不一致"; return false; }

            var state = MainState();
            var playerFaction = Game.Instance != null && Game.Instance.BlueprintRoot != null
                ? Game.Instance.BlueprintRoot.PlayerFaction : null;
            if (state == null || playerFaction == null) { failure = "MainState 或 PlayerFaction 不可用"; return false; }
            var plan = new FleetPlan
            {
                Op = SpawnOp, Encounter = payload[2], Area = payload[3], PlayerUid = payload[4],
                Player = player, PlayerX = playerX, PlayerZ = playerZ, Direction = playerDir,
                PlayerFaction = playerFaction, State = state
            };
            var graph = playerNode.Graph as CustomGridGraph;
            HashSet<long> occupied;
            if (!TrySnapshotOccupiedStarshipCells(graph, state, null, out occupied, out failure)) return false;
            ReservePlayerForwardPassage(player, playerNode, playerDir, occupied);
            var reserved = new HashSet<long>();
            var reservedStarts = new HashSet<long>();
            var entryIds = new HashSet<string>(StringComparer.Ordinal);
            int offset = 10;
            for (int i = 0; i < count; i++)
            {
                if (offset + 14 > payload.Length)
                { failure = "fleet spawn 记录被截断"; return false; }
                string entryId = payload[offset];
                var spec = SpaceFleetCatalog.Find(payload[offset + 1]);
                int ax, az, dir, bs, ns, br, nr, shield, armour, ram, loadoutCount;
                if (string.IsNullOrEmpty(entryId) || !entryIds.Add(entryId) || spec == null
                    || !TryInt(payload[offset + 3], out ax) || !TryInt(payload[offset + 4], out az)
                    || !TryDirection(payload[offset + 5], out dir) || dir != playerDir
                    || !TryInt(payload[offset + 6], out bs) || !TryInt(payload[offset + 7], out ns)
                    || !TryInt(payload[offset + 8], out br) || !TryInt(payload[offset + 9], out nr)
                    || !TryInt(payload[offset + 10], out shield)
                    || !TryInt(payload[offset + 11], out armour)
                    || !TryInt(payload[offset + 12], out ram)
                    || shield < 0 || shield > 1000 || armour < 0 || armour > 1000
                    || ram < 0 || ram > 1000
                    || !TryInt(payload[offset + 13], out loadoutCount)
                    || loadoutCount < 0 || loadoutCount > spec.Slots.Length
                    || offset + 14 + loadoutCount * 2 > payload.Length)
                { failure = "舰队条目、强化、loadout 或锚点无效"; return false; }
                var parsed = new SpaceFleetEntry
                {
                    Id = entryId, BlueprintGuid = spec.BlueprintGuid, Name = payload[offset + 2],
                    BroadsideExtraShots = bs, NonBroadsideExtraShots = ns,
                    BroadsideExtraRange = br, NonBroadsideExtraRange = nr
                };
                List<SpaceFleetLoadoutChoice> parsedLoadout;
                if (!TryParseLoadout(spec, payload, offset + 14, loadoutCount,
                    out parsedLoadout, out failure)) return false;
                parsed.Loadout.AddRange(parsedLoadout);
                if (bs < 0 || bs > SpaceFleetCatalog.MaxExtraShots
                    || ns < 0 || ns > SpaceFleetCatalog.MaxExtraShots
                    || br < 0 || br > SpaceFleetCatalog.MaxExtraRange
                    || nr < 0 || nr > SpaceFleetCatalog.MaxExtraRange)
                { failure = "逐舰强化超出协议硬上限"; return false; }
                var profile = new SpaceFleetRuntimeProfile
                {
                    EntryId = entryId, HullGuid = spec.BlueprintGuid,
                    BroadsideExtraShots = bs, NonBroadsideExtraShots = ns,
                    BroadsideExtraRange = br, NonBroadsideExtraRange = nr,
                    ShieldPct = shield, ArmourPct = armour, RamPct = ram
                };

                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintStarship>(spec.BlueprintGuid);
                if (bp == null || !ValidateBlueprint(bp, spec, out failure)) return false;
                var anchor = graph != null ? graph.GetNode(ax, az) : null;
                List<CustomGridNodeBase> cells, passage;
                if (!TryValidateAnchorClearance(anchor, dir, bp, occupied, reserved,
                    reservedStarts, out cells, out passage, out failure)) return false;
                ReserveCells(reserved, cells);
                ReserveCells(reservedStarts, cells);
                ReserveCells(reserved, passage);
                Vector3 facing = CustomGraphHelper.GetVector3Direction(dir);
                var rect = SizePathfindingHelper.GetRectForSize(bp.Size);
                var row = new PlannedShip
                {
                    EntryId = entryId, Name = payload[offset + 2], Spec = spec, Blueprint = bp,
                    Anchor = anchor, AnchorX = ax, AnchorZ = az, Direction = dir, Profile = profile,
                    SpawnPosition = anchor.Vector3Position + SizePathfindingHelper.GetSizePositionOffset(rect, facing),
                    Rotation = Quaternion.Euler(0f, CustomGraphHelper.GetOrientationFromDirection(dir), 0f)
                };
                row.Loadout.AddRange(parsed.Loadout);
                row.Cells.AddRange(cells);
                plan.Ships.Add(row);
                offset += 14 + loadoutCount * 2;
            }
            if (offset != payload.Length)
            { failure = "fleet spawn 含尾随字段"; return false; }
            signature = BuildSignature(payload, plan);
            opaquePlan = plan;
            if (string.IsNullOrEmpty(signature)) return false;
            _preparedHostSpawnEncounter = plan.Encounter;
            return true;
        }

        private static bool TryPrepareCleanup(string[] payload,
            out object opaquePlan, out string signature, out string failure)
        {
            opaquePlan = null;
            signature = "";
            failure = "";
            int count;
            if (!TryInt(payload[5], out count) || count < 1 || count > MaxShips
                || payload.Length != 6 + count
                || !string.Equals(payload[3], RetinueLifecycle.CurrentAreaId(), StringComparison.Ordinal))
            { failure = "fleet cleanup 参数无效"; return false; }
            var actual = MarkedEscorts(false);
            foreach (var pending in PendingSpawned)
                if (pending != null && !pending.IsDisposed && !pending.WillBeDestroyed && !actual.Contains(pending)) actual.Add(pending);
            actual.Sort((a, b) => string.CompareOrdinal(a.UniqueId, b.UniqueId));
            if (actual.Count != count) { failure = "已部署舰队数量不一致"; return false; }
            var plan = new FleetPlan
            {
                Op = CleanupOp, Encounter = payload[2], Area = payload[3],
                PlayerUid = payload[4], Player = PlayerShip(), State = MainState()
            };
            for (int i = 0; i < count; i++)
            {
                if (!string.Equals(actual[i].UniqueId, payload[6 + i], StringComparison.Ordinal)
                    || !IsExpectedEscort(actual[i]))
                { failure = "cleanup 舰队 UID 或身份不一致"; return false; }
                plan.Escorts.Add(actual[i]);
                plan.EscortUids.Add(payload[6 + i]);
            }
            if (plan.State == null) { failure = "cleanup MainState 为空"; return false; }
            signature = BuildSignature(payload, plan);
            opaquePlan = plan;
            return !string.IsNullOrEmpty(signature);
        }

        internal static bool CanCommitSynchronizedEscort(object opaquePlan, out string failure)
        { return CanCommit(opaquePlan, true, out failure); }

        internal static bool CanCommitSynchronizedEscortInHandler(object opaquePlan, out string failure)
        { return CanCommit(opaquePlan, false, out failure); }

        private static bool CanCommit(object opaquePlan, bool verifyFootprints, out string failure)
        {
            failure = "";
            var plan = opaquePlan as FleetPlan;
            if (plan == null || !string.Equals(plan.Area, RetinueLifecycle.CurrentAreaId(), StringComparison.Ordinal)
                || !ReferenceEquals(plan.State, MainState()))
            { failure = "舰队计划或区域已变化"; return false; }
            if (plan.Op == SpawnOp)
            {
                if (verifyFootprints && (_phase != Phase.SpawnPending
                    || !string.Equals(plan.Encounter, _encounter, StringComparison.Ordinal)))
                { failure = "spawn 事务已不属于当前海战"; return false; }
                if (!verifyFootprints && !string.Equals(plan.Encounter,
                    _preparedHostSpawnEncounter, StringComparison.Ordinal))
                { failure = "spawn commit 未通过本端当前 prepare"; return false; }
                // 本机开关只由单机/房主在发出 commit 前读取；加入方必须服从房主 payload。
                // autocommit handler 若再读各端设置，会让「房主开、加入方关」直接分叉。
                if (verifyFootprints
                    && (!Main.Enabled || Main.Settings == null || !Main.Settings.SpaceEscortEnabled))
                { failure = "海战舰队已停用"; return false; }
                if (_spawnCancelled)
                { failure = "spawn 事务已被本端战斗结束事件作废"; return false; }
                var player = PlayerShip();
                var node = player != null ? GridAreaHelper.GetNearestNodeXZUnwalkable(player.Position) : null;
                if (!InCurrentSpaceCombat() || player == null || !ReferenceEquals(player, plan.Player)
                    || node == null || node.XCoordinateInGrid != plan.PlayerX || node.ZCoordinateInGrid != plan.PlayerZ
                    || CustomGraphHelper.GuessDirection(player.Forward) != plan.Direction
                    || MarkedEscorts(false).Count != 0 || PendingSpawned.Count != 0)
                { failure = "spawn 提交前玩家舰或已部署状态变化"; return false; }
                if (!verifyFootprints) return true;
                var graph = node.Graph as CustomGridGraph;
                HashSet<long> occupied;
                if (!TrySnapshotOccupiedStarshipCells(graph, plan.State, null, out occupied, out failure)) return false;
                ReservePlayerForwardPassage(player, node, plan.Direction, occupied);
                var reserved = new HashSet<long>();
                var reservedStarts = new HashSet<long>();
                foreach (var row in plan.Ships)
                {
                    List<CustomGridNodeBase> cells, passage;
                    if (!TryValidateAnchorClearance(row.Anchor, row.Direction, row.Blueprint,
                        occupied, reserved, reservedStarts, out cells, out passage, out failure)) return false;
                    ReserveCells(reserved, cells);
                    ReserveCells(reservedStarts, cells);
                    ReserveCells(reserved, passage);
                }
                return true;
            }
            if (plan.Op == CleanupOp)
            {
                if (_phase != Phase.CleanupPending || InCurrentSpaceCombat())
                { failure = "cleanup 已被新的海战状态作废"; return false; }
                var actual = MarkedEscorts(false);
                foreach (var pending in PendingSpawned)
                    if (pending != null && !pending.IsDisposed && !pending.WillBeDestroyed && !actual.Contains(pending)) actual.Add(pending);
                actual.Sort((a, b) => string.CompareOrdinal(a.UniqueId, b.UniqueId));
                if (actual.Count != plan.EscortUids.Count) { failure = "cleanup 前舰队数量变化"; return false; }
                for (int i = 0; i < actual.Count; i++)
                    if (!string.Equals(actual[i].UniqueId, plan.EscortUids[i], StringComparison.Ordinal)
                        || !IsExpectedEscort(actual[i]))
                    { failure = "cleanup 前舰队身份变化"; return false; }
                return true;
            }
            failure = "未知舰队操作";
            return false;
        }

        internal static void CommitSynchronizedEscort(object opaquePlan)
        {
            var plan = opaquePlan as FleetPlan;
            if (plan == null) throw new InvalidOperationException("舰队计划类型无效");
            if (plan.Op == SpawnOp) { CommitSpawn(plan); return; }
            if (plan.Op == CleanupOp) { CommitCleanup(plan); return; }
            throw new InvalidOperationException("未知舰队操作 " + plan.Op);
        }

        private static void CommitSpawn(FleetPlan plan)
        {
            if (MarkedEscorts(false).Count != 0 || PendingSpawned.Count != 0) return;
            var created = new List<StarshipEntity>(plan.Ships.Count);
            try
            {
                foreach (var row in plan.Ships)
                {
                    var ship = Game.Instance.EntitySpawner.SpawnUnit(row.Blueprint, row.SpawnPosition,
                        row.Rotation, plan.State) as StarshipEntity;
                    if (ship == null) throw new InvalidOperationException("SpawnUnit 未返回 StarshipEntity");
                    created.Add(ship);
                    if (row.Profile == null)
                        throw new InvalidOperationException("舰队 runtime profile 为空 " + row.EntryId);
                    PendingSpawned.Add(ship);
                    KnownEscortSources.GetValue(ship, delegate { return WeakTableMarker; });
                    RegisterRuntimeProfile(ship, row.Profile);

                    var desc = ship.GetOrCreate<PartUnitDescription>();
                    desc.CustomPetName = BuildMarker(row.Profile);
                    if (!string.IsNullOrEmpty(row.Name)) desc.SetName(row.Name);
                    ApplyLoadout(ship, row);
                    ReloadProfileWeapons(ship);
                    // PlayerFaction 让 JoinCombat 不再把 mechanics 锚点当 view 中心二次扣偏移；
                    // 独立 UID CombatGroup + ForceAIControl 保证它仍是 AI 舰，不进玩家手控组。
                    ship.Faction.Set(plan.PlayerFaction);
                    RefreshNavigationFaction(ship);
                    ApplyRuntimeControl(ship);
                    if ((ship.Position - row.Anchor.Vector3Position).sqrMagnitude > 0.01f)
                        throw new InvalidOperationException("舰队锚点与计划不一致 " + row.EntryId);
                }
                _phase = Phase.Active;
                _encounter = plan.Encounter;
                _area = plan.Area;
                Main.Log("[海战舰队] 已部署 " + plan.Ships.Count + " 艘舰。"
                    + string.Join("，", plan.Ships.ConvertAll(x => x.Name + "@(" + x.AnchorX + "," + x.AnchorZ + ")").ToArray()));
            }
            catch
            {
                for (int i = created.Count - 1; i >= 0; i--)
                    try
                    {
                        var ship = created[i];
                        if (ship != null && !ship.IsDisposed && !ship.WillBeDestroyed)
                            Game.Instance.EntityDestroyer.Destroy(ship);
                    }
                    catch { }
                PendingSpawned.Clear();
                _phase = Phase.Idle;
                _encounter = "";
                throw;
            }
        }

        private static void CommitCleanup(FleetPlan plan)
        {
            int queued = 0;
            int failed = 0;
            foreach (var ship in plan.Escorts)
            {
                if (ship == null || ship.IsDisposed || ship.WillBeDestroyed || !IsExpectedEscort(ship)) continue;
                try
                {
                    Game.Instance.EntityDestroyer.Destroy(ship);
                    queued++;
                }
                catch (Exception e)
                {
                    failed++;
                    Main.LogError("[海战舰队] 收回 " + ship.UniqueId + " 失败: " + e.Message);
                }
            }
            Main.Log("[海战舰队] 战后已排队收回 " + queued + " 艘舰。"
                + (failed > 0 ? "失败 " + failed + " 艘。" : ""));
            if (failed > 0) ScheduleCleanupRetry("部分舰船未能加入销毁队列");
            else ScheduleCleanupVerification();
        }

        private static void ScheduleCleanupVerification()
        {
            if (_phase != Phase.CleanupPending || _workScheduled) return;
            _workScheduled = true;
            int generation = _generation;
            Deferred.NextFrames(180, delegate
            {
                if (generation != _generation) return;
                _workScheduled = false;
                if (_phase != Phase.CleanupPending || InCurrentSpaceCombat()) return;
                if (MarkedEscorts(true).Count == 0)
                {
                    FinishCleanup();
                    return;
                }
                ScheduleCleanupRetry("销毁队列处理后仍有舰队实体存活");
            });
        }

        private static void FinishCleanup()
        {
            PendingSpawned.Clear();
            _cleanupRetryCount = 0;
            _phase = Phase.Idle;
            _encounter = "";
        }

        // ---------------------------------------------------------------- 放置 / 蓝图 / 身份

        private static bool TryFindAnchor(CustomGridNodeBase origin, int direction,
            BlueprintStarship bp, HashSet<long> occupied, HashSet<long> reserved,
            HashSet<long> reservedStarts, out CustomGridNodeBase anchor, out List<CustomGridNodeBase> cells,
            out List<CustomGridNodeBase> passage, out string failure)
        {
            anchor = null;
            cells = null;
            passage = null;
            failure = "";
            if (origin == null || bp == null) { failure = "玩家锚点或舰船蓝图无效"; return false; }

            IntRect rect = SizePathfindingHelper.GetRectForSize(bp.Size);
            int preferredRadius = Math.Max(MinRadius, rect.Width + 2);
            int[] order =
            {
                TurnLeft(direction, 3), TurnRight(direction, 3),
                TurnLeft(direction, 2), TurnRight(direction, 2),
                CustomGraphHelper.OppositeDirections[direction]
            };

            if (TryFindAlongFormationRays(origin, direction, bp, occupied, reserved,
                order, preferredRadius, MaxRadius, true, reservedStarts, out anchor, out cells, out passage)) return true;
            if (TryFindAlongFormationRays(origin, direction, bp, occupied, reserved,
                order, preferredRadius, MaxRadius, false, reservedStarts, out anchor, out cells, out passage)) return true;
            if (TryFindOnRings(origin, direction, bp, occupied, reserved,
                MinRadius, MaxRadius, reservedStarts, out anchor, out cells, out passage)) return true;

            failure = "没有容纳" + ShipDialog.SizeName(bp.Size) + "并保留第一航步的合法落点";
            return false;
        }

        private static bool TryFindAlongFormationRays(CustomGridNodeBase origin, int direction,
            BlueprintStarship bp, HashSet<long> occupied, HashSet<long> reserved,
            int[] order, int minRadius, int maxRadius, bool requireMoat,
            HashSet<long> reservedStarts, out CustomGridNodeBase anchor, out List<CustomGridNodeBase> cells,
            out List<CustomGridNodeBase> passage)
        {
            anchor = null;
            cells = null;
            passage = null;
            for (int radius = minRadius; radius <= maxRadius; radius++)
                for (int i = 0; i < order.Length; i++)
                {
                    var candidate = Offset(origin, order[i], radius);
                    List<CustomGridNodeBase> found, firstStep;
                    string why;
                    if (!TryValidateAnchorClearance(candidate, direction, bp, occupied, reserved,
                        reservedStarts, out found, out firstStep, out why)) continue;
                    if (requireMoat && !HasChebyshevMoat(found, occupied, reserved)) continue;
                    anchor = candidate;
                    cells = found;
                    passage = firstStep;
                    return true;
                }
            return false;
        }

        private static bool TryFindOnRings(CustomGridNodeBase origin, int direction,
            BlueprintStarship bp, HashSet<long> occupied, HashSet<long> reserved,
            int minRadius, int maxRadius, HashSet<long> reservedStarts,
            out CustomGridNodeBase anchor, out List<CustomGridNodeBase> cells,
            out List<CustomGridNodeBase> passage)
        {
            anchor = null;
            cells = null;
            passage = null;
            var graph = origin != null ? origin.Graph as CustomGridGraph : null;
            if (graph == null) return false;
            for (int radius = minRadius; radius <= maxRadius; radius++)
                for (int dz = -radius; dz <= radius; dz++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius) continue;
                        var candidate = graph.GetNode(origin.XCoordinateInGrid + dx,
                            origin.ZCoordinateInGrid + dz);
                        List<CustomGridNodeBase> found, firstStep;
                        string why;
                        if (!TryValidateAnchorClearance(candidate, direction, bp, occupied, reserved,
                            reservedStarts, out found, out firstStep, out why)) continue;
                        anchor = candidate;
                        cells = found;
                        passage = firstStep;
                        return true;
                    }
            return false;
        }

        private static bool TryValidateAnchorClearance(CustomGridNodeBase anchor, int direction,
            BlueprintStarship bp, HashSet<long> occupied, HashSet<long> reserved,
            HashSet<long> reservedStarts, out List<CustomGridNodeBase> cells,
            out List<CustomGridNodeBase> passage,
            out string failure)
        {
            return TryValidateAnchorClearance(anchor, direction, bp, occupied, reserved,
                reservedStarts, null, out cells, out passage, out failure);
        }

        private static bool TryValidateAnchorClearance(CustomGridNodeBase anchor, int direction,
            BlueprintStarship bp, HashSet<long> occupied, HashSet<long> reserved,
            HashSet<long> reservedStarts, WarhammerSingleNodeBlocker exceptBlocker,
            out List<CustomGridNodeBase> cells,
            out List<CustomGridNodeBase> passage, out string failure)
        {
            cells = null;
            passage = new List<CustomGridNodeBase>();
            if (!FootprintFree(anchor, direction, bp, occupied, reserved, exceptBlocker,
                out cells, out failure)) return false;

            IntRect rect = SizePathfindingHelper.GetRectForSize(bp.Size);
            var step = anchor;
            var source = cells;
            for (int i = 0; i < rect.Width; i++)
            {
                step = step != null ? step.GetNeighbourAlongDirection(direction) : null;
                if (step == null || !GridAreaHelper.AllNodesConnectedToNeighbours(rect, step))
                { failure = "第一航步网格内部未连接"; return false; }
                foreach (var node in source)
                    if (node == null || !GridAreaHelper.HasConnectionInDirection(node, direction, null))
                    { failure = "第一航步跨越未连接网格"; return false; }
                List<CustomGridNodeBase> swept;
                if (!FootprintFree(step, direction, bp, occupied, reservedStarts, exceptBlocker,
                    out swept, out failure))
                { failure = "第一航步不可用：" + failure; return false; }
                foreach (var node in swept)
                    if (!passage.Contains(node)) passage.Add(node);
                source = swept;
            }
            return true;
        }

        private static bool FootprintFree(CustomGridNodeBase anchor, int direction,
            BlueprintStarship bp, HashSet<long> occupied, HashSet<long> reserved,
            WarhammerSingleNodeBlocker exceptBlocker, out List<CustomGridNodeBase> cells,
            out string failure)
        {
            cells = new List<CustomGridNodeBase>();
            failure = "";
            if (anchor == null || bp == null) { failure = "落点或蓝图无效"; return false; }
            IntRect rect = SizePathfindingHelper.GetRectForSize(bp.Size);
            using (NodeList nodes = GridAreaHelper.GetNodes(anchor, rect, direction))
                foreach (var node in nodes) if (node != null) cells.Add(node);
            int expected = GridAreaHelper.GetOffsets(rect, direction).Count;
            if (cells.Count != expected) { failure = "footprint 越出网格"; return false; }
            foreach (var node in cells)
            {
                bool blocked = exceptBlocker == null
                    ? WarhammerBlockManager.Instance.NodeContainsAny(node)
                    : WarhammerBlockManager.Instance.NodeContainsAnyExcept(node, exceptBlocker);
                if (!node.Walkable || blocked
                    || (occupied != null && occupied.Contains(CellKey(node)))
                    || (reserved != null && reserved.Contains(CellKey(node))))
                { failure = "footprint 不可用"; return false; }
            }
            if (!GridAreaHelper.AllNodesConnectedToNeighbours(rect, anchor)
                || !FootprintIsConnected(cells))
            { failure = "footprint 跨越未连接网格"; return false; }
            return true;
        }

        private static bool FootprintIsConnected(List<CustomGridNodeBase> cells)
        {
            if (cells == null || cells.Count == 0) return false;
            var byKey = new Dictionary<long, CustomGridNodeBase>();
            foreach (var cell in cells)
                if (cell != null) byKey[CellKey(cell)] = cell;
            if (byKey.Count != cells.Count) return false;

            var seen = new HashSet<long>();
            var queue = new Queue<CustomGridNodeBase>();
            queue.Enqueue(cells[0]);
            seen.Add(CellKey(cells[0]));
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                for (int direction = 0; direction < 8; direction++)
                {
                    var next = current.GetNeighbourAlongDirection(direction);
                    CustomGridNodeBase member;
                    if (next == null || !byKey.TryGetValue(CellKey(next), out member)
                        || !seen.Add(CellKey(member))) continue;
                    queue.Enqueue(member);
                }
            }
            return seen.Count == cells.Count;
        }

        private static bool HasChebyshevMoat(List<CustomGridNodeBase> cells,
            HashSet<long> occupied, HashSet<long> reserved)
        {
            if (cells == null) return false;
            var graph = cells.Count > 0 ? cells[0].Graph as CustomGridGraph : null;
            if (graph == null) return false;
            foreach (var cell in cells)
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var near = graph.GetNode(cell.XCoordinateInGrid + dx,
                            cell.ZCoordinateInGrid + dz);
                        if (near == null) continue;
                        long key = CellKey(near);
                        if ((occupied != null && occupied.Contains(key))
                            || (reserved != null && reserved.Contains(key))) return false;
                    }
            return true;
        }

        private static void ReserveCells(HashSet<long> reserved, List<CustomGridNodeBase> cells)
        {
            if (reserved == null || cells == null) return;
            foreach (var cell in cells) if (cell != null) reserved.Add(CellKey(cell));
        }

        private static bool TrySnapshotOccupiedStarshipCells(CustomGridGraph graph,
            SceneEntitiesState state, StarshipEntity except, out HashSet<long> occupied,
            out string failure)
        {
            occupied = new HashSet<long>();
            failure = "";
            try
            {
                var game = Game.Instance;
                if (graph == null || game == null || game.State == null || game.EntitySpawner == null)
                { failure = "无法读取当前海战网格或实体状态"; return false; }
                foreach (var unit in game.State.AllUnits)
                    AddShipOccupiedCells(unit as StarshipEntity, graph, except, occupied, true);
                foreach (var entry in game.EntitySpawner.CreationQueue)
                    if (ReferenceEquals(entry.State, state))
                        AddShipOccupiedCells(entry.Entity as StarshipEntity, graph, except, occupied, false);
                return true;
            }
            catch (Exception e)
            { failure = "读取当前舰船占位失败：" + e.Message; return false; }
        }

        private static void AddShipOccupiedCells(StarshipEntity ship, CustomGridGraph graph,
            StarshipEntity except, HashSet<long> occupied, bool requireInGame)
        {
            if (ship == null || ReferenceEquals(ship, except) || ship.IsDisposed
                || ship.WillBeDestroyed || (requireInGame && !ship.IsInGame)) return;
            using (NodeList nodes = ship.GetOccupiedNodes())
                foreach (var node in nodes)
                    if (node != null && ReferenceEquals(node.Graph, graph))
                        occupied.Add(CellKey(node));
        }

        private static void ReservePlayerForwardPassage(StarshipEntity player,
            CustomGridNodeBase anchor, int direction, HashSet<long> occupied)
        {
            if (player == null || anchor == null || occupied == null) return;
            var step = anchor;
            for (int i = 0; i < player.SizeRect.Width; i++)
            {
                step = step != null ? step.GetNeighbourAlongDirection(direction) : null;
                if (step == null) break;
                using (NodeList nodes = GridAreaHelper.GetNodes(step, player.SizeRect, direction))
                    foreach (var node in nodes) if (node != null) occupied.Add(CellKey(node));
            }
        }

        private static long CellKey(CustomGridNodeBase node)
        { return ((long)node.XCoordinateInGrid << 32) ^ (uint)node.ZCoordinateInGrid; }

        private static CustomGridNodeBase Offset(CustomGridNodeBase origin, int direction, int cells)
        {
            var graph = origin != null ? origin.Graph as CustomGridGraph : null;
            if (graph == null) return null;
            int[] dx = { 0, 1, 0, -1, 1, 1, -1, -1 };
            int[] dz = { -1, 0, 1, 0, -1, 1, 1, -1 };
            return graph.GetNode(origin.XCoordinateInGrid + dx[direction] * cells,
                                 origin.ZCoordinateInGrid + dz[direction] * cells);
        }

        private static int TurnLeft(int direction, int steps)
        { while (steps-- > 0) direction = CustomGraphHelper.LeftNeighbourDirection[direction]; return direction; }
        private static int TurnRight(int direction, int steps)
        { while (steps-- > 0) direction = CustomGraphHelper.RightNeighbourDirection[direction]; return direction; }

        private static int CapacityOf(List<SpaceFleetEntry> entries)
        {
            int used = 0;
            foreach (var entry in entries)
            {
                var hull = entry != null ? SpaceFleetCatalog.Find(entry.BlueprintGuid) : null;
                if (hull == null) return FleetCapacity + 1;
                used += hull.Capacity;
            }
            return used;
        }

        internal static bool ValidateBlueprint(BlueprintStarship bp, out string failure)
        { return ValidateBlueprint(bp, bp != null ? SpaceFleetCatalog.Find(bp.AssetGuid.ToString()) : null, out failure); }

        private static bool ValidateBlueprint(BlueprintStarship bp, SpaceFleetHullDef hull, out string failure)
        {
            failure = "";
            if (bp == null || hull == null || !string.Equals(bp.AssetGuid.ToString(),
                hull.BlueprintGuid, StringComparison.OrdinalIgnoreCase))
            { failure = L.T("蓝图不在允许规格表"); return false; }
            if (hull.NativeChildShipGuids != null && hull.NativeChildShipGuids.Length != 0)
            {
                if (!SpaceFleetNativeChildPatch.IsReady())
                { failure = L.T("原生舰载机支持未启用"); return false; }
                foreach (string craftGuid in hull.NativeChildShipGuids)
                {
                    var craft = ResourcesLibrary.TryGetBlueprint<BlueprintStarship>(craftGuid);
                    if (craft == null || !(craft.DefaultBrain is BlueprintStarshipBrain)
                        || craft.Prefab == null || craft.Prefab.Load() == null)
                    { failure = L.T("原装舰载机的蓝图、AI 或模型不可用"); return false; }
                }
            }
            if (bp.Size != hull.Size || bp.Faction == null
                || !string.Equals(bp.Faction.AssetGuid.ToString(), hull.OriginalFactionGuid, StringComparison.OrdinalIgnoreCase)
                || bp.DefaultBrain == null
                || !string.Equals(bp.DefaultBrain.AssetGuid.ToString(), hull.BrainGuid, StringComparison.OrdinalIgnoreCase)
                || bp.Prefab == null || bp.Prefab.AssetId != hull.PrefabAssetId || bp.Prefab.Load() == null)
            { failure = L.F("{0}尺寸 / faction / brain / prefab 不匹配", SpaceFleetCatalog.DisplayName(hull)); return false; }

            if (!SpaceFleetCatalog.ValidateOriginalComponents(bp, hull, out failure)) return false;
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slot in hull.Slots)
                if (slot != null && slot.IsWeapon)
                    expected[slot.Key] = slot.OriginalItemGuid;
            if (bp.Weapons == null || bp.Weapons.Count != expected.Count)
            { failure = L.F("{0}武器槽数量不匹配", SpaceFleetCatalog.DisplayName(hull)); return false; }

            var indices = new Dictionary<WeaponSlotType, int>();
            foreach (var slot in bp.Weapons)
            {
                if (slot == null) { failure = L.F("{0}含空武器槽", SpaceFleetCatalog.DisplayName(hull)); return false; }
                int index; indices.TryGetValue(slot.Type, out index); indices[slot.Type] = index + 1;
                string key = "weapon:" + slot.Type + ":" + index;
                var weapon = slot.Weapon != null ? slot.Weapon.Get() : null;
                string original;
                if (weapon == null || !expected.TryGetValue(key, out original)
                    || !string.Equals(weapon.AssetGuid.ToString(), original, StringComparison.OrdinalIgnoreCase))
                { failure = L.F("{0}武器槽 {1} 不匹配", SpaceFleetCatalog.DisplayName(hull), key); return false; }
                bool recognized = false;
                try
                {
                    foreach (var ability in weapon.Abilities)
                        if (ability != null && ((BlueprintStarshipBrain)bp.DefaultBrain)
                            .GetAbilitySettings(ability) != null)
                        { recognized = true; break; }
                }
                catch { }
                if (!recognized)
                { failure = L.F("{0}原装武器槽 {1} 没有 AI 逻辑", SpaceFleetCatalog.DisplayName(hull), key); return false; }
            }
            return true;
        }

        internal static string ValidateAllBlueprints(out int passed, out int total)
        {
            passed = 0;
            var hulls = SpaceFleetCatalog.All();
            total = hulls.Length + 1;
            var details = new List<string>();
            foreach (var hull in hulls)
            {
                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintStarship>(hull.BlueprintGuid);
                string failure = "";
                string displayName = SpaceFleetCatalog.DisplayName(hull);
                if (bp != null && ValidateBlueprint(bp, hull, out failure)) { passed++; details.Add(displayName + " ✓"); }
                else details.Add(displayName + "：" + (string.IsNullOrEmpty(failure) ? "不可用" : failure));
            }
            if (Game.Instance != null && Game.Instance.BlueprintRoot != null
                && Game.Instance.BlueprintRoot.PlayerFaction != null)
            { passed++; details.Add("PlayerFaction ✓"); }
            else details.Add("PlayerFaction 不可用");
            return string.Join("；", details.ToArray());
        }

        internal static int ProfileRestoreFailures { get { return _profileRestoreFailures; } }

        internal static bool TryGetRuntimeProfile(StarshipEntity ship,
            out SpaceFleetRuntimeProfile profile)
        {
            profile = null;
            return ship != null && RuntimeProfiles.TryGetValue(ship, out profile)
                && profile != null;
        }

        private static void RegisterRuntimeProfile(StarshipEntity ship,
            SpaceFleetRuntimeProfile profile)
        {
            if (ship == null || profile == null) return;
            RuntimeProfiles.Remove(ship);
            RuntimeProfiles.Add(ship, profile);
        }

        private static Dictionary<string, ItemSlot> PhysicalSlots(StarshipEntity ship,
            SpaceFleetHullDef hull)
        {
            if (ship == null || hull == null || ship.Hull == null || ship.Hull.HullSlots == null)
                throw new InvalidOperationException("舰装实体槽不可用");
            var result = new Dictionary<string, ItemSlot>(StringComparer.Ordinal);
            var indices = new Dictionary<WeaponSlotType, int>();
            foreach (WeaponSlot physical in ship.Hull.WeaponSlots)
            {
                if (physical == null) throw new InvalidOperationException("存在空武器槽实体");
                int index; indices.TryGetValue(physical.Type, out index);
                indices[physical.Type] = index + 1;
                string key = "weapon:" + physical.Type + ":" + index;
                if (SpaceFleetCatalog.FindSlot(hull, key) != null) result.Add(key, physical);
            }
            foreach (var slot in hull.Slots)
            {
                if (slot != null && !slot.IsWeapon)
                {
                    var physical = SpaceFleetCatalog.ComponentSlot(ship.Hull.HullSlots, slot);
                    if (physical == null) throw new InvalidOperationException("主组件槽不可用 " + slot.Key);
                    result.Add(slot.Key, physical);
                }
            }
            if (result.Count != hull.Slots.Length)
                throw new InvalidOperationException("实际开放槽位数量与目录不符");
            return result;
        }

        private static bool TryRestoreProfileLoadout(StarshipEntity ship,
            SpaceFleetRuntimeProfile profile, out string failure)
        {
            failure = "";
            try
            {
                var hull = SpaceFleetCatalog.Find(profile.HullGuid);
                var physical = PhysicalSlots(ship, hull);
                profile.AbilityRoles.Clear();
                // 原版 HullSlots / ItemSlot.m_ItemRef 已保存实际舰装及其实体。
                // 冷读档只校验并恢复武器 AI role，不重插、不重建，也不读当前 Settings 的 loadout。
                foreach (var slot in hull.Slots)
                {
                    var entity = physical[slot.Key].MaybeItem;
                    string guid = entity != null ? entity.Blueprint.AssetGuid.ToString() : "";
                    BlueprintStarshipItem item;
                    SpaceFleetWeaponDef weapon;
                    if (!SpaceFleetCatalog.TryResolveItem(hull, slot.Key, guid, out item, out weapon)
                        || (entity != null && !ReferenceEquals(entity.HoldingSlot, physical[slot.Key])))
                    { failure = "已保存舰装不适配 " + slot.Key; return false; }
                    if (weapon != null) profile.AbilityRoles[weapon.AbilityGuid] = weapon.Role;
                }
                return true;
            }
            catch (Exception e) { failure = e.Message; return false; }
        }

        private static void ApplyLoadout(StarshipEntity ship, PlannedShip row)
        {
            if (ship == null || row == null || row.Loadout.Count == 0) return;
            var byKey = PhysicalSlots(ship, row.Spec);
            foreach (var choice in row.Loadout)
            {
                ItemSlot physical;
                BlueprintStarshipItem bp;
                SpaceFleetWeaponDef weaponDef;
                if (choice == null || !byKey.TryGetValue(choice.SlotKey, out physical)
                    || !SpaceFleetCatalog.TryResolveItem(row.Spec, choice.SlotKey,
                        choice.ItemGuid, out bp, out weaponDef) || bp == null)
                    throw new InvalidOperationException("无法应用舰装 " + (choice != null ? choice.SlotKey : "<null>"));
                var previous = physical.MaybeItem;
                // 新建原版实体；不取用玩家库存。InsertItem 将其加入目标舰自己的库存。
                var created = bp.CreateEntity();
                if (created == null) throw new InvalidOperationException("舰装实例创建失败 " + choice.ItemGuid);
                try
                {
                    // CreateEntity 可能因 DLC 限制改成另一蓝图，必须在安装前拒绝。
                    if (!string.Equals(created.Blueprint.AssetGuid.ToString(), choice.ItemGuid,
                        StringComparison.OrdinalIgnoreCase) || created.Collection != null
                        || !ReferenceEquals(physical.Owner, ship))
                        throw new InvalidOperationException("舰装实体蓝图或归属不一致 " + choice.SlotKey);
                    using (ContextData<ItemSlot.IgnoreLock>.Request())
                    {
                        if (!physical.CanInsertItem(created))
                            throw new InvalidOperationException("实际槽位拒绝舰装 " + choice.SlotKey);
                        physical.InsertItem(created, true);
                    }
                    if (!ReferenceEquals(physical.MaybeItem, created)
                        || !ReferenceEquals(created.HoldingSlot, physical))
                        throw new InvalidOperationException("舰装插槽确认失败 " + choice.SlotKey);
                    if (previous != null && !ReferenceEquals(previous, created))
                    {
                        if (previous.Collection != null) previous.Collection.Remove(previous);
                        previous.Dispose();
                    }
                    if (weaponDef != null)
                        row.Profile.AbilityRoles[weaponDef.AbilityGuid] = weaponDef.Role;
                }
                catch
                {
                    // 本轮生成失败由 CommitSpawn 回收全部临时舰；先清除失败实体，避免悬空槽引用。
                    try
                    {
                        if (ReferenceEquals(physical.MaybeItem, created)) physical.RemoveItem(false, true);
                        if (created.Collection != null) created.Collection.Remove(created);
                        if (!created.IsDisposed) created.Dispose();
                    }
                    catch { }
                    throw;
                }
            }
        }

        private static void ReloadProfileWeapons(StarshipEntity ship)
        {
            if (ship == null || ship.Hull == null || ship.Hull.WeaponSlots == null) return;
            foreach (WeaponSlot slot in ship.Hull.WeaponSlots)
                if (slot != null && slot.Weapon != null) slot.Weapon.Reload();
        }

        private static string BuildMarker(SpaceFleetRuntimeProfile profile)
        {
            string entry = Convert.ToBase64String(Encoding.UTF8.GetBytes(profile.EntryId ?? ""));
            return MarkerPrefix + "3:" + entry + ":"
                + profile.BroadsideExtraShots + ":" + profile.NonBroadsideExtraShots + ":"
                + profile.BroadsideExtraRange + ":" + profile.NonBroadsideExtraRange + ":"
                + profile.ShieldPct + ":" + profile.ArmourPct + ":" + profile.RamPct;
        }

        private static bool TryReadMarkerProfile(StarshipEntity ship,
            out SpaceFleetRuntimeProfile profile)
        {
            profile = null;
            try
            {
                string marker = ship != null && ship.Description != null
                    ? ship.Description.CustomPetName ?? "" : "";
                if (!marker.StartsWith(MarkerPrefix + "3:", StringComparison.Ordinal)) return false;
                string[] parts = marker.Substring((MarkerPrefix + "3:").Length).Split(':');
                int bs, ns, br, nr, shield, armour, ram;
                if (parts.Length != 8 || string.IsNullOrEmpty(parts[0])
                    || !TryInt(parts[1], out bs) || !TryInt(parts[2], out ns)
                    || !TryInt(parts[3], out br) || !TryInt(parts[4], out nr)
                    || !TryInt(parts[5], out shield) || !TryInt(parts[6], out armour)
                    || !TryInt(parts[7], out ram)
                    || bs < 0 || bs > SpaceFleetCatalog.MaxExtraShots
                    || ns < 0 || ns > SpaceFleetCatalog.MaxExtraShots
                    || br < 0 || br > SpaceFleetCatalog.MaxExtraRange
                    || nr < 0 || nr > SpaceFleetCatalog.MaxExtraRange
                    || shield < 0 || shield > 1000 || armour < 0 || armour > 1000
                    || ram < 0 || ram > 1000)
                    return false;
                var bp = ship.OriginalBlueprint ?? ship.Blueprint;
                string entryId;
                try { entryId = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])); }
                catch { return false; }
                if (string.IsNullOrEmpty(entryId)) return false;
                profile = new SpaceFleetRuntimeProfile
                {
                    EntryId = entryId,
                    HullGuid = bp != null ? bp.AssetGuid.ToString() : "",
                    BroadsideExtraShots = bs,
                    NonBroadsideExtraShots = ns,
                    BroadsideExtraRange = br,
                    NonBroadsideExtraRange = nr,
                    ShieldPct = shield,
                    ArmourPct = armour,
                    RamPct = ram
                };
                return SpaceFleetCatalog.Find(profile.HullGuid) != null;
            }
            catch { return false; }
        }

        internal static bool IsMarkedEscort(StarshipEntity ship)
        {
            try { return ship != null && ship.Description != null
                && (ship.Description.CustomPetName ?? "").StartsWith(MarkerPrefix, StringComparison.Ordinal); }
            catch { return false; }
        }

        internal static bool IsNativeFleetChild(StarshipEntity ship)
        {
            object known;
            if (ship != null && KnownChildSources.TryGetValue(ship, out known)) return true;
            try { return ship != null && ship.Description != null
                && (ship.Description.CustomPetName ?? "").StartsWith(ChildMarkerPrefix, StringComparison.Ordinal); }
            catch { return false; }
        }

        internal static bool IsFleetOwnedShip(StarshipEntity ship)
        { return IsMarkedEscort(ship) || IsNativeFleetChild(ship); }

        internal static void MarkNativeChildBeforeFaction(BaseUnitEntity unit, MechanicEntity caster)
        {
            if (!Main.Enabled) return;
            var child = unit as StarshipEntity;
            var carrier = caster as StarshipEntity;
            SpaceFleetRuntimeProfile profile;
            if (child == null || !TryGetRuntimeProfile(carrier, out profile)) return;
            var hull = SpaceFleetCatalog.Find(profile.HullGuid);
            if (hull == null || hull.NativeChildShipGuids == null) return;
            string guid = child.Blueprint != null ? child.Blueprint.AssetGuid.ToString() : "";
            foreach (string allowed in hull.NativeChildShipGuids)
            {
                if (!string.Equals(allowed, guid, StringComparison.OrdinalIgnoreCase)) continue;
                child.GetOrCreate<PartUnitDescription>().CustomPetName = ChildMarkerPrefix + carrier.UniqueId;
                KnownChildSources.GetValue(child, delegate { return WeakTableMarker; });
                return;
            }
        }

        internal static void RestoreNativeChildControl(StarshipEntity child, bool seedEnemies)
        {
            if (child == null || child.IsDisposed || child.WillBeDestroyed || !IsNativeFleetChild(child)) return;
            KnownChildSources.GetValue(child, delegate { return WeakTableMarker; });
            // Summoned units return from IsDirectlyControllable before its ForceAIControl
            // check, so this earlier native flag is also required. Never stack retains.
            if (!child.PreventDirectControl) child.PreventDirectControl.Retain();
            ApplyRuntimeControl(child);
            RefreshNavigationFaction(child);
            if (seedEnemies) SeedEnemyMemory(child);
        }

        private static void RestoreNativeChildren(bool seedEnemies)
        {
            var state = MainState();
            if (state == null || state.AllEntityData == null) return;
            foreach (var entity in state.AllEntityData)
            {
                var child = entity as StarshipEntity;
                if (IsNativeFleetChild(child)) RestoreNativeChildControl(child, seedEnemies);
            }
        }

        internal static bool IsOurShip(StarshipEntity ship)
        {
            if (ship == null || ship.IsDisposed || ship.WillBeDestroyed) return false;
            // Native craft keep their own geometry and charges, without carrier bonuses.
            if (IsNativeFleetChild(ship)) return false;
            return IsOurShipSource(ship);
        }

        // 延迟舰炮在 caster 进入 WillBeDestroyed 后仍可能完成已发射的 burst。
        // 这里只识别来源归属，不把 caster 的销毁状态当成否定条件。
        internal static bool IsOurShipSource(StarshipEntity ship)
        {
            if (ship == null) return false;
            object marker;
            if (KnownEscortSources.TryGetValue(ship, out marker)) return true;
            try
            {
                if (IsNativeFleetChild(ship)) return true;
                if (ReferenceEquals(ship, PlayerShip())) return true;
                var bp = ship.OriginalBlueprint ?? ship.Blueprint;
                return IsMarkedEscort(ship) && bp != null && SpaceFleetCatalog.Find(bp.AssetGuid.ToString()) != null;
            }
            catch { return false; }
        }

        private static bool IsExpectedEscort(StarshipEntity ship)
        {
            if (!IsMarkedEscort(ship) || ship.IsDisposed) return false;
            var bp = ship.OriginalBlueprint ?? ship.Blueprint;
            return bp != null && SpaceFleetCatalog.Find(bp.AssetGuid.ToString()) != null;
        }

        private static List<StarshipEntity> MarkedEscorts(bool includeDestroying)
        {
            var result = new List<StarshipEntity>();
            var state = MainState();
            if (state == null || state.AllEntityData == null) return result;
            foreach (var entity in state.AllEntityData)
            {
                var ship = entity as StarshipEntity;
                if (ship == null || ship.IsDisposed || (!includeDestroying && ship.WillBeDestroyed)) continue;
                if (IsExpectedEscort(ship)) result.Add(ship);
            }
            return result;
        }

        private static void RefreshNavigationFaction(StarshipEntity ship)
        {
            try
            {
                var provider = NavigationTraversalProviderField != null && ship != null
                    ? NavigationTraversalProviderField.GetValue(ship.Navigation) as WarhammerTraversalProvider
                    : null;
                if (provider == null)
                    throw new InvalidOperationException("无法访问 PartStarshipNavigation.traversalProvider");
                var blocker = ship.MaybeMovementAgent != null ? ship.MaybeMovementAgent.Blocker : null;
                bool wasBlocking = blocker != null && blocker.IsBlocking;
                if (wasBlocking) blocker.Unblock();
                try { provider.SetIsPlayerEnemy(ship.Faction.IsPlayerEnemy); }
                finally
                {
                    if (wasBlocking && !blocker.IsBlocking) blocker.BlockAtCurrentPosition();
                }
            }
            catch (Exception e)
            { Main.LogError("[海战舰队] navigation 阵营刷新失败: " + e.Message); }
        }

        private static void ApplyRuntimeControl(StarshipEntity ship)
        {
            if (ship == null) return;
            try
            {
                var flag = ship.GetMechanicFeature(MechanicsFeatureType.ForceAIControl);
                if (!flag.Value) flag.Retain();
            }
            catch (Exception e) { Main.LogError("[海战舰队] ForceAIControl 挂载失败: " + e.Message); }
        }

        private static void DiagnoseJoinedPlacement(StarshipEntity ship)
        {
            try
            {
                var anchor = GridAreaHelper.GetNearestNodeXZUnwalkable(ship.Position);
                var graph = anchor != null ? anchor.Graph as CustomGridGraph : null;
                var state = MainState();
                HashSet<long> occupied;
                string failure;
                if (!TrySnapshotOccupiedStarshipCells(graph, state, ship, out occupied, out failure))
                {
                    Main.LogError("[海战舰队] 入战占位诊断失败 " + ship.UniqueId + "：" + failure);
                    return;
                }
                int direction = CustomGraphHelper.GuessDirection(ship.Forward);
                var bp = ship.OriginalBlueprint ?? ship.Blueprint;
                var blocker = ship.MaybeMovementAgent != null ? ship.MaybeMovementAgent.Blocker : null;
                List<CustomGridNodeBase> cells, passage;
                if (bp == null || !TryValidateAnchorClearance(anchor, direction, bp, occupied, null,
                    null, blocker, out cells, out passage, out failure))
                {
                    Main.LogError("[海战舰队] 入战后第一航步异常 " + ship.CharacterName
                        + " uid=" + ship.UniqueId + " anchor=("
                        + (anchor != null ? anchor.XCoordinateInGrid.ToString() : "?") + ","
                        + (anchor != null ? anchor.ZCoordinateInGrid.ToString() : "?") + ")：" + failure);
                }
            }
            catch (Exception e)
            { Main.LogError("[海战舰队] 入战占位诊断异常: " + e.Message); }
        }

        private static void SeedEnemyMemory(StarshipEntity ship)
        {
            if (ship == null || ship.CombatGroup == null) return;
            var enemies = new List<BaseUnitEntity>();
            var tc = Game.Instance != null ? Game.Instance.TurnController : null;
            if (tc == null) return;
            foreach (var entity in tc.AllUnits)
            {
                var unit = entity as BaseUnitEntity;
                if (unit != null && !ReferenceEquals(unit, ship) && !unit.IsExtra
                    && !ship.IsExtra && unit.IsInCombat && unit.LifeState.IsConscious
                    && ship.IsEnemy(unit) && ship.HasLOS(unit)) enemies.Add(unit);
            }
            enemies.Sort((a, b) => string.CompareOrdinal(a.UniqueId, b.UniqueId));
            foreach (var enemy in enemies)
            { ship.CombatGroup.Memory.Add(enemy); enemy.CombatGroup.Memory.Add(ship); }
        }

        // ---------------------------------------------------------------- 签名 / 小工具

        private static string BuildSignature(string[] payload, FleetPlan plan)
        {
            var raw = new StringBuilder("spacefleet|");
            foreach (string value in payload) AddField(raw, value);
            AddField(raw, plan.PlayerFaction != null ? plan.PlayerFaction.AssetGuid.ToString() : "");
            foreach (var row in plan.Ships)
            {
                AddField(raw, row.Blueprint.AssetGuid.ToString());
                AddField(raw, row.Blueprint.DefaultBrain != null ? row.Blueprint.DefaultBrain.AssetGuid.ToString() : "");
                AddField(raw, row.Blueprint.Prefab != null ? row.Blueprint.Prefab.AssetId : "");
                AddField(raw, row.Blueprint.Size.ToString());
                row.Cells.Sort((a, b) => a.XCoordinateInGrid != b.XCoordinateInGrid
                    ? a.XCoordinateInGrid.CompareTo(b.XCoordinateInGrid)
                    : a.ZCoordinateInGrid.CompareTo(b.ZCoordinateInGrid));
                foreach (var cell in row.Cells)
                { AddField(raw, cell.XCoordinateInGrid + "," + cell.ZCoordinateInGrid); }
            }
            foreach (var ship in plan.Escorts)
            {
                AddField(raw, ship.UniqueId);
                var bp = ship.OriginalBlueprint ?? ship.Blueprint;
                AddField(raw, bp != null ? bp.AssetGuid.ToString() : "");
                AddField(raw, ship.Description != null ? ship.Description.CustomPetName : "");
            }
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(raw.ToString())));
        }

        private static void AddField(StringBuilder raw, string value)
        { value = value ?? ""; raw.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value); }
        private static bool TryInt(string value, out int result)
        { return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result); }
        private static bool TryDirection(string value, out int result)
        { return TryInt(value, out result) && result >= 0 && result <= 7; }

        private static string NewEncounterKey(string area, StarshipEntity player)
        {
            if (string.IsNullOrEmpty(area) || player == null || string.IsNullOrEmpty(player.UniqueId)) return "";
            int tick; try { tick = Game.Instance.RealTimeController.CurrentNetworkTick; } catch { return ""; }
            return area + ":" + player.UniqueId + ":" + tick.ToString(CultureInfo.InvariantCulture);
        }

        private static bool InCurrentSpaceCombat()
        {
            try
            {
                var game = Game.Instance;
                var tc = game != null ? game.TurnController : null;
                return game != null && game.IsSpaceCombat && tc != null && tc.TurnBasedModeActive && tc.InCombat;
            }
            catch { return false; }
        }

        private static StarshipEntity PlayerShip()
        { try { return Game.Instance != null && Game.Instance.Player != null ? Game.Instance.Player.PlayerShip : null; } catch { return null; } }
        private static SceneEntitiesState MainState()
        { try { return Game.Instance != null && Game.Instance.State != null && Game.Instance.State.LoadedAreaState != null
            ? Game.Instance.State.LoadedAreaState.MainState : null; } catch { return null; } }

        private static void DestroyMarkedImmediately(string why)
        {
            var ships = MarkedEscorts(false);
            foreach (var pending in PendingSpawned)
                if (pending != null && !pending.IsDisposed && !pending.WillBeDestroyed && !ships.Contains(pending)) ships.Add(pending);
            int n = 0;
            int failed = 0;
            foreach (var ship in ships)
            {
                if (!IsExpectedEscort(ship)) continue;
                try
                {
                    Game.Instance.EntityDestroyer.Destroy(ship);
                    n++;
                }
                catch (Exception e)
                {
                    failed++;
                    Main.LogError("[海战舰队] " + why + "：收回 " + ship.UniqueId + " 失败: " + e.Message);
                }
            }
            if (n > 0 || failed > 0) Main.Log("[海战舰队] " + why + "，已排队收回 " + n
                + " 艘" + (failed > 0 ? "，失败 " + failed + " 艘。" : "。"));
            PendingSpawned.Clear();
        }

        private static void ResetRuntime()
        {
            _generation++;
            _workScheduled = false;
            _cleanupRetryCount = 0;
            _phase = Phase.Idle;
            _spawnCancelled = false;
            _area = "";
            _encounter = "";
            PendingSpawned.Clear();
        }
    }
}
