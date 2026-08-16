using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Kingmaker.EntitySystem.Persistence.Scenes;

public readonly struct TasksWithCombinedProgress(IProgress<float> progress)
{
	private readonly List<Task> m_Tasks = new List<Task>();

	private readonly CombinedProgress m_Progress = new CombinedProgress(progress);

	public void Add(Task task)
	{
		m_Tasks.Add(TaskWithProgress(task, m_Progress.CreateChild()));
	}

	private static async Task TaskWithProgress(Task task, IProgress<float> progress)
	{
		progress.Report(0f);
		await task;
		progress.Report(1f);
	}

	public TaskAwaiter GetAwaiter()
	{
		return Task.WhenAll(m_Tasks).GetAwaiter();
	}
}
