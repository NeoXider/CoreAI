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

        private void Start()
        {
            if (runOnStart) Run();
        }

        [ContextMenu("Run comparison")]
        public void Run()
        {
            try
            {
                // Correctness-only smoke: safe on single-threaded WebGL/WASM (no threads/timers), and forces
                // Lua-CSharp's Lua.dll to run under IL2CPP/AOT — the point of the WebGL viability check.
                string report = LuaVmBench.RunCorrectnessSmoke();
                Debug.Log("[LuaVmComparison]\n" + report);
                _summary = "LuaVmComparison ran OK — " + report;
                _ok = !report.Contains("FAIL");
            }
            catch (System.Exception e)
            {
                _summary = "LuaVmComparison FAILED: " + e.GetType().Name + ": " + e.Message;
                _ok = false;
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
