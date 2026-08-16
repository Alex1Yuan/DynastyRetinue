using Unity.Burst;
using Unity.Collections;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal struct OutlineBuilder(CellBuffer cellBuffer, NativeArray<ushort> chunkAreaMasks, float halfCellSize, float turnSmoothDistance, NativeArray<BezierPoint> bezierPoints, NativeArray<byte> processStatusFlags, NativeList<OutlinePlotCommand> outlinePlotCommands)
{
	private readonly CellBuffer m_CellBuffer = cellBuffer;

	private readonly NativeArray<ushort> m_ChunkAreaMasks = chunkAreaMasks;

	private readonly float m_HalfCellSize = halfCellSize;

	private readonly float m_TurnSmoothDistance = turnSmoothDistance;

	private NativeArray<BezierPoint> m_BezierPoints = bezierPoints;

	private NativeArray<byte> m_ProcessStatusFlags = processStatusFlags;

	private NativeList<OutlinePlotCommand> m_OutlinePlotCommands = outlinePlotCommands;

	private byte m_IterationNumber = 0;

	public void Execute(OutlineType outlineType, in OutlineCellFilter shape, in OutlineCellFilter shapeMask, ref EdgeBuffer edgeBuffer, ref OutlineSplineMeshBuilder splineMeshBuilder)
	{
		m_IterationNumber++;
		SplinePlotter<OutlineSplineMetaData, OutlineSplineMeshBuilder> splinePlotter = new SplinePlotter<OutlineSplineMetaData, OutlineSplineMeshBuilder>(splineMeshBuilder, m_BezierPoints, 0f);
		EdgeBuffer edgeBuffer2 = edgeBuffer;
		SplinePlotter<OutlineSplineMetaData, OutlineSplineMeshBuilder> splinePlotter2 = splinePlotter;
		float halfCellSize = m_HalfCellSize;
		float turnSmoothDistance = m_TurnSmoothDistance;
		OutlinePlotter outlinePlotter = new OutlinePlotter(shapeMask, m_CellBuffer, edgeBuffer2, splinePlotter2, halfCellSize, turnSmoothDistance);
		OutlineTracer2 outlineTracer = new OutlineTracer2(m_HalfCellSize * 2f, m_IterationNumber, m_CellBuffer, m_ChunkAreaMasks, m_ProcessStatusFlags, m_OutlinePlotCommands, shape);
		OutlineTraceResult result;
		while (outlineTracer.Trace(out result))
		{
			if (outlineType switch
			{
				OutlineType.Default => true, 
				OutlineType.Inner => !result.outer, 
				OutlineType.Outer => result.outer, 
				_ => false, 
			})
			{
				outlinePlotter.Plot(in result.startPosition, (NativeArray<OutlinePlotCommand>)m_OutlinePlotCommands);
			}
			m_OutlinePlotCommands.Clear();
		}
	}
}
