using System;
using System.Collections.Generic;
using Kingmaker.Blueprints.JsonSystem.Helpers;
using Kingmaker.ElementsSystem;
using Kingmaker.Items;
using Kingmaker.Mechanics.Entities;
using Kingmaker.UnitLogic.Parts;
using Owlcat.Runtime.Core.Utility;
using UnityEngine;

namespace Kingmaker.Designers.EventConditionActionSystem.Actions;

[TypeId("99927183761300749b3c4a75bbaa1a3b")]
public class MovePartyItemsAction : GameAction
{
	[Serializable]
	private class LeaveSettings
	{
		public bool Remote;

		public bool Ex;

		public bool Detached;
	}

	[Flags]
	public enum ItemType
	{
		Weapon = 1,
		Shield = 2,
		Armor = 4,
		Usable = 8,
		Simple = 0x10
	}

	public ItemType PickupTypes = ItemType.Weapon;

	[SerializeReference]
	public ItemsCollectionEvaluator TargetCollection;

	[SerializeField]
	[Tooltip("Do not remove items equipped on some companions")]
	private LeaveSettings m_LeaveEquipmentOf;

	public override string GetCaption()
	{
		return "Pick up party items";
	}

	protected override void RunAction()
	{
		if (TargetCollection == null || !TargetCollection.TryGetValue(out var value))
		{
			Element.LogError(this, "Не удалось получить ItemsCollection из {0}", TargetCollection);
			return;
		}
		MoveItemsBetweenCollections(Game.Instance.Player.Inventory, value);
		PartInventory partInventory = Game.Instance.Player?.MainCharacterEntity?.ToBaseUnitEntity().Inventory;
		if (partInventory != null)
		{
			MoveItemsBetweenCollections(partInventory.Collection, value);
		}
	}

	private void MoveItemsBetweenCollections(ItemsCollection sourceCollection, ItemsCollection targetCollection)
	{
		List<ItemEntity> list = ListPool<ItemEntity>.Claim();
		foreach (ItemEntity item6 in sourceCollection.Items)
		{
			if (item6.HoldingSlot != null && item6.HoldingSlot.Owner != null)
			{
				CompanionState? companionState = item6.HoldingSlot.Owner.GetOptional<UnitPartCompanion>()?.State;
				if ((m_LeaveEquipmentOf.Detached && companionState == CompanionState.InPartyDetached) || (m_LeaveEquipmentOf.Remote && companionState == CompanionState.Remote) || (m_LeaveEquipmentOf.Ex && companionState == CompanionState.ExCompanion))
				{
					continue;
				}
			}
			if (!(item6 is ItemEntityWeapon item))
			{
				if (!(item6 is ItemEntityShield item2))
				{
					if (!(item6 is ItemEntityArmor item3))
					{
						if (!(item6 is ItemEntityUsable item4))
						{
							if (item6 is ItemEntitySimple item5 && PickupTypes.HasFlag(ItemType.Simple))
							{
								list.Add(item5);
							}
						}
						else if (PickupTypes.HasFlag(ItemType.Usable))
						{
							list.Add(item4);
						}
					}
					else if (PickupTypes.HasFlag(ItemType.Armor))
					{
						list.Add(item3);
					}
				}
				else if (PickupTypes.HasFlag(ItemType.Shield))
				{
					list.Add(item2);
				}
			}
			else if (PickupTypes.HasFlag(ItemType.Weapon))
			{
				list.Add(item);
			}
		}
		list.ForEach(delegate(ItemEntity item6)
		{
			sourceCollection.Transfer(item6, targetCollection);
		});
	}
}
