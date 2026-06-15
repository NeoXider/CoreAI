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
        private Vector2 _sessionScroll;
        private string[] _roleIds = Array.Empty<string>();
        private int _selectedRoleIndex;
        private string _manualRoleId = "";
        private AgentSessionSnapshot _snapshot;
        private string _statsText = "";
        private string _sessionText = "No snapshot loaded.";
        private string _status = "Pick a role and inspect the live CoreAI container, or the active scene's serialized CoreAILifetimeScope in Edit Mode.";

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
                    }
                }

                if (GUILayout.Button("Refresh", GUILayout.Width(90)))
                {
                    RefreshRolesAndSnapshot();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _manualRoleId = EditorGUILayout.TextField("RoleId", _manualRoleId);
                bool changed = EditorGUI.EndChangeCheck();

                if (GUILayout.Button("Inspect", GUILayout.Width(90)) || changed && Event.current.keyCode == KeyCode.Return)
                {
                    RefreshSnapshotOnly();
                }
            }

            GUILayout.Space(6);
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

                if (GUILayout.Button("Copy both", GUILayout.Width(90)))
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

            float available = Mathf.Max(120f, position.height - 200f);
            EditorGUILayout.LabelField("Statistics", EditorStyles.boldLabel);
            _statsScroll = DrawTextPanel(_statsText, _statsScroll, available * 0.38f);
            GUILayout.Space(4);
            EditorGUILayout.LabelField("Session (what the model sees)", EditorStyles.boldLabel);
            _sessionScroll = DrawTextPanel(_sessionText, _sessionScroll, available * 0.62f);
        }

        private Vector2 DrawTextPanel(string text, Vector2 scroll, float height)
        {
            GUIStyle textStyle = new(EditorStyles.textArea) { wordWrap = false, richText = false };
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(height));
            string content = text ?? "";
            float width = Mathf.Max(200f, position.width - 28f);
            float contentHeight = Mathf.Max(height, textStyle.CalcHeight(new GUIContent(content), width));
            EditorGUILayout.SelectableLabel(content, textStyle, GUILayout.ExpandWidth(true), GUILayout.Height(contentHeight));
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
                    _status = $"{error}\n{editError}";
                    Repaint();
                    return;
                }

                IReadOnlyList<string> editKnown = AgentSessionInspector.GetKnownRoleIds(
                    editContext.Policy,
                    editContext.PromptsDefinition);
                _roleIds = ToArray(editKnown);
                ApplyRoleSelection();
                RefreshSnapshotOnly(editContext);
                return;
            }

            IReadOnlyList<string> known = inspector.GetKnownRoleIds();
            _roleIds = ToArray(known);
            ApplyRoleSelection();

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
            Repaint();
        }

        private void RefreshSnapshotOnly(AgentSessionInspector inspector)
        {
            string roleId = string.IsNullOrWhiteSpace(_manualRoleId)
                ? (_roleIds.Length > 0 ? _roleIds[_selectedRoleIndex] : "")
                : _manualRoleId.Trim();

            if (string.IsNullOrWhiteSpace(roleId))
            {
                _status = "No role id selected.";
                _snapshot = null;
                _statsText = "";
                _sessionText = "";
                Repaint();
                return;
            }

            try
            {
                _snapshot = inspector.Inspect(roleId);
                _statsText = _snapshot.ToStatsText();
                _sessionText = _snapshot.ToSessionText();
                _status = $"Snapshot refreshed for role '{roleId}' via live container.";
            }
            catch (Exception ex)
            {
                _status = $"Inspect failed for role '{roleId}'.";
                _snapshot = null;
                _statsText = "";
                _sessionText = ex.ToString();
            }

            Repaint();
        }

        private void RefreshSnapshotOnly(EditModeInspectorContext context)
        {
            string roleId = string.IsNullOrWhiteSpace(_manualRoleId)
                ? (_roleIds.Length > 0 ? _roleIds[_selectedRoleIndex] : "")
                : _manualRoleId.Trim();

            if (string.IsNullOrWhiteSpace(roleId))
            {
                _status = "No role id selected.";
                _snapshot = null;
                _statsText = "";
                _sessionText = "";
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
                _statsText = _snapshot.ToStatsText();
                _sessionText = _snapshot.ToSessionText();
                _status = $"Snapshot refreshed for role '{roleId}' via edit-mode (serialized scene) from '{context.ScopeName}'.";
            }
            catch (Exception ex)
            {
                _status = $"Edit-mode inspect failed for role '{roleId}'.";
                _snapshot = null;
                _statsText = "";
                _sessionText = ex.ToString();
            }

            Repaint();
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
            LifetimeScope[] scopes = UnityEngine.Object.FindObjectsByType<LifetimeScope>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (scopes == null || scopes.Length == 0)
            {
                error = "No VContainer LifetimeScope found in the loaded scenes. Boot a scene that wires CoreAI, then click Refresh.";
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
                    if (scope.Container.TryResolve(typeof(AgentSessionInspector), out object resolved) &&
                        resolved is AgentSessionInspector found)
                    {
                        inspector = found;
                        return true;
                    }
                }
                catch (Exception)
                {
                    // Scope without the inspector registered — keep scanning.
                }
            }

            error = "Found LifetimeScope(s) but none register AgentSessionInspector. Ensure the scene boots CoreAI (RegisterCorePortable) and is in Play Mode.";
            return false;
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
            CoreAILifetimeScope[] scopes = UnityEngine.Object.FindObjectsByType<CoreAILifetimeScope>(
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
