#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using UnityEngine;

namespace CoreAI.Demos
{
    /// <summary>
    /// Minimal scene driver for Full-tier Lua: shows a target cube the LLM can move/recolor via
    /// <c>unity_*</c> APIs when <see cref="CoreAI.Composition.CoreAILifetimeScope"/> has Full access enabled.
    /// Chat UI is the same CoreAiChatPanel stack as LiveMechanics (Programmer role).
    /// </summary>
    public sealed class FullAccessDemoController : MonoBehaviour
    {
        [SerializeField] private Transform targetCube;

        private void OnGUI()
        {
            if (targetCube == null)
            {
                GUILayout.Label("Assign TargetCube in inspector.");
                return;
            }

            Vector3 p = targetCube.position;
            GUILayout.BeginArea(new Rect(12, 12, 420, 120), GUI.skin.box);
            GUILayout.Label("<b>Full Access Demo</b> — enable Full Lua on CoreAILifetimeScope", Rich());
            GUILayout.Label($"TargetCube position: ({p.x:0.##}, {p.y:0.##}, {p.z:0.##})", Rich());
            GUILayout.EndArea();
        }

        private static GUIStyle Rich()
        {
            return new GUIStyle(GUI.skin.label) { richText = true };
        }
    }
}
#else
using UnityEngine;

namespace CoreAI.Demos
{
    public sealed class FullAccessDemoController : MonoBehaviour
    {
        private void Start()
        {
            Debug.LogWarning("[FullAccessDemo] Lua disabled; demo inactive.");
            enabled = false;
        }
    }
}
#endif
