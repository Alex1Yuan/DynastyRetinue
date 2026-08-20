using System;
using System.Collections;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>极小的延帧执行器。</summary>
    public static class Deferred
    {
        private sealed class Runner : MonoBehaviour
        {
            public void Go(int frames, Action a) { StartCoroutine(Co(frames, a)); }
            private IEnumerator Co(int frames, Action a)
            {
                for (int i = 0; i < frames; i++) yield return null;
                try { if (a != null) a(); }
                catch (Exception e) { Main.LogError("[Deferred] 回调异常: " + e); }
            }
        }

        private static Runner _runner;

        private static Runner Get()
        {
            if (_runner == null)
            {
                GameObject go = new GameObject("DynastyRetinue_Deferred");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.DontSave;   // 不要 HideAndDontSave：那样场景卸载也回收不了
                _runner = go.AddComponent<Runner>();
            }
            return _runner;
        }

        public static void NextFrames(int frames, Action a)
        {
            try { Get().Go(frames < 1 ? 1 : frames, a); }
            catch (Exception e)
            {
                Main.LogError("[Deferred] 调度失败，改为立即执行: " + e.Message);
                if (a != null) a();
            }
        }

        public static void Shutdown()
        {
            try { if (_runner != null) UnityEngine.Object.Destroy(_runner.gameObject); } catch { }
            _runner = null;
        }
    }
}
