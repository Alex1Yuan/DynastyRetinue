using Kingmaker.EntitySystem.Entities;
using Kingmaker.UI.Models.Log.Events;
using Kingmaker.UI.Models.Log.GameLogCntxt;

namespace Kingmaker.UI.Models.Log.CombatLog_ThreadSystem.LogThreads.Combat;

public class InterruptCurrentTurnLogThread : LogThreadBase, IGameLogEventHandler<GameLogEventInterruptCurrentTurn>
{
	public void HandleEvent(GameLogEventInterruptCurrentTurn evt)
	{
		if (evt.Actor.Entity is BaseUnitEntity baseUnitEntity)
		{
			GameLogContext.SourceEntity = baseUnitEntity;
			AddMessage(LogThreadBase.Strings.InterruptTurn.CreateCombatLogMessage());
		}
	}
}
