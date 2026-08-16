using JetBrains.Annotations;
using Kingmaker.Controllers.Interfaces;
using Kingmaker.Mechanics.Entities;
using Kingmaker.View.Mechanics.Entities;

namespace Kingmaker.Controllers.Units;

public class UnitMoveOnPlatformControllerLate : IControllerTick, IController
{
	public TickType GetTickType()
	{
		return TickType.EndOfFrame;
	}

	public void Tick()
	{
		foreach (AbstractUnitEntity item in default(MovableEntitiesEnumerable))
		{
			TickUnit(item);
		}
	}

	private static void TickUnit([NotNull] AbstractUnitEntity unit)
	{
		AbstractUnitEntityView view = unit.View;
		if (!(view == null))
		{
			view.InterpolationHelper.ApplyPlatformDelta();
		}
	}
}
