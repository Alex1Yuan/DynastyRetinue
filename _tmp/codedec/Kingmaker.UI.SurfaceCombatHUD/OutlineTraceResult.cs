using Unity.Burst;
using Unity.Mathematics;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal readonly struct OutlineTraceResult(bool outer, float3 startPosition)
{
	public readonly bool outer = outer;

	public readonly float3 startPosition = startPosition;
}
