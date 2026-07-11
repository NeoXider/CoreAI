#if !COREAI_NO_LUA
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
        [SerializeField]
        private Transform targetCube;

        [Tooltip("Show or hide the instructions panel.")] [SerializeField]
        private bool _showPanel = true;

        [Tooltip("Hotkey that toggles the panel at runtime. Set to None to disable the hotkey.")] [SerializeField]
        private KeyCode _toggleKey = KeyCode.F7;

        private const float PanelWidth = 440f;
        private const float PanelHeight = 150f;

        private void Awake()
        {
            // Guarantee unity_find('TargetCube') resolves to something even on a bare scene, so the
            // demo works out of the box once Full Lua access is enabled on the scope.
            if (targetCube == null)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Infrastructure.World.CoreAiPrimitiveFactory.EnsureRenderPipelineCompatibleMaterial(cube);
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

        private void Start()
        {
            // Turn on the chat agent/role dropdown for this demo so testers can switch the responding
            // agent (Programmer, SmartChat, AINpc, ...) at runtime without editing the scene config.
            StartCoroutine(EnableAgentDropdownWhenReady());
        }

        private System.Collections.IEnumerator EnableAgentDropdownWhenReady()
        {
            Chat.CoreAiChatPanel panel = null;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                panel = FindFirstObjectByType<Chat.CoreAiChatPanel>(FindObjectsInactive.Include);
                if (panel != null)
                {
                    break;
                }

                yield return null;
            }

            if (panel != null)
            {
                panel.EnableAgentSwitching();
                Debug.Log("[FullAccessDemo] Agent dropdown enabled on CoreAiChatPanel.");
            }
            else
            {
                Debug.LogWarning("[FullAccessDemo] CoreAiChatPanel not found; agent dropdown not enabled.");
            }
        }

        private void Update()
        {
            if (_toggleKey != KeyCode.None && Input.GetKeyDown(_toggleKey))
            {
                _showPanel = !_showPanel;
            }
        }

        private void OnGUI()
        {
            if (targetCube == null || !_showPanel)
            {
                return;
            }

            Vector3 p = targetCube.position;
            GUILayout.BeginArea(new Rect(12, 12, PanelWidth, PanelHeight), GUI.skin.box);
            if (GUI.Button(new Rect(PanelWidth - 58f, 2f, 52f, 18f), "Hide"))
            {
                _showPanel = false;
            }

            GUILayout.Label($"<b>Full Access Demo</b> ({_toggleKey}) - enable Full Lua on CoreAILifetimeScope", Rich());
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