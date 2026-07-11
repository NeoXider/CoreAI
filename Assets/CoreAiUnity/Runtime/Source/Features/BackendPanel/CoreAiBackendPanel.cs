#nullable enable
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CoreAI.UI
{
    /// <summary>
    /// Drop-in Canvas panel for runtime LLM backend control: pick Auto / LLMUnity / HTTP API / Offline,
    /// edit the base URL / API key / model, apply the switch, and health-check the active backend.
    /// All logic goes through <see cref="CoreAiBackend"/>; the panel is a thin uGUI/TextMeshPro view.
    /// <para>
    /// Ship-ready prefab: <c>Assets/CoreAiUnity/Prefabs/CoreAiBackendPanel.prefab</c> (also creatable
    /// via <c>GameObject → CoreAI → Backend Panel (Canvas)</c> in the editor).
    /// </para>
    /// </summary>
    public sealed class CoreAiBackendPanel : MonoBehaviour
    {
        /// <summary>Dropdown order — must match <see cref="BuildBackendOptions"/>.</summary>
        private static readonly LlmExecutionMode[] DropdownModes =
        {
            LlmExecutionMode.Auto,
            LlmExecutionMode.LocalModel,
            LlmExecutionMode.ClientOwnedApi,
            LlmExecutionMode.Offline
        };

        [Header("Wiring (auto-filled by the prefab)")]
        [SerializeField]
        private TMP_Dropdown? backendDropdown;

        [SerializeField]
        private TMP_InputField? baseUrlInput;

        [SerializeField]
        private TMP_InputField? apiKeyInput;

        [SerializeField]
        private TMP_InputField? modelInput;

        [SerializeField]
        private Button? applyButton;

        [SerializeField]
        private Button? testButton;

        [SerializeField]
        private Button? closeButton;

        [SerializeField]
        private TMP_Text? statusText;

        [Header("Behaviour")]
        [Tooltip("Populate the fields from the current CoreAISettings on enable.")]
        [SerializeField]
        private bool loadCurrentOnEnable = true;

        [Tooltip("Health-check timeout in seconds for the Test button.")]
        [SerializeField]
        private int verifyTimeoutSeconds = 30;

        private bool _busy;

        /// <summary>Raised after the panel applied a backend switch (mirrors CoreAiBackend.OnBackendChanged).</summary>
        public event Action<CoreAiBackendStatus>? OnApplied;

        private void OnEnable()
        {
            if (backendDropdown != null && backendDropdown.options.Count == 0)
            {
                backendDropdown.options = BuildBackendOptions();
            }

            if (applyButton != null)
            {
                applyButton.onClick.AddListener(Apply);
            }

            if (testButton != null)
            {
                testButton.onClick.AddListener(Test);
            }

            if (closeButton != null)
            {
                closeButton.onClick.AddListener(Close);
            }

            if (backendDropdown != null)
            {
                backendDropdown.onValueChanged.AddListener(OnBackendOptionChanged);
            }

            CoreAiBackend.OnBackendChanged += HandleBackendChanged;

            if (loadCurrentOnEnable)
            {
                LoadCurrent();
            }
        }

        private void OnDisable()
        {
            if (applyButton != null)
            {
                applyButton.onClick.RemoveListener(Apply);
            }

            if (testButton != null)
            {
                testButton.onClick.RemoveListener(Test);
            }

            if (closeButton != null)
            {
                closeButton.onClick.RemoveListener(Close);
            }

            if (backendDropdown != null)
            {
                backendDropdown.onValueChanged.RemoveListener(OnBackendOptionChanged);
            }

            CoreAiBackend.OnBackendChanged -= HandleBackendChanged;
        }

        /// <summary>Reads the current backend configuration into the fields.</summary>
        public void LoadCurrent()
        {
            CoreAiBackendStatus status = CoreAiBackend.Status;

            if (backendDropdown != null)
            {
                int index = Array.IndexOf(DropdownModes, NormalizeMode(status.Mode));
                backendDropdown.SetValueWithoutNotify(index < 0 ? 0 : index);
            }

            if (baseUrlInput != null)
            {
                baseUrlInput.SetTextWithoutNotify(status.BaseUrl);
            }

            if (modelInput != null)
            {
                modelInput.SetTextWithoutNotify(status.Model);
            }

            // The API key is intentionally NOT echoed back into the field (it stays write-only in the
            // UI); an empty field on Apply keeps the currently configured key.
            RefreshInteractivity();
            SetStatus($"Current: {status}");
        }

        /// <summary>Applies the selected backend + fields via <see cref="CoreAiBackend"/>.</summary>
        public void Apply()
        {
            if (_busy)
            {
                return;
            }

            LlmExecutionMode mode = SelectedMode;
            bool live;
            switch (mode)
            {
                case LlmExecutionMode.LocalModel:
                    live = CoreAiBackend.ApplyLlmUnity(
                        string.IsNullOrWhiteSpace(ModelText) ? null : ModelText);
                    break;
                case LlmExecutionMode.Offline:
                    live = CoreAiBackend.ApplyOffline();
                    break;
                case LlmExecutionMode.Auto:
                    live = CoreAiBackend.ApplyAuto();
                    break;
                default:
                {
                    string key = apiKeyInput != null ? apiKeyInput.text : "";
                    if (string.IsNullOrEmpty(key))
                    {
                        // Empty key field = keep the configured key (never force-clear from the UI).
                        key = CoreAiSettingsApiKey();
                    }

                    live = CoreAiBackend.ApplyHttpApi(BaseUrlText, key, ModelText);
                    break;
                }
            }

            CoreAiBackendStatus status = CoreAiBackend.Status;
            SetStatus(live
                ? $"Applied (live): {status}"
                : $"Saved to settings (no live scope yet): {status}");
            OnApplied?.Invoke(status);
        }

        /// <summary>Runs the health probe and shows the result in the status label.</summary>
        public async void Test()
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            SetButtonsInteractable(false);
            SetStatus("Testing backend...");
            try
            {
                CoreAiBackendHealth health = await CoreAiBackend.VerifyAsync(verifyTimeoutSeconds);
                SetStatus(health.Ok
                    ? $"OK: {health.Mode} ({health.Model}) in {health.LatencyMs:F0} ms"
                    : $"FAILED: {health.Error}");
            }
            finally
            {
                _busy = false;
                SetButtonsInteractable(true);
            }
        }

        private void HandleBackendChanged(CoreAiBackendStatus status)
        {
            // Keep the panel in sync when the backend is switched from code elsewhere.
            if (!_busy)
            {
                LoadCurrent();
            }
        }

        private void OnBackendOptionChanged(int _)
        {
            RefreshInteractivity();
        }

        private void RefreshInteractivity()
        {
            bool http = SelectedMode is LlmExecutionMode.ClientOwnedApi
                or LlmExecutionMode.ClientLimited
                or LlmExecutionMode.ServerManagedApi;
            bool local = SelectedMode == LlmExecutionMode.LocalModel;

            if (baseUrlInput != null)
            {
                baseUrlInput.interactable = http;
            }

            if (apiKeyInput != null)
            {
                apiKeyInput.interactable = http;
            }

            if (modelInput != null)
            {
                modelInput.interactable = http || local;
            }
        }

        private LlmExecutionMode SelectedMode =>
            backendDropdown != null &&
            backendDropdown.value >= 0 && backendDropdown.value < DropdownModes.Length
                ? DropdownModes[backendDropdown.value]
                : LlmExecutionMode.Auto;

        private string BaseUrlText => baseUrlInput != null ? baseUrlInput.text : "";

        private string ModelText => modelInput != null ? modelInput.text : "";

        private static string CoreAiSettingsApiKey()
        {
            Infrastructure.Llm.CoreAISettingsAsset? settings = Infrastructure.Llm.CoreAISettingsAsset.Instance;
            return settings != null ? settings.ApiKey : "";
        }

        private static LlmExecutionMode NormalizeMode(LlmExecutionMode mode)
        {
            // Collapse the HTTP flavours onto the single "HTTP API" dropdown entry.
            return mode is LlmExecutionMode.ClientLimited or LlmExecutionMode.ServerManagedApi
                ? LlmExecutionMode.ClientOwnedApi
                : mode;
        }

        private static List<TMP_Dropdown.OptionData> BuildBackendOptions()
        {
            return new List<TMP_Dropdown.OptionData>
            {
                new("Auto"),
                new("LLMUnity (local)"),
                new("HTTP API"),
                new("Offline")
            };
        }

        private void SetButtonsInteractable(bool value)
        {
            if (applyButton != null)
            {
                applyButton.interactable = value;
            }

            if (testButton != null)
            {
                testButton.interactable = value;
            }
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
            }
        }

        #region Editor/prefab wiring

        /// <summary>Programmatic wiring used by the prefab builder and tests.</summary>
        public void Wire(
            TMP_Dropdown dropdown,
            TMP_InputField baseUrl,
            TMP_InputField apiKey,
            TMP_InputField model,
            Button apply,
            Button test,
            TMP_Text status,
            Button? close = null)
        {
            backendDropdown = dropdown;
            baseUrlInput = baseUrl;
            apiKeyInput = apiKey;
            modelInput = model;
            applyButton = apply;
            testButton = test;
            statusText = status;
            closeButton = close;
        }

        /// <summary>Hides the panel (the close "X"). Re-enable the GameObject to show it again.</summary>
        public void Close()
        {
            gameObject.SetActive(false);
        }

        #endregion
    }
}
