using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
internal unsafe struct OutlineSplineMeshBuilder(OutlineSplineMeshBuilder.State* state, float halfThickness, MeshComposer<LineVertex, uint> meshComposer) : ISplinePlotterListener<OutlineSplineMetaData>
{
	public struct State
	{
		public int sequencePointsCount;

		public float spatialLength;

		public float segmentedLength;

		public SplinePoint pendingSplinePoint;

		public float3 vertexPositionOffset;
	}

	private unsafe readonly State* m_State = state;

	private readonly float m_HalfThickness = halfThickness;

	private MeshComposer<LineVertex, uint> m_MeshComposer = meshComposer;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public unsafe void SetVertexPositionOffset(in float3 value)
	{
		m_State->vertexPositionOffset = value;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public unsafe void StartLine()
	{
		m_State->sequencePointsCount = 0;
		m_State->spatialLength = 0f;
		m_State->segmentedLength = 0f;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public unsafe void PushPoint(in SplinePoint point)
	{
		if (point.masked)
		{
			FinishSequence();
			m_State->pendingSplinePoint = point;
			m_State->sequencePointsCount = 1;
		}
		else
		{
			PushPointInternal(in point);
			if (point.breakLine)
			{
				FinishSequence();
			}
		}
		m_State->spatialLength = point.spatialDistance;
		m_State->segmentedLength = point.segmentedDistance;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void FinishLine(in OutlineSplineMetaData metaData)
	{
		FinishSequence();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private unsafe void PushPointInternal(in SplinePoint point)
	{
		if (m_State->sequencePointsCount > 1)
		{
			PushVertices(in point);
		}
		else if (m_State->sequencePointsCount > 0)
		{
			PushVertices(in m_State->pendingSplinePoint);
			PushVertices(in point);
		}
		else
		{
			m_State->pendingSplinePoint = point;
		}
		m_State->sequencePointsCount++;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private unsafe void FinishSequence()
	{
		int sequencePointsCount = m_State->sequencePointsCount;
		if (sequencePointsCount > 1)
		{
			PushIndices(sequencePointsCount);
			SetSplineLength(sequencePointsCount);
		}
		m_State->sequencePointsCount = 0;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private unsafe void PushVertices(in SplinePoint point)
	{
		float3 float5 = math.rotate(point.rotation, new float3(m_HalfThickness, 0f, 0f));
		float3 obj = point.position + m_State->vertexPositionOffset;
		float3 position = obj - float5;
		float3 position2 = obj + float5;
		LineVertex vertex = new LineVertex
		{
			position = position,
			spatialUv = MakeHalf2(0f, point.spatialDistance),
			segmentedUv = MakeHalf2(0f, point.segmentedDistance)
		};
		m_MeshComposer.PushVertex(in vertex);
		vertex.position = position2;
		vertex.spatialUv = MakeHalf2(1f, point.spatialDistance);
		vertex.segmentedUv = MakeHalf2(1f, point.segmentedDistance);
		m_MeshComposer.PushVertex(in vertex);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void PushIndices(int pointsCount)
	{
		int num = pointsCount - 1;
		int num2 = m_MeshComposer.GetVertexCount() - pointsCount * 2;
		uint num3 = (uint)((uint)num2 + num * 2);
		for (uint num4 = (uint)num2; num4 < num3; num4 += 2)
		{
			ref MeshComposer<LineVertex, uint> meshComposer = ref m_MeshComposer;
			uint i = num4;
			meshComposer.PushQuad(in i, num4 + 2, num4 + 1, num4 + 3);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private unsafe void SetSplineLength(int pointsCount)
	{
		half2 segmentedLengthSpatialLength = new half2((half)m_State->segmentedLength, (half)m_State->spatialLength);
		NativeList<LineVertex> vertexBuffer = m_MeshComposer.GetProceduralMesh().vertexBuffer;
		int length = vertexBuffer.Length;
		for (int i = length - pointsCount * 2; i < length; i++)
		{
			vertexBuffer.ElementAt(i).segmentedLengthSpatialLength = segmentedLengthSpatialLength;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static half2 MakeHalf2(float x, float y)
	{
		return math.half2(math.half(x), math.half(y));
	}

	void ISplinePlotterListener<OutlineSplineMetaData>.PushPoint(in SplinePoint point)
	{
		PushPoint(in point);
	}

	void ISplinePlotterListener<OutlineSplineMetaData>.FinishLine(in OutlineSplineMetaData metaData)
	{
		FinishLine(in metaData);
	}
}
