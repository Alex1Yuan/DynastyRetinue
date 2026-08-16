using UnityEngine;

namespace Kingmaker.Visual.Particles.SnapController;

internal readonly struct OffsetAnimationSampler(float particleSystemStartDelay, in SnapControllerBase.OffsetAnimationSettings offsetAnimationSettings, Transform offsetRotationRoot)
{
	private readonly bool m_OffsetAnimationEnabled = offsetAnimationSettings.Enabled;

	private readonly Transform m_OffsetRotationRoot = offsetRotationRoot;

	private readonly AnimationCurve m_OffsetCurveX = offsetAnimationSettings.OffsetX;

	private readonly AnimationCurve m_OffsetCurveY = offsetAnimationSettings.OffsetY;

	private readonly AnimationCurve m_OffsetCurveZ = offsetAnimationSettings.OffsetZ;

	private readonly bool m_OffsetUseWorldRotation = offsetAnimationSettings.UseWorldRotation;

	private readonly float m_ParticleSystemStartDelay = particleSystemStartDelay;

	public Vector3 Sample(float particleSystemTime)
	{
		if (!m_OffsetAnimationEnabled)
		{
			return default(Vector3);
		}
		float time = Mathf.Max(0f, particleSystemTime - m_ParticleSystemStartDelay);
		Vector3 vector = new Vector3(m_OffsetCurveX.Evaluate(time), m_OffsetCurveY.Evaluate(time), m_OffsetCurveZ.Evaluate(time));
		if (m_OffsetRotationRoot != null)
		{
			vector.Scale(m_OffsetRotationRoot.lossyScale);
			if (m_OffsetUseWorldRotation)
			{
				return m_OffsetRotationRoot.TransformDirection(vector);
			}
		}
		return vector;
	}
}
