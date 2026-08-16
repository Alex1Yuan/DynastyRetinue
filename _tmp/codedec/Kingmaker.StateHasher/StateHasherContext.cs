using Kingmaker.ElementsSystem.ContextData;
using Kingmaker.Networking;
using Kingmaker.Networking.Hash;
using Kingmaker.Networking.Serialization;
using Kingmaker.Signals;
using StateHasher.Core;

namespace Kingmaker.StateHasher;

public readonly ref struct StateHasherContext
{
	private readonly GameStateSerializationContext m_Context;

	private StateHasherContext(GameStateSerializationContext context)
	{
		m_Context = context;
		RandomState.Instance.Refresh();
	}

	public HashableState GetHashableState()
	{
		return new HashableState
		{
			player = Game.Instance.State.PlayerState,
			sceneEntitiesState = Game.Instance.State.PlayerState.CrossSceneState,
			areaPersistentState = Game.Instance.State.LoadedAreaState,
			randomState = RandomState.Instance,
			synchronizedData = Game.Instance.SynchronizedDataController.SynchronizedData,
			signalService = SignalService.Instance.State
		};
	}

	public void Dispose()
	{
		m_Context.Dispose();
		RecursiveReferences.Reset();
	}

	public static StateHasherContext Request()
	{
		return new StateHasherContext(ContextData<GameStateSerializationContext>.Request());
	}
}
