using System;
using Owlcat.Runtime.Visual.FogOfWar;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Kingmaker.Controllers.FogOfWar.Culling;

public class FogOfWarCullingBlocker : MonoBehaviour, IBlocker
{
	private enum CullingSystemRegistration
	{
		NotRegistered,
		RegisteredAsStatic,
		RegisteredAsDynamic
	}

	private FogOfWarBlocker m_Blocker;

	private CullingSystemRegistration m_CullingSystemRegistration;

	internal UnsafeList<BlockerSegment> m_CullingSegments;

	public static Func<FogOfWarBlocker, bool> IsStatic;

	int IBlocker.CullingRegistryIndex { get; set; } = -1;

	public void Setup(FogOfWarBlocker blocker)
	{
		m_Blocker = blocker;
		Rebuild();
		if (!base.gameObject.isStatic)
		{
			Func<FogOfWarBlocker, bool> isStatic = IsStatic;
			if (isStatic == null || !isStatic(blocker))
			{
				FogOfWarCulling.RegisterDynamicBlocker(this);
				m_CullingSystemRegistration = CullingSystemRegistration.RegisteredAsDynamic;
				return;
			}
		}
		FogOfWarCulling.RegisterStaticBlocker(this);
		m_CullingSystemRegistration = CullingSystemRegistration.RegisteredAsStatic;
	}

	public void Cleanup()
	{
		if (m_CullingSystemRegistration == CullingSystemRegistration.RegisteredAsStatic)
		{
			FogOfWarCulling.UnregisterStaticBlocker(this);
		}
		else if (m_CullingSystemRegistration == CullingSystemRegistration.RegisteredAsDynamic)
		{
			FogOfWarCulling.UnregisterDynamicBlocker(this);
		}
		if (m_CullingSegments.IsCreated)
		{
			m_CullingSegments.Dispose();
		}
		m_Blocker = null;
		m_CullingSystemRegistration = CullingSystemRegistration.NotRegistered;
	}

	public void Rebuild()
	{
		if (m_Blocker == null)
		{
			return;
		}
		if (m_CullingSegments.IsCreated)
		{
			m_CullingSegments.Clear();
		}
		else
		{
			m_CullingSegments = new UnsafeList<BlockerSegment>(4, Allocator.Persistent);
		}
		Vector2[] points = m_Blocker.Points;
		bool closed = m_Blocker.Closed;
		Vector2 heightMinMax = m_Blocker.HeightMinMax;
		bool twoSided = m_Blocker.TwoSided;
		if (points.Length < 2)
		{
			return;
		}
		float2 float5 = points[closed ? (points.Length - 1) : 0];
		float heightMin = Mathf.Min(heightMinMax.x, heightMinMax.y);
		float heightMax = Mathf.Max(heightMinMax.x, heightMinMax.y);
		if (twoSided)
		{
			for (int i = ((!closed) ? 1 : 0); i < points.Length; i++)
			{
				float2 float6 = points[i];
				m_CullingSegments.Add(new BlockerSegment
				{
					PointA = float5,
					PointB = float6,
					HeightMin = heightMin,
					HeightMax = heightMax
				});
				m_CullingSegments.Add(new BlockerSegment
				{
					PointA = float6,
					PointB = float5,
					HeightMin = heightMin,
					HeightMax = heightMax
				});
				float5 = float6;
			}
		}
		else
		{
			for (int j = ((!closed) ? 1 : 0); j < points.Length; j++)
			{
				Vector2 vector = points[j];
				m_CullingSegments.Add(new BlockerSegment
				{
					PointA = float5,
					PointB = vector,
					HeightMin = heightMin,
					HeightMax = heightMax
				});
				float5 = vector;
			}
		}
	}

	unsafe void IBlocker.AppendCullingSegments(NativeList<BlockerSegment> results)
	{
		results.AddRange(m_CullingSegments.Ptr, m_CullingSegments.Length);
	}
}
