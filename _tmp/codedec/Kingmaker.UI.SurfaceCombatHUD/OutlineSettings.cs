using System;

namespace Kingmaker.UI.SurfaceCombatHUD;

[Serializable]
public struct OutlineSettings
{
	public float lineThickness;

	public float turnSmoothDistance;

	public int turnSmoothSegmentsCount;

	public bool mergeSubMeshes;

	public static OutlineSettings Default => new OutlineSettings
	{
		lineThickness = 0.2f,
		turnSmoothDistance = 0.2f,
		turnSmoothSegmentsCount = 6
	};
}
