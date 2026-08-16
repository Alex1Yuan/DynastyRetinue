using System;
using Kingmaker.Blueprints;
using Kingmaker.PubSubSystem.Core;

namespace Kingmaker.EntitySystem;

public struct BlueprintComponentAndRuntime<TComponent>(TComponent component, EntityFactComponent runtime) where TComponent : BlueprintComponent
{
	public readonly TComponent Component = component;

	public readonly EntityFactComponent Runtime = runtime;

	public TData GetData<TData>() where TData : class
	{
		return Runtime.GetData<TData>();
	}

	public IDisposable RequestEventContext()
	{
		return (Runtime as ISubscriptionProxy)?.RequestEventContext();
	}
}
