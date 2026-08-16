using System;

namespace Kingmaker.UI.SurfaceCombatHUD;

[Serializable]
public struct PathLineSettings
{
	public float thickness;

	public int smoothSegmentsCount;

	public float edgeSmoothDistance;

	public float turnSmoothDistance;

	public float stepSmoothDistance;

	public float hardTurnSmoothDistanceFactor;

	public float edgePenetrationThreshold;

	public float edgeHoverThreshold;

	public float stepHeightDeltaThreshold;

	public float stepOffset;

	public static PathLineSettings Default => new PathLineSettings
	{
		thickness = 0.2f,
		smoothSegmentsCount = 6,
		edgeSmoothDistance = 0.3f,
		turnSmoothDistance = 0.3f,
		stepSmoothDistance = 0.1f,
		hardTurnSmoothDistanceFactor = 1f,
		edgePenetrationThreshold = 0f,
		edgeHoverThreshold = 10f,
		stepHeightDeltaThreshold = 0.1f,
		stepOffset = 0.2f
	};
}
