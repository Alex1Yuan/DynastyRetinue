using System;
using System.Collections.Generic;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.JsonSystem.Helpers;
using Kingmaker.Blueprints.Loot;
using Kingmaker.Cargo;
using Kingmaker.ElementsSystem;
using Kingmaker.ElementsSystem.ContextData;
using Kingmaker.EntitySystem.Persistence.Versioning;
using Kingmaker.Items;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.UI.Common;

namespace Kingmaker.Designers.EventConditionActionSystem.Actions;

[PlayerUpgraderAllowed(false)]
[TypeId("343049de4e36454c85b36f38485730f3")]
public class AddCargo : GameAction
{
	public ItemsItemOrigin m_Origin;

	public BlueprintLootReference m_Loot;

	public BlueprintLoot Loot => m_Loot?.Get();

	public override string GetCaption()
	{
		return $"Create and add cargo from {Loot}";
	}

	protected override void RunAction()
	{
		List<CargoEntity> cargoes = new List<CargoEntity>();
		Action<CargoEntity> action = delegate(CargoEntity cargoEntity2)
		{
			cargoes.Add(cargoEntity2);
			EventBus.RaiseEvent(delegate(ICargoStateChangedHandler h)
			{
				h.HandleCreateNewCargo(cargoEntity2);
			});
		};
		CargoEntity cargoEntity = null;
		LootEntry[] items = Loot.Items;
		foreach (LootEntry lootEntry in items)
		{
			int num2 = lootEntry.Count;
			while (num2 > 0)
			{
				if (cargoEntity == null || cargoEntity.IsFull)
				{
					using (ContextData<ItemsCollection.SuppressEvents>.Request())
					{
						cargoEntity = Game.Instance.Player.CargoState.Create(m_Origin);
					}
				}
				if (!cargoEntity.CanAdd(lootEntry.Item, out var canAddCount) || canAddCount <= 0)
				{
					break;
				}
				int num3 = Math.Min(num2, canAddCount);
				num2 -= num3;
				cargoEntity.AddItem(lootEntry.Item, num3);
				if (cargoEntity.IsFull)
				{
					action(cargoEntity);
				}
			}
		}
		if (cargoEntity != null && !cargoEntity.IsFull)
		{
			action(cargoEntity);
		}
		EventBus.RaiseEvent(delegate(IAddCargoActionHandler h)
		{
			h.HandleAddCargoAction(cargoes);
		});
	}
}
