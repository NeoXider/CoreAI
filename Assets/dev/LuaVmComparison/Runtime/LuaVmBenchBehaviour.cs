using UnityEngine;

namespace LuaVmComparison
{
    /// <summary>
    /// Drop on a GameObject in a scene to run the comparison at runtime (used for the WebGL/IL2CPP viability check).
    /// Logs the full Markdown report to the console/player log and paints a short summary on screen so a WebGL
    /// build can be eyeballed without a console. If Lua-CSharp has an AOT/reflection problem under IL2CPP, the
    /// exception surfaces here instead of in the Editor's Mono runtime.
    /// </summary>
    public sealed class LuaVmBenchBehaviour : MonoBehaviour
    {
        [Tooltip("Run automatically on Start.")]
        public bool runOnStart = true;

        private string _summary = "(not run)";
        private bool _ok;
        private string _oneLine = "LUAVM_RESULT: (not run)";
        private float _nextLog;
        private int _reLogsLeft = 6;

        private void Start()
        {
            if (runOnStart) Run();
        }

        // Re-emit a compact one-line verdict a few times so it stays at the tail of the browser console,
        // which is the only part a WebGL host reliably reads back (the full report scrolls out of view).
        private void Update()
        {
            if (_reLogsLeft <= 0 || Time.unscaledTime < _nextLog) return;
            _nextLog = Time.unscaledTime + 2f;
            _reLogsLeft--;
            Debug.Log(_oneLine);
        }

        [ContextMenu("Run comparison")]
        public void Run()
        {
            try
            {
                // Correctness-only smoke: safe on single-threaded WebGL/WASM (no threads/timers), and forces
                // Lua-CSharp's Lua.dll to run under IL2CPP/AOT — the point of the WebGL viability check.
                // Pass Debug.Log as a per-step sink so each step is flushed to the browser console BEFORE it runs;
                // if a step blocks the WASM main thread, the last "LUAVM_STEP:" line names the culprit.
                string report = LuaVmBench.RunCorrectnessSmoke(s => Debug.Log("[LuaVmComparison] " + s));
                Debug.Log("[LuaVmComparison]\n" + report);
                _summary = "LuaVmComparison ran OK — " + report;
                _ok = !report.Contains("FAIL");

                // Compact, greppable, single-line verdict (last line of the report carries the RESULT: ...).
                string resultLine = "unknown";
                foreach (var l in report.Split('\n'))
                    if (l.StartsWith("RESULT:")) { resultLine = l.Trim(); break; }
                _oneLine = "LUAVM_RESULT: " + (_ok ? "OK" : "FAIL") + " — " + resultLine;
            }
            catch (System.Exception e)
            {
                _summary = "LuaVmComparison FAILED: " + e.GetType().Name + ": " + e.Message;
                _ok = false;
                _oneLine = "LUAVM_RESULT: FAIL — " + e.GetType().Name + ": " + e.Message;
                Debug.LogError("[LuaVmComparison] " + e);
            }
        }

        private void OnGUI()
        {
            GUI.color = _ok ? Color.green : Color.red;
            GUI.Label(new Rect(12, 12, Screen.width - 24, 120), _summary);
        }
    }
}
