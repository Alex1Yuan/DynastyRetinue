using Kingmaker.EntitySystem.Entities;
using Kingmaker.UI.Models.Log.Events;
using Kingmaker.UI.Models.Log.GameLogCntxt;
using UnityEngine;

namespace Kingmaker.UI.Models.Log.CombatLog_ThreadSystem.LogThreads.LifeEvents;

public class StarshipLevelUpLogThread : LogThreadBase, IGameLogEventHandler<GameLogEventStarshipLevelUp>, IGameLogEventHandler<GameLogEventStarshipExpToNextLevel>
{
	public void HandleEvent(GameLogEventStarshipLevelUp evt)
	{
		GameLogContext.SourceEntity = evt.Starship;
		int level = evt.Level;
		GameLogContext.Text = level.ToString();
		AddMessage(new CombatLogMessage(LogThreadBase.Strings.StarshipLevelUp.CreateCombatLogMessage(), null, hasTooltip: false));
	}

	public void HandleEvent(GameLogEventStarshipExpToNextLevel evt)
	{
		StarshipEntity entity = evt.Starship.Entity;
		if (entity != null)
		{
			int bonus = Mathf.Max(evt.ExpCurrent - evt.ExpGained, 0);
			if (entity.StarshipProgression.ExperienceTable.GetLevel(bonus) != entity.Progression.ExperienceLevel)
			{
				GameLogContext.SourceEntity = entity;
				GameLogContext.Text = entity.Progression.ExperienceLevel.ToString();
				AddMessage(new CombatLogMessage(LogThreadBase.Strings.StarshipLevelUp.CreateCombatLogMessage(), null, hasTooltip: false));
			}
		}
	}
}
