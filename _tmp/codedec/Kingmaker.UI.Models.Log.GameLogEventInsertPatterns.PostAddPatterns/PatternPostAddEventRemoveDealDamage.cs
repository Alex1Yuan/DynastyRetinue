using System.Collections.Generic;
using Kingmaker.RuleSystem.Rules.Damage;
using Kingmaker.UI.Models.Log.Events;

namespace Kingmaker.UI.Models.Log.GameLogEventInsertPatterns.PostAddPatterns;

public class PatternPostAddEventRemoveDealDamage : PatternPostAddEvent
{
	public static PatternPostAddEvent Create()
	{
		return new PatternPostAddEventRemoveDealDamage();
	}

	private PatternPostAddEventRemoveDealDamage()
	{
	}

	protected override void ApplyImpl(List<GameLogEvent> queue, GameLogEvent @event)
	{
		if (@event is GameLogEventAttack gameLogEventAttack)
		{
			RemoveDepDealDamage(queue, gameLogEventAttack);
		}
		if (@event is GameLogRuleEvent<RuleDealDamage> && @event.ParentEvent is GameLogEventAttack gameLogEventAttack2)
		{
			RemoveDepDealDamage(queue, gameLogEventAttack2);
		}
	}

	private void RemoveDepDealDamage(List<GameLogEvent> eventsQueue, GameLogEventAttack @event)
	{
		eventsQueue.RemoveAll((GameLogEvent evt) => evt is GameLogRuleEvent<RuleDealDamage> gameLogRuleEvent && gameLogRuleEvent.ParentEvent == @event);
	}
}
