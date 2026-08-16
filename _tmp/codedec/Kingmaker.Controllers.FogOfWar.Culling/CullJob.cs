using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Kingmaker.Controllers.FogOfWar.Culling;

[BurstCompile]
internal struct CullJob : IJobParallelFor
{
	public int StaticBlockerSegmentCount;

	public int DynamicBlockerSegmentCount;

	[ReadOnly]
	public NativeArray<RevealerProperties> Revealers;

	[ReadOnly]
	public NativeArray<TargetProperties> Targets;

	[ReadOnly]
	public NativeArray<int> StaticPlaneSetCounts;

	[ReadOnly]
	public NativeArray<BlockerPlaneSet> StaticPlaneSets;

	[ReadOnly]
	public NativeArray<int> DynamicPlaneSetCounts;

	[ReadOnly]
	public NativeArray<BlockerPlaneSet> DynamicPlaneSets;

	[ReadOnly]
	public NativeArray<bool> TargetForceRevealStates;

	public NativeArray<bool> TargetRevealedStates;

	public NativeList<ushort>.ParallelWriter NotifyTargetIndices;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Execute(int targetIndex)
	{
		bool num = TargetForceRevealStates[targetIndex];
		bool flag = TargetRevealedStates[targetIndex];
		bool flag2 = num || IsTargetVisible(Targets[targetIndex]) != 0;
		if (flag != flag2)
		{
			TargetRevealedStates[targetIndex] = flag2;
			NotifyTargetIndices.AddNoResize((ushort)targetIndex);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int IsTargetVisible(in TargetProperties target)
	{
		for (int i = 0; i < Revealers.Length; i++)
		{
			RevealerProperties revealer = Revealers[i];
			float2 float5 = target.Center - revealer.Center;
			float num = math.length(float5);
			if (num <= target.Radius)
			{
				return 1;
			}
			float2 float6 = float5 / num;
			float2 point = revealer.Center + float6 * (num - target.Radius);
			if (IsPointInsideRevealerRange(in point, in revealer) && !IsPointInsideAnyBlockerVolume(i, in point))
			{
				return 1;
			}
		}
		return 0;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsPointInsideRevealerRange(in float2 point, in RevealerProperties revealer)
	{
		float num = revealer.Radius * revealer.Radius;
		return math.distancesq(point, revealer.Center) < num;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool IsPointInsideAnyBlockerVolume(int revealerIndex, in float2 point)
	{
		int num = revealerIndex * StaticBlockerSegmentCount;
		int num2 = num + StaticPlaneSetCounts[revealerIndex];
		int num3 = revealerIndex * DynamicBlockerSegmentCount;
		int num4 = num3 + DynamicPlaneSetCounts[revealerIndex];
		for (int i = num; i < num2; i++)
		{
			if (IsPointInsideBlockerVolume(in point, StaticPlaneSets[i]))
			{
				return true;
			}
		}
		for (int j = num3; j < num4; j++)
		{
			if (IsPointInsideBlockerVolume(in point, DynamicPlaneSets[j]))
			{
				return true;
			}
		}
		return false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsPointInsideBlockerVolume(in float2 point, in BlockerPlaneSet cullingPlanes)
	{
		if (Plane2D.SignedDistance(in cullingPlanes.PlaneA, in point) > 0f && Plane2D.SignedDistance(in cullingPlanes.PlaneB, in point) > 0f)
		{
			return Plane2D.SignedDistance(in cullingPlanes.PlaneC, in point) > 0f;
		}
		return false;
	}
}
