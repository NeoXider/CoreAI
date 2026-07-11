using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Diagnostics;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Prompts;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Editor.Diagnostics
{
    /// <summary>
    /// Editor diagnostics window for inspecting the live context of a CoreAI agent role.
    /// </summary>
    public sealed class AgentSessionInspectorWindow : EditorWindow
    {
        private Vector2 _statsScroll;
        private Vector2 _sessionDetailScroll;
        private Vector2 _systemDetailScroll;
        private Vector2 _historyDetailScroll;
        private string[] _roleIds = Array.Empty<string>();
        private int _selectedRoleIndex;
        private int _detailViewIndex;
        private string _manualRoleId = "";
        private AgentSessionSnapshot _snapshot;
        private string _statsText = "";
        private string _sessionText = "No snapshot loaded.";
        private string _systemText = "No snapshot loaded.";
        private string _historyText = "No snapshot loaded.";

        private string _status =
            "Pick a role and inspect the live CoreAI container, or the active scene's serialized CoreAILifetimeScope in Edit Mode.";

        private double _nextKnownRolesRefreshTime;
        private int _modeIndex;
        private Vector2 _liveScroll;
        private string _liveTurnText = "";

        private static readonly string[] DetailViewLabels = { "Session", "System", "History" };
        private static readonly string[] ModeLabels = { "Saved", "Live turn" };

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter> { new StringEnumConverter() }
        };

        [MenuItem("CoreAI/Agent Session Inspector")]
        public static void Open()
        {
            GetWindow<AgentSessionInspectorWindow>("Agent Session Inspector");
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            RefreshRolesAndSnapshot();
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            // Auto-refresh once the runtime container exists, so the user does not have to click Refresh manually.
            if (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.ExitingPlayMode)
            {
                RefreshRolesAndSnapshot();
            }
        }

        private void OnInspectorUpdate()
        {
            if (!Application.isPlaying || EditorApplication.timeSinceStartup < _nextKnownRolesRefreshTime)
            {
                return;
            }

            _nextKnownRolesRefreshTime = EditorApplication.timeSinceStartup + 1.0d;
            if (TryResolveInspector(out AgentSessionInspector inspector, out _) &&
                UpdateKnownRoles(inspector.GetKnownRoleIds()))
            {
                Repaint();
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("CoreAI Agent Session Inspector", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(_status, MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_roleIds.Length == 0))
                {
                    int nextIndex = EditorGUILayout.Popup("Known role", _selectedRoleIndex, _roleIds);
                    if (nextIndex != _selectedRoleIndex)
                    {
                        _selectedRoleIndex = nextIndex;
                        if (_selectedRoleIndex >= 0 && _selectedRoleIndex < _roleIds.Length)
                        {
                            _manualRoleId = _roleIds[_selectedRoleIndex];
                        }

                        RefreshSnapshotOnly();
                        if (_modeIndex == 1)
                        {
                            RefreshLiveTurnOnly();
                        }
                    }
                }

                if (GUILayout.Button("Refresh", GUILayout.Width(90)))
                {
                    RefreshRolesAndSnapshot();
                    if (_modeIndex == 1)
                    {
                        RefreshLiveTurnOnly();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _manualRoleId = EditorGUILayout.TextField("RoleId", _manualRoleId);
                bool changed = EditorGUI.EndChangeCheck();

                if (GUILayout.Button("Inspect", GUILayout.Width(90)) ||
                    (changed && Event.current.keyCode == KeyCode.Return))
                {
                    RefreshSnapshotOnly();
                    if (_modeIndex == 1)
                    {
                        RefreshLiveTurnOnly();
                    }
                }
            }

            GUILayout.Space(6);
            int nextMode = GUILayout.Toolbar(Mathf.Clamp(_modeIndex, 0, ModeLabels.Length - 1), ModeLabels);
            if (nextMode != _modeIndex)
            {
                _modeIndex = nextMode;
                if (_modeIndex == 1)
                {
                    RefreshLiveTurnOnly();
                }
            }

            GUILayout.Space(4);
            if (_modeIndex == 1)
            {
                DrawLiveTurnGui();
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Copy stats", GUILayout.Width(90)))
                {
                    EditorGUIUtility.systemCopyBuffer = _statsText ?? "";
                }

                if (GUILayout.Button("Copy session", GUILayout.Width(100)))
                {
                    EditorGUIUtility.systemCopyBuffer = _sessionText ?? "";
                }

                if (GUILayout.Button("Copy system", GUILayout.Width(100)))
                {
                    EditorGUIUtility.systemCopyBuffer = _systemText ?? "";
                }

                if (GUILayout.Button("Copy history", GUILayout.Width(100)))
                {
                    EditorGUIUtility.systemCopyBuffer = _historyText ?? "";
                }

                if (GUILayout.Button("Copy stats+session", GUILayout.Width(130)))
                {
                    EditorGUIUtility.systemCopyBuffer = (_statsText ?? "") + "\n" + (_sessionText ?? "");
                }

                using (new EditorGUI.DisabledScope(_snapshot == null))
                {
                    if (GUILayout.Button("Copy JSON", GUILayout.Width(90)))
                    {
                        EditorGUIUtility.systemCopyBuffer = _snapshot != null
                            ? SerializeSnapshotToJson(_snapshot)
                            : "";
                    }
                }
            }

            float available = Mathf.Max(120f, position.height - 220f);
            EditorGUILayout.LabelField("Statistics", EditorStyles.boldLabel);
            _statsScroll = DrawTextPanel(_statsText, _statsScroll, available * 0.38f);
            GUILayout.Space(4);
            _detailViewIndex = GUILayout.Toolbar(
                Mathf.Clamp(_detailViewIndex, 0, DetailViewLabels.Length - 1),
                DetailViewLabels);
            EditorGUILayout.LabelField(GetDetailTitle(), EditorStyles.boldLabel);
            Vector2 detailScroll = GetDetailScroll();
            detailScroll = DrawTextPanel(GetDetailText(), detailScroll, available * 0.62f);
            SetDetailScroll(detailScroll);
        }

        private string GetDetailTitle()
        {
            return _detailViewIndex switch
            {
                1 => "System prompt, memory tails, summary, and tools",
                2 => "History without system prompt messages",
                _ => "Session (what the model sees)"
            };
        }

        private string GetDetailText()
        {
            return _detailViewIndex switch
            {
                1 => _systemText,
                2 => _historyText,
                _ => _sessionText
            };
        }

        private Vector2 GetDetailScroll()
        {
            return _detailViewIndex switch
            {
                1 => _systemDetailScroll,
                2 => _historyDetailScroll,
                _ => _sessionDetailScroll
            };
        }

        private void SetDetailScroll(Vector2 scroll)
        {
            switch (_detailViewIndex)
            {
                case 1:
                    _systemDetailScroll = scroll;
                    break;
                case 2:
                    _historyDetailScroll = scroll;
                    break;
                default:
                    _sessionDetailScroll = scroll;
                    break;
            }
        }

        private Vector2 DrawTextPanel(string text, Vector2 scroll, float height)
        {
            GUIStyle textStyle = new(EditorStyles.textArea) { wordWrap = false, richText = false };
            string content = text ?? "";
            float width = Mathf.Max(200f, position.width - 28f);
            float panelHeight = Mathf.Max(1f, height);
            float contentHeight = Mathf.Max(panelHeight, textStyle.CalcHeight(new GUIContent(content), width));
            scroll.y = Mathf.Clamp(scroll.y, 0f, Mathf.Max(0f, contentHeight - panelHeight));

            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(panelHeight));
            EditorGUILayout.SelectableLabel(content, textStyle, GUILayout.ExpandWidth(true),
                GUILayout.Height(contentHeight));
            EditorGUILayout.EndScrollView();
            return scroll;
        }

        public static string SerializeSnapshotToJson(AgentSessionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            return JsonConvert.SerializeObject(snapshot, JsonSettings);
        }

        private void RefreshRolesAndSnapshot()
        {
            if (!TryResolveInspector(out AgentSessionInspector inspector, out string error))
            {
                if (!TryBuildEditModeContext(out EditModeInspectorContext editContext, out string editError))
                {
                    _roleIds = Array.Empty<string>();
                    _selectedRoleIndex = 0;
                    _snapshot = null;
                    _statsText = "";
                    _sessionText = "";
                    _systemText = "";
                    _historyText = "";
                    _status = $"{error}\n{editError}";
                    Repaint();
                    return;
                }

                IReadOnlyList<string> editKnown = AgentSessionInspector.GetKnownRoleIds(
                    editContext.Policy,
                    editContext.PromptsDefinition);
                UpdateKnownRoles(editKnown);
                RefreshSnapshotOnly(editContext);
                return;
            }

            UpdateKnownRoles(inspector.GetKnownRoleIds());
            RefreshSnapshotOnly(inspector);
        }

        private void RefreshSnapshotOnly()
        {
            if (TryResolveInspector(out AgentSessionInspector inspector, out string error))
            {
                RefreshSnapshotOnly(inspector);
                return;
            }

            if (TryBuildEditModeContext(out EditModeInspectorContext editContext, out string editError))
            {
                RefreshSnapshotOnly(editContext);
                return;
            }

            _status = $"{error}\n{editError}";
            _snapshot = null;
            _statsText = "";
            _sessionText = "";
            _systemText = "";
            _historyText = "";
            Repaint();
        }

        private void RefreshSnapshotOnly(AgentSessionInspector inspector)
        {
            UpdateKnownRoles(inspector.GetKnownRoleIds());
            string roleId = string.IsNullOrWhiteSpace(_manualRoleId)
                ? _roleIds.Length > 0 ? _roleIds[_selectedRoleIndex] : ""
                : _manualRoleId.Trim();

            if (string.IsNullOrWhiteSpace(roleId))
            {
                _status = "No role id selected.";
                _snapshot = null;
                _statsText = "";
                _sessionText = "";
                _systemText = "";
                _historyText = "";
                Repaint();
                return;
            }

            try
            {
                _snapshot = inspector.Inspect(roleId);
                ApplySnapshotTexts();
                _status = $"Snapshot refreshed for role '{roleId}' via live container.";
            }
            catch (Exception ex)
            {
                _status = $"Inspect failed for role '{roleId}'.";
                _snapshot = null;
                _statsText = "";
                _sessionText = ex.ToString();
                _systemText = ex.ToString();
                _historyText = ex.ToString();
            }

            Repaint();
        }

        private void RefreshSnapshotOnly(EditModeInspectorContext context)
        {
            UpdateKnownRoles(AgentSessionInspector.GetKnownRoleIds(context.Policy, context.PromptsDefinition));
            string roleId = string.IsNullOrWhiteSpace(_manualRoleId)
                ? _roleIds.Length > 0 ? _roleIds[_selectedRoleIndex] : ""
                : _manualRoleId.Trim();

            if (string.IsNullOrWhiteSpace(roleId))
            {
                _status = "No role id selected.";
                _snapshot = null;
                _statsText = "";
                _sessionText = "";
                _systemText = "";
                _historyText = "";
                Repaint();
                return;
            }

            try
            {
                _snapshot = AgentSessionInspector.InspectSerializedInputs(
                    roleId,
                    context.Settings,
                    context.PromptsDefinition,
                    context.Policy,
                    context.MemoryStore,
                    context.SummaryStore,
                    context.FallbackSystemPrompts);
                ApplySnapshotTexts();
                _status =
                    $"Snapshot refreshed for role '{roleId}' via edit-mode (serialized scene) from '{context.ScopeName}'.";
            }
            catch (Exception ex)
            {
                _status = $"Edit-mode inspect failed for role '{roleId}'.";
                _snapshot = null;
                _statsText = "";
                _sessionText = ex.ToString();
                _systemText = ex.ToString();
                _historyText = ex.ToString();
            }

            Repaint();
        }

        private void ApplySnapshotTexts()
        {
            _statsText = _snapshot?.ToStatsText() ?? "";
            _sessionText = _snapshot?.ToSessionText() ?? "";
            _systemText = _snapshot?.ToSystemPromptText() ?? "";
            _historyText = _snapshot?.ToHistoryText() ?? "";
        }

        private void DrawLiveTurnGui()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh live turn", GUILayout.Width(140)))
                {
                    RefreshLiveTurnOnly();
                }

                if (GUILayout.Button("Copy live turn", GUILayout.Width(120)))
                {
                    EditorGUIUtility.systemCopyBuffer = _liveTurnText ?? "";
                }
            }

            EditorGUILayout.HelpBox(
                "Live turn shows the latest in-flight or completed turn from the agent turn-trace sink " +
                "(composed prompt, tool calls, assistant text, status, and timing). Unlike Saved, it does " +
                "not depend on persisted chat history, so mid-turn and failed turns are visible.",
                MessageType.None);

            float available = Mathf.Max(160f, position.height - 240f);
            EditorGUILayout.LabelField("Latest turn trace", EditorStyles.boldLabel);
            _liveScroll = DrawTextPanel(_liveTurnText, _liveScroll, available);
        }

        private void RefreshLiveTurnOnly()
        {
            string roleId = string.IsNullOrWhiteSpace(_manualRoleId)
                ? _roleIds.Length > 0 ? _roleIds[_selectedRoleIndex] : ""
                : _manualRoleId.Trim();

            if (string.IsNullOrWhiteSpace(roleId))
            {
                _liveTurnText = "No role id selected.";
                Repaint();
                return;
            }

            if (!TryResolveTraceReader(out IAgentTurnTraceReader reader, out string error))
            {
                _liveTurnText = $"Live trace unavailable.\n{error}";
                Repaint();
                return;
            }

            if (!reader.TryGetLatestTrace(roleId, out AgentTurnTrace trace) || trace == null)
            {
                _liveTurnText = $"No turn recorded yet for role '{roleId}'.\n" +
                                "Trigger an agent turn for this role, then click Refresh live turn.";
                Repaint();
                return;
            }

            _liveTurnText = FormatLiveTurn(trace);
            Repaint();
        }

        private static bool TryResolveTraceReader(out IAgentTurnTraceReader reader, out string error)
        {
            reader = null;
            error = "";

            if (!Application.isPlaying)
            {
                error = "Not in Play Mode. Live turn traces are only available from the running container.";
                return false;
            }

            LifetimeScope[] scopes = FindObjectsByType<LifetimeScope>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (scopes == null || scopes.Length == 0)
            {
                error = "No VContainer LifetimeScope found in the loaded scenes.";
                return false;
            }

            foreach (LifetimeScope scope in scopes)
            {
                if (scope == null || scope.Container == null)
                {
                    continue;
                }

                try
                {
                    if (scope.Container.TryResolve(typeof(IAgentTurnTraceReader), out object resolved) &&
                        resolved is IAgentTurnTraceReader found)
                    {
                        reader = found;
                        return true;
                    }

                    if (scope.Container.TryResolve(typeof(IAgentTurnTraceSink), out object sink) &&
                        sink is IAgentTurnTraceReader readerSink)
                    {
                        reader = readerSink;
                        return true;
                    }
                }
                catch (Exception)
                {
                    // Scope without the reader registered — keep scanning.
                }
            }

            error = "The active container registers no readable turn-trace sink (a NullAgentTurnTraceSink " +
                    "is the default). Register an InMemoryAgentTurnTraceSink (or any IAgentTurnTraceReader) " +
                    "to capture live turns.";
            return false;
        }

        private static string FormatLiveTurn(AgentTurnTrace trace)
        {
            System.Text.StringBuilder sb = new();
            sb.AppendLine("CoreAI Live Turn");
            sb.AppendLine("================");
            sb.AppendLine($"RoleId: {trace.RoleId}");
            sb.AppendLine($"TraceId: {trace.TraceId}");
            sb.AppendLine($"Status: {trace.Status}");
            sb.AppendLine($"Model: {EmptyLabel(trace.Model)}");

            string recordedAt = trace.RecordedAtUtcTicks > 0
                ? new DateTime(trace.RecordedAtUtcTicks, DateTimeKind.Utc).ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss")
                : "(unknown)";
            sb.AppendLine($"Recorded at: {recordedAt}");
            sb.AppendLine(
                $"Tokens: prompt {trace.PromptTokens}, completion {trace.CompletionTokens}, total {trace.TotalTokens}");
            if (trace.CacheReadTokens > 0 || trace.CacheWriteTokens > 0)
            {
                sb.AppendLine($"Cache tokens: read {trace.CacheReadTokens}, write {trace.CacheWriteTokens}");
            }

            sb.AppendLine($"Chat history messages: {trace.ChatHistoryMessageCount}");
            sb.AppendLine();

            sb.AppendLine("User Prompt");
            sb.AppendLine("-----------");
            sb.AppendLine(EmptyLabel(trace.UserPayload));
            sb.AppendLine();

            sb.AppendLine("Tool Calls");
            sb.AppendLine("----------");
            if (trace.ToolCalls == null || trace.ToolCalls.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                for (int i = 0; i < trace.ToolCalls.Count; i++)
                {
                    AgentTurnToolCallTrace call = trace.ToolCalls[i];
                    string outcome = call.Success ? "ok" : "FAILED";
                    sb.AppendLine(
                        $"{i + 1}. {call.Name} [{outcome}] ({call.DurationMs:0.#} ms, source={EmptyLabel(call.Source)})");
                    sb.AppendLine($"   {EmptyLabel(call.Detail)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Assistant Response");
            sb.AppendLine("------------------");
            sb.AppendLine(EmptyLabel(trace.AssistantResponse));

            if (!string.IsNullOrWhiteSpace(trace.Error))
            {
                sb.AppendLine();
                sb.AppendLine("Error");
                sb.AppendLine("-----");
                sb.AppendLine(trace.Error);
            }

            sb.AppendLine();
            sb.AppendLine("System Prompt Preview");
            sb.AppendLine("---------------------");
            sb.AppendLine(EmptyLabel(trace.SystemPromptPreview));

            return sb.ToString();
        }

        private static string EmptyLabel(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
        }

        private static bool TryResolveInspector(out AgentSessionInspector inspector, out string error)
        {
            inspector = null;
            error = "";

            if (!Application.isPlaying)
            {
                error = "No live container: not in Play Mode.";
                return false;
            }

            // Resolve from ANY live VContainer scope that registered the inspector — not just the concrete
            // CoreAILifetimeScope type. Demos and games may boot CoreAI through their own scope; this finds
            // whichever container exposes AgentSessionInspector.
            LifetimeScope[] scopes = FindObjectsByType<LifetimeScope>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (scopes == null || scopes.Length == 0)
            {
                error =
                    "No VContainer LifetimeScope found in the loaded scenes. Boot a scene that wires CoreAI, then click Refresh.";
                return false;
            }

            AgentSessionInspector bestInspector = null;
            int bestScore = int.MinValue;
            foreach (LifetimeScope scope in scopes)
            {
                if (scope == null || scope.Container == null)
                {
                    continue;
                }

                try
                {
                    if (scope.Container.TryResolve(typeof(AgentSessionInspector), out object resolved) &&
                        resolved is AgentSessionInspector found)
                    {
                        int score = ComputeInspectorCandidateScore(found, GetScopeDepth(scope.transform));
                        if (bestInspector == null || score >= bestScore)
                        {
                            bestInspector = found;
                            bestScore = score;
                        }
                    }
                }
                catch (Exception)
                {
                    // Scope without the inspector registered — keep scanning.
                }
            }

            if (bestInspector != null)
            {
                inspector = bestInspector;
                return true;
            }

            error =
                "Found LifetimeScope(s) but none register AgentSessionInspector. Ensure the scene boots CoreAI (RegisterCorePortable) and is in Play Mode.";
            return false;
        }

        private static int ComputeInspectorCandidateScore(AgentSessionInspector inspector, int scopeDepth)
        {
            int roleCount = 0;
            try
            {
                roleCount = inspector?.GetKnownRoleIds()?.Count ?? 0;
            }
            catch (Exception)
            {
                roleCount = 0;
            }

            // Project child scopes commonly add game-specific agents/tools over the parent CoreAI scope.
            // Prefer the inspector with the richest role set; use hierarchy depth as a stable tie-breaker.
            return roleCount * 1000 + Mathf.Max(0, scopeDepth);
        }

        private static int GetScopeDepth(Transform transform)
        {
            int depth = 0;
            while (transform != null)
            {
                depth++;
                transform = transform.parent;
            }

            return depth;
        }

        private static bool TryBuildEditModeContext(
            out EditModeInspectorContext context,
            out string error)
        {
            context = null;
            error = "";

            CoreAILifetimeScope scope = FindActiveSceneCoreAiScope();
            if (scope == null)
            {
                Scene activeScene = SceneManager.GetActiveScene();
                error = $"No serialized CoreAILifetimeScope found in the active scene '{activeScene.name}'.";
                return false;
            }

            SerializedObject serializedScope = new(scope);
            CoreAISettingsAsset settings = ReadObjectReference<CoreAISettingsAsset>(
                serializedScope,
                "coreAiSettings");
            settings ??= Resources.Load<CoreAISettingsAsset>("CoreAISettings");
            if (settings == null)
            {
                error = "CoreAILifetimeScope has no CoreAISettingsAsset, and Resources/CoreAISettings was not found.";
                return false;
            }

            AgentPromptsManifest manifest = ReadObjectReference<AgentPromptsManifest>(
                serializedScope,
                "agentPromptsManifest");
            AgentPromptsDefinition promptsDefinition = manifest != null ? manifest.ToDefinition() : null;

            string persistentRoot = Path.Combine(
                Application.persistentDataPath,
                CoreAiPersistentPaths.RootFolderName);
            context = new EditModeInspectorContext
            {
                ScopeName = scope.name,
                Settings = settings,
                PromptsDefinition = promptsDefinition,
                Policy = new AgentMemoryPolicy(),
                MemoryStore = new FileAgentMemoryStore(
                    rootDirectory: Path.Combine(persistentRoot, CoreAiPersistentPaths.AgentMemory)),
                SummaryStore = new FileConversationSummaryStore(
                    Path.Combine(persistentRoot, CoreAiPersistentPaths.ConversationSummaries)),
                FallbackSystemPrompts = new ResourcesAgentSystemPromptProvider("AgentPrompts/System")
            };
            return true;
        }

        private static CoreAILifetimeScope FindActiveSceneCoreAiScope()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            CoreAILifetimeScope[] scopes = FindObjectsByType<CoreAILifetimeScope>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < scopes.Length; i++)
            {
                CoreAILifetimeScope scope = scopes[i];
                if (scope != null && scope.gameObject.scene == activeScene)
                {
                    return scope;
                }
            }

            return null;
        }

        private static T ReadObjectReference<T>(SerializedObject serializedObject, string propertyName)
            where T : UnityEngine.Object
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            return property?.objectReferenceValue as T;
        }

        private void ApplyRoleSelection()
        {
            if (_roleIds.Length == 0)
            {
                _selectedRoleIndex = 0;
                return;
            }

            _selectedRoleIndex = Mathf.Clamp(_selectedRoleIndex, 0, _roleIds.Length - 1);
            if (string.IsNullOrWhiteSpace(_manualRoleId))
            {
                _manualRoleId = _roleIds[_selectedRoleIndex];
                return;
            }

            for (int i = 0; i < _roleIds.Length; i++)
            {
                if (string.Equals(_roleIds[i], _manualRoleId.Trim(), StringComparison.Ordinal))
                {
                    _selectedRoleIndex = i;
                    return;
                }
            }
        }

        private bool UpdateKnownRoles(IReadOnlyList<string> knownRoles)
        {
            string[] next = ToArray(knownRoles);
            if (ArraysEqual(_roleIds, next))
            {
                return false;
            }

            _roleIds = next;
            ApplyRoleSelection();
            return true;
        }

        private static bool ArraysEqual(string[] a, string[] b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static string[] ToArray(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return Array.Empty<string>();
            }

            List<string> list = new(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    list.Add(values[i]);
                }
            }

            return list.ToArray();
        }

        private sealed class EditModeInspectorContext
        {
            public string ScopeName;
            public CoreAISettingsAsset Settings;
            public AgentPromptsDefinition PromptsDefinition;
            public AgentMemoryPolicy Policy;
            public FileAgentMemoryStore MemoryStore;
            public FileConversationSummaryStore SummaryStore;
            public IAgentSystemPromptProvider FallbackSystemPrompts;
        }
    }
}
