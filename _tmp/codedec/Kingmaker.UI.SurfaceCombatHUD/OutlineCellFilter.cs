using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Unity.Burst;
using Unity.Collections;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal readonly struct OutlineCellFilter(CellBuffer cellBuffer, FillBuffer fillBuffer, OutlineCellFilterData data)
{
	[ReadOnly]
	private readonly CellBuffer m_CellBuffer = cellBuffer;

	[ReadOnly]
	private readonly FillBuffer m_FillBuffer = fillBuffer;

	private readonly SurfaceBufferMask m_SurfaceBufferMask = data.surfaceBuffer;

	private readonly int m_StrictTestMask = data.belongToAllAreaMask | data.notBelongToAnyAreasMask;

	private readonly int m_OptionalTestMask = data.belongToAnyAreasMask;

	private readonly int m_StrictTestReference = data.belongToAllAreaMask;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	[Pure]
	public bool Test(int cellIndex)
	{
		if (TestArea(m_CellBuffer.GetAreaMask(cellIndex)))
		{
			return TestSurface(cellIndex);
		}
		return false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	[Pure]
	public bool TestArea(int areaMask)
	{
		if ((areaMask & m_StrictTestMask) != m_StrictTestReference)
		{
			return false;
		}
		if (m_OptionalTestMask != 0 && (areaMask & m_OptionalTestMask) == 0)
		{
			return false;
		}
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	[Pure]
	private bool TestSurface(int cellIndex)
	{
		return m_SurfaceBufferMask switch
		{
			SurfaceBufferMask.HasValue => m_FillBuffer.HasValue(cellIndex), 
			SurfaceBufferMask.HasNoValue => m_FillBuffer.HasNoValue(cellIndex), 
			_ => true, 
		};
	}
}
