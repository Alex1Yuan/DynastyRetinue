using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;

namespace Kingmaker.AI.AreaScanning;

public class DefaultTravelDelayProvider : ITravelDelayProvider
{
	public PassInfo GetPassInfo(BaseUnitEntity unit, CustomGridNodeBase nodeFrom, CustomGridNodeBase nodeTo, bool isEvenDiagonal, bool isDiagonalDirection)
	{
		float num = unit.Blueprint.WarhammerMovementApPerCell * (float)((!isEvenDiagonal) ? 1 : 2);
		return new PassInfo
		{
			pathCost = num,
			delay = num + (isDiagonalDirection ? 0.1f : 0f),
			enteredAoE = 0,
			provokedAoO = 0
		};
	}
}
