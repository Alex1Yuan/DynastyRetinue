using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal struct OutlinePlotter(OutlineCellFilter shapeMask, CellBuffer cellBuffer, EdgeBuffer edgeBuffer, SplinePlotter<OutlineSplineMetaData, OutlineSplineMeshBuilder> splinePlotter, float halfCellSize, float turnSmoothDistance)
{
	private readonly float m_HalfCellSize = halfCellSize;

	private readonly float m_TurnSmoothDistance = turnSmoothDistance;

	private readonly OutlineCellFilter m_ShapeMask = shapeMask;

	private readonly CellBuffer m_CellBuffer = cellBuffer;

	private EdgeBuffer m_EdgeBuffer = edgeBuffer;

	private SplinePlotter<OutlineSplineMetaData, OutlineSplineMeshBuilder> m_SplinePlotter = splinePlotter;

	private OutlinePlotterBasis m_Basis = OutlinePlotterBasis.Default;

	private float3 m_CursorPosition = default(float3);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Plot(in float3 startPosition, in NativeArray<OutlinePlotCommand> commands)
	{
		m_SplinePlotter.StartLine(startPosition, quaternion.LookRotation(m_Basis.Forward, new float3(0f, 1f, 0f)));
		m_CursorPosition = startPosition;
		foreach (OutlinePlotCommand command2 in commands)
		{
			OutlinePlotCommand command = command2;
			switch (command.code)
			{
			case OutlinePlotCommandCode.Forward:
				PlotForward(in command);
				break;
			case OutlinePlotCommandCode.TurnLeft:
				PlotTurnLeft(in command);
				break;
			case OutlinePlotCommandCode.TurnRight:
				PlotTurnRight(in command);
				break;
			case OutlinePlotCommandCode.TurnBackward:
				PlotTurnBackward(in command);
				break;
			}
		}
		m_SplinePlotter.FinishLine(new OutlineSplineMetaData(outer: false));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void PlotForward(in OutlinePlotCommand command)
	{
		bool masked = !TestAndWriteEdgeSegment(command.firstSegmentCellIndex, m_Basis.EastSideNorthPartEdge);
		bool masked2 = !TestAndWriteEdgeSegment(command.secondSegmentCellIndex, m_Basis.EastSideSouthPartEdge);
		Cell cell = m_CellBuffer.GetCell(command.firstSegmentCellIndex);
		Cell cell2 = m_CellBuffer.GetCell(command.secondSegmentCellIndex);
		float cornerHeight = cell.GetCornerHeight(m_Basis.CornerNE);
		float y = math.lerp(cell.GetCornerHeight(m_Basis.CornerNE), cell2.GetCornerHeight(m_Basis.CornerNE), 0.5f);
		float3 float5 = m_Basis.Forward * m_HalfCellSize;
		float3 float6 = m_CursorPosition + float5;
		float6.y = cornerHeight;
		float3 float7 = float6 + float5;
		float7.y = y;
		m_SplinePlotter.PushPoint(float6, 0f, 0f, masked);
		m_SplinePlotter.PushPoint(float7, 0f, 0f, masked2);
		m_CursorPosition = float7;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void PlotTurnRight(in OutlinePlotCommand command)
	{
		bool masked = !TestAndWriteEdgeSegment(command.firstSegmentCellIndex, m_Basis.EastSideNorthPartEdge);
		bool masked2 = !TestAndWriteEdgeSegment(command.secondSegmentCellIndex, m_Basis.SouthSideWestPartEdge);
		Cell cell = m_CellBuffer.GetCell(command.firstSegmentCellIndex);
		Cell cell2 = m_CellBuffer.GetCell(command.secondSegmentCellIndex);
		float cornerHeight = cell.GetCornerHeight(m_Basis.CornerNE);
		float y = math.lerp(cell.GetCornerHeight(m_Basis.CornerNE), cell2.GetCornerHeight(m_Basis.CornerSE), 0.5f);
		float3 float5 = m_Basis.Forward * m_HalfCellSize;
		float3 float6 = m_Basis.Right * m_HalfCellSize;
		float3 float7 = m_CursorPosition + float5;
		float7.y = cornerHeight;
		float3 float8 = float7 + float6;
		float8.y = y;
		m_SplinePlotter.PushPoint(float7, m_TurnSmoothDistance, 0f, masked);
		m_SplinePlotter.PushPoint(float8, 0f, 0f, masked2);
		m_CursorPosition = float8;
		m_Basis.TurnClockwise90();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void PlotTurnLeft(in OutlinePlotCommand command)
	{
		bool masked = !TestAndWriteEdgeSegment(command.firstSegmentCellIndex, m_Basis.EastSideNorthPartEdge);
		bool masked2 = !TestAndWriteEdgeSegment(command.secondSegmentCellIndex, m_Basis.NorthSideEastPartEdge);
		Cell cell = m_CellBuffer.GetCell(command.firstSegmentCellIndex);
		Cell cell2 = m_CellBuffer.GetCell(command.secondSegmentCellIndex);
		float cornerHeight = cell.GetCornerHeight(m_Basis.CornerNE);
		float y = math.lerp(cell.GetCornerHeight(m_Basis.CornerNE), cell2.GetCornerHeight(m_Basis.CornerNW), 0.5f);
		float3 float5 = m_Basis.Forward * m_HalfCellSize;
		float3 float6 = m_Basis.Left * m_HalfCellSize;
		float3 float7 = m_CursorPosition + float5;
		float7.y = cornerHeight;
		float3 float8 = float7 + float6;
		float8.y = y;
		m_SplinePlotter.PushPoint(float7, m_TurnSmoothDistance, 0f, masked);
		m_SplinePlotter.PushPoint(float8, 0f, 0f, masked2);
		m_CursorPosition = float8;
		m_Basis.TurnCounterClockwise90();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void PlotTurnBackward(in OutlinePlotCommand command)
	{
		bool masked = !TestAndWriteEdgeSegment(command.firstSegmentCellIndex, m_Basis.EastSideNorthPartEdge);
		bool masked2 = !TestAndWriteEdgeSegment(command.secondSegmentCellIndex, m_Basis.WestSideNorthPartEdge);
		Cell cell = m_CellBuffer.GetCell(command.firstSegmentCellIndex);
		Cell cell2 = m_CellBuffer.GetCell(command.secondSegmentCellIndex);
		float cornerHeight = cell.GetCornerHeight(m_Basis.CornerNE);
		float y = math.lerp(cell.GetCornerHeight(m_Basis.CornerNE), cell2.GetCornerHeight(m_Basis.CornerSW), 0.5f);
		float3 float5 = m_Basis.Forward * m_HalfCellSize;
		float3 float6 = m_CursorPosition + float5;
		float6.y = cornerHeight;
		float3 cursorPosition = m_CursorPosition;
		cursorPosition.y = y;
		float num = math.sign(cursorPosition.y - m_CursorPosition.y);
		float3 float7 = float6 + math.normalize(m_CursorPosition - float6) * m_TurnSmoothDistance;
		float3 float8 = float6 + math.normalize(cursorPosition - float6) * m_TurnSmoothDistance;
		float3 float9 = math.lerp(float7, float8, 0.5f);
		float3 forward = math.normalize(float8 - float7);
		float3 float10 = num * math.normalize(float9 - float6);
		quaternion rotation = quaternion.LookRotation(forward, float10);
		quaternion rotation2 = quaternion.LookRotation(forward, -float10);
		float smoothDistance = math.distance(float7, float8) / 2f;
		m_SplinePlotter.PushPoint(float7, smoothDistance, 0f, masked);
		m_SplinePlotter.PushPoint(float9, rotation, 0f, 0f, masked);
		m_SplinePlotter.BreakLine();
		m_SplinePlotter.PushPoint(float9, rotation2, 0f, 0f, masked2);
		m_SplinePlotter.PushPoint(float8, rotation2, smoothDistance, 0f, masked2);
		m_SplinePlotter.PushPoint(cursorPosition, 0f, 0f, masked2);
		m_CursorPosition = cursorPosition;
		m_Basis.Turn180();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TestAndWriteEdgeSegment(int cellIndex, in CellEdgeSegment edgeSegment)
	{
		if (m_ShapeMask.Test(cellIndex) && m_EdgeBuffer.Test(cellIndex, edgeSegment))
		{
			m_EdgeBuffer.Write(cellIndex, edgeSegment);
			return true;
		}
		return false;
	}
}
