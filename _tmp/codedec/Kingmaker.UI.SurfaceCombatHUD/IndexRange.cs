using Unity.Burst;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
public struct IndexRange(int begin, int end)
{
	public int begin = begin;

	public int end = end;

	public override string ToString()
	{
		return $"{{{begin}:{end}}}";
	}
}
