namespace Kingmaker.Pathfinding;

public readonly struct WarhammerPathChargeMetric(float length, int diagonalsCount)
{
	public readonly float Length = length;

	public readonly int DiagonalsCount = diagonalsCount;

	public override string ToString()
	{
		return $"L:{Length}";
	}
}
