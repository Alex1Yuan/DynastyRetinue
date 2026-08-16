using Kingmaker.Mechanics.Entities;
using Kingmaker.UI.Models.Log.Events;
using Kingmaker.UI.Models.Log.GameLogCntxt;
using Kingmaker.UnitLogic.Enums;

namespace Kingmaker.UI.Models.Log.CombatLog_ThreadSystem.LogThreads.Combat;

public class UnitLifeStateChangedLogThread : LogThreadBase, IGameLogEventHandler<GameLogEventLifeStateChanged>
{
	public void HandleEvent(GameLogEventLifeStateChanged evt)
	{
		AbstractUnitEntity unit = evt.Unit;
		if (unit.LifeState.State == UnitLifeState.Dead)
		{
			GameLogContext.SourceEntity = unit;
			if (!unit.LifeState.IsFinallyDead)
			{
				CombatLogMessage message = LogThreadBase.Strings.UnitFallsUnconscious.CreateCombatLogMessage();
				AddMessage(new CombatLogMessage(message, null, hasTooltip: false, unit));
			}
			else
			{
				CombatLogMessage message2 = LogThreadBase.Strings.UnitDeath.CreateCombatLogMessage();
				AddMessage(new CombatLogMessage(message2, null, hasTooltip: false, unit));
			}
		}
	}
}
