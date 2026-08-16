using Unity.Burst;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal readonly struct OutlineSplineMetaData(bool outer)
{
	public readonly bool outer = outer;
}
