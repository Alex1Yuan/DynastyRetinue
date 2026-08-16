using System.Runtime.CompilerServices;
using Unity.Burst;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal readonly struct SurfaceCellFilter(CellBuffer cellBuffer, SurfaceCellFilterData data)
{
	private readonly int m_StrictTestMask = data.belongToAllAreaMask | data.notBelongToAnyAreasMask;

	private readonly int m_OptionalTestMask = data.belongToAnyAreasMask;

	private readonly int m_StrictTestReference = data.belongToAllAreaMask;

	private readonly CellBuffer m_CellBuffer = cellBuffer;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Test(int cellIndex)
	{
		int areaMask = m_CellBuffer.GetAreaMask(cellIndex);
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
}
