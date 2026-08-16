using System;
using System.Collections.Generic;
using System.Threading;
using ExitGames.Client.Photon;
using Kingmaker.Networking.Player;

namespace Kingmaker.Networking;

public class AvatarSender : DataSender
{
	private readonly PlayerAvatar m_Avatar;

	public AvatarSender(List<PhotonActorNumber> targetActors, PlayerAvatar avatar, int uniqueNumber, PhotonManager sender, CancellationToken interruptSendingCancellationToken, CancellationToken fullStopSendingCancellationToken)
		: base(targetActors, uniqueNumber, sender, 21, interruptSendingCancellationToken, fullStopSendingCancellationToken)
	{
		m_Avatar = avatar;
	}

	protected override ByteArraySlice GetMetaPackage()
	{
		return NetMessageSerializer.SerializeToSlice(new AvatarMetaData
		{
			SenderUniqueNumber = m_UniqueNumber,
			AvatarWidth = m_Avatar.Width,
			SaveLength = m_Avatar.Data.Length
		});
	}

	protected override ArraySegment<byte> GetMainPartBytes()
	{
		return new ArraySegment<byte>(m_Avatar.Data);
	}
}
