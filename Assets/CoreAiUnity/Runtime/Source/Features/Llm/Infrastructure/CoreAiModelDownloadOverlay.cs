#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using LLMUnity;
using UnityEngine;
using UnityEngine.UI;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Self-spawning, self-dismissing on-screen indicator for LLMUnity's first-run model download.
    ///
    /// LLMUnity ships no ready-made download-progress prefab/component - it only exposes the data
    /// (<see cref="LLMManager.downloadProgress"/> 0..1) and the setup flags (<see cref="LLM.modelSetupComplete"/>,
    /// <see cref="LLM.modelSetupFailed"/>); the app is expected to draw its own bar (the MobileDemo sample
    /// wires them to a Scrollbar + Text by hand). CoreAI's "Download on Build" APKs therefore looked frozen:
    /// the model downloads in <c>LLM.Awake</c> (<c>#if !UNITY_EDITOR</c>), but nothing surfaced it.
    ///
    /// This component is that missing indicator, built entirely in code so it is genuinely drop-in: it spawns
    /// itself on startup (see <see cref="AutoSpawn"/>), builds its own screen-space overlay, polls the LLMUnity
    /// download state from the main thread (no cross-thread UI access), reveals only while a download is
    /// actually in progress, and destroys itself once the model is ready (or shows an error and clears).
    /// The model download itself is driven by LLMUnity's <c>LLM.Awake</c> -> <c>LLMManager.Setup()</c>; this
    /// overlay only observes and renders it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoreAiModelDownloadOverlay : MonoBehaviour
    {
        // If nothing ever starts downloading within this window, assume the app has no pending
        // download (e.g. the model is bundled or absent) and quietly remove the (never-shown) overlay.
        private const float NoDownloadTimeoutSeconds = 30f;
        private const float DoneLingerSeconds = 0.8f;
        private const float ErrorLingerSeconds = 6f;

        private CanvasGroup _group;
        private Text _titleText;
        private Text _percentText;
        private Image _fillImage;
        private RectTransform _fill;

        private bool _revealed;
        private bool _errorShown;
        private float _elapsed;
        private float _doneTimer = -1f;
        private float _errorTimer;

        // Editor-only preview state (see the CoreAI/Debug menu item); animates a fake download.
        private bool _demoMode;
        private float _demoProgress;

        private static Font _cachedFont;

#if !UNITY_EDITOR
        // The download only happens in player builds (LLM.Awake runs LLMManager.Setup under #if !UNITY_EDITOR),
        // so the overlay is compiled to auto-spawn only there. This also keeps it out of Editor PlayMode tests.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            CoreAISettingsAsset settings = Resources.Load<CoreAISettingsAsset>("CoreAISettings");
            if (settings == null || !settings.UseLlmUnity || !settings.LlmUnityAutostartLocalServer)
            {
                return;
            }

            Spawn();
        }
