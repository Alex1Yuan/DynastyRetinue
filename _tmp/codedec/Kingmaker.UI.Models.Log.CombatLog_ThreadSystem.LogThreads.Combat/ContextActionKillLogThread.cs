using Kingmaker.Blueprints.Root.Strings.GameLog;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UI.Models.Log.Events;
using Kingmaker.UI.Models.Log.GameLogCntxt;

namespace Kingmaker.UI.Models.Log.CombatLog_ThreadSystem.LogThreads.Combat;

public class ContextActionKillLogThread : LogThreadBase, IGameLogEventHandler<GameLogEventContextActionKill>
{
	public void HandleEvent(GameLogEventContextActionKill evt)
	{
		if (evt.RulePerformSavingThrow != null)
		{
			CombatLogMessage combatLogMessage = RulebookSavingThrowLogThread.GetCombatLogMessage(evt.RulePerformSavingThrow, ignoreInitiatorDeath: true);
			if (combatLogMessage != null)
			{
				AddMessage(combatLogMessage);
			}
		}
		GameLogContext.SourceEntity = (BaseUnitEntity)evt.Caster.Entity;
		GameLogContext.TargetEntity = (BaseUnitEntity)evt.Target.Entity;
		GameLogContext.Text = evt.Blueprint.Name;
		GameLogContext.Count = evt.Damage;
		GameLogMessage gameLogMessage = ((evt.Caster.Entity == evt.Target.Entity) ? LogThreadBase.Strings.TargetContextActionKill : LogThreadBase.Strings.SourceContextActionKill);
		AddMessage(gameLogMessage.CreateCombatLogMessage());
	}
}
