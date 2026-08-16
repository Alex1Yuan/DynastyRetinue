using System;
using JetBrains.Annotations;

namespace Kingmaker.Networking;

public class CustomPortraitReceiver : DataReceiver
{
	public readonly struct Result(PhotonActorNumber playerSource, CustomPortraitMetaData metaData, byte[] bytes)
	{
		public readonly PhotonActorNumber PlayerSource = playerSource;

		public readonly ArraySegment<byte> SmallPortraitBytes = new ArraySegment<byte>(bytes, 0, metaData.LengthSmallPortrait);

		public readonly ArraySegment<byte> HalfPortraitBytes = new ArraySegment<byte>(bytes, metaData.LengthSmallPortrait, metaData.LengthHalfPortrait);

		public readonly ArraySegment<byte> FullPortraitBytes = new ArraySegment<byte>(bytes, metaData.LengthSmallPortrait + metaData.LengthHalfPortrait, metaData.LengthFullPortrait);

		public readonly string CustomPortraitId = metaData.CustomPortraitId;

		public readonly Guid PortraitGuid = metaData.PortraitGuid;
	}

	private readonly Action<Result> m_OnReceived;

	private readonly Action<Guid> m_OnCancel;

	private CustomPortraitMetaData m_MetaData;

	protected override int MainPartLength => m_MetaData.LengthSmallPortrait + m_MetaData.LengthHalfPortrait + m_MetaData.LengthFullPortrait;

	protected override int SenderUniqueNumber => m_MetaData.SenderUniqueNumber;

	public CustomPortraitReceiver(PhotonActorNumber playerSource, [NotNull] PhotonManager sender, [NotNull] Action<Result> onReceived, [NotNull] Action<Guid> onCancel, IProgress<DataTransferProgressInfo> progress)
		: base(playerSource, sender, progress)
	{
		m_OnReceived = onReceived;
		m_OnCancel = onCancel;
	}

	protected override void DeserializeMeta(ReadOnlySpan<byte> bytes)
	{
		m_MetaData = NetMessageSerializer.DeserializeFromSpan<CustomPortraitMetaData>(bytes);
	}

	protected override void OnMainPartReceiveCompleted(PhotonActorNumber playerSource, byte[] bytes)
	{
		m_OnReceived(new Result(playerSource, m_MetaData, bytes));
	}

	public override void OnCancel()
	{
		base.OnCancel();
		m_OnCancel(m_MetaData.PortraitGuid);
	}
}
