using Unity.Burst;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal readonly struct PathSplinePointMetaData(int spatialDistance)
{
	public readonly int spatialDistance = spatialDistance;
}
