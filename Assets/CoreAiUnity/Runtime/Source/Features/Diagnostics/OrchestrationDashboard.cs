using System.Text;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// Runtime debug dashboard для метрик оркестрации.
    /// Отображает OnGUI overlay с ключевыми метриками LLM-пайплайна.
    /// Включается через CoreAISettings или добавлением компонента на сцену.
    /// </summary>
    public sealed class OrchestrationDashboard : MonoBehaviour
    {
        [Header("Metrics Source")]
        [Tooltip("Ссылка на InMemoryAiOrchestrationMetrics. Если null — создаётся при Start.")]
        private InMemoryAiOrchestrationMetrics _metrics;

        [Header("Display Settings")]
        [SerializeField] private bool _showDashboard = true;
        [SerializeField] private KeyCode _toggleKey = KeyCode.F9;
        [SerializeField] private float _unresponsiveThresholdSeconds = 300f;

        private Rect _windowRect = new(10, 10, 360, 280);
        private GUIStyle _headerStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _alertStyle;
        private bool _stylesInitialized;

        /// <summary>Назначить источник метрик (вызывается из DI / LifetimeScope).</summary>
        public void SetMetrics(InMemoryAiOrchestrationMetrics metrics) => _metrics = metrics;

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey))
            {
                _showDashboard = !_showDashboard;
            }
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

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
            if (!_showDashboard || _metrics == null) return;
            InitStyles();

            _windowRect = GUI.Window(98765, _windowRect, DrawWindow, "CoreAI — Orchestration Dashboard");
        }

        private void DrawWindow(int id)
        {
            StringBuilder sb = new();

            // ─── Global counters ───
            GUILayout.Label("Global Metrics", _headerStyle);

            sb.Clear();
            sb.AppendLine($"  Completions: {_metrics.TotalCompletions} (OK: {_metrics.SuccessfulCompletions}, Fail: {_metrics.FailedCompletions})");
            sb.AppendLine($"  Avg Latency: {_metrics.AverageLatencyMs:F0} ms");
            sb.AppendLine($"  Retries:     {_metrics.StructuredRetries}");
            sb.AppendLine($"  Published:   {_metrics.CommandsPublished}");
            GUILayout.Label(sb.ToString(), _valueStyle);

            // ─── Health ───
            double secsSinceLast = _metrics.SecondsSinceLastSuccess;
            bool unresponsive = _metrics.IsLlmUnresponsive(_unresponsiveThresholdSeconds);

            GUILayout.Label("Health", _headerStyle);
            GUILayout.Label($"  Last OK: {secsSinceLast:F0}s ago", unresponsive ? _alertStyle : _valueStyle);

            if (unresponsive)
            {
                GUILayout.Label("  ⚠ LLM UNRESPONSIVE", _alertStyle);
            }

            // ─── Per-role summary ───
            var roles = _metrics.GetAllRoleMetrics();
            if (roles.Count > 0)
            {
                GUILayout.Label("Per-Role", _headerStyle);
                foreach (var kvp in roles)
                {
                    var rm = kvp.Value;
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
