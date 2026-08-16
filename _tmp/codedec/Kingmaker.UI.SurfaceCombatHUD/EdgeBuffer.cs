using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Unity.Burst;
using Unity.Collections;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal struct EdgeBuffer(CellBuffer cellBuffer, NativeArray<byte> edgeSegmentFlagArray, bool testDisabled)
{
	private readonly CellBuffer m_CellBuffer = cellBuffer;

	private readonly bool m_TestDisabled = testDisabled;

	private NativeArray<byte> m_EdgeSegmentFlagArray = edgeSegmentFlagArray;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	[Pure]
	public bool Test(int cellIndex, CellEdgeSegment edgeSegment)
	{
		if (m_TestDisabled)
		{
			return true;
		}
		if ((m_EdgeSegmentFlagArray[cellIndex] & edgeSegment.Flag) != 0)
		{
			return false;
		}
		if (m_CellBuffer.GetCell(cellIndex).TryGetAdjacent(edgeSegment.GetAdjacentCellDirection(), out var cellIndex2) && (m_EdgeSegmentFlagArray[cellIndex2] & edgeSegment.MirrorSide().Flag) != 0)
		{
			return false;
		}
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Write(int cellIndex, CellEdgeSegment edgeSegment)
	{
		m_EdgeSegmentFlagArray[cellIndex] |= edgeSegment.Flag;
	}
}
