using System.Text;
using CoreAI.Ai;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM && COREAI_HAS_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

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
            if (IsToggleKeyPressedThisFrame())
            {
                _showDashboard = !_showDashboard;
            }
        }

        /// <summary>
        /// Совместимо с обоими input-системами Unity: Legacy Input Manager и new Input System Package.
        /// При <c>Active Input Handling = Both</c> сначала пробуем legacy (быстрый путь), затем new.
        /// При установленном только Input System пакете обращение к <c>UnityEngine.Input</c>
        /// бросает <c>InvalidOperationException</c>, поэтому защищены символом <c>ENABLE_LEGACY_INPUT_MANAGER</c>.
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
        /// Маппинг наиболее популярных <see cref="KeyCode"/> → new-Input-System <see cref="Key"/>.
        /// Возвращает <see cref="Key.None"/> для неподдерживаемых, чтобы клиент мог переопределить.
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
