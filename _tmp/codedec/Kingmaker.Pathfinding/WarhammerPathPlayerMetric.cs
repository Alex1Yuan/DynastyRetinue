namespace Kingmaker.Pathfinding;

public readonly struct WarhammerPathPlayerMetric(int diagonalsCount, float length)
{
	public readonly int DiagonalsCount = diagonalsCount;

	public readonly float Length = length;

	public override string ToString()
	{
		return $"L:{Length}";
	}
}
