using Kingmaker.Enums;
using Kingmaker.RuleSystem;

namespace Kingmaker.Blueprints.Items.Weapons;

public struct WeaponDamageScaleTableItem(Size size, DiceFormula dice)
{
	public readonly Size Size = size;

	public readonly DiceFormula Dice = dice;
}
