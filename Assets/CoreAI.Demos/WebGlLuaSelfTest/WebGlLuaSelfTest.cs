#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Sandbox;
using UnityEngine;

namespace CoreAI.Demos
{
    /// <summary>
    /// Runtime self-test for the Lua sandbox, meant for a WebGL-player build where EditMode/PlayMode
    /// runners are unavailable. On <see cref="Start"/> it runs <see cref="SecureLuaEnvironment.TryRunSelfTest"/>
    /// and logs + renders a PASS/FAIL report. Attach to a GameObject in a scene and build to WebGL with
    /// CoreAISettingsAsset.EnableLuaOnWebGl = true to verify the sandbox survives IL2CPP stripping.
    /// </summary>
    public sealed class WebGlLuaSelfTest : MonoBehaviour
    {
        private string _report = "Lua self-test: not run yet.";
        private bool _passed;

        private void Start()
        {
            _passed = SecureLuaEnvironment.TryRunSelfTest(out string report);
            _report = report;
            if (_passed)
            {
                Debug.Log("[CoreAI][WebGlLuaSelfTest] PASS\n" + _report);
            }
            else
            {
                Debug.LogError("[CoreAI][WebGlLuaSelfTest] FAIL\n" + _report);
            }
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 520, 240), GUI.skin.box);
            GUILayout.Label("CoreAI Lua sandbox self-test: " + (_passed ? "PASS" : "FAIL"));
            GUILayout.Label(_report);
            GUILayout.EndArea();
        }
    }
}
#else
using UnityEngine;

namespace CoreAI.Demos
{
    /// <summary>No-op fallback when the MoonSharp Lua package is stripped (no-lua build).</summary>
    public sealed class WebGlLuaSelfTest : MonoBehaviour
    {
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 520, 60), GUI.skin.box);
            GUILayout.Label("CoreAI Lua sandbox self-test unavailable: MoonSharp package not present.");
            GUILayout.EndArea();
        }
    }
}
#endif
