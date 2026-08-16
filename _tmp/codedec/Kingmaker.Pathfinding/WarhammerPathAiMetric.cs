namespace Kingmaker.Pathfinding;

public readonly struct WarhammerPathAiMetric(int diagonalsCount, float length, float delay, int enteredAoE, int stepsInsideDamagingAoE, int provokedAttacks)
{
	public readonly int DiagonalsCount = diagonalsCount;

	public readonly float Length = length;

	public readonly float Delay = delay;

	public readonly int EnteredAoE = enteredAoE;

	public readonly int StepsInsideDamagingAoE = stepsInsideDamagingAoE;

	public readonly int ProvokedAttacks = provokedAttacks;
}
