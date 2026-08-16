using System.Collections.Generic;
using Kingmaker.Controllers;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;
using Kingmaker.EntitySystem.Interfaces;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.View.Mechanics.Entities;
using StateHasher.Core;
using UnityEngine;

namespace Kingmaker.EntitySystem;

public class PlatformObjectEntity : Entity, IUpdatable, IHashable
{
	private Dictionary<MechanicEntity, Delta> m_EntitiesOnPlatform = new Dictionary<MechanicEntity, Delta>();

	public Vector3 LastUpdatePosition { get; private set; }

	public Vector3 PlatformPosition => base.View?.ViewTransform.position ?? Vector3.zero;

	public PlatformObjectEntity(string uniqueId, bool isInGame)
		: base(uniqueId, isInGame)
	{
	}

	public PlatformObjectEntity(JsonConstructorMark _)
		: base(_)
	{
	}

	public PlatformObjectEntity(PlatformObjectView view)
		: this(view.UniqueId, view.IsInGameBySettings)
	{
	}

	protected override IEntityViewBase CreateViewForData()
	{
		return null;
	}

	public void AddEntity(MechanicEntity entity)
	{
		m_EntitiesOnPlatform.Add(entity, new Delta
		{
			Position = PlatformPosition - entity.Position
		});
		if (m_EntitiesOnPlatform.Count == 1)
		{
			LastUpdatePosition = PlatformPosition;
			Game.Instance.CustomUpdateController.Add(this);
		}
	}

	public void RemoveEntity(MechanicEntity entity)
	{
		m_EntitiesOnPlatform.Remove(entity);
		if (m_EntitiesOnPlatform.Count == 0)
		{
			Game.Instance.CustomUpdateController.Remove(this);
		}
	}

	void IUpdatable.Tick(float delta)
	{
		LastUpdatePosition = PlatformPosition;
		bool hasAwaitingSettle = Game.Instance.SceneControllables.HasAwaitingSettle;
		foreach (var (mechanicEntity2, delta3) in m_EntitiesOnPlatform)
		{
			EntityPartStayOnPlatform optional = mechanicEntity2.GetOptional<EntityPartStayOnPlatform>();
			if (optional != null && optional.IsOnPlatform(this))
			{
				if (hasAwaitingSettle)
				{
					delta3.Position = PlatformPosition - mechanicEntity2.Position;
				}
				else
				{
					mechanicEntity2.Position = PlatformPosition - delta3.Position;
				}
			}
		}
	}

	public override Hash128 GetHash128()
	{
		Hash128 result = default(Hash128);
		Hash128 val = base.GetHash128();
		result.Append(ref val);
		return result;
	}
}
