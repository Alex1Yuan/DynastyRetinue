using UnityEngine;

namespace Kingmaker.Visual.Particles.SnapController;

internal readonly struct CameraData(Vector3 position)
{
	public readonly Vector3 position = position;
}
