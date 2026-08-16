using Kingmaker.UnitLogic.Mechanics.Damage;

namespace Kingmaker.RuleSystem.Rules.Damage;

public struct OverpenetrationData(DamageData overpenetrationDamage)
{
	public float? DamageRoll = overpenetrationDamage.Roll;

	public int MinBaseValue = overpenetrationDamage.MinValueBase;

	public int MaxBaseValue = overpenetrationDamage.MaxValueBase;

	public int OverpenetrationPercent = overpenetrationDamage.OverpenetrationFactorPercents;
}
