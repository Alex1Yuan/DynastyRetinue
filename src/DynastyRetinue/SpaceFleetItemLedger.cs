using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.Cargo;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Warhammer.SpaceCombat.Blueprints;

namespace DynastyRetinue
{
    /// <summary>
    /// 随当前游戏存档保存“曾获得过”的原版舰装 GUID。只在读档/过图补种，之后靠物品事件增量维护；
    /// 不逐帧扫描，不移动或消耗玩家库存，出售/卸装也不删除历史解锁。
    /// </summary>
    internal sealed class SpaceFleetItemLedger : IItemsCollectionHandler,
        ICargoStateChangedHandler
    {
        private static SpaceFleetItemLedger _instance;
        private static readonly HashSet<ItemsCollection> TrackedCollections
            = new HashSet<ItemsCollection>();

        internal static void Subscribe()
        {
            if (_instance != null) return;
            try
            {
                _instance = new SpaceFleetItemLedger();
                EventBus.Subscribe(_instance);
                SeedCurrentPlayer();
            }
            catch (Exception e)
            {
                try { if (_instance != null) EventBus.Unsubscribe(_instance); } catch { }
                _instance = null;
                Main.LogError("[舰装账本] 订阅失败: " + e.Message);
            }
        }

        internal static void Unsubscribe()
        {
            try { if (_instance != null) EventBus.Unsubscribe(_instance); } catch { }
            _instance = null;
            TrackedCollections.Clear();
        }

        internal static void SeedCurrentPlayer()
        {
            var player = Game.Instance != null ? Game.Instance.Player : null;
            if (player == null) return;
            var roster = SpaceEscortService.CurrentRoster(true);
            if (roster == null) return;
            if (roster.KnownShipItemGuids == null)
                roster.KnownShipItemGuids = new List<string>();

            TrackedCollections.Clear();
            var pending = new HashSet<string>(roster.KnownShipItemGuids,
                StringComparer.OrdinalIgnoreCase);
            Track(player.Inventory, pending);
            Track(player.SharedStash, pending);
            if (player.VirtualStashes != null)
                foreach (var collection in player.VirtualStashes.Values)
                    Track(collection, pending);
            if (player.CargoState != null)
                foreach (var cargo in player.CargoState.CargoEntities)
                    if (cargo != null && cargo.Inventory != null)
                        Track(cargo.Inventory.Collection, pending);
            foreach (var unit in player.AllStarships)
            {
                var ship = unit as StarshipEntity;
                if (ship == null || SpaceEscortService.IsFleetOwnedShip(ship)
                    || ship.Hull == null || ship.Hull.HullSlots == null) continue;
                foreach (var slot in ship.Hull.HullSlots.EquipmentSlots)
                    if (slot != null) Collect(slot.MaybeItem, pending);
            }
            if (pending.Count == roster.KnownShipItemGuids.Count) return;
            var previous = roster.KnownShipItemGuids;
            roster.KnownShipItemGuids = new List<string>(pending);
            roster.KnownShipItemGuids.Sort(StringComparer.Ordinal);
            if (!SpaceEscortService.SaveRoster())
            {
                roster.KnownShipItemGuids = previous;
                Main.LogError("[舰装账本] 更新当前存档数据失败，补种已回滚。");
            }
            else Main.Log("[舰装账本] 已记录 " + pending.Count + " 种曾获得舰装。");
        }

        private static void Track(ItemsCollection collection, HashSet<string> pending)
        {
            if (collection == null) return;
            TrackedCollections.Add(collection);
            foreach (var item in collection.Items) Collect(item, pending);
        }

        private static bool Collect(ItemEntity item, HashSet<string> pending)
        {
            var bp = item != null ? item.Blueprint as BlueprintStarshipItem : null;
            if (bp == null || bp is BlueprintItemArsenal) return false;
            return pending.Add(bp.AssetGuid.ToString());
        }

        private static bool Observe(ItemEntity item)
        {
            var roster = SpaceEscortService.CurrentRoster(true);
            if (roster == null) return false;
            if (roster.KnownShipItemGuids == null)
                roster.KnownShipItemGuids = new List<string>();
            var bp = item != null ? item.Blueprint as BlueprintStarshipItem : null;
            if (bp == null || bp is BlueprintItemArsenal) return false;
            string guid = bp.AssetGuid.ToString();
            for (int i = 0; i < roster.KnownShipItemGuids.Count; i++)
                if (string.Equals(roster.KnownShipItemGuids[i], guid,
                    StringComparison.OrdinalIgnoreCase)) return false;
            roster.KnownShipItemGuids.Add(guid);
            roster.KnownShipItemGuids.Sort(StringComparer.Ordinal);
            if (SpaceEscortService.SaveRoster()) return true;
            roster.KnownShipItemGuids.Remove(guid);
            return false;
        }

        public void HandleItemsAdded(ItemsCollection collection, ItemEntity item, int count)
        {
            if (collection != null && TrackedCollections.Contains(collection)) Observe(item);
        }

        public void HandleItemsRemoved(ItemsCollection collection, ItemEntity item, int count) { }

        public void HandleCreateNewCargo(CargoEntity entity)
        {
            if (entity == null || entity.Inventory == null) return;
            var collection = entity.Inventory.Collection;
            TrackedCollections.Add(collection);
            foreach (var item in entity.Inventory.Items) Observe(item);
        }

        public void HandleRemoveCargo(CargoEntity entity, bool fromMassSell)
        {
            if (entity != null && entity.Inventory != null)
                TrackedCollections.Remove(entity.Inventory.Collection);
        }

        public void HandleAddItemToCargo(ItemEntity item, ItemsCollection from,
            CargoEntity to, int oldIndex)
        {
            if (to != null && to.Inventory != null)
                TrackedCollections.Add(to.Inventory.Collection);
            Observe(item);
        }

        public void HandleRemoveItemFromCargo(ItemEntity item, CargoEntity from) { }
    }
}
