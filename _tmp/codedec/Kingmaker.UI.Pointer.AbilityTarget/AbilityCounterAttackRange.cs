using Kingmaker.Pathfinding;
using Kingmaker.UI.SurfaceCombatHUD;
using Kingmaker.UnitLogic.Abilities.Components.Patterns;
using Kingmaker.Utility;
using UnityEngine;

namespace Kingmaker.UI.Pointer.AbilityTarget;

public class AbilityCounterAttackRange : AbilityRange
{
	protected override bool CanEnable()
	{
		if (base.CanEnable())
		{
			return Ability.GetPatternSettings() == null;
		}
		return false;
	}

	protected override void SetRangeToWorldPosition(Vector3 castPosition, bool ignoreCache = false)
	{
		int counterAttackRange = Ability.CounterAttackRange;
		if (counterAttackRange >= 0 && GridPatterns.TryGetEnclosingRect(Ability.Caster.GetOccupiedNodes(castPosition), out var result))
		{
			CombatHUDRenderer.AbilityAreaHudInfo abilityAreaHUD = new CombatHUDRenderer.AbilityAreaHudInfo
			{
				pattern = OrientedPatternData.Empty,
				casterRect = result,
				minRange = counterAttackRange,
				maxRange = counterAttackRange,
				effectiveRange = 0,
				ignoreRangesByDefault = false,
				ignorePatternPrimaryAreaByDefault = Ability.IsStarshipAttack,
				combatHudCommandsOverride = Ability.Blueprint.CombatHudCommandsOverride
			};
			CombatHUDRenderer.Instance.SetAbilityAreaHUD(abilityAreaHUD);
		}
	}
}
