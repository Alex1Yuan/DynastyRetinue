using System;
using JetBrains.Annotations;
using Kingmaker.Controllers.Interfaces;
using Kingmaker.Mechanics.Entities;
using Kingmaker.Utility.DotNetExtensions;
using Kingmaker.View.Mechanics.Entities;

namespace Kingmaker.Controllers.Units;

public class UnitMoveControllerLate : IControllerTick, IController, IControllerDisable
{
	private readonly TimeSpan m_MinDeltaTime = 0.001f.Seconds();

	public TickType GetTickType()
	{
		return TickType.EndOfFrame;
	}

	public void Tick()
	{
		float interpolationProgress = Game.Instance.RealTimeController.InterpolationProgress;
		foreach (AbstractUnitEntity item in default(MovableEntitiesEnumerable))
		{
			TickUnit(item, interpolationProgress);
		}
	}

	void IControllerDisable.OnDisable()
	{
		foreach (AbstractUnitEntity item in default(MovableEntitiesEnumerable))
		{
			TickUnit(item, 1f);
		}
	}

	private void TickUnit([NotNull] AbstractUnitEntity unit, float progress)
	{
		AbstractUnitEntityView view = unit.View;
		if (!(view == null))
		{
			view.InterpolationHelper.Interpolate(progress);
		}
	}
}
