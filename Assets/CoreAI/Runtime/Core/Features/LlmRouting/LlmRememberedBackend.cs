using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// The model server a player chose and tested, kept on the device so the next start uses it without a click:
    /// the execution mode, the base URL, the model and whether the server needed a key. The key itself is never
    /// part of it (keys live for one session only); <see cref="NeedsKey"/> lets the next start say that the key
    /// has to be entered again instead of failing every request.
    /// </summary>
    public sealed class LlmRememberedBackend
    {
        /// <summary>Version of the stored shape.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>An HTTP execution mode: ClientOwnedApi, ClientLimited or ServerManagedApi.</summary>
        public LlmExecutionMode Mode { get; set; } = LlmExecutionMode.ClientOwnedApi;

        /// <summary>OpenAI-compatible base URL.</summary>
        public string BaseUrl { get; set; } = "";

        /// <summary>Model name.</summary>
        public string Model { get; set; } = "";

        /// <summary>True when the test that chose this server sent an API key.</summary>
        public bool NeedsKey { get; set; }

        /// <summary>True when <paramref name="mode"/> is one a remembered server may use.</summary>
        public static bool IsHttpMode(LlmExecutionMode mode)
        {
            return mode == LlmExecutionMode.ClientOwnedApi || mode == LlmExecutionMode.ClientLimited ||
                   mode == LlmExecutionMode.ServerManagedApi;
        }

        /// <summary>True when the mode is an HTTP mode, the base URL a valid web address and a model is named.</summary>
        public bool IsValid()
        {
            return IsHttpMode(Mode) && !string.IsNullOrWhiteSpace(Model) &&
                   LlmConnectionCheck.TryValidateBaseUrl(BaseUrl, out _, out _, out _);
        }

        /// <summary>The stored JSON; it has no field for a key.</summary>
        public string ToJson()
        {
            JObject json = new()
            {
                ["schemaVersion"] = CurrentSchemaVersion,
                ["mode"] = Mode.ToString(),
                ["baseUrl"] = BaseUrl ?? "",
                ["model"] = Model ?? "",
                ["needsKey"] = NeedsKey
            };
            return json.ToString(Formatting.Indented);
        }

        /// <summary>
        /// Reads what <see cref="ToJson"/> wrote. False for an empty or broken file, another schema version, or a
        /// value that is not <see cref="IsValid"/>; any other field (a key someone added by hand) is ignored.
        /// </summary>
        public static bool TryParse(string json, out LlmRememberedBackend backend)
        {
            backend = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                if (JToken.Parse(json) is not JObject root ||
                    root.Value<int?>("schemaVersion") != CurrentSchemaVersion ||
                    !Enum.TryParse(root.Value<string>("mode") ?? "", false, out LlmExecutionMode mode))
                {
                    return false;
                }

                LlmRememberedBackend parsed = new()
                {
                    Mode = mode,
                    BaseUrl = LlmConnectionCheck.NormalizeBaseUrl(root.Value<string>("baseUrl")),
                    Model = (root.Value<string>("model") ?? "").Trim(),
                    NeedsKey = root.Value<bool?>("needsKey") ?? false
                };
                if (!parsed.IsValid())
                {
                    return false;
                }

                backend = parsed;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (InvalidCastException)
            {
                return false;
            }
        }
    }

    /// <summary>Where a <see cref="LlmRememberedBackend"/> is kept between starts.</summary>
    public interface ILlmRememberedBackendStore
    {
        /// <summary>The remembered server; false when there is none or it cannot be read.</summary>
        bool TryLoad(out LlmRememberedBackend backend);

        /// <summary>
        /// Keeps <paramref name="backend"/>. True when it will survive a restart (in a browser: the page's storage is
        /// armed); false when it was kept for this run only.
        /// </summary>
        bool Save(LlmRememberedBackend backend);

        /// <summary>Forgets the remembered server.</summary>
        void Clear();
    }

    /// <summary>An <see cref="ILlmRememberedBackendStore"/> in memory: for tests and hosts without storage.</summary>
    public sealed class InMemoryLlmRememberedBackendStore : ILlmRememberedBackendStore
    {
        private string _json = "";

        /// <summary>What <see cref="Save"/> answers.</summary>
        public bool Durable { get; set; } = true;

        /// <summary>The JSON kept now; empty when nothing is remembered.</summary>
        public string Json => _json;

        /// <inheritdoc />
        public bool TryLoad(out LlmRememberedBackend backend)
        {
            return LlmRememberedBackend.TryParse(_json, out backend);
        }

        /// <inheritdoc />
        public bool Save(LlmRememberedBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException(nameof(backend));
            }

            _json = backend.ToJson();
            return Durable;
        }

        /// <inheritdoc />
        public void Clear()
        {
            _json = "";
        }
    }
}
