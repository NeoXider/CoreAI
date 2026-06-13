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
        [Tooltip("Object the LLM manipulates via unity_* APIs. Auto-created as 'TargetCube' when empty.")]
        [SerializeField] private Transform targetCube;

        private void Awake()
        {
            // Guarantee unity_find('TargetCube') resolves to something even on a bare scene, so the
            // demo works out of the box once Full Lua access is enabled on the scope.
            if (targetCube == null)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "TargetCube";
                cube.transform.position = new Vector3(0f, 0.5f, 0f);
                targetCube = cube.transform;
            }
            else if (targetCube.name != "TargetCube")
            {
                // The prompts refer to it by name; keep find-by-name reliable.
                targetCube.name = "TargetCube";
            }
        }

        private void OnGUI()
        {
            if (targetCube == null)
            {
                return;
            }

            Vector3 p = targetCube.position;
            GUILayout.BeginArea(new Rect(12, 12, 440, 150), GUI.skin.box);
            GUILayout.Label("<b>Full Access Demo</b> — enable Full Lua on CoreAILifetimeScope", Rich());
            GUILayout.Label("Programmer mods reach this cube via unity_find / unity_set_member.", Rich());
            GUILayout.Label("Private members need 'Enable Full Lua Private Access' (off by default).", Rich());
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
