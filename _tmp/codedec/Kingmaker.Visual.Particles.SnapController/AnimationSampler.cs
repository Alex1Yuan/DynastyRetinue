namespace Kingmaker.Visual.Particles.SnapController;

internal readonly struct AnimationSampler(OffsetAnimationSampler offsetAnimationSampler, CameraOffsetScaleAnimationSampler cameraOffsetScaleAnimationSampler)
{
	private readonly OffsetAnimationSampler m_OffsetAnimationSampler = offsetAnimationSampler;

	private readonly CameraOffsetScaleAnimationSampler m_CameraOffsetScaleAnimationSampler = cameraOffsetScaleAnimationSampler;

	public AnimationSample Sample(float particleSystemTime)
	{
		return new AnimationSample(m_OffsetAnimationSampler.Sample(particleSystemTime), m_CameraOffsetScaleAnimationSampler.Sample(particleSystemTime));
	}
}
