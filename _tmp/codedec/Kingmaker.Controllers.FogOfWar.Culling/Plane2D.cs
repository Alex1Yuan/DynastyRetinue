using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Mathematics;

namespace Kingmaker.Controllers.FogOfWar.Culling;

[BurstCompile]
public static class Plane2D
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static float4 PlaneFromPointAndNormal(in float2 point, in float2 normal)
	{
		return new float4(normal, 0f, 0f - math.dot(normal, point));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static float4 PlaneFromTwoPoints(in float2 pointA, in float2 pointB)
	{
		float2 float5 = pointB - pointA;
		float2 float6 = math.normalize(new float2(0f - float5.y, float5.x));
		return new float4(float6, 0f, 0f - math.dot(float6, pointA));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static float SignedDistance(in float4 plane, in float2 point)
	{
		return math.dot(plane, new float4(point, 0f, 1f));
	}
}
