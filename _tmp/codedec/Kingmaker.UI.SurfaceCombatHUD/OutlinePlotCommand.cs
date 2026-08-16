using Unity.Burst;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal readonly struct OutlinePlotCommand(OutlinePlotCommandCode code, int firstSegmentCellIndex, int secondSegmentCellIndex)
{
	public readonly OutlinePlotCommandCode code = code;

	public readonly int firstSegmentCellIndex = firstSegmentCellIndex;

	public readonly int secondSegmentCellIndex = secondSegmentCellIndex;
}
