using System.Threading;
using System.Threading.Tasks;
using Core.Cheats;

namespace Kingmaker.Console.NintendoSwitch2;

public static class Switch2NetworkManager
{
	public readonly struct Token(long length, byte[] data)
	{
		public readonly long Length = length;

		public readonly byte[] Data = data;

		public static readonly Token Invalid;
	}

	public static async Task EnsureConnectionAsync(CancellationToken token)
	{
	}

	public static bool HasToken()
	{
		return false;
	}

	public static async Task<Token> GetNetworkToken()
	{
		return Token.Invalid;
	}

	[Cheat(Name = "get_nsa_token_cheat")]
	public static async Task<string> GetTokenCheat()
	{
		await GetNetworkToken();
		return "Success";
	}
}
