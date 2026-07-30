#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using LLMUnity;
#endif

namespace CoreAI.Infrastructure.Llm.Editor
{
    /// <summary>
    /// UI Toolkit inspector for CoreAISettingsAsset. Layout lives in
    /// CoreAISettingsAssetEditor.uxml (styles in the .uss next to it); this class loads the
    /// tree, binds it, and wires the dynamic behavior: conditional rows, the advanced tabs,
    /// the GGUF picker, and the connection test.
    /// </summary>
    [CustomEditor(typeof(CoreAISettingsAsset))]
    public sealed class CoreAISettingsAssetEditor : UnityEditor.Editor
    {
        private const string UxmlPath = "Assets/CoreAiUnity/Editor/CoreAISettingsAssetEditor.uxml";
        private const string PrefAdvancedOpen = "CoreAI.SettingsEditor.AdvancedOpen";
        private const string PrefActiveTab = "CoreAI.SettingsEditor.ActiveTab";

        private static readonly string[] HttpModelPresets =
        {
            "gpt-4o-mini",
            "gpt-4o",
            "gpt-4.1-mini",
            "gpt-4.1",
            "o4-mini",
            "qwen3.5-4b",
            "qwen3.5-8b",
            "llama-3.1-8b-instruct",
            "llama-3.2-3b-instruct",
            "deepseek-chat"
        };

        private const string CustomPresetLabel = "(custom)";

        // Connection-test state.
        private bool _isTestingConnection;
        private string _testResultMessage;
        private MessageType _testResultType;

        // Dynamic elements resolved from the UXML tree.
        private VisualElement _autoPriorityRow;
        private VisualElement _httpEssentials;
        private VisualElement _llmUnityEssentials;
        private HelpBox _productionWarning;
        private HelpBox _webGlStreamingHint;
        private DropdownField _modelPreset;
        private VisualElement _modelRow;
        private HelpBox _modelMissingWarning;
        private HelpBox _serverManagedModelInfo;
        private VisualElement _httpAdvancedGroup;
        private HelpBox _serverManagedWarning;
        private HelpBox _httpAutoHint;
        private HelpBox _fallbackIncompleteWarning;
        private HelpBox _fallbackActiveInfo;
        private HelpBox _webGlNoNativeWarning;
        private HelpBox _webGlCredentialsInfo;
        private VisualElement _llmUnityAdvancedGroup;
        private HelpBox _llmUnityAutoHint;
        private PropertyField _temperatureField;
        private PropertyField _maxTokensField;
        private PropertyField _contextWindowField;
        private HelpBox _emptyPrefixHint;
        private VisualElement _offlineCustomGroup;
        private HelpBox _offlineDefaultInfo;
        private Button _testButton;
        private Label _routeHint;
        private HelpBox _testResultBox;
        private DropdownField _ggufDropdown;
        private HelpBox _ggufEmptyHint;
        private readonly List<string> _ggufFileNames = new();

        public override VisualElement CreateInspectorGUI()
        {
            serializedObject.Update();

            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (tree == null)
            {
                VisualElement fallback = new();
                fallback.Add(new HelpBox(
                    $"CoreAI settings inspector layout is missing: {UxmlPath}. Showing the default inspector.",
                    HelpBoxMessageType.Error));
                InspectorElement.FillDefaultInspector(fallback, serializedObject, this);
                return fallback;
            }

            VisualElement root = tree.Instantiate();
            QueryDynamicElements(root);
            WireAdvancedTabs(root);
            WireModelPreset();
            WireGgufPicker(root);
            WireConnectionTest();
            WireFooter(root);
            BuildLlmUnityStatus(root.Q<VisualElement>("llmunity-status-container"));

            // One tracker refreshes every conditional row/warning; cheaper and simpler than
            // wiring a callback per field.
            root.TrackSerializedObjectValue(serializedObject, _ => RefreshDynamicState());
            root.RegisterCallback<AttachToPanelEvent>(_ => RefreshDynamicState());
            return root;
        }

        private CoreAISettingsAsset Settings => (CoreAISettingsAsset)target;

        private void QueryDynamicElements(VisualElement root)
        {
            _autoPriorityRow = root.Q<VisualElement>("auto-priority-row");
            _httpEssentials = root.Q<VisualElement>("http-essentials");
            _llmUnityEssentials = root.Q<VisualElement>("llmunity-essentials");
            _productionWarning = root.Q<HelpBox>("production-warning");
            _webGlStreamingHint = root.Q<HelpBox>("webgl-streaming-hint");
            _modelPreset = root.Q<DropdownField>("model-preset");
            _modelRow = root.Q<VisualElement>("model-row");
            _modelMissingWarning = root.Q<HelpBox>("model-missing-warning");
            _serverManagedModelInfo = root.Q<HelpBox>("servermanaged-model-info");
            _httpAdvancedGroup = root.Q<VisualElement>("http-advanced-group");
            _serverManagedWarning = root.Q<HelpBox>("servermanaged-warning");
            _httpAutoHint = root.Q<HelpBox>("http-auto-hint");
            _fallbackIncompleteWarning = root.Q<HelpBox>("fallback-incomplete-warning");
            _fallbackActiveInfo = root.Q<HelpBox>("fallback-active-info");
            _webGlNoNativeWarning = root.Q<HelpBox>("webgl-no-native-warning");
            _webGlCredentialsInfo = root.Q<HelpBox>("webgl-credentials-info");
            _llmUnityAdvancedGroup = root.Q<VisualElement>("llmunity-advanced-group");
            _llmUnityAutoHint = root.Q<HelpBox>("llmunity-auto-hint");
            _temperatureField = root.Q<PropertyField>("temperature-field");
            _maxTokensField = root.Q<PropertyField>("max-tokens-field");
            _contextWindowField = root.Q<PropertyField>("context-window-field");
            _emptyPrefixHint = root.Q<HelpBox>("empty-prefix-hint");
            _offlineCustomGroup = root.Q<VisualElement>("offline-custom-group");
            _offlineDefaultInfo = root.Q<HelpBox>("offline-default-info");
            _testButton = root.Q<Button>("test-button");
            _routeHint = root.Q<Label>("route-hint");
            _testResultBox = root.Q<HelpBox>("test-result");
            _ggufDropdown = root.Q<DropdownField>("gguf-dropdown");
            _ggufEmptyHint = root.Q<HelpBox>("gguf-empty-hint");
        }

        private void WireAdvancedTabs(VisualElement root)
        {
            Foldout advanced = root.Q<Foldout>("advanced-foldout");
            advanced.value = EditorPrefs.GetBool(PrefAdvancedOpen, false);
            advanced.RegisterValueChangedCallback(evt =>
            {
                if (ReferenceEquals(evt.target, advanced))
                {
                    EditorPrefs.SetBool(PrefAdvancedOpen, evt.newValue);
                }
            });

            TabView tabs = root.Q<TabView>("advanced-tabs");
            VisualElement chipBar = root.Q<VisualElement>("tab-chip-bar");
            List<Tab> tabList = tabs.Query<Tab>().ToList();
            List<Button> chips = new(tabList.Count);
            for (int i = 0; i < tabList.Count; i++)
            {
                int index = i;
                Button chip = new(() => tabs.selectedTabIndex = index) { text = tabList[i].label };
                chip.AddToClassList("coreai-tab-chip");
                chipBar.Add(chip);
                chips.Add(chip);
            }

            void SyncChips()
            {
                for (int i = 0; i < chips.Count; i++)
                {
                    chips[i].EnableInClassList("coreai-tab-chip--selected", i == tabs.selectedTabIndex);
                }
            }

            int savedTab = EditorPrefs.GetInt(PrefActiveTab, 0);
            if (savedTab >= 0 && savedTab < tabList.Count)
            {
                tabs.selectedTabIndex = savedTab;
            }

            tabs.activeTabChanged += (_, _) =>
            {
                EditorPrefs.SetInt(PrefActiveTab, tabs.selectedTabIndex);
                SyncChips();
            };
            SyncChips();
        }

        private void WireModelPreset()
        {
            List<string> choices = new() { CustomPresetLabel };
            choices.AddRange(HttpModelPresets);
            _modelPreset.choices = choices;
            _modelPreset.SetValueWithoutNotify(CustomPresetLabel);
            _modelPreset.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == CustomPresetLabel)
                {
                    return;
                }

                serializedObject.FindProperty("modelName").stringValue = evt.newValue;
                serializedObject.ApplyModifiedProperties();
            });
        }

        private void WireGgufPicker(VisualElement root)
        {
            VisualElement ggufRow = root.Q<VisualElement>("gguf-row");
            TextField manual = root.Q<TextField>("gguf-manual");
            VisualElement fallbackGroup = root.Q<VisualElement>("gguf-fallback-group");

#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            Show(fallbackGroup, false);
            manual.isDelayed = true;

            _ggufDropdown.RegisterValueChangedCallback(evt =>
            {
                int index = _ggufDropdown.choices.IndexOf(evt.newValue);
                if (index >= 0 && index < _ggufFileNames.Count)
                {
                    serializedObject.FindProperty("ggufModelPath").stringValue = _ggufFileNames[index];
                    serializedObject.ApplyModifiedProperties();
                }
            });

            root.Q<Button>("gguf-browse").clicked += () =>
            {
                string path = EditorUtility.OpenFilePanel("Select GGUF Model", "", "gguf");
                if (!string.IsNullOrEmpty(path))
                {
                    serializedObject.FindProperty("ggufModelPath").stringValue = Path.GetFileName(path);
                    serializedObject.ApplyModifiedProperties();
                }
            };

            root.Q<Button>("gguf-rescan").clicked += () =>
            {
                try
                {
                    // WHY: never call LLMManager.LoadFromDisk() directly here — it replaces the whole model
                    // registry with the (usually empty) build snapshot, and the next Model Manager write
                    // then persists that empty list, losing every registration.
                    LlmUnityModelBootstrap.RefreshModelEntries();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CoreAI] GGUF rescan: LLMUnity model rescan failed: {ex.Message}");
                }

                RefreshGgufChoices();
            };

            RefreshGgufChoices();
#else
            // Without the LLMUnity package the dropdown/browse/rescan row is meaningless:
            // show the plain path field instead.
            Show(ggufRow, false);
            Show(manual, false);
            Show(_ggufEmptyHint, false);
            Show(fallbackGroup, true);
#endif
        }

        private void WireConnectionTest()
        {
            _testButton.clicked += () => TestConnection(Settings);
        }

        private void WireFooter(VisualElement root)
        {
            root.Q<Button>("copy-key-button").clicked += () =>
            {
                EditorGUIUtility.systemCopyBuffer = Settings.ApiKey;
                Debug.Log("[CoreAI] API Key copied to clipboard");
            };

            root.Q<Button>("reset-button").clicked += () =>
            {
                if (EditorUtility.DisplayDialog("Reset Settings",
                        "Reset all settings to default values?",
                        "Reset", "Cancel"))
                {
                    Settings.ConfigureAuto();
                    Settings.ConfigureHttpApi("http://localhost:1234/v1", "", "gpt-4o-mini");
                    Settings.ConfigureLlmUnity();
                    EditorUtility.SetDirty(target);
                    serializedObject.Update();
                    RefreshDynamicState();
                }
            };
        }

        private static void BuildLlmUnityStatus(VisualElement container)
        {
            if (container == null)
            {
                return;
            }

            container.AddToClassList("coreai-card");
            container.AddToClassList("coreai-card--status");

            Label title = new("LLMUnity status");
            title.AddToClassList("coreai-section-label");
            title.AddToClassList("coreai-section-label--status");
            container.Add(title);

            bool packageInstalled = IsLlmUnityPackageInstalled();
            bool defineActive = IsLlmUnityDefineActive();

            if (!packageInstalled)
            {
                container.Add(new HelpBox(
                    "LLMUnity package is not installed. Add package `ai.undream.llm` from Package Manager to enable local GGUF models.",
                    HelpBoxMessageType.Warning));
                Button open = new(() => EditorApplication.ExecuteMenuItem("Window/Package Manager"))
                {
                    text = "Open Package Manager"
                };
                open.AddToClassList("coreai-button");
                container.Add(open);
            }
            else if (!defineActive)
            {
                container.Add(new HelpBox(
                    "LLMUnity package is installed, but COREAI_HAS_LLMUNITY is not active for CoreAI assemblies. " +
                    "This usually means asmdef versionDefines point to the old package name.",
                    HelpBoxMessageType.Warning));
                Button fix = new(FixLlmUnityAsmdefWiring) { text = "Auto-fix asmdef wiring" };
                fix.AddToClassList("coreai-button");
                container.Add(fix);
            }
            else
            {
                container.Add(new HelpBox(
                    "LLMUnity package is installed and CoreAI assemblies see COREAI_HAS_LLMUNITY.",
                    HelpBoxMessageType.Info));
            }
        }

#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
        private void RefreshGgufChoices()
        {
            List<string> options = new() { "[ Auto / Fallback ]" };
            _ggufFileNames.Clear();
            _ggufFileNames.Add("");

            int discovered = 0;
            try
            {
                // ModelEntries can be empty until the LLMUnity manager scans disk.
                LlmUnityModelBootstrap.EnsureModelEntriesLoaded();

                if (LLMManager.modelEntries != null)
                {
                    foreach (ModelEntry entry in LLMManager.modelEntries)
                    {
                        if (entry == null || entry.lora)
                        {
                            continue;
                        }

                        options.Add(entry.filename);
                        _ggufFileNames.Add(entry.filename);
                        discovered++;
                    }
                }
            }
            catch (Exception ex)
            {
                // Keep the inspector usable even if LLMUnity cannot scan the model directory.
                Debug.LogWarning($"[CoreAI] GGUF dropdown: LLMUnity model scan failed: {ex.Message}");
            }

            string currentValue = serializedObject.FindProperty("ggufModelPath")?.stringValue ?? "";
            int currentIndex = _ggufFileNames.IndexOf(currentValue);
            if (currentIndex == -1 && !string.IsNullOrEmpty(currentValue))
            {
                options.Add(currentValue + " (manual)");
                _ggufFileNames.Add(currentValue);
                currentIndex = _ggufFileNames.Count - 1;
            }
            else if (currentIndex == -1)
            {
                currentIndex = 0;
            }

            _ggufDropdown.choices = options;
            _ggufDropdown.SetValueWithoutNotify(options[currentIndex]);
            Show(_ggufEmptyHint, discovered == 0);
        }
#endif

        /// <summary>
        /// Re-evaluates every conditional row, warning, and hint after any settings change.
        /// </summary>
        private void RefreshDynamicState()
        {
            CoreAISettingsAsset settings = Settings;
            if (settings == null)
            {
                return;
            }

            bool isAuto = settings.BackendType == LlmBackendType.Auto ||
                          settings.ExecutionMode == LlmExecutionMode.Auto;
            bool showHttp = isAuto || settings.UseHttpApi;
            bool showLlmUnity = isAuto || settings.ExecutionMode == LlmExecutionMode.LocalModel;
            bool isWebGlTarget = EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL;

            Show(_autoPriorityRow, isAuto);
            Show(_httpEssentials, showHttp);
            Show(_llmUnityEssentials, showLlmUnity);

            string productionWarning = CoreAI.Editor.CoreAIProductionSettingsValidator.GetWebGlClientKeyWarning(
                settings, isWebGlTarget);
            _productionWarning.text = productionWarning ?? "";
            Show(_productionWarning, !string.IsNullOrEmpty(productionWarning));

            bool streaming = serializedObject.FindProperty("enableStreaming").boolValue;
            bool nativeSse = serializedObject.FindProperty("webGlNativeStreaming").boolValue;
            bool missingNativeSse = isWebGlTarget && streaming && !nativeSse;
            Show(_webGlStreamingHint, missingNativeSse);
            Show(_webGlNoNativeWarning, missingNativeSse);
            Show(_webGlCredentialsInfo,
                isWebGlTarget && serializedObject.FindProperty("sameOriginCredentials").boolValue);

            // Model preset mirrors the model field without writing back.
            string model = serializedObject.FindProperty("modelName").stringValue?.Trim() ?? "";
            _modelPreset.SetValueWithoutNotify(
                Array.IndexOf(HttpModelPresets, model) >= 0 ? model : CustomPresetLabel);

            // The backend owns the model choice under ServerManagedApi, so the client-side field is dead
            // configuration there: hide it instead of letting a stale value look meaningful. Every other
            // HTTP mode now REQUIRES a model (no built-in fallback), so an empty field is worth a warning.
            bool serverManagedModel = settings.ExecutionMode == LlmExecutionMode.ServerManagedApi;
            Show(_modelRow, showHttp && !serverManagedModel);
            Show(_serverManagedModelInfo, serverManagedModel);
            Show(_modelMissingWarning, showHttp && !serverManagedModel && model.Length == 0);

            _httpAdvancedGroup.SetEnabled(isAuto || settings.UseHttpApi);
            Show(_serverManagedWarning, settings.ExecutionMode == LlmExecutionMode.ServerManagedApi);
            string priorityHint = settings.AutoPriority == LlmAutoPriority.HttpFirst
                ? "HTTP API -> LLMUnity -> Offline"
                : "LLMUnity -> HTTP API -> Offline";
            _httpAutoHint.text = $"Auto priority: {priorityHint}.";
            Show(_httpAutoHint, isAuto);
            _llmUnityAutoHint.text = $"Auto priority: {priorityHint}.";
            Show(_llmUnityAutoHint, isAuto);
            _llmUnityAdvancedGroup.SetEnabled(isAuto || settings.ExecutionMode == LlmExecutionMode.LocalModel);

            bool fallbackEnabled = serializedObject.FindProperty("enableFallbackBackend").boolValue;
            bool missingUrl = string.IsNullOrWhiteSpace(
                serializedObject.FindProperty("secondaryApiBaseUrl").stringValue);
            bool missingModel = string.IsNullOrWhiteSpace(
                serializedObject.FindProperty("secondaryModelName").stringValue);
            Show(_fallbackIncompleteWarning, fallbackEnabled && (missingUrl || missingModel));
            Show(_fallbackActiveInfo, settings.HasValidFallbackBackend);

            _temperatureField.SetEnabled(settings.OverrideTemperature);
            _maxTokensField.SetEnabled(settings.OverrideMaxTokens);
            _contextWindowField.SetEnabled(settings.OverrideContextWindow);
            Show(_emptyPrefixHint, string.IsNullOrEmpty(settings.UniversalSystemPromptPrefix));

            Show(_offlineCustomGroup, settings.OfflineUseCustomResponse);
            Show(_offlineDefaultInfo, !settings.OfflineUseCustomResponse);

            _routeHint.text = BuildRouteHint(settings, priorityHint);
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            if (_ggufDropdown != null)
            {
                RefreshGgufChoices();
            }
#endif
        }

        private static string BuildRouteHint(CoreAISettingsAsset settings, string priorityHint)
        {
            switch (settings.BackendType)
            {
                case LlmBackendType.OpenAiHttp:
                    return $"HTTP API: {settings.ApiBaseUrl} (model: {settings.ModelName})";
                case LlmBackendType.LlmUnity:
                    return "LLMUnity: LLMAgent scene/runtime host with GGUF model";
                case LlmBackendType.Auto:
                    return $"Auto: {priorityHint}";
                case LlmBackendType.Offline:
                    return "Offline mode: no networked LLM will be used";
                default:
                    return "";
            }
        }

        private void UpdateTestResultUi()
        {
            if (_testButton != null)
            {
                _testButton.SetEnabled(!_isTestingConnection);
                _testButton.text = _isTestingConnection ? "Testing..." : "Test Connection";
            }

            if (_testResultBox == null)
            {
                return;
            }

            bool hasMessage = !string.IsNullOrEmpty(_testResultMessage);
            Show(_testResultBox, hasMessage);
            if (hasMessage)
            {
                _testResultBox.text = _testResultMessage;
                _testResultBox.messageType = ToHelpBoxType(_testResultType);
            }
        }

        private static HelpBoxMessageType ToHelpBoxType(MessageType type)
        {
            switch (type)
            {
                case MessageType.Error: return HelpBoxMessageType.Error;
                case MessageType.Warning: return HelpBoxMessageType.Warning;
                case MessageType.Info: return HelpBoxMessageType.Info;
                default: return HelpBoxMessageType.None;
            }
        }

        private static void Show(VisualElement element, bool visible)
        {
            if (element != null)
            {
                element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        // ---------- LLMUnity wiring helpers ----------

        private static bool IsLlmUnityPackageInstalled()
        {
            return UnityEditor.PackageManager.PackageInfo.FindForPackageName("ai.undream.llm") != null;
        }

        private static bool IsLlmUnityDefineActive()
        {
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            return true;
#else
            return false;
#endif
        }

        private static void FixLlmUnityAsmdefWiring()
        {
            string[] asmdefPaths =
            {
                "Assets/CoreAiUnity/Runtime/Source/CoreAI.Source.asmdef",
                "Assets/CoreAiUnity/Editor/CoreAI.Editor.asmdef",
                "Assets/CoreAiUnity/Tests/CoreAI.Tests.asmdef",
                "Assets/CoreAiUnity/Tests/PlayMode/LlmInfra/CoreAI.Tests.PlayMode.LlmInfra.asmdef",
                "Assets/CoreAiUnity/Tests/PlayMode/LlmVerification/CoreAI.Tests.PlayMode.LlmVerification.asmdef",
                "Assets/CoreAiUnity/Tests/PlayMode/Scenarios/CoreAI.Tests.PlayMode.Scenarios.asmdef"
            };

            int changed = 0;
            foreach (string assetPath in asmdefPaths)
            {
                string fullPath = Path.GetFullPath(assetPath);
                if (!File.Exists(fullPath))
                {
                    Debug.LogWarning($"[CoreAI] LLMUnity wiring: asmdef not found: {assetPath}");
                    continue;
                }

                string text = File.ReadAllText(fullPath);
                string updated = Regex.Replace(
                    text,
                    "\"name\"\\s*:\\s*\"undream\\.llmunity\"\\s*,\\s*\"expression\"\\s*:\\s*\"\"",
                    "\"name\": \"ai.undream.llm\",\n      \"expression\": \"1.0.0\"");

                if (updated == text)
                {
                    continue;
                }

                File.WriteAllText(fullPath, updated);
                AssetDatabase.ImportAsset(assetPath);
                changed++;
            }

            AssetDatabase.Refresh();
            string message = changed > 0
                ? $"[CoreAI] LLMUnity asmdef wiring updated in {changed} file(s). Unity will recompile; reopen this inspector after compilation."
                : "[CoreAI] LLMUnity asmdef wiring already looks correct.";
            Debug.Log(message);
            EditorUtility.DisplayDialog("CoreAI LLMUnity Wiring", message, "OK");
        }

        // ---------- Connection test ----------

        /// <summary>
        /// Runs the selected connection test.
        /// </summary>
        private async void TestConnection(CoreAISettingsAsset settings)
        {
            _isTestingConnection = true;
            _testResultMessage = "";
            UpdateTestResultUi();

            Debug.Log("[CoreAI Test] Starting connection test");
            Debug.Log($"[CoreAI Test] Backend: {settings.BackendType}");

            try
            {
                switch (settings.BackendType)
                {
                    case LlmBackendType.OpenAiHttp:
                        await TestHttpConnection(settings);
                        break;

                    case LlmBackendType.LlmUnity:
                        TestLlmUnityConnection(settings);
                        break;

                    case LlmBackendType.Auto:
                        // Auto tests the configured priority chain.
                        await TestAutoConnection(settings);
                        break;

                    case LlmBackendType.Offline:
                        _testResultMessage =
                            "Offline mode is active.\n\nSwitch to HTTP API or LLMUnity to test a live model.";
                        _testResultType = MessageType.Info;
                        break;
                }
            }
            catch (Exception ex)
            {
                _testResultMessage = $"Connection test failed: {ex.Message}";
                _testResultType = MessageType.Error;
            }
            finally
            {
                _isTestingConnection = false;
                UpdateTestResultUi();

                // Mirror the inspector result in the Console for automation logs.
                if (!string.IsNullOrEmpty(_testResultMessage))
                {
                    switch (_testResultType)
                    {
                        case MessageType.Error:
                            Debug.LogError($"[CoreAI Test] {_testResultMessage}");
                            break;
                        case MessageType.Warning:
                            Debug.LogWarning($"[CoreAI Test] {_testResultMessage}");
                            break;
                        case MessageType.Info:
                            Debug.Log($"[CoreAI Test] {_testResultMessage}");
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Tests HTTP API by probing /models for small local APIs, then chat completions.
        /// </summary>
        private async System.Threading.Tasks.Task TestHttpConnection(CoreAISettingsAsset settings)
        {
            string baseUrl = settings.ApiBaseUrl.TrimEnd('/');

            // Large hosted APIs can return very large /models payloads, so go straight to chat completions.
            bool isLargeApi = baseUrl.Contains("openrouter") || baseUrl.Contains("api.openai.com");

            if (isLargeApi)
            {
                Debug.Log(
                    "[CoreAI Test] Large API detected, skipping /models check, going straight to chat completions");
                await TestViaChatCompletions(settings);
                return;
            }

            // Local APIs such as LM Studio usually have a small /models payload.
            string modelsUrl = baseUrl + "/models";
            Debug.Log($"[CoreAI Test] Probing API: {modelsUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(modelsUrl))
            {
                req.timeout = 10;
                UnityWebRequestAsyncOperation op = req.SendWebRequest();
                while (!op.isDone)
                {
                    await System.Threading.Tasks.Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.Log("[CoreAI Test] /models probe failed, falling back to chat completions...");
                    await TestViaChatCompletions(settings);
                    return;
                }

                string responseText = req.downloadHandler.text;
                Debug.Log($"[CoreAI Test] /models returned {responseText.Length} characters");

                // Guard against unexpectedly large provider payloads.
                if (responseText.Length > 100000)
                {
                    Debug.Log("[CoreAI Test] Response too large, skipping model check");
                    await TestViaChatCompletions(settings);
                    return;
                }

                // Always verify the actual chat completion route.
                await TestViaChatCompletions(settings);
            }
        }

        /// <summary>
        /// <c>"model":"…",</c> fragment for the diagnostic request bodies, or an empty string. Mirrors the
        /// runtime contract: an unset model is OMITTED so a ServerManagedApi backend picks one, instead of
        /// sending an empty string that every provider rejects.
        /// </summary>
        private static string BuildModelJsonFragment(CoreAISettingsAsset settings)
        {
            string model = settings.ModelName;
            return string.IsNullOrWhiteSpace(model) ? "" : $"\"model\":\"{model}\",";
        }

        /// <summary>
        /// Sends a minimal chat completions request.
        /// </summary>
        private async System.Threading.Tasks.Task TestViaChatCompletions(CoreAISettingsAsset settings)
        {
            string url = settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

            string jsonBody =
                $"{{{BuildModelJsonFragment(settings)}\"messages\":[{{\"role\":\"user\",\"content\":\"Say exactly: OK\"}}],\"max_tokens\":64}}";

            Debug.Log($"[CoreAI Test] Chat URL: {url}");
            Debug.Log($"[CoreAI Test] Chat request: {jsonBody}");

            using (UnityWebRequest req = new(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader(OpenAiHttpConstants.HttpRefererHeaderName,
                    OpenAiHttpConstants.HttpRefererUnityUrl);
                req.SetRequestHeader("X-Title", "CoreAI");

                if (!string.IsNullOrEmpty(settings.ApiKey))
                {
                    req.SetRequestHeader("Authorization", "Bearer " + settings.ApiKey);
                }

                req.timeout = settings.RequestTimeoutSeconds;

                UnityWebRequestAsyncOperation op = req.SendWebRequest();
                while (!op.isDone)
                {
                    await System.Threading.Tasks.Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string error = req.error;
                    string responseText = req.downloadHandler?.text ?? "";

                    // Log the raw provider response only on failure.
                    if (!string.IsNullOrEmpty(responseText))
                    {
                        Debug.LogError($"[CoreAI Test] Error response: {responseText}");
                    }

                    // Parse OpenAI-compatible error envelopes when present.
                    if (!string.IsNullOrEmpty(responseText) && responseText.Contains("\"error\""))
                    {
                        try
                        {
                            dynamic json = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(responseText);
                            string serverError = json?.error?.message?.ToString();
                            string errorCode = json?.error?.code?.ToString();
                            string errorType = json?.error?.type?.ToString();

                            // OpenRouter may include the useful provider error in metadata.raw.
                            string rawMessage = json?.error?.metadata?.raw?.ToString();
                            if (!string.IsNullOrEmpty(rawMessage))
                            {
                                serverError = rawMessage;
                            }

                            if (!string.IsNullOrEmpty(serverError))
                            {
                                error = serverError;
                                if (!string.IsNullOrEmpty(errorCode))
                                {
                                    error = $"[{errorCode}] {error}";
                                }

                                if (!string.IsNullOrEmpty(errorType))
                                {
                                    error += $" (type: {errorType})";
                                }
                            }
                        }
                        catch
                        {
                            /* ignore */
                        }
                    }

                    // Add a short operator hint for common provider failures.
                    string hint = "";
                    if (error.Contains("authentication") || error.Contains("Unauthorized") ||
                        error.Contains("invalid_api") || error.Contains("api_key"))
                    {
                        hint = "\n\nCheck the API key.";
                    }
                    else if (error.Contains("model") || error.Contains("not_found") || error.Contains("does not exist"))
                    {
                        hint = "\n\nCheck that the selected model exists on this provider.";
                    }
                    else if (error.Contains("credit") || error.Contains("billing") || error.Contains("payment"))
                    {
                        hint = "\n\nCheck provider credits or billing.";
                    }
                    else if (error.Contains("rate") || error.Contains("too_many") || error.Contains("429"))
                    {
                        hint = "\n\nRate limited. Wait 30-60 seconds or check provider limits.";
                    }
                    else if (error.Contains("temporarily") || error.Contains("upstream"))
                    {
                        hint = "\n\nProvider is temporarily unavailable. Try again later.";
                    }
                    else if (error.Contains("timeout") || error.Contains("connect"))
                    {
                        hint = "\n\nCheck the Base URL and local server status.";
                    }

                    _testResultMessage =
                        $"Chat completions failed:\n{error}{hint}\n\nURL: {url}\nModel: {settings.ModelName}";
                    _testResultType = MessageType.Error;
                    Debug.LogError($"[CoreAI Test] Chat completions failed: {error}");
                }
                else
                {
                    string responseText = req.downloadHandler.text;

                    // Basic OpenAI-compatible success shape.
                    bool hasContent = responseText.Contains("\"content\"") ||
                                      responseText.Contains("\"choices\"");

                    if (hasContent)
                    {
                        // Extract a short preview when possible.
                        string content = "";
                        try
                        {
                            dynamic json = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(responseText);
                            content = json?.choices?[0]?.message?.content?.ToString() ?? "";

                            // Usage is optional but useful when providers include it.
                            dynamic usage = json?.usage;
                            if (usage != null)
                            {
                                int promptTokens = (int)usage?.prompt_tokens;
                                int completionTokens = (int)usage?.completion_tokens;
                                Debug.Log(
                                    $"[CoreAI Test] Token usage: prompt={promptTokens}, completion={completionTokens}");
                            }
                        }
                        catch
                        {
                            /* ignore */
                        }

                        if (!string.IsNullOrEmpty(content))
                        {
                            _testResultMessage =
                                $"Connection succeeded.\n\nBase URL: {settings.ApiBaseUrl}\nModel: {settings.ModelName}\nResponse: \"{content}\"";
                            _testResultType = MessageType.Info;
                            Debug.Log($"[CoreAI Test] Connection successful! Response: {content}");
                        }
                        else
                        {
                            Debug.LogWarning($"[CoreAI Test] Empty-content response: {responseText}");
                            _testResultMessage =
                                "Provider returned choices but no message content. Check the model response shape, reasoning settings, or max output tokens.";
                            _testResultType = MessageType.Warning;
                        }
                    }
                    else
                    {
                        _testResultMessage =
                            "Provider response did not look OpenAI-compatible. Check the endpoint and model.";
                        _testResultType = MessageType.Warning;
                    }
                }
            }
        }

        /// <summary>
        /// Tests LLMUnity scene/runtime wiring.
        /// </summary>
        private void TestLlmUnityConnection(CoreAISettingsAsset settings)
        {
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            LLMAgent agent = null;

            // Prefer the explicitly named agent, then fall back to the first LLMAgent in the scene.
            if (!string.IsNullOrEmpty(settings.LlmUnityAgentName))
            {
                GameObject go = GameObject.Find(settings.LlmUnityAgentName);
                if (go != null)
                {
                    agent = go.GetComponent<LLMAgent>();
                }
            }

            // Fallback:
            if (agent == null)
            {
                agent = FindFirstObjectByType<LLMAgent>();
            }

            if (agent == null)
            {
                _testResultMessage =
                    "LLMAgent was not found.\n\nAdd an LLMAgent GameObject to the scene or enable auto-created runtime host settings.";
                _testResultType = MessageType.Error;
                return;
            }

            LLM llm = agent.GetComponent<LLM>();
            if (llm == null)
            {
                _testResultMessage =
                    "The LLMAgent GameObject has no LLM component.\n\nAdd an LLM component and assign a GGUF model.";
                _testResultType = MessageType.Error;
                return;
            }

            if (string.IsNullOrWhiteSpace(llm.model))
            {
                _testResultMessage =
                    $"LLM model is empty.\n\nGameObject: {agent.gameObject.name}\nAssign a GGUF model.";
                _testResultType = MessageType.Warning;
                return;
            }

            string modelPath = LLMManager.GetAssetPath(llm.model);
            bool modelExists = !string.IsNullOrEmpty(modelPath) && File.Exists(modelPath);

            if (llm.started && !llm.failed)
            {
                _testResultMessage =
                    $"LLMAgent is running.\n\nGameObject: {agent.gameObject.name}\nModel: {llm.model}\nPath: {modelPath ?? "N/A"}";
                _testResultType = MessageType.Info;
            }
            else if (modelExists)
            {
                _testResultMessage =
                    $"LLMAgent is configured but not started yet.\n\nGameObject: {agent.gameObject.name}\nModel: {llm.model}\nThe model file was found.";
                _testResultType = MessageType.Info;
            }
            else
            {
                _testResultMessage =
                    $"GGUF model file was not found.\n\nModel: {llm.model}\nPath: {modelPath ?? "N/A"}\nCheck LLMUnity Model Manager.";
                _testResultType = MessageType.Error;
            }
#else
            _testResultMessage =
                "LLMUnity test is unavailable in this build context (package missing or UNITY_WEBGL). Use HTTP API or Offline mode.";
            _testResultType = MessageType.Warning;
#endif
        }

        /// <summary>
        /// Tests the Auto route order and reports the first usable backend.
        /// </summary>
        private async System.Threading.Tasks.Task TestAutoConnection(CoreAISettingsAsset settings)
        {
            StringBuilder messages = new();
            bool anyWorking = false;

            string priorityText = settings.AutoPriority == LlmAutoPriority.HttpFirst
                ? "HTTP API -> LLMUnity -> Offline"
                : "LLMUnity -> HTTP API -> Offline";
            messages.AppendLine($"Auto priority: {priorityText}");
            messages.AppendLine();

#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            // 1. LLMUnity
            LLMAgent agent = null;
            if (!string.IsNullOrEmpty(settings.LlmUnityAgentName))
            {
                GameObject go = GameObject.Find(settings.LlmUnityAgentName);
                if (go != null)
                {
                    agent = go.GetComponent<LLMAgent>();
                }
            }

            if (agent == null)
            {
                agent = FindFirstObjectByType<LLMAgent>();
            }

            if (agent != null)
            {
                LLM llm = agent.GetComponent<LLM>();
                if (llm != null && !string.IsNullOrWhiteSpace(llm.model))
                {
                    string modelPath = LLMManager.GetAssetPath(llm.model);
                    bool modelExists = !string.IsNullOrEmpty(modelPath) && File.Exists(modelPath);

                    if (llm.started && !llm.failed)
                    {
                        messages.AppendLine("1. LLMUnity: running");
                        messages.AppendLine($"   Model: {llm.model}");
                        messages.AppendLine($"   Path: {modelPath ?? "N/A"}");
                        anyWorking = true;
                    }
                    else if (modelExists)
                    {
                        messages.AppendLine("1. LLMUnity: configured, model file found");
                        messages.AppendLine($"   Model: {llm.model}");
                        messages.AppendLine("   It can start when the local backend is requested.");
                        anyWorking = true;
                    }
                    else
                    {
                        messages.AppendLine("1. LLMUnity: GGUF file not found");
                        messages.AppendLine($"   Model: {llm.model}");
                    }
                }
                else
                {
                    messages.AppendLine("1. LLMUnity: LLM component or model is missing");
                }
            }
            else
            {
                messages.AppendLine("1. LLMUnity: LLMAgent not found");
            }

            messages.AppendLine();
#endif

            // 2. HTTP API
            if (!string.IsNullOrEmpty(settings.ApiBaseUrl))
            {
                messages.AppendLine($"2. HTTP API: {settings.ApiBaseUrl}");
                messages.AppendLine(string.IsNullOrWhiteSpace(settings.ModelName)
                    ? "   Model: (not set - the backend picks it)"
                    : $"   Model: {settings.ModelName}");

                try
                {
                    string url = settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";
                    string jsonBody =
                        $"{{{BuildModelJsonFragment(settings)}\"messages\":[{{\"role\":\"user\",\"content\":\"Say OK\"}}],\"max_tokens\":10}}";

                    using (UnityWebRequest req = new(url, "POST"))
                    {
                        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                        req.downloadHandler = new DownloadHandlerBuffer();
                        req.SetRequestHeader("Content-Type", "application/json");
                        req.SetRequestHeader(OpenAiHttpConstants.HttpRefererHeaderName,
                            OpenAiHttpConstants.HttpRefererUnityUrl);
                        req.SetRequestHeader("X-Title", "CoreAI");

                        if (!string.IsNullOrEmpty(settings.ApiKey))
                        {
                            req.SetRequestHeader("Authorization", "Bearer " + settings.ApiKey);
                        }

                        req.timeout = 15;

                        UnityWebRequestAsyncOperation op = req.SendWebRequest();
                        while (!op.isDone)
                        {
                            await System.Threading.Tasks.Task.Yield();
                        }

                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            string responseText = req.downloadHandler.text;
                            if (responseText.Contains("\"content\"") || responseText.Contains("\"choices\""))
                            {
                                messages.AppendLine("2. HTTP API: chat completions succeeded");
                                anyWorking = true;
                            }
                            else
                            {
                                messages.AppendLine("2. HTTP API: response did not include choices/content");
                            }
                        }
                        else
                        {
                            string error = req.error;
                            if (!string.IsNullOrEmpty(req.downloadHandler?.text))
                            {
                                try
                                {
                                    dynamic json =
                                        Newtonsoft.Json.JsonConvert
                                            .DeserializeObject<dynamic>(req.downloadHandler.text);
                                    error = json?.error?.message?.ToString() ?? error;
                                }
                                catch
                                {
                                    /* ignore */
                                }
                            }

                            messages.AppendLine($"2. HTTP API: {error}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    messages.AppendLine($"2. HTTP API: {ex.Message}");
                }
            }
            else
            {
                messages.AppendLine("2. HTTP API: Base URL is empty");
            }

            messages.AppendLine();

            // 3. Summarize route health.
            if (anyWorking)
            {
                messages.AppendLine("Auto route has at least one usable backend.");
                _testResultMessage = messages.ToString();
                _testResultType = MessageType.Info;
            }
            else
            {
                messages.AppendLine("No live backend was confirmed. Check LLMUnity model setup or HTTP API settings.");
                _testResultMessage = messages.ToString();
                _testResultType = MessageType.Warning;
            }
        }
    }
}
#endif
