using UnityEngine;

namespace Kingmaker.Visual.Particles.SnapController;

internal readonly struct CameraOffsetScaleAnimationSampler(float particleSystemStartDelay, AnimationCurve cameraOffsetScaleCurve, float snapMapAdditionalScaleReduced)
{
	private readonly float m_ParticleSystemStartDelay = particleSystemStartDelay;

	private readonly AnimationCurve m_CameraOffsetScaleCurve = cameraOffsetScaleCurve;

	private readonly float m_CameraOffsetScaleFactor = snapMapAdditionalScaleReduced;

	public float Sample(float particleSystemTime)
	{
		float time = Mathf.Max(0f, particleSystemTime - m_ParticleSystemStartDelay);
		return m_CameraOffsetScaleCurve.Evaluate(time) * m_CameraOffsetScaleFactor;
	}
}
