using UnityEngine;

namespace Kingmaker.Visual.Particles.SnapController;

internal readonly struct AnimationSample(Vector3 offset, float cameraOffsetScale)
{
	public readonly Vector3 offset = offset;

	public readonly float cameraOffsetScale = cameraOffsetScale;
}