#endif

        /// <summary>Creates the overlay at runtime (kept alive across scene loads). Idempotent enough for one call.</summary>
        public static CoreAiModelDownloadOverlay Spawn()
        {
            GameObject go = new("CoreAI_ModelDownloadOverlay");
            DontDestroyOnLoad(go);
            return go.AddComponent<CoreAiModelDownloadOverlay>();
        }

        private void Awake()
        {
            BuildUi();
            SetVisible(false);
        }

        private void Update()
        {
            if (_demoMode)
            {
                UpdateDemo();
                return;
            }

            _elapsed += Time.unscaledDeltaTime;

            bool complete = LLM.modelSetupComplete;
            bool failed = LLM.modelSetupFailed;
            float progress = Mathf.Clamp01(LLMManager.downloadProgress);

            // A download is active while setup is not complete and progress has moved off its idle value of 1.
            if (!complete && progress < 1f && !_revealed)
            {
                Reveal();
            }

            if (_revealed && !_errorShown)
            {
                SetProgress(progress);
            }

            if (complete)
            {
                if (failed)
                {
                    if (!_errorShown)
                    {
                        ShowError();
                    }

                    _errorTimer += Time.unscaledDeltaTime;
                    if (_errorTimer >= ErrorLingerSeconds)
                    {
                        Destroy(gameObject);
                    }

                    return;
                }

                if (!_revealed)
                {
                    // Setup finished without ever downloading (bundled/local model) - nothing to show.
                    Destroy(gameObject);
                    return;
                }

                if (_doneTimer < 0f)
                {
                    SetProgress(1f);
                    _titleText.text = "Model ready";
                    _doneTimer = 0f;
                }

                _doneTimer += Time.unscaledDeltaTime;
                if (_doneTimer >= DoneLingerSeconds)
                {
                    Destroy(gameObject);
                }

                return;
            }

            // Never saw a download start - this app has nothing to fetch; drop the invisible overlay.
            if (!_revealed && _elapsed >= NoDownloadTimeoutSeconds)
            {
                Destroy(gameObject);
            }
        }

        private void UpdateDemo()
        {
            if (!_revealed)
            {
                Reveal();
            }

            if (_demoProgress < 1f)
            {
                _demoProgress += Time.unscaledDeltaTime * 0.25f; // ~4 seconds end to end
                _titleText.text = "Downloading AI model… (preview)";
                SetProgress(_demoProgress);
                return;
            }

            SetProgress(1f);
            _titleText.text = "Model ready (preview)";
            _doneTimer += Time.unscaledDeltaTime;
            if (_doneTimer >= DoneLingerSeconds)
            {
                Destroy(gameObject);
            }
        }

        private void Reveal()
        {
            _revealed = true;
            SetVisible(true);
        }

        private void SetVisible(bool visible)
        {
            if (_group == null)
            {
                return;
            }

            _group.alpha = visible ? 1f : 0f;
            _group.blocksRaycasts = visible;
            _group.interactable = visible;
        }

        private void SetProgress(float progress)
        {
            progress = Mathf.Clamp01(progress);
            if (_fill != null)
            {
                _fill.anchorMax = new Vector2(progress, 1f);
                _fill.offsetMin = Vector2.zero;
                _fill.offsetMax = Vector2.zero;
            }

            if (_percentText != null)
            {
                _percentText.text = Mathf.RoundToInt(progress * 100f) + "%";
            }
        }

        private void ShowError()
        {
            _errorShown = true;
            Reveal();
            if (_titleText != null)
            {
                _titleText.text = "Model download failed";
            }

            if (_percentText != null)
            {
                _percentText.text = "Check your connection and relaunch the app.";
            }

            if (_fillImage != null)
            {
                _fillImage.color = new Color(0.90f, 0.32f, 0.28f, 1f);
            }
        }

        private void BuildUi()
        {
            Canvas canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue; // draw above everything, including UI Toolkit panels

            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            gameObject.AddComponent<GraphicRaycaster>();
            _group = gameObject.AddComponent<CanvasGroup>();

            RectTransform dim = CreateChild("Dim", transform);
            Stretch(dim);
            Image dimImage = dim.gameObject.AddComponent<Image>();
            dimImage.color = new Color(0f, 0f, 0f, 0.72f);

            RectTransform panel = CreateChild("Panel", dim);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(820f, 300f);
            panel.anchoredPosition = Vector2.zero;
            Image panelImage = panel.gameObject.AddComponent<Image>();
            panelImage.color = new Color(0.10f, 0.11f, 0.13f, 0.98f);

            _titleText = CreateText("Title", panel, 40, TextAnchor.MiddleCenter);
            RectTransform titleRect = _titleText.rectTransform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = new Vector2(-80f, 64f);
            titleRect.anchoredPosition = new Vector2(0f, -48f);
            _titleText.text = "Downloading AI model…";

            RectTransform track = CreateChild("Track", panel);
            track.anchorMin = new Vector2(0f, 0.5f);
            track.anchorMax = new Vector2(1f, 0.5f);
            track.pivot = new Vector2(0.5f, 0.5f);
            track.sizeDelta = new Vector2(-100f, 26f);
            track.anchoredPosition = new Vector2(0f, -4f);
            Image trackImage = track.gameObject.AddComponent<Image>();
            trackImage.color = new Color(1f, 1f, 1f, 0.12f);

            _fill = CreateChild("Fill", track);
            _fill.anchorMin = new Vector2(0f, 0f);
            _fill.anchorMax = new Vector2(0f, 1f);
            _fill.pivot = new Vector2(0f, 0.5f);
            _fill.offsetMin = Vector2.zero;
            _fill.offsetMax = Vector2.zero;
            _fillImage = _fill.gameObject.AddComponent<Image>();
            _fillImage.color = new Color(0.30f, 0.68f, 1f, 1f);

            _percentText = CreateText("Percent", panel, 30, TextAnchor.MiddleCenter);
            RectTransform percentRect = _percentText.rectTransform;
            percentRect.anchorMin = new Vector2(0f, 0f);
            percentRect.anchorMax = new Vector2(1f, 0f);
            percentRect.pivot = new Vector2(0.5f, 0f);
            percentRect.sizeDelta = new Vector2(-80f, 48f);
            percentRect.anchoredPosition = new Vector2(0f, 44f);
            _percentText.text = "0%";
        }

        private static RectTransform CreateChild(string childName, Transform parent)
        {
            GameObject go = new(childName, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static Text CreateText(string childName, Transform parent, int fontSize, TextAnchor anchor)
        {
            RectTransform rect = CreateChild(childName, parent);
            Text text = rect.gameObject.AddComponent<Text>();
            text.font = UiFont();
            text.fontSize = fontSize;
            text.alignment = anchor;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        private static Font UiFont()
        {
            if (_cachedFont != null)
            {
                return _cachedFont;
            }

            // Unity 2022+/6 ships the legacy dynamic font as LegacyRuntime.ttf; older editors used Arial.ttf.
            _cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_cachedFont == null)
            {
                _cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            if (_cachedFont == null)
            {
                _cachedFont = Font.CreateDynamicFontFromOSFont("Arial", 24);
            }

            return _cachedFont;
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("CoreAI/Debug/Preview Model Download Overlay")]
        private static void PreviewMenu()
        {
            if (!Application.isPlaying)
            {
                UnityEditor.EditorUtility.DisplayDialog(
                    "CoreAI",
                    "Enter Play Mode first, then run this menu item to preview the download overlay.",
                    "OK");
                return;
            }

            CoreAiModelDownloadOverlay overlay = Spawn();
            overlay._demoMode = true;
        }
#endif
    }
}
#endif