using System;
using System.Collections;
using Kingmaker.Utility.ManualCoroutines;

namespace Kingmaker.Controllers;

public static class CoroutinesControllerExtensions
{
	public static CoroutineHandler InvokeInTicks(this CoroutinesController coroutinesController, Action action, int ticks)
	{
		int targetTick = CurrentTick() + ticks;
		return coroutinesController.Start(Delay(action, targetTick));
		static int CurrentTick()
		{
			return Game.Instance.RealTimeController.CurrentSystemStepIndex;
		}
		static IEnumerator Delay(Action action2, int num)
		{
			while (CurrentTick() < num)
			{
				yield return null;
			}
			action2();
		}
	}

	public static CoroutineHandler InvokeInTime(this CoroutinesController coroutinesController, Action action, TimeSpan delay)
	{
		TimeSpan targetTime = CurrentTime() + delay;
		return coroutinesController.Start(Delay(action, targetTime));
		static TimeSpan CurrentTime()
		{
			return Game.Instance.TimeController.RealTime;
		}
		static IEnumerator Delay(Action action2, TimeSpan timeSpan)
		{
			while (CurrentTime() < timeSpan)
			{
				yield return null;
			}
			action2();
		}
	}
}
