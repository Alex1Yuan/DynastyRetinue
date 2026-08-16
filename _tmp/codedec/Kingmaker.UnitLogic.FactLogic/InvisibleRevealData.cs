using Kingmaker.EntitySystem.Entities.Base;
using Kingmaker.EntitySystem.Interfaces;
using StateHasher.Core;
using UnityEngine;

namespace Kingmaker.UnitLogic.FactLogic;

public readonly struct InvisibleRevealData(IEntity entity, bool interruptPlayerMovement) : IHashable
{
	public readonly EntityRef EntityRef = new EntityRef(entity);

	public readonly bool InterruptPlayerMovement = interruptPlayerMovement;

	public Hash128 GetHash128()
	{
		return default(Hash128);
	}
}
