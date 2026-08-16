using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal readonly struct CellBuffer(NativeArray<CellUnion> cellArray, NativeArray<ushort> cellAreaMaskArray)
{
	private readonly NativeArray<CellUnion> m_CellArray = cellArray;

	private readonly NativeArray<ushort> m_CellAreaMaskArray = cellAreaMaskArray;

	public int Length
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return m_CellArray.Length;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Cell GetCell(int index)
	{
		return m_CellArray[index].cell;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int GetAreaMask(int index)
	{
		return m_CellAreaMaskArray[index];
	}
}
