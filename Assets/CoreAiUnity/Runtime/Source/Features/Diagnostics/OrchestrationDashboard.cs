using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM && COREAI_HAS_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// On-screen diagnostics panel for CoreAI orchestration metrics.
    /// </summary>
    public sealed class OrchestrationDashboard : MonoBehaviour
    {
        [Header("Metrics Source")] [Tooltip("Optional metrics source. If null, one is created on Start.")]
        private InMemoryAiOrchestrationMetrics _metrics;

        [Header("Display Settings")] [SerializeField]
        private bool _showDashboard = true;

        [SerializeField] private KeyCode _toggleKey = KeyCode.F9;
        [SerializeField] private float _unresponsiveThresholdSeconds = 300f;

        private Rect _windowRect = new(10, 10, 360, 280);
        private GUIStyle _headerStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _alertStyle;
        private bool _stylesInitialized;

        /// <summary>Assigns the orchestration metrics source displayed by the dashboard.</summary>
        public void SetMetrics(InMemoryAiOrchestrationMetrics metrics)
        {
            _metrics = metrics;
        }

        private void Update()
        {
            if (IsToggleKeyPressedThisFrame())
            {
                _showDashboard = !_showDashboard;
            }
        }

        /// <summary>
        /// Returns whether the configured dashboard toggle key was pressed during this frame.
        /// </summary>
        private bool IsToggleKeyPressedThisFrame()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(_toggleKey))
            {
                return true;
            }
#endif
#if ENABLE_INPUT_SYSTEM && COREAI_HAS_INPUT_SYSTEM
            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                Key key = ToInputSystemKey(_toggleKey);
                if (key != Key.None && kb[key].wasPressedThisFrame)
                {
                    return true;
                }
            }
#endif
            return false;
        }

#if ENABLE_INPUT_SYSTEM && COREAI_HAS_INPUT_SYSTEM
        /// <summary>
        /// Maps a legacy <see cref="KeyCode"/> value to the Input System key enum.
        /// </summary>
        private static Key ToInputSystemKey(KeyCode keyCode)
        {
            switch (keyCode)
            {
                case KeyCode.F1: return Key.F1;
                case KeyCode.F2: return Key.F2;
                case KeyCode.F3: return Key.F3;
                case KeyCode.F4: return Key.F4;
                case KeyCode.F5: return Key.F5;
                case KeyCode.F6: return Key.F6;
                case KeyCode.F7: return Key.F7;
                case KeyCode.F8: return Key.F8;
                case KeyCode.F9: return Key.F9;
                case KeyCode.F10: return Key.F10;
                case KeyCode.F11: return Key.F11;
                case KeyCode.F12: return Key.F12;
                case KeyCode.BackQuote: return Key.Backquote;
                case KeyCode.Tab: return Key.Tab;
                case KeyCode.Escape: return Key.Escape;
                case KeyCode.Return: return Key.Enter;
                case KeyCode.Space: return Key.Space;
                default: return Key.None;
            }
        }
#endif

        private void InitStyles()
        {
            if (_stylesInitialized)
            {
                return;
            }

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 14,
                normal = { textColor = new Color(0.3f, 0.8f, 1f) }
            };
            _valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = Color.white }
            };
            _alertStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                normal = { textColor = new Color(1f, 0.3f, 0.3f) }
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            if (!_showDashboard || _metrics == null)
            {
                return;
            }

            InitStyles();

            _windowRect = GUI.Window(98765, _windowRect, DrawWindow, "CoreAI - Orchestration Dashboard");
        }

        private void DrawWindow(int id)
        {
            StringBuilder sb = new();

            GUILayout.Label("Global Metrics", _headerStyle);

            sb.Clear();
            sb.AppendLine(
                $"  Completions: {_metrics.TotalCompletions} (OK: {_metrics.SuccessfulCompletions}, Fail: {_metrics.FailedCompletions})");
            sb.AppendLine($"  Avg Latency: {_metrics.AverageLatencyMs:F0} ms");
            sb.AppendLine($"  Retries:     {_metrics.StructuredRetries}");
            sb.AppendLine($"  Published:   {_metrics.CommandsPublished}");
            GUILayout.Label(sb.ToString(), _valueStyle);

            // Resolve and cache required local values.
            double secsSinceLast = _metrics.SecondsSinceLastSuccess;
            bool unresponsive = _metrics.IsLlmUnresponsive(_unresponsiveThresholdSeconds);

            GUILayout.Label("Health", _headerStyle);
            GUILayout.Label($"  Last OK: {secsSinceLast:F0}s ago", unresponsive ? _alertStyle : _valueStyle);

            if (unresponsive)
            {
                GUILayout.Label("  ! LLM UNRESPONSIVE", _alertStyle);
            }

            Dictionary<string, InMemoryAiOrchestrationMetrics.RoleMetrics> roles = _metrics.GetAllRoleMetrics();
            if (roles.Count > 0)
            {
                GUILayout.Label("Per-Role", _headerStyle);
                foreach (KeyValuePair<string, InMemoryAiOrchestrationMetrics.RoleMetrics> kvp in roles)
                {
                    InMemoryAiOrchestrationMetrics.RoleMetrics rm = kvp.Value;
                    GUILayout.Label(
                        $"  {kvp.Key}: {rm.Successes}/{rm.Completions} OK, {rm.AverageLatencyMs:F0}ms avg",
                        _valueStyle);
                }
            }

            GUILayout.Label($"\n[{_toggleKey}] toggle  |  drag to move", _valueStyle);
            GUI.DragWindow();
        }
    }
}