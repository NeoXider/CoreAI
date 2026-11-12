using System;
using CoreAI.ChatOverlay;
using CoreAI.Composition;
using CoreAI.Hub.UI;
using CoreAI.Infrastructure.Llm;
using CoreAI.PanelFonts;
using CoreAI.Presentation;
using CoreAI.UiFocus;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// The CoreAI Hub inside a play world: a play world module that brings the project's Hub prefab into the running
    /// game, closed, and opens it with one key (F2 by default; the same key or Escape closes it). While it is open it
    /// holds the keyboard (<see cref="CoreAiHubWindow.CaptureKeyboardWhileExpanded"/>), so the world, the in-world
    /// chat's open keys and the mod console key stand down and the default camera frees the pointer. With no model
    /// set up it opens on AI Settings, where Test connection tests a server and makes it the game's model at once.
    /// Before the scopes build it uses the server this device remembers
    /// (<see cref="CoreAiBackend.RestoreRememberedBackend"/>), and it tells the offline Programmer reply which key
    /// opens the settings in this scene. A small hint at the top of the screen names the key.
    /// </summary>
    /// <remarks>
    /// WHY a prefab reference and not assets from Resources: the Hub's shell, panel settings and chat template are
    /// assets of the Hub package; a scene that references the prefab carries all of them into a player build, the
    /// WebGL one included, with no extra scene, Resources folder or define.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class CoreAiPlayWorldHubModule : MonoBehaviour, ICoreAiPlayWorldModule
    {
        /// <summary>The key that opens and closes the Hub unless the scene sets another.</summary>
        public const KeyCode DefaultOpenKey = KeyCode.F2;

        /// <summary>Name of the GameObject that holds the Hub (inactive while the Hub is closed).</summary>
        public const string HubObjectName = "CoreAI Hub";

        /// <summary>Name of the GameObject of the key hint.</summary>
        public const string HintObjectName = "CoreAI Hub Hint";

        /// <summary>Element name of the key hint label.</summary>
        public const string HintElementName = "coreai-playworld-hub-hint";

        [Tooltip("The Hub to open: the package prefab Assets/CoreAIHub/Runtime/CoreAiHub.prefab, or one of your own "
                 + "with a CoreAiHubWindow. The Mods tab is added by a CoreAiModsHubBinder when the prefab has none.")]
        [SerializeField]
        private GameObject hubPrefab;

        [Tooltip("Opens and closes the Hub. F2 has no meaning in a browser or in the bundled play world; T, Enter and "
                 + "/ belong to the chat, F8 to the mod console, F9 and F10 to diagnostics overlays.")]
        [SerializeField]
        private KeyCode openKey = DefaultOpenKey;

        [Tooltip("Escape closes an open Hub as well.")]
        [SerializeField]
        private bool escapeCloses = true;

        [Tooltip("Show the key hint at the top of the screen while the Hub is closed.")]
        [SerializeField]
        private bool showHint = true;

        [Tooltip("Sorting order of the hint's panel: below the play world HUD (50), so the HUD's own screens cover it.")]
        [SerializeField]
        private int hintSortingOrder = 45;

        [Tooltip("Use the model server this device remembers from a successful Test connection, before the scopes build.")]
        [SerializeField]
        private bool restoreRememberedBackend = true;

        [Tooltip("With no model set up, open the Hub on its AI Settings tab.")]
        [SerializeField]
        private bool openOnSettingsWithoutModel = true;

        private GameObject _holder;
        private CoreAiHubWindow _window;
        private CoreAiModConsoleKey _key;
        private CoreAiModConsoleKey _escape;
        private ICoreAiUiFocus _focus;
        private IChatOverlayInputState _chatInput;
        private CoreAISettingsAsset _settings;
        private string _setupHint = "";
        private int _settingsTabPending;
        private GameObject _hintObject;
        private UIDocument _hintDocument;
        private PanelSettings _hintPanel;
        private Label _hintLabel;
        private string _hintText = "";

        /// <summary>True while the Hub is open.</summary>
        public bool IsOpen => _holder != null && _holder.activeSelf;

        /// <summary>The Hub window; null before <see cref="OnComposed"/> or without a usable prefab.</summary>
        public CoreAiHubWindow Window => _window;

        /// <summary>The key that opens and closes the Hub.</summary>
        public KeyCode OpenKey => openKey;

        /// <summary>What the start did with the remembered server.</summary>
        public CoreAiRememberedBackendRestore RestoreOutcome { get; private set; }

        /// <summary>The sentence this module gave the offline Programmer reply; empty before composition.</summary>
        public string ModelSetupHint => _setupHint;

        /// <summary>The text of the key hint now; empty while it is hidden.</summary>
        public string HintText => _hintText;

        /// <summary>The one line that says why the Hub cannot open; empty when it can.</summary>
        public string Failure { get; private set; } = "";

        /// <summary>Raised with the new state after the Hub opened or closed.</summary>
        public event Action<bool> OpenChanged;

        /// <summary>Sets the Hub prefab from code; call before the play world composes.</summary>
        public void SetHubPrefab(GameObject prefab)
        {
            hubPrefab = prefab;
        }

        /// <inheritdoc />
        public void OnComposing(CoreAiPlayWorldComposition composition)
        {
            RestoreOutcome = restoreRememberedBackend
                ? CoreAiBackend.RestoreRememberedBackend()
                : CoreAiRememberedBackendRestore.NothingRemembered;
        }

        /// <inheritdoc />
        public void OnComposed(CoreAiPlayWorldComposition composition)
        {
            IObjectResolver core = composition?.CoreScope?.Container;
            _focus = core?.ResolveOrDefault<ICoreAiUiFocus>();
            _chatInput = core?.ResolveOrDefault<IChatOverlayInputState>();
            _settings = core?.ResolveOrDefault<ICoreAISettings>() as CoreAISettingsAsset ?? CoreAISettingsAsset.Instance;
            _key = new CoreAiModConsoleKey(openKey);
            _escape = new CoreAiModConsoleKey(KeyCode.Escape);
            _setupHint = ComposeSetupHint(openKey, RestoreOutcome, _settings != null ? _settings.PendingKeyBackend : null);
            _settings?.SetSessionModelSetupHint(_setupHint);
            BuildHub(composition?.Root != null ? composition.Root.transform : transform);
            if (showHint && _window != null)
            {
                BuildHint(composition?.Root != null ? composition.Root.transform : transform);
            }
        }

        /// <summary>Opens the Hub; nothing when it is open already or there is no Hub.</summary>
        public void Open()
        {
            if (_holder == null || IsOpen)
            {
                return;
            }

            _holder.SetActive(true);
            _window.SetCollapsed(false);
            _settingsTabPending = openOnSettingsWithoutModel && !HasModel(_settings) ? 10 : 0;
            RefreshHint();
            OpenChanged?.Invoke(true);
        }

        /// <summary>Closes the Hub; its tabs and drafts stay as they were for the next open.</summary>
        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            _settingsTabPending = 0;
            _holder.SetActive(false);
            RefreshHint();
            OpenChanged?.Invoke(false);
        }

        /// <summary>Opens a closed Hub or closes an open one.</summary>
        public void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        /// <summary>
        /// The sentence the offline Programmer reply ends with in a scene that opens its settings with
        /// <paramref name="key"/>: how to set up a model, or, when the remembered server waits for its key, that the
        /// key is missing. Pure for EditMode tests.
        /// </summary>
        public static string ComposeSetupHint(KeyCode key, CoreAiRememberedBackendRestore restore,
            LlmRememberedBackend pending)
        {
            string name = KeyName(key);
            if (restore == CoreAiRememberedBackendRestore.NeedsKey && pending != null)
            {
                return "The server " + pending.BaseUrl + " needs its API key again: keys are not saved on this device. "
                       + "Press " + name + " to open the AI settings, enter the key and press "
                       + HubSettingsPage.TestConnectionLabel + ", then ask again.";
            }

            return "Press " + name + " to open the AI settings, enter a model server and press "
                   + HubSettingsPage.TestConnectionLabel + ", then ask again.";
        }

        /// <summary>The hint's text: the key and what it opens, which says "set up" while no model is set up.</summary>
        public static string ComposeHintText(KeyCode key, bool hasModel)
        {
            return KeyName(key) + "  " + (hasModel ? "AI settings" : "Set up the AI model");
        }

        /// <summary>True when <paramref name="settings"/> names a backend other than Offline.</summary>
        public static bool HasModel(CoreAISettingsAsset settings)
        {
            return settings != null && settings.ExecutionMode != LlmExecutionMode.Offline &&
                   settings.BackendType != LlmBackendType.Offline;
        }

        /// <summary>
        /// Whether the open key may open the Hub now: not while the in-world chat's input or another CoreAI text
        /// field has the keyboard (the key belongs to the typing then). Pure for EditMode tests.
        /// </summary>
        public static bool MayOpen(bool chatInputOpen, bool anySurfaceHasKeyboard)
        {
            return !chatInputOpen && !anySurfaceHasKeyboard;
        }

        private static string KeyName(KeyCode key)
        {
            return key == KeyCode.None ? "the AI settings key" : key.ToString();
        }

        private void Update()
        {
            if (_window == null || _key == null)
            {
                return;
            }

            if (IsOpen)
            {
                if (_window.IsCollapsed || _key.WasPressedThisFrame() || (escapeCloses && _escape.WasPressedThisFrame()))
                {
                    Close();
                    return;
                }

                if (_settingsTabPending > 0)
                {
                    _settingsTabPending--;
                    // WHY a frame later: the window builds its tabs and the mods binder upgrades the Settings page to
                    // the live settings in their first frame; the tab is chosen once the tab bar exists.
                    if (_window.ActivePageId != null)
                    {
                        _window.ActivatePage(HubSettingsPage.DefaultPageId);
                        _settingsTabPending = 0;
                    }
                }
            }
            else if (_key.WasPressedThisFrame() &&
                     MayOpen(_chatInput != null && _chatInput.IsInputOpen, _focus != null && _focus.HoldsKeyboard))
            {
                Open();
                return;
            }

            RefreshHint();
        }

        private void OnDestroy()
        {
            if (_settings != null && _setupHint.Length > 0 &&
                string.Equals(_settings.ModelSetupHint, _setupHint, StringComparison.Ordinal))
            {
                _settings.SetSessionModelSetupHint("");
            }

            if (_hintObject != null)
            {
                Destroy(_hintObject);
            }

            if (_hintPanel != null)
            {
                Destroy(_hintPanel);
            }

            if (_holder != null)
            {
                Destroy(_holder);
            }
        }

        private void BuildHub(Transform parent)
        {
            if (hubPrefab == null)
            {
                Fail("no Hub prefab is set; assign Assets/CoreAIHub/Runtime/CoreAiHub.prefab to Hub Prefab.");
                return;
            }

            // WHY an inactive holder: the prefab's components wake on the first open, so a closed Hub costs nothing
            // and never shows for a frame while the world is being built.
            _holder = new GameObject(HubObjectName);
            _holder.SetActive(false);
            _holder.transform.SetParent(parent, false);
            GameObject hub = Instantiate(hubPrefab, _holder.transform, false);
            _window = hub.GetComponent<CoreAiHubWindow>();
            if (_window == null)
            {
                Destroy(_holder);
                _holder = null;
                Fail("the Hub prefab '" + hubPrefab.name + "' has no CoreAiHubWindow.");
                return;
            }

            _window.CaptureKeyboardWhileExpanded = true;
            if (_focus != null)
            {
                _window.SetUiFocus(_focus);
            }

            if (hub.GetComponent<CoreAiModsHubBinder>() == null)
            {
                hub.AddComponent<CoreAiModsHubBinder>();
            }
        }

        private void BuildHint(Transform parent)
        {
            _hintPanel = ScriptableObject.CreateInstance<PanelSettings>();
            _hintPanel.name = "CoreAI Hub Hint Panel";
            _hintPanel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            _hintPanel.referenceResolution = new Vector2Int(1920, 1080);
            _hintPanel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            _hintPanel.match = 0.5f;
            _hintPanel.sortingOrder = hintSortingOrder;
            ThemeStyleSheet theme = Resources.Load<ThemeStyleSheet>(CoreAiChatOverlaySettings.DefaultThemeResourcePath);
            if (theme != null)
            {
                _hintPanel.themeStyleSheet = theme;
            }

            _hintObject = new GameObject(HintObjectName);
            _hintObject.SetActive(false);
            _hintObject.transform.SetParent(parent, false);
            CoreAiPanelFonts.ApplyTo(_hintPanel);
            _hintDocument = _hintObject.AddComponent<UIDocument>();
            _hintDocument.panelSettings = _hintPanel;
            _hintDocument.sortingOrder = hintSortingOrder;
            _hintObject.SetActive(true);
            RefreshHint();
        }

        private void RefreshHint()
        {
            if (_hintDocument == null)
            {
                return;
            }

            VisualElement root = _hintDocument.rootVisualElement;
            if (root == null)
            {
                return;
            }

            if (_hintLabel == null || _hintLabel.panel == null)
            {
                // WHY rebuilt when detached: a UIDocument can recreate its root; the label follows it.
                root.pickingMode = PickingMode.Ignore;
                _hintLabel = new Label { name = HintElementName, pickingMode = PickingMode.Ignore };
                _hintLabel.style.position = Position.Absolute;
                _hintLabel.style.top = 14f;
                _hintLabel.style.left = new Length(50f, LengthUnit.Percent);
                _hintLabel.style.translate = new Translate(new Length(-50f, LengthUnit.Percent), 0f);
                _hintLabel.style.paddingLeft = 10f;
                _hintLabel.style.paddingRight = 10f;
                _hintLabel.style.paddingTop = 4f;
                _hintLabel.style.paddingBottom = 4f;
                _hintLabel.style.borderTopLeftRadius = 6f;
                _hintLabel.style.borderTopRightRadius = 6f;
                _hintLabel.style.borderBottomLeftRadius = 6f;
                _hintLabel.style.borderBottomRightRadius = 6f;
                _hintLabel.style.backgroundColor = new Color(10f / 255f, 14f / 255f, 24f / 255f, 0.62f);
                _hintLabel.style.color = Color.white;
                _hintLabel.style.fontSize = 15f;
                root.Add(_hintLabel);
                _hintText = "";
            }

            string text = IsOpen ? "" : ComposeHintText(openKey, HasModel(_settings));
            if (string.Equals(text, _hintText, StringComparison.Ordinal))
            {
                return;
            }

            _hintText = text;
            _hintLabel.text = text;
            _hintLabel.style.display = text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void Fail(string reason)
        {
            Failure = "CoreAI Hub in the play world: " + reason;
            Debug.LogError("[CoreAI] " + Failure, this);
        }
    }
}
