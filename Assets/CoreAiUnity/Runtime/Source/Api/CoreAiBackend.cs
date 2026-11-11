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
using CoreAI.Infrastructure.Llm.Gateway;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using UnityEngine;
using VContainer;
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

    /// <summary>What <see cref="CoreAiBackend.RestoreRememberedBackend"/> did.</summary>
    public enum CoreAiRememberedBackendRestore
    {
        /// <summary>No server is remembered on this device (or the file could not be read); nothing changed.</summary>
        NothingRemembered = 0,

        /// <summary>The remembered server and model are in use for this session.</summary>
        Applied = 1,

        /// <summary>
        /// The remembered server needs a key, which is never kept between starts: the session stays offline, and
        /// <see cref="CoreAISettingsAsset.PendingKeyBackend"/> holds the server so a settings page asks for the key.
        /// </summary>
        NeedsKey = 2
    }

    /// <summary>Result of <see cref="CoreAiBackend.ConnectHttpApiAsync"/>.</summary>
    public sealed class CoreAiConnectResult
    {
        /// <summary>What the connection check found; its message is the line to show.</summary>
        public LlmConnectionCheckResult Check { get; set; } = new();

        /// <summary>True when the server answered and is now the backend of this session.</summary>
        public bool Ok => Check != null && Check.Ok && Applied;

        /// <summary>True when the session switched to the tested server (every role on Automatic routing uses it).</summary>
        public bool Applied { get; set; }

        /// <summary>True when the running client was hot-swapped, so the very next request uses the server.</summary>
        public bool IsLive { get; set; }

        /// <summary>True when the address and the model were kept on this device and will be used at the next start.</summary>
        public bool Remembered { get; set; }

        /// <summary>Why they were not kept; empty when <see cref="Remembered"/> or when remembering was not asked.</summary>
        public string RememberProblem { get; set; } = "";
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
    /// Switches are <b>session overrides</b> on the <see cref="CoreAISettingsAsset"/>
    /// (<see cref="CoreAISettingsAsset.HasSessionOverrides"/>): the getters return the switched values while the
    /// asset's serialized fields stay untouched, so a key supplied at runtime is never saved into the asset, never
    /// survives Play Mode and never reaches a build. The routed primary client inside the live
    /// <see cref="LlmClientRegistry"/> is hot-swapped; the very next request uses the new backend. When no
    /// <see cref="CoreAILifetimeScope"/> is up yet, only the session values change and the normal bootstrap picks
    /// them up (methods then return <c>false</c>). <see cref="ClearSessionOverrides"/> returns to the asset's own
    /// values; edit the asset in the inspector (or through its <c>Configure*</c> methods) to change them for good.
    /// </para>
    /// </summary>
    public static class CoreAiBackend
    {
        private static readonly object SyncRoot = new();

        /// <summary>Raised after a successful switch/mutation, with the now-active execution mode.</summary>
        public static event Action<CoreAiBackendStatus>? OnBackendChanged;

        private static ILlmConnectionHttp? s_connectionHttp;
        private static ILlmRememberedBackendStore? s_rememberedStore;

        internal static void ResetStaticState()
        {
            lock (SyncRoot)
            {
                OnBackendChanged = null;
                s_connectionHttp = null;
                s_rememberedStore = null;
            }
        }

        /// <summary>
        /// The HTTP that <see cref="CheckConnectionAsync"/>, <see cref="ConnectHttpApiAsync"/> and
        /// <see cref="ListModelsAsync"/> send through. The default goes through <c>UnityWebRequest</c>, which works in
        /// a WebGL player; a host or a test sets its own; null restores the default. Null when the build has no HTTP
        /// model support (<c>COREAI_LLM</c> off) and nothing was set.
        /// </summary>
        public static ILlmConnectionHttp? ConnectionHttp
        {
            get
            {
                ILlmConnectionHttp? set = s_connectionHttp;
                if (set != null)
                {
                    return set;
                }
#if COREAI_LLM
                return new UnityWebRequestLlmConnectionHttp();
#else
                return null;
#endif
            }
            set => s_connectionHttp = value;
        }

        /// <summary>
        /// Where the player's tested server is remembered between starts: by default
        /// <see cref="FileLlmRememberedBackendStore"/> (a file under CoreAI's persistent data, the page's storage in
        /// a browser). A host replaces it; null restores the default. It never holds a key.
        /// </summary>
        public static ILlmRememberedBackendStore RememberedBackendStore
        {
            get => s_rememberedStore ??= new FileLlmRememberedBackendStore();
            set => s_rememberedStore = value;
        }

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

                ApplySessionHttp(settings, LlmExecutionMode.ClientOwnedApi, baseUrl, apiKey, model, temperature,
                    timeoutSeconds, maxTokens, overrideTemperature);
                return RebuildAndNotify(settings);
            }
        }

        /// <summary>
        /// <see cref="ApplyHttpApi(string,string,string,float?,int?,int?,bool?)"/> that keeps a given HTTP mode:
        /// <see cref="LlmExecutionMode.ClientOwnedApi"/>, <see cref="LlmExecutionMode.ClientLimited"/> (the asset's
        /// request and prompt caps stay in force) or <see cref="LlmExecutionMode.ServerManagedApi"/> (pass the
        /// backend token as <paramref name="apiKey"/>).
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="mode"/> is not an HTTP mode.</exception>
        public static bool ApplyHttpApi(
            LlmExecutionMode mode,
            string baseUrl,
            string apiKey,
            string model,
            float? temperature = null,
            int? timeoutSeconds = null,
            int? maxTokens = null,
            bool? overrideTemperature = null)
        {
            if (!LlmPipelineInstaller.IsHttpMode(mode))
            {
                throw new ArgumentException($"'{mode}' is not an HTTP execution mode.", nameof(mode));
            }

            lock (SyncRoot)
            {
                CoreAISettingsAsset? settings = ResolveSettings();
                if (settings == null)
                {
                    LogWarning("ApplyHttpApi: no CoreAISettingsAsset available.");
                    return false;
                }

                ApplySessionHttp(settings, mode, baseUrl, apiKey, model, temperature, timeoutSeconds, maxTokens,
                    overrideTemperature);
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

                ApplySessionHttp(settings, LlmExecutionMode.ServerManagedApi, backendBaseUrl, backendAuthToken, model,
                    null, null, null, null);
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
                if (!LocalModelPlatformSupport.IsSupported(Application.platform))
                {
                    LocalModelPlatformSupport.LogUnavailableOnce(Application.platform);
                    return false;
                }

                CoreAISettingsAsset? settings = ResolveSettings();
                if (settings == null)
                {
                    LogWarning("ApplyLlmUnity: no CoreAISettingsAsset available.");
                    return false;
                }

                settings.ApplySessionLlmUnity(
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
            if (!LocalModelPlatformSupport.IsSupported(Application.platform))
            {
                return Array.Empty<string>();
            }

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

                settings.ApplySessionOffline(useCustomResponse, customResponse, roles);
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

                settings.ApplySessionAuto();
                return RebuildAndNotify(settings);
            }
        }

        /// <summary>
        /// Changes the model on the current backend configuration (hot; next request uses it). Session-only: the
        /// asset keeps its own model.
        /// </summary>
        public static bool SetModel(string model)
        {
            return MutateAndRebuild(s => s.SetSessionModelName(model));
        }

        /// <summary>
        /// Changes the API key on the current backend configuration (hot). Session-only: the key is never written
        /// into the asset, so the next inspector edit cannot save it and a build cannot pack it.
        /// </summary>
        public static bool SetApiKey(string apiKey)
        {
            return MutateAndRebuild(s => s.SetSessionApiKey(apiKey));
        }

        /// <summary>Changes the OpenAI-compatible base URL on the current backend configuration (hot; session-only).</summary>
        public static bool SetApiBaseUrl(string baseUrl)
        {
            return MutateAndRebuild(s => s.SetSessionApiBaseUrl(baseUrl));
        }

        /// <summary>Drops every runtime switch of this session and returns to the asset's own backend fields (hot).</summary>
        public static bool ClearSessionOverrides()
        {
            return MutateAndRebuild(s => s.ClearSessionOverrides());
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
                    MaxOutputTokens = 262144
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
        /// servers usually ignore it). It goes through <see cref="ConnectionHttp"/> (<c>UnityWebRequest</c>), so it
        /// works in a WebGL player too, and a failure reads as the connection check's sentence (unreachable, CORS,
        /// a refused key, ...).
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

            ILlmConnectionHttp? http = ConnectionHttp;
            if (http == null)
            {
                return new CoreAiModelListResult(false, Array.Empty<string>(), NoHttpSupportMessage);
            }

            try
            {
                bool inBrowser = Application.platform == RuntimePlatform.WebGLPlayer;
                LlmModelListing listing = await LlmConnectionCheck.ListModelsAsync(http, baseUrl, apiKey ?? "",
                    timeoutSeconds <= 0 ? 15 : timeoutSeconds, inBrowser, inBrowser ? Application.absoluteURL : "",
                    cancellationToken);
                return new CoreAiModelListResult(listing.Ok, listing.Models, listing.Message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new CoreAiModelListResult(
                    false, Array.Empty<string>(), $"Request timed out after {timeoutSeconds}s.");
            }
        }

        /// <summary>The sentence a check answers with when the build has no HTTP model support.</summary>
        public const string NoHttpSupportMessage =
            "This build has no HTTP model support (the COREAI_LLM define is off), so no server can be tested.";

        /// <summary>
        /// Tests the server at <paramref name="baseUrl"/> with <paramref name="model"/> as a player needs it tested
        /// (see <see cref="LlmConnectionCheck"/>): its model list, then one tiny completion. Changes no setting and
        /// never throws for a server problem; the result's message says what happened in a player's words. In a
        /// WebGL player the browser's rules (CORS, mixed content) are named.
        /// </summary>
        public static async Task<LlmConnectionCheckResult> CheckConnectionAsync(
            string baseUrl,
            string apiKey,
            string model,
            int timeoutSeconds = 45,
            CancellationToken cancellationToken = default)
        {
            ILlmConnectionHttp? http = ConnectionHttp;
            if (http == null)
            {
                return new LlmConnectionCheckResult
                {
                    Problem = LlmConnectionProblem.ServerError,
                    Message = NoHttpSupportMessage,
                    BaseUrl = LlmConnectionCheck.NormalizeBaseUrl(baseUrl),
                    Model = (model ?? "").Trim()
                };
            }

            bool inBrowser = Application.platform == RuntimePlatform.WebGLPlayer;
            return await LlmConnectionCheck.RunAsync(new LlmConnectionCheckRequest
            {
                BaseUrl = baseUrl ?? "",
                ApiKey = apiKey ?? "",
                Model = model ?? "",
                InBrowser = inBrowser,
                PageUrl = inBrowser ? Application.absoluteURL : "",
                CompletionTimeoutSeconds = timeoutSeconds <= 0 ? 45 : timeoutSeconds
            }, http, cancellationToken);
        }

        /// <summary>
        /// Tests the server (<see cref="CheckConnectionAsync"/>) and, when it answered, makes it the backend of this
        /// session at once, as <see cref="ApplyHttpApi(LlmExecutionMode,string,string,string,float?,int?,int?,bool?)"/>
        /// does (a ClientLimited or ServerManagedApi backend keeps its mode, anything else becomes ClientOwnedApi):
        /// every role on Automatic routing uses it from the next request. With <paramref name="remember"/> the
        /// address and the model are also kept on this device (<see cref="RememberedBackendStore"/>) and
        /// <see cref="RestoreRememberedBackend"/> uses them at the next start. The key is never kept: it lives in
        /// the session override only, so after a restart a server that needs one asks for it again.
        /// </summary>
        public static async Task<CoreAiConnectResult> ConnectHttpApiAsync(
            string baseUrl,
            string apiKey,
            string model,
            bool remember = true,
            int timeoutSeconds = 45,
            CancellationToken cancellationToken = default)
        {
            LlmConnectionCheckResult check =
                await CheckConnectionAsync(baseUrl, apiKey, model, timeoutSeconds, cancellationToken);
            CoreAiConnectResult result = new() { Check = check };
            if (!check.Ok)
            {
                return result;
            }

            CoreAISettingsAsset? settings = ResolveSettings();
            if (settings == null)
            {
                result.RememberProblem = "No CoreAI settings asset is available, so the server cannot be used.";
                return result;
            }

            LlmExecutionMode current = settings.ExecutionMode;
            LlmExecutionMode mode = LlmRememberedBackend.IsHttpMode(current) ? current : LlmExecutionMode.ClientOwnedApi;
            string key = (apiKey ?? "").Trim();
            result.IsLive = ApplyHttpApi(mode, check.BaseUrl, key, check.Model);
            result.Applied = true;
            settings.SetSessionPendingKeyBackend(null);
            if (!remember)
            {
                return result;
            }

            try
            {
                result.Remembered = RememberedBackendStore.Save(new LlmRememberedBackend
                {
                    Mode = mode,
                    BaseUrl = check.BaseUrl,
                    Model = check.Model,
                    NeedsKey = key.Length > 0
                });
                if (!result.Remembered)
                {
                    result.RememberProblem =
                        "This page cannot keep settings (its storage is off), so the server is used until the page closes.";
                }
            }
            catch (Exception ex)
            {
                // WHY contained: the server works; only keeping it for the next start failed, and that is said.
                result.RememberProblem = "The server could not be kept on this device: " + ex.Message;
            }

            return result;
        }

        /// <summary>The server remembered on this device; false when there is none.</summary>
        public static bool TryGetRememberedBackend(out LlmRememberedBackend? backend)
        {
            backend = null;
            try
            {
                if (RememberedBackendStore.TryLoad(out LlmRememberedBackend loaded))
                {
                    backend = loaded;
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogWarning("Reading the remembered server failed: " + ex.Message);
            }

            return false;
        }

        /// <summary>Forgets the remembered server; the current session keeps whatever backend it uses now.</summary>
        public static void ForgetRememberedBackend()
        {
            RememberedBackendStore.Clear();
            ResolveSettings()?.SetSessionPendingKeyBackend(null);
        }

        /// <summary>
        /// Uses the server remembered on this device for this session, without a click: called by a scene that offers
        /// players a settings surface (the play world's Hub module calls it before the scopes build). A server that
        /// needed no key is applied as a session override; one that needs a key leaves the session Offline, so the
        /// Programmer's offline line can say the key is missing, and sets
        /// <see cref="CoreAISettingsAsset.PendingKeyBackend"/>. Nothing remembered changes nothing.
        /// </summary>
        public static CoreAiRememberedBackendRestore RestoreRememberedBackend()
        {
            CoreAISettingsAsset? settings = ResolveSettings();
            if (settings == null || !TryGetRememberedBackend(out LlmRememberedBackend? backend) || backend == null)
            {
                return CoreAiRememberedBackendRestore.NothingRemembered;
            }

            lock (SyncRoot)
            {
                ApplySessionHttp(settings, backend.Mode, backend.BaseUrl, "", backend.Model, null, null, null, null);
                if (backend.NeedsKey)
                {
                    // WHY offline and not the server without its key: every request would fail with 401, while the
                    // offline line tells the player what is missing and where to enter it.
                    settings.ApplySessionOffline(false, null, null);
                    settings.SetSessionPendingKeyBackend(backend);
                }
                else
                {
                    settings.SetSessionPendingKeyBackend(null);
                }

                RebuildAndNotify(settings);
            }

            return backend.NeedsKey ? CoreAiRememberedBackendRestore.NeedsKey : CoreAiRememberedBackendRestore.Applied;
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
            // WHY empty for a body that is not a list: an HTML error page or a proxy notice is a normal failure of a
            // mistyped URL; the caller says what answered.
            return LlmConnectionCheck.TryParseModelIds(json, out List<string> ids) ? ids : new List<string>();
        }

        #region Internals

        private static void ApplySessionHttp(
            CoreAISettingsAsset settings,
            LlmExecutionMode mode,
            string baseUrl,
            string apiKey,
            string model,
            float? temperature,
            int? timeoutSeconds,
            int? maxTokens,
            bool? overrideTemperature)
        {
            settings.ApplySessionHttpApi(
                mode,
                baseUrl,
                apiKey,
                model,
                temperature ?? settings.Temperature,
                timeoutSeconds ?? settings.RequestTimeoutSeconds,
                maxTokens ?? settings.MaxTokens,
                overrideTemperature ?? settings.OverrideTemperature);
        }

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
                scope.Container.TryResolve<ILlmAgentProvider>(out ILlmAgentProvider agentProvider);
                ILog log = scope.Container.Resolve(typeof(ILog)) as ILog ?? Log.Instance;
                scope.Container.TryResolve<IUnityGatewayClientBinding>(out IUnityGatewayClientBinding gatewayBinding);

                ILlmClient rebuilt = LlmPipelineInstaller.BuildRoutedPrimaryClient(
                    settings, logger, memoryStore, agentProvider, log, gatewayBinding);
                try { registry.SetLegacyFallback(rebuilt); }
                catch
                {
                    if (rebuilt is IOwnedGatewayClient owned && owned.OwnsGatewayResources) owned.Dispose();
                    throw;
                }
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
