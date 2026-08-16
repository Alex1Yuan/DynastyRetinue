using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal struct FillBufferWriter(CellBuffer cellBuffer, FillBuffer fillBuffer, MaterialAreaDescriptorBuffer materialAreaDescriptorBuffer)
{
	private readonly CellBuffer m_CellBuffer = cellBuffer;

	private FillBuffer m_FillBuffer = fillBuffer;

	private MaterialAreaDescriptorBuffer m_MaterialAreaDescriptorBuffer = materialAreaDescriptorBuffer;

	public void Write(int materialId, SurfaceCellFilterData filterData)
	{
		SurfaceCellFilter surfaceCellFilter = new SurfaceCellFilter(m_CellBuffer, filterData);
		int2 int5 = new int2(int.MaxValue);
		int2 int6 = new int2(int.MinValue);
		for (int i = 0; i < m_CellBuffer.Length; i++)
		{
			if (surfaceCellFilter.Test(i))
			{
				m_FillBuffer[i] = (byte)materialId;
				int2 coords = m_CellBuffer.GetCell(i).coords;
				int5 = math.min(int5, coords);
				int6 = math.max(int6, coords);
			}
		}
		MaterialAreaDescriptor descriptor = new MaterialAreaDescriptor
		{
			materialId = (byte)materialId,
			coordsMin = int5,
			coordsMax = int6
		};
		m_MaterialAreaDescriptorBuffer.Add(descriptor);
	}

	public void Write(int materialId, NativeSlice<int> cellIndices, SurfaceCellFilterData filterData)
	{
		SurfaceCellFilter surfaceCellFilter = new SurfaceCellFilter(m_CellBuffer, filterData);
		int2 int5 = new int2(int.MaxValue);
		int2 int6 = new int2(int.MinValue);
		for (int i = 0; i < cellIndices.Length; i++)
		{
			int num = cellIndices[i];
			if (surfaceCellFilter.Test(num))
			{
				m_FillBuffer[num] = (byte)materialId;
				int2 coords = m_CellBuffer.GetCell(num).coords;
				int5 = math.min(int5, coords);
				int6 = math.max(int6, coords);
			}
		}
		MaterialAreaDescriptor descriptor = new MaterialAreaDescriptor
		{
			materialId = (byte)materialId,
			coordsMin = int5,
			coordsMax = int6
		};
		m_MaterialAreaDescriptorBuffer.Add(descriptor);
	}
}
