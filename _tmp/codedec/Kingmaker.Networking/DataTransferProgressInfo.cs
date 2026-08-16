namespace Kingmaker.Networking;

public readonly struct DataTransferProgressInfo(int progressChange, int currentProgress, int fullProgress)
{
	public readonly int ProgressChange = progressChange;

	public readonly int CurrentProgress = currentProgress;

	public readonly int FullProgress = fullProgress;
}
