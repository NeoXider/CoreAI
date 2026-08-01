#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using UnityEngine;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using LLMUnity;
#endif

namespace CoreAI
{
    /// <summary>Snapshot of the currently configured LLM backend.</summary>
    public readonly struct CoreAiBackendStatus
    {
        /// <summary>Resolved execution mode (Auto is already mapped through the legacy backend field).</summary>
        public LlmExecutionMode Mode { get; }

        /// <summary>OpenAI-compatible base URL of the primary HTTP backend.</summary>
        public string BaseUrl { get; }

        /// <summary>Model identifier of the primary HTTP backend.</summary>
        public string Model { get; }

        /// <summary>GGUF model path used by the LLMUnity local backend.</summary>
        public string GgufModelPath { get; }

        /// <summary>True when a live scope was found, so switches hot-swap the active client.</summary>
        public bool IsLive { get; }

        internal CoreAiBackendStatus(LlmExecutionMode mode, string baseUrl, string model,
            string ggufModelPath, bool isLive)
        {
            Mode = mode;
            BaseUrl = baseUrl ?? "";
            Model = model ?? "";
            GgufModelPath = ggufModelPath ?? "";
            IsLive = isLive;
        }

        public override string ToString()
        {
            return Mode switch
            {
                LlmExecutionMode.LocalModel => $"LLMUnity ({GgufModelPath})",
                LlmExecutionMode.Offline => "Offline",
                _ => $"{Mode} ({Model} @ {BaseUrl})"
            };
        }
    }

    /// <summary>Result of a backend health probe (<see cref="CoreAiBackend.VerifyAsync"/>).</summary>
    public sealed class CoreAiBackendHealth
    {
        /// <summary>True when the probe completion round-tripped successfully.</summary>
        public bool Ok { get; set; }

        /// <summary>Human-readable error when <see cref="Ok"/> is false.</summary>
        public string Error { get; set; } = "";

        /// <summary>Wall-clock latency of the probe.</summary>
        public double LatencyMs { get; set; }

        /// <summary>Execution mode that was probed.</summary>
        public LlmExecutionMode Mode { get; set; }

        /// <summary>Model identifier that was probed (HTTP backends).</summary>
        public string Model { get; set; } = "";
    }

    /// <summary>Result of an OpenAI-compatible model listing (<see cref="CoreAiBackend.ListModelsAsync"/>).</summary>
    public readonly struct CoreAiModelListResult
    {
        /// <summary>Creates a model-list result.</summary>
        public CoreAiModelListResult(bool ok, IReadOnlyList<string> models, string error)
        {
            Ok = ok;
            Models = models ?? Array.Empty<string>();
            Error = error ?? "";
        }

        /// <summary>True when at least one model id was returned.</summary>
        public bool Ok { get; }

        /// <summary>Advertised model ids, in server order, de-duplicated.</summary>
        public IReadOnlyList<string> Models { get; }

        /// <summary>Human-readable error when <see cref="Ok"/> is false.</summary>
        public string Error { get; }
    }

    /// <summary>
    /// Runtime backend control for CoreAI: switch between the OpenAI-compatible HTTP API, the LLMUnity
    /// local model, and Offline mode, change the API base URL / key / model, and health-check the
    /// active backend — all without restarting the scene or rebuilding the DI container.
    /// <para><b>Quick start.</b></para>
    /// <code>
    /// // Point at a different OpenAI-compatible server mid-game:
    /// CoreAiBackend.ApplyHttpApi("https://openrouter.ai/api/v1", key, "openai/gpt-4o-mini");
    /// // Switch to the on-device GGUF model:
    /// CoreAiBackend.ApplyLlmUnity();
    /// // Just change the model on the current HTTP backend:
    /// CoreAiBackend.SetModel("qwen3.5-4b-mtp");
    /// // Probe the active backend:
    /// CoreAiBackendHealth health = await CoreAiBackend.VerifyAsync();
    /// </code>
    /// <para>
    /// Switches mutate the shared <see cref="CoreAISettingsAsset"/> and hot-swap the routed primary
    /// client inside the live <see cref="LlmClientRegistry"/>; the very next request uses the new
    /// backend. When no <see cref="CoreAILifetimeScope"/> is up yet, only the settings are mutated and
    /// the normal bootstrap picks them up (methods then return <c>false</c>).
    /// </para>
    /// </summary>
    public static class CoreAiBackend
    {
        private static readonly object SyncRoot = new();

        /// <summary>Raised after a successful switch/mutation, with the now-active execution mode.</summary>
        public static event Action<CoreAiBackendStatus>? OnBackendChanged;

        /// <summary>Current backend snapshot (settings-level; independent of in-flight requests).</summary>
        public static CoreAiBackendStatus Status
        {
            get
            {
                CoreAISettingsAsset? settings = ResolveSettings();
                if (settings == null)
                {
                    return new CoreAiBackendStatus(LlmExecutionMode.Offline, "", "", "", false);
                }

                return new CoreAiBackendStatus(
                    settings.ExecutionMode,
                    settings.ApiBaseUrl,
                    settings.ModelName,
                    settings.GgufModelPath,
                    TryResolveRegistry(out _));
            }
        }

        /// <summary>
        /// Switches to a user-owned OpenAI-compatible HTTP API (LM Studio, llama.cpp server, OpenRouter,
        /// OpenAI, ...). Takes effect on the next request.
        /// </summary>
        /// <returns>True when a live client was rebuilt and hot-swapped; false when only settings changed.</returns>
        public static bool ApplyHttpApi(
            string baseUrl,
            string apiKey,
            string model,
            float? temperature = null,
            int? timeoutSeconds = null,
            int? maxTokens = null,
            bool? overrideTemperature = null)
        {
            lock (SyncRoot)
            {
                CoreAISettingsAsset? settings = ResolveSettings();
                if (settings == null)
                {
                    LogWarning("ApplyHttpApi: no CoreAISettingsAsset available.");
                    return false;
                }

                settings.ConfigureClientOwnedApi(
                    baseUrl,
                    apiKey,
                    model,
                    temperature ?? settings.Temperature,
                    timeoutSeconds ?? settings.RequestTimeoutSeconds,
                    maxTokens ?? settings.MaxTokens,
                    overrideTemperature ?? settings.OverrideTemperature);
                return RebuildAndNotify(settings);
            }
        }

        /// <summary>
        /// Switches to a backend-managed OpenAI-compatible proxy (no provider key in the client).
        /// </summary>
        public static bool ApplyServerManagedApi(string backendBaseUrl, string model, string backendAuthToken = "")
        {
            lock (SyncRoot)
            {
                CoreAISettingsAsset? settings = ResolveSettings();
                if (settings == null)
                {
                    LogWarning("ApplyServerManagedApi: no CoreAISettingsAsset available.");
                    return false;
                }

                settings.ConfigureServerManagedApi(backendBaseUrl, model, backendAuthToken,
                    settings.Temperature, settings.RequestTimeoutSeconds, settings.MaxTokens);
                return RebuildAndNotify(settings);
            }
        }

        /// <summary>
        /// Switches to the LLMUnity on-device backend. Pass <paramref name="ggufModelPath"/> to change
        /// the model file; null keeps the currently configured path. Requires the LLMUnity package
        /// (<c>COREAI_HAS_LLMUNITY</c>) and a resolvable <c>LLMAgent</c>; otherwise the switch degrades
        /// to a stub client and <see cref="VerifyAsync"/> reports the failure.
        /// </summary>
        public static bool ApplyLlmUnity(string? ggufModelPath = null, string? agentName = null,
            int? numGpuLayers = null)
        {
            lock (SyncRoot)
            {
                CoreAISettingsAsset? settings = ResolveSettings();
                if (settings == null)
                {
                    LogWarning("ApplyLlmUnity: no CoreAISettingsAsset available.");
                    return false;
                }

                settings.ConfigureLlmUnity(
                    agentName ?? settings.LlmUnityAgentName,
                    string.IsNullOrWhiteSpace(ggufModelPath) ? settings.GgufModelPath : ggufModelPath,
                    settings.LlmUnityKeepAlive,
                    settings.LlmUnityStartupTimeoutSeconds,
                    settings.LlmUnityStartupDelaySeconds,
                    settings.LlmUnityDontDestroyOnLoad,
                    numGpuLayers ?? settings.NumGPULayers);
                return RebuildAndNotify(settings);
            }
        }

        /// <summary>
        /// Returns the GGUF model filenames known to the LLMUnity Model Manager (non-LoRA), or an empty array
        /// when LLMUnity is unavailable or the model directory has not been scanned yet. Used by UI model
        /// pickers (e.g. the Hub Settings page) to populate a model dropdown without referencing LLMUnity
        /// directly.
        /// </summary>
        public static string[] GetLlmUnityModelFileNames()
        {
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            try
            {
                LlmUnityModelBootstrap.EnsureModelEntriesLoaded();

                List<string> list = new();
                if (LLMManager.modelEntries != null)
                {
                    foreach (ModelEntry entry in LLMManager.modelEntries)
                    {
                        if (entry == null || entry.lora)
                        {
                            continue;
                        }

                        list.Add(entry.filename);
                    }
                }

                return list.ToArray();
            }
            catch (Exception ex)
            {
                LogWarning("GetLlmUnityModelFileNames: LLMUnity model scan failed: " + ex.Message);
                return Array.Empty<string>();
            }
#else
            return Array.Empty<string>();
#endif
        }

        /// <summary>Switches to the offline stub backend (optionally with a fixed custom response).</summary>
        public static bool ApplyOffline(bool useCustomResponse = false, string? customResponse = null,
            string? roles = null)
        {
            lock (SyncRoot)
            {
                CoreAISettingsAsset? settings = ResolveSettings();
                if (settings == null)
                {
                    LogWarning("ApplyOffline: no CoreAISettingsAsset available.");
                    return false;
                }

                settings.ConfigureOffline(useCustomResponse, customResponse, roles);
                return RebuildAndNotify(settings);
            }
        }

        /// <summary>Switches to automatic backend resolution (LLMUnity/HTTP priority from settings).</summary>
        public static bool ApplyAuto()
        {
            lock (SyncRoot)
            {
                CoreAISettingsAsset? settings = ResolveSettings();
                if (settings == null)
                {
                    LogWarning("ApplyAuto: no CoreAISettingsAsset available.");
                    return false;
                }

                settings.ConfigureAuto();
                return RebuildAndNotify(settings);
            }
        }

        /// <summary>Changes the model on the current backend configuration (hot; next request uses it).</summary>
        public static bool SetModel(string model)
        {
            return MutateAndRebuild(s => s.SetModelName(model));
        }

        /// <summary>Changes the API key on the current backend configuration (hot).</summary>
        public static bool SetApiKey(string apiKey)
        {
            return MutateAndRebuild(s => s.SetApiKey(apiKey));
        }

        /// <summary>Changes the OpenAI-compatible base URL on the current backend configuration (hot).</summary>
        public static bool SetApiBaseUrl(string baseUrl)
        {
            return MutateAndRebuild(s => s.SetApiBaseUrl(baseUrl));
        }

        /// <summary>
        /// Sends a tiny completion through the active backend and reports success, error, and latency.
        /// Safe to call from UI (never throws).
        /// </summary>
        public static async Task<CoreAiBackendHealth> VerifyAsync(
            int timeoutSeconds = 30, CancellationToken cancellationToken = default)
        {
            CoreAISettingsAsset? settings = ResolveSettings();
            LlmExecutionMode mode = settings != null ? settings.ExecutionMode : LlmExecutionMode.Offline;
            string model = settings != null ? settings.ModelName : "";

            if (!TryResolveRegistry(out ILlmClientRegistry? registry) || registry == null)
            {
                return new CoreAiBackendHealth
                {
                    Ok = false,
                    Error = "CoreAI scope is not running (no CoreAILifetimeScope with a built container).",
                    Mode = mode,
                    Model = model
                };
            }

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(
                    timeoutSeconds <= 0 ? 30 : timeoutSeconds));
                using CancellationTokenSource linked =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                ILlmClient client = registry.ResolveClientForRole(BuiltInAgentRoleIds.SmartChat);
                LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
                {
                    AgentRoleId = BuiltInAgentRoleIds.SmartChat,
                    SystemPrompt = "You are a connectivity probe. Reply with the single word: ok",
                    UserPayload = "ping",
                    // WHY: Generous enough for reasoning models that spend tokens on thinking before the
                    // visible answer (thinking counts toward max_tokens on OpenAI-compatible servers);
                    // still a negligible probe cost. An empty visible answer after the budget is a
                    // REAL failure signal (limits/reasoning misconfiguration), so the probe surfaces it.
                    MaxOutputTokens = 128000
                }, linked.Token);

                sw.Stop();
                return new CoreAiBackendHealth
                {
                    Ok = result != null && result.Ok,
                    Error = result == null ? "null result" : result.Ok ? "" : result.Error ?? "unknown error",
                    LatencyMs = sw.Elapsed.TotalMilliseconds,
                    Mode = mode,
                    Model = model
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                return new CoreAiBackendHealth
                {
                    Ok = false,
                    Error = $"Probe timed out after {timeoutSeconds}s.",
                    LatencyMs = sw.Elapsed.TotalMilliseconds,
                    Mode = mode,
                    Model = model
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new CoreAiBackendHealth
                {
                    Ok = false,
                    Error = ex.Message,
                    LatencyMs = sw.Elapsed.TotalMilliseconds,
                    Mode = mode,
                    Model = model
                };
            }
        }

        /// <summary>
        /// Queries an OpenAI-compatible <c>GET {baseUrl}/models</c> endpoint and returns the advertised
        /// model ids. Purely a discovery convenience for the settings UI (so a user can see/copy the exact
        /// model names a local server such as LM Studio or Ollama exposes); it does NOT change any backend
        /// setting. <paramref name="apiKey"/> is optional — sent as a Bearer token only when non-empty (local
        /// servers usually ignore it).
        /// </summary>
        public static async Task<CoreAiModelListResult> ListModelsAsync(
            string baseUrl,
            string apiKey = "",
            int timeoutSeconds = 15,
            CancellationToken cancellationToken = default)
        {
            string url = BuildModelsUrl(baseUrl);
            if (string.IsNullOrEmpty(url))
            {
                return new CoreAiModelListResult(false, Array.Empty<string>(), "Base URL is empty.");
            }

            try
            {
                using System.Net.Http.HttpClient http = new()
                {
                    Timeout = TimeSpan.FromSeconds(timeoutSeconds <= 0 ? 15 : timeoutSeconds)
                };
                using System.Net.Http.HttpRequestMessage request =
                    new(System.Net.Http.HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
                }

                using System.Net.Http.HttpResponseMessage response =
                    await http.SendAsync(request, cancellationToken);
                string payload = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    return new CoreAiModelListResult(
                        false,
                        Array.Empty<string>(),
                        $"HTTP {(int)response.StatusCode} from {url}. {Truncate(payload, 200)}");
                }

                IReadOnlyList<string> models = ParseModelIds(payload);
                return models.Count == 0
                    ? new CoreAiModelListResult(false, models, "Server returned no models.")
                    : new CoreAiModelListResult(true, models, "");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new CoreAiModelListResult(
                    false, Array.Empty<string>(), $"Request timed out after {timeoutSeconds}s.");
            }
            catch (Exception ex)
            {
                return new CoreAiModelListResult(false, Array.Empty<string>(), ex.Message);
            }
        }

        /// <summary>
        /// Builds the OpenAI-compatible models endpoint from a base URL. Returns an empty string for a
        /// blank base URL; idempotent when the caller already passed a <c>.../models</c> URL.
        /// </summary>
        internal static string BuildModelsUrl(string baseUrl)
        {
            string trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
            if (trimmed.Length == 0)
            {
                return "";
            }

            return trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : trimmed + "/models";
        }

        /// <summary>
        /// Extracts model ids from an OpenAI-compatible <c>/models</c> response. Accepts both the standard
        /// <c>{"data":[{"id":...}]}</c> envelope and a bare JSON array of model objects/strings. Ids are
        /// de-duplicated and returned in server order; a malformed body yields an empty list.
        /// </summary>
        internal static IReadOnlyList<string> ParseModelIds(string json)
        {
            List<string> ids = new();
            if (string.IsNullOrWhiteSpace(json))
            {
                return ids;
            }

            try
            {
                Newtonsoft.Json.Linq.JToken root = Newtonsoft.Json.Linq.JToken.Parse(json);
                Newtonsoft.Json.Linq.JToken? array =
                    root is Newtonsoft.Json.Linq.JArray ? root : root["data"];
                if (array is not Newtonsoft.Json.Linq.JArray items)
                {
                    return ids;
                }

                HashSet<string> seen = new(StringComparer.Ordinal);
                foreach (Newtonsoft.Json.Linq.JToken item in items)
                {
                    string id = item is Newtonsoft.Json.Linq.JValue
                        ? item.ToString()
                        : (string?)item["id"] ?? "";
                    id = id.Trim();
                    if (id.Length > 0 && seen.Add(id))
                    {
                        ids.Add(id);
                    }
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // WHY: A non-JSON body (HTML error page, proxy notice) is a normal failure mode for a
                // mistyped URL; treat it as "no models" and let the caller surface the raw HTTP status.
            }

            return ids;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        #region Internals

        private static bool MutateAndRebuild(Action<CoreAISettingsAsset> mutate)
        {
            lock (SyncRoot)
            {
                CoreAISettingsAsset? settings = ResolveSettings();
                if (settings == null)
                {
                    LogWarning("Backend mutation skipped: no CoreAISettingsAsset available.");
                    return false;
                }

                mutate(settings);
                return RebuildAndNotify(settings);
            }
        }

        /// <summary>
        /// Rebuilds the routed primary client from the (already mutated) settings and hot-swaps it into
        /// the live registry. Fires <see cref="OnBackendChanged"/> either way — the settings DID change.
        /// </summary>
        private static bool RebuildAndNotify(CoreAISettingsAsset settings)
        {
            bool swapped = TryHotSwap(settings);
            RaiseBackendChanged();
            return swapped;
        }

        private static bool TryHotSwap(CoreAISettingsAsset settings)
        {
            if (!TryResolveScopeContainer(out CoreAILifetimeScope? scope) || scope?.Container == null)
            {
                return false;
            }

            try
            {
                LlmClientRegistry? registry = scope.Container.Resolve(typeof(ILlmClientRegistry)) as LlmClientRegistry;
                if (registry == null)
                {
                    LogWarning("Hot swap skipped: ILlmClientRegistry is not the built-in LlmClientRegistry.");
                    return false;
                }

                IGameLogger logger = scope.Container.Resolve(typeof(IGameLogger)) as IGameLogger
                                     ?? GameLoggerUnscopedFallback.Instance;
                IAgentMemoryStore? memoryStore =
                    scope.Container.Resolve(typeof(IAgentMemoryStore)) as IAgentMemoryStore;
                ILlmAgentProvider? agentProvider =
                    scope.Container.Resolve(typeof(ILlmAgentProvider)) as ILlmAgentProvider;
                ILog log = scope.Container.Resolve(typeof(ILog)) as ILog ?? Log.Instance;

                ILlmClient rebuilt = LlmPipelineInstaller.BuildRoutedPrimaryClient(
                    settings, logger, memoryStore, agentProvider, log);
                registry.SetLegacyFallback(rebuilt);
                logger.LogInfo(GameLogFeature.Llm,
                    $"CoreAiBackend: active backend switched to {new CoreAiBackendStatus(settings.ExecutionMode, settings.ApiBaseUrl, settings.ModelName, settings.GgufModelPath, true)}");
                return true;
            }
            catch (Exception ex)
            {
                LogWarning($"Hot swap failed ({ex.GetType().Name}: {ex.Message}); " +
                           "settings were still updated and apply on next bootstrap.");
                return false;
            }
        }

        private static void RaiseBackendChanged()
        {
            Action<CoreAiBackendStatus>? handlers = OnBackendChanged;
            if (handlers == null)
            {
                return;
            }

            try
            {
                handlers.Invoke(Status);
            }
            catch (Exception ex)
            {
                LogWarning($"OnBackendChanged handler error: {ex.Message}");
            }
        }

        private static CoreAISettingsAsset? ResolveSettings()
        {
            // WHY: Prefer the scope-registered asset (it is what the pipeline was built from); fall back to
            // the global instance so pre-bootstrap configuration still lands on the right object.
            if (TryResolveScopeContainer(out CoreAILifetimeScope? scope) && scope?.Container != null)
            {
                try
                {
                    if (scope.Container.Resolve(typeof(ICoreAISettings)) is CoreAISettingsAsset fromScope)
                    {
                        return fromScope;
                    }
                }
                catch
                {
                    /* fall through to the global instance */
                }
            }

            return CoreAISettingsAsset.Instance;
        }

        private static bool TryResolveRegistry(out ILlmClientRegistry? registry)
        {
            registry = null;
            if (!TryResolveScopeContainer(out CoreAILifetimeScope? scope) || scope?.Container == null)
            {
                return false;
            }

            try
            {
                registry = scope.Container.Resolve(typeof(ILlmClientRegistry)) as ILlmClientRegistry;
                return registry != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveScopeContainer(out CoreAILifetimeScope? scope)
        {
            scope = UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include);
            return scope != null && scope.Container != null;
        }

        private static void LogWarning(string message)
        {
            GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.Llm, $"[CoreAiBackend] {message}");
        }

        #endregion
    }
}
