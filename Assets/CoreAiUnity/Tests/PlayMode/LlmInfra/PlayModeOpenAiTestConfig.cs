using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Single configuration surface for the LIVE PlayMode suite against ANY OpenAI-compatible provider
    /// (OpenAI, OpenRouter, LM Studio, Ollama, vLLM, a local gateway, ...).
    ///
    /// <para>Resolution precedence (highest wins), evaluated field-by-field:</para>
    /// <list type="number">
    ///   <item>Environment variables (CI / shell): <c>COREAI_TEST_*</c> (canonical) and legacy aliases.</item>
    ///   <item>Optional gitignored local config file (JSON) — see <see cref="LocalConfigFileName"/>.</item>
    ///   <item>Auto-detect from the project's <c>CoreAISettingsAsset</c> (only when explicitly opted in;
    ///         the factory already prefers a fully configured asset on its own).</item>
    /// </list>
    ///
    /// <para>So: <b>env overrides the local file, the local file overrides the asset/auto-detect.</b>
    /// Each field resolves independently, so you can set only <c>COREAI_TEST_MODEL</c> in the shell
    /// while keeping base URL + key in the local file.</para>
    ///
    /// <para>Canonical env vars:</para>
    /// <list type="bullet">
    ///   <item><c>COREAI_TEST_BASE_URL</c> — OpenAI-compatible base, e.g. <c>https://openrouter.ai/api/v1</c>.</item>
    ///   <item><c>COREAI_TEST_API_KEY</c> — bearer token (may be empty for keyless local servers).</item>
    ///   <item><c>COREAI_TEST_MODEL</c> — model id, e.g. <c>openai/gpt-4o-mini</c>.</item>
    ///   <item><c>COREAI_TEST_STREAMING</c> — <c>true</c>/<c>false</c> (default <c>true</c>).</item>
    ///   <item><c>COREAI_TEST_NATIVE_TOOLS</c> — <c>true</c>/<c>false</c> (default <c>true</c>).</item>
    /// </list>
    ///
    /// <para>Local config file (gitignored): place <c>coreai-live-tests.local.json</c> at the Unity project
    /// root (the folder that contains <c>Assets/</c>). See <c>Assets/CoreAiUnity/Docs/RUNNING_LIVE_TESTS.md</c>.</para>
    /// </summary>
    public static class PlayModeOpenAiTestConfig
    {
        // ----- Canonical env vars -----
        public const string EnvBaseUrl = "COREAI_TEST_BASE_URL";
        public const string EnvApiKey = "COREAI_TEST_API_KEY";
        public const string EnvModel = "COREAI_TEST_MODEL";
        public const string EnvStreaming = "COREAI_TEST_STREAMING";
        public const string EnvNativeTools = "COREAI_TEST_NATIVE_TOOLS";

        // ----- Legacy env aliases (kept so existing setups keep working) -----
        private const string LegacyEnvBase = "COREAI_OPENAI_TEST_BASE";
        private const string LegacyEnvModel = "COREAI_OPENAI_TEST_MODEL";
        private const string LegacyEnvApiKey = "COREAI_OPENAI_TEST_API_KEY";
        private const string EnvUseProjectDefaults = "COREAI_OPENAI_TEST_USE_PROJECT_DEFAULTS";

        /// <summary>Gitignored local config file name, resolved relative to the project root (next to <c>Assets/</c>).</summary>
        public const string LocalConfigFileName = "coreai-live-tests.local.json";

        /// <summary>Optional env var that points at an explicit local config file path (overrides the default location).</summary>
        public const string EnvLocalConfigPath = "COREAI_TEST_CONFIG";

        /// <summary>Fallback base URL used only when <see cref="EnvUseProjectDefaults"/> opts in. Override via env/file.</summary>
        public const string FallbackLmStudioBaseUrl = "http://192.168.56.1:1234/v1";

        /// <summary>Fallback model used only when <see cref="EnvUseProjectDefaults"/> opts in. Override via env/file.</summary>
        public const string FallbackLmStudioModelId = "qwen3.5-35b-a3b-uncensored-hauhaucs-aggressive@iq4_xs";

        /// <summary>Default when neither the legacy opt-in env nor a file/env value is present.</summary>
        private const bool UseProjectDefaults = false;

        /// <summary>Immutable snapshot of the resolved live-test configuration.</summary>
        public sealed class ResolvedConfig
        {
            public string BaseUrl { get; }
            public string ApiKey { get; }
            public string Model { get; }
            public bool Streaming { get; }
            public bool NativeTools { get; }

            /// <summary>True when both base URL and model resolved to non-empty values.</summary>
            public bool IsComplete => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

            internal ResolvedConfig(string baseUrl, string apiKey, string model, bool streaming, bool nativeTools)
            {
                BaseUrl = baseUrl;
                ApiKey = apiKey ?? "";
                Model = model;
                Streaming = streaming;
                NativeTools = nativeTools;
            }
        }

        /// <summary>
        /// Resolves the full live-test configuration applying the documented env &gt; file &gt; auto precedence.
        /// </summary>
        /// <param name="modelOverride">
        /// Optional per-test model id (e.g. a vision-capable model). When non-empty it wins over every other source.
        /// </param>
        public static ResolvedConfig Resolve(string modelOverride = null)
        {
            LocalFileConfig file = LoadLocalFile();

            string baseUrl = FirstNonEmpty(
                GetEnv(EnvBaseUrl),
                GetEnv(LegacyEnvBase),
                file?.BaseUrl,
                ProjectDefaultsEnabled() ? FallbackLmStudioBaseUrl : null);
            baseUrl = NormalizeBaseUrl(baseUrl);

            string apiKey = FirstNonEmpty(
                GetEnv(EnvApiKey),
                GetEnv(LegacyEnvApiKey),
                file?.ApiKey) ?? "";

            string model = FirstNonEmpty(
                modelOverride,
                GetEnv(EnvModel),
                GetEnv(LegacyEnvModel),
                file?.Model,
                ProjectDefaultsEnabled() ? FallbackLmStudioModelId : null);
            model = model?.Trim();

            bool streaming = ResolveBool(GetEnv(EnvStreaming), file?.Streaming, true);
            bool nativeTools = ResolveBool(GetEnv(EnvNativeTools), file?.NativeTools, true);

            return new ResolvedConfig(baseUrl, apiKey, model, streaming, nativeTools);
        }

        /// <summary>
        /// Builds the developer-facing reason a live test was ignored, naming the exact env vars / file to set.
        /// </summary>
        public static string BuildIgnoreReason(ResolvedConfig config = null)
        {
            config ??= Resolve();
            string missing = string.IsNullOrWhiteSpace(config.BaseUrl)
                ? string.IsNullOrWhiteSpace(config.Model) ? "base URL and model" : "base URL"
                : "model";

            return
                $"LIVE PlayMode suite is not configured (missing {missing}). " +
                $"Point it at an OpenAI-compatible provider by setting env vars " +
                $"{EnvBaseUrl} + {EnvModel} (and {EnvApiKey} if the provider needs a key), " +
                $"or create a gitignored '{LocalConfigFileName}' at the project root " +
                $"(see Assets/CoreAiUnity/Docs/RUNNING_LIVE_TESTS.md). " +
                $"Optional toggles: {EnvStreaming}, {EnvNativeTools}. " +
                $"A fully configured CoreAISettingsAsset (HTTP backend) is also honored automatically.";
        }

        // ---------------------------------------------------------------------
        // Backwards-compatible helpers (existing callers keep working unchanged)
        // ---------------------------------------------------------------------

        /// <summary>Legacy entry point: resolved base URL or <c>null</c> when unconfigured.</summary>
        public static string ResolveBaseUrl()
        {
            return Resolve().BaseUrl;
        }

        /// <summary>Legacy entry point: resolved model id or <c>null</c> when unconfigured.</summary>
        public static string ResolveModelId()
        {
            return Resolve().Model;
        }

        // ---------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------

        private static bool ProjectDefaultsEnabled()
        {
            string e = GetEnv(EnvUseProjectDefaults);
            if (!string.IsNullOrWhiteSpace(e))
            {
                return ParseBool(e, UseProjectDefaults);
            }

            return UseProjectDefaults;
        }

        private static string GetEnv(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (string v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v.Trim();
                }
            }

            return null;
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            return string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/');
        }

        private static bool ResolveBool(string envValue, bool? fileValue, bool fallback)
        {
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                return ParseBool(envValue, fallback);
            }

            return fileValue ?? fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                case "enabled":
                    return true;
                case "0":
                case "false":
                case "no":
                case "off":
                case "disabled":
                    return false;
                default:
                    return fallback;
            }
        }

        /// <summary>Resolves the local config file path: explicit env override, else project root.</summary>
        public static string ResolveLocalConfigPath()
        {
            string explicitPath = GetEnv(EnvLocalConfigPath);
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return explicitPath;
            }

            // Application.dataPath ends with "/Assets"; the project root is its parent.
            string dataPath = Application.dataPath;
            string projectRoot = string.IsNullOrEmpty(dataPath)
                ? Directory.GetCurrentDirectory()
                : Directory.GetParent(dataPath)?.FullName ?? Directory.GetCurrentDirectory();

            return Path.Combine(projectRoot, LocalConfigFileName);
        }

        private static LocalFileConfig LoadLocalFile()
        {
            try
            {
                string path = ResolveLocalConfigPath();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                JObject root = JObject.Parse(json);
                return new LocalFileConfig
                {
                    BaseUrl = ReadString(root, "baseUrl", "base_url", "BaseUrl"),
                    ApiKey = ReadString(root, "apiKey", "api_key", "ApiKey"),
                    Model = ReadString(root, "model", "Model"),
                    Streaming = ReadBool(root, "streaming", "Streaming"),
                    NativeTools = ReadBool(root, "nativeTools", "native_tools", "NativeTools")
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayModeOpenAiTestConfig] Failed to read '{LocalConfigFileName}': {ex.Message}");
                return null;
            }
        }

        private static string ReadString(JObject root, params string[] keys)
        {
            foreach (string key in keys)
            {
                JToken token = root[key];
                if (token != null && token.Type != JTokenType.Null)
                {
                    string v = token.ToString();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        return v.Trim();
                    }
                }
            }

            return null;
        }

        private static bool? ReadBool(JObject root, params string[] keys)
        {
            foreach (string key in keys)
            {
                JToken token = root[key];
                if (token == null || token.Type == JTokenType.Null)
                {
                    continue;
                }

                if (token.Type == JTokenType.Boolean)
                {
                    return token.Value<bool>();
                }

                bool parsed = ParseBool(token.ToString(), false);
                return parsed;
            }

            return null;
        }

        private sealed class LocalFileConfig
        {
            public string BaseUrl;
            public string ApiKey;
            public string Model;
            public bool? Streaming;
            public bool? NativeTools;
        }
    }
}