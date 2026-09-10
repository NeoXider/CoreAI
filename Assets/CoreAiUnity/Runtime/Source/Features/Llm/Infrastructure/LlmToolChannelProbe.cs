#if COREAI_LLM
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>What the server answered to a probe request that declared a tool.</summary>
    public enum LlmToolChannelProbeOutcome
    {
        /// <summary>The server accepted <c>tools</c> and answered successfully - the channel is there.</summary>
        Accepted = 0,

        /// <summary>The server rejected <c>tools</c> with an explicit jinja error - there is no channel.</summary>
        Rejected = 1,

        /// <summary>The answer says nothing about the channel (transport, timeout, another server error).</summary>
        Inconclusive = 2
    }

    /// <summary>Input of the tool-channel probe.</summary>
    public sealed class LlmToolChannelProbeRequest
    {
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "";

        /// <summary>
        /// The probe asks for a single token, but before that the server runs the prompt carrying the tool
        /// description through it, so the ceiling is higher than the readiness probe's: on a CPU with a
        /// heavy model the prefill takes seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 15;
    }

    /// <summary>Outcome of the tool-channel probe, free of host HTTP types.</summary>
    public sealed class LlmToolChannelProbeResult
    {
        public LlmToolChannelProbeOutcome Outcome { get; set; }
        public int StatusCode { get; set; }
        public string Detail { get; set; } = "";
    }

    /// <summary>Host-side probe: does an OpenAI-compatible server accept the <c>tools</c> parameter.</summary>
    public interface ILlmToolChannelProbe
    {
        Task<LlmToolChannelProbeResult> ProbeAsync(
            LlmToolChannelProbeRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// The probe request body and the rule for reading the answer - shared by every transport and testable
    /// without a network.
    /// </summary>
    public static class LlmToolChannelProbePolicy
    {
        public const string ProbeToolName = "coreai_tool_channel_probe";

        /// <summary>
        /// A minimal request with a single declared tool: one token of output, so the probe costs a prefill
        /// and not a generation. The model is under no obligation to call the tool itself - the question is
        /// addressed to the server, not to the model.
        /// </summary>
        public static string BuildRequestBody(string model)
        {
            JObject body = new()
            {
                ["model"] = string.IsNullOrWhiteSpace(model) ? "default" : model.Trim(),
                ["messages"] = new JArray(new JObject
                {
                    ["role"] = "user",
                    ["content"] = "ping"
                }),
                ["max_tokens"] = 1,
                ["temperature"] = 0,
                ["stream"] = false,
                ["tools"] = new JArray(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = ProbeToolName,
                        ["description"] = "CoreAI tool channel probe. Never call it.",
                        ["parameters"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject()
                        }
                    }
                })
            };
            return body.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Reads the server's answer. A refusal is recognized by the TEXT of the error, not by its code:
        /// llama.cpp returns "tools param requires --jinja flag" as HTTP 500 (`server_error`), verified by a
        /// live run on 2026-09-06; a 400 from other builds is read the same way. Anything that is neither a
        /// success nor that refusal is proof in neither direction.
        /// </summary>
        public static LlmToolChannelProbeResult Classify(int statusCode, string body, string transportError)
        {
            if (!string.IsNullOrWhiteSpace(transportError) || statusCode <= 0)
            {
                return new LlmToolChannelProbeResult
                {
                    Outcome = LlmToolChannelProbeOutcome.Inconclusive,
                    StatusCode = Math.Max(0, statusCode),
                    Detail = "transport: " + (string.IsNullOrWhiteSpace(transportError) ? "no response" : transportError.Trim())
                };
            }

            if (statusCode is >= 200 and < 300)
            {
                return new LlmToolChannelProbeResult
                {
                    Outcome = LlmToolChannelProbeOutcome.Accepted,
                    StatusCode = statusCode,
                    Detail = "HTTP " + statusCode.ToString(CultureInfo.InvariantCulture)
                };
            }

            string message = ExtractErrorMessage(body);
            string detail = "HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) +
                            (string.IsNullOrEmpty(message) ? "" : ": " + message);
            bool jinjaRejection = message.IndexOf("jinja", StringComparison.OrdinalIgnoreCase) >= 0;
            return new LlmToolChannelProbeResult
            {
                Outcome = jinjaRejection
                    ? LlmToolChannelProbeOutcome.Rejected
                    : LlmToolChannelProbeOutcome.Inconclusive,
                StatusCode = statusCode,
                Detail = detail
            };
        }

        private static string ExtractErrorMessage(string body)
        {
            string text = (body ?? "").Trim();
            if (text.Length == 0)
            {
                return "";
            }

            try
            {
                JToken parsed = JToken.Parse(text);
                string message = parsed?["error"]?["message"]?.Type == JTokenType.String
                    ? parsed["error"]["message"].Value<string>()
                    : parsed?["message"]?.Type == JTokenType.String
                        ? parsed["message"].Value<string>()
                        : null;
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return SingleLine(message, 200);
                }
            }
            catch (Exception)
            {
                // WHY: the body is not JSON (a proxy's HTML page, plain text) - read it as is, truncated.
            }

            return SingleLine(text, 200);
        }

        private static string SingleLine(string value, int maxLength)
        {
            string flat = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return flat.Length <= maxLength ? flat : flat.Substring(0, maxLength) + "…";
        }
    }

    /// <summary>Where the channel decision came from - the log level depends on it.</summary>
    public enum LlmToolChannelDecisionSource
    {
        /// <summary>An explicit endpoint setting.</summary>
        Declared = 0,

        /// <summary>Known from the endpoint's kind, or from an established fact about the server, with no probe.</summary>
        Known = 1,

        /// <summary>The probe gave an unambiguous answer.</summary>
        Probe = 2,

        /// <summary>The probe failed - the conservative channel was taken.</summary>
        ProbeInconclusive = 3
    }

    /// <summary>The channel decision together with its reason - the reason goes to the log verbatim.</summary>
    public readonly struct LlmToolChannelDecision
    {
        public LlmToolChannelDecision(bool native, LlmToolChannelDecisionSource source, string reason)
        {
            Native = native;
            Source = source;
            Reason = reason ?? "";
        }

        public bool Native { get; }
        public LlmToolChannelDecisionSource Source { get; }
        public string Reason { get; }
        public string ChannelName => Native ? "native" : "text";
    }

    /// <summary>
    /// The single place where the setting and the probe add up to a decision. The order: an explicit
    /// setting wins with no probe and no extra request; otherwise the probe runs; a failed probe means the
    /// text channel, because that one works on any server (the client executes native <c>tool_calls</c>
    /// inside it too), whereas native on a server without jinja fails every request carrying tools.
    /// </summary>
    public static class LlmToolChannelResolution
    {
        /// <summary>
        /// The reason used on the probe-less paths under LLMUnity. The fact was established by a live run on
        /// 2026-09-06: the LlamaLib v2.0.5 server (bundled with LLMUnity 3.0.3), brought up by the same
        /// <c>LLMService_Construct</c> + <c>LLM_Start_Server</c> as the <c>LLM</c> component, accepts
        /// <c>tools</c> and returns <c>tool_calls</c> (over SSE as well) - its llama.cpp is built with jinja
        /// on by default.
        /// </summary>
        public const string BundledLlamaLibReason =
            "LLMUnity's bundled LlamaLib v2.0.5 server starts with jinja chat templates on and returns native " +
            "tool_calls (verified 2026-09-06); no probe is possible on this path because the client is built " +
            "before the server exists — set LlmUnityToolChannel=Text for a LlamaLib build that rejects tools";

        /// <summary>Description of the explicitly chosen legacy OpenAI adapter; runtime Auto uses the probe.</summary>
        public const string OpenAiHttpReason =
            "Legacy OpenAI adapter declares native tool calling; runtime endpoints use ToolChannel=Auto " +
            "to probe the actual server capability";

        public static bool RequiresProbe(LlmToolChannel setting)
        {
            return setting == LlmToolChannel.Auto;
        }

        /// <summary>The decision where there is no probe: an explicit setting, or the known answer for <c>Auto</c>.</summary>
        public static LlmToolChannelDecision ResolveWithoutProbe(LlmToolChannel setting, string autoReason)
        {
            return setting == LlmToolChannel.Auto
                ? new LlmToolChannelDecision(true, LlmToolChannelDecisionSource.Known, autoReason)
                : Declared(setting);
        }

        /// <summary>The decision from a probe; an explicit setting still wins, and then no probe is needed.</summary>
        public static LlmToolChannelDecision Resolve(LlmToolChannel setting, LlmToolChannelProbeResult probe)
        {
            if (setting != LlmToolChannel.Auto)
            {
                return Declared(setting);
            }

            if (probe == null)
            {
                return new LlmToolChannelDecision(
                    false,
                    LlmToolChannelDecisionSource.ProbeInconclusive,
                    "tool-channel probe did not run; keeping the text channel without unsupported native tool fields — " +
                    "set ToolChannel explicitly to skip the probe");
            }

            switch (probe.Outcome)
            {
                case LlmToolChannelProbeOutcome.Accepted:
                    return new LlmToolChannelDecision(
                        true,
                        LlmToolChannelDecisionSource.Probe,
                        "server accepted the tools parameter on a probe request (" + probe.Detail + ")");
                case LlmToolChannelProbeOutcome.Rejected:
                    return new LlmToolChannelDecision(
                        false,
                        LlmToolChannelDecisionSource.Probe,
                        "server rejected the tools parameter (" + probe.Detail + "); the model's prose will be " +
                        "read for tool calls — start llama.cpp with --jinja and a tool-use chat template to get " +
                        "the native channel");
                default:
                    return new LlmToolChannelDecision(
                        false,
                        LlmToolChannelDecisionSource.ProbeInconclusive,
                        "tool-channel probe inconclusive (" + probe.Detail + "); keeping the text channel without " +
                        "unsupported native tool fields — set ToolChannel explicitly to skip the probe");
            }
        }

        private static LlmToolChannelDecision Declared(LlmToolChannel setting)
        {
            return new LlmToolChannelDecision(
                setting == LlmToolChannel.Native,
                LlmToolChannelDecisionSource.Declared,
                "declared by endpoint configuration (ToolChannel=" + setting + ")");
        }
    }

    /// <summary>One log line about the chosen channel - so the choice is never a silent one.</summary>
    public static class LlmToolChannelLog
    {
        public static string Format(string endpointId, string displayName, LlmToolChannelDecision decision)
        {
            return "[CoreAI.LLM] tool channel for endpoint \"" + Safe(displayName, endpointId) + "\"" +
                   " (endpointId=\"" + Safe(endpointId, "") + "\")" +
                   " = " + decision.ChannelName +
                   " — " + Safe(decision.Reason, "");
        }

        private static string Safe(string value, string fallback)
        {
            string text = string.IsNullOrWhiteSpace(value) ? fallback ?? "" : value;
            return text.Trim().Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
        }
    }

    /// <summary>Unity adapter of the channel probe: one POST to <c>/chat/completions</c> with a declared tool.</summary>
    public sealed class UnityWebRequestToolChannelProbe : ILlmToolChannelProbe
    {
        public async Task<LlmToolChannelProbeResult> ProbeAsync(
            LlmToolChannelProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!Uri.TryCreate((request.BaseUrl ?? "").TrimEnd('/'), UriKind.Absolute, out Uri baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            {
                return LlmToolChannelProbePolicy.Classify(0, "", "endpoint base URL is invalid");
            }

            UriBuilder builder = new(baseUri)
            {
                Path = baseUri.AbsolutePath.TrimEnd('/') + "/chat/completions",
                Query = "",
                Fragment = ""
            };

            using UnityWebRequest webRequest = new(builder.Uri.AbsoluteUri, UnityWebRequest.kHttpVerbPOST);
            webRequest.redirectLimit = 0;
            webRequest.downloadHandler = new DownloadHandlerBuffer();
            webRequest.uploadHandler = new UploadHandlerRaw(
                Encoding.UTF8.GetBytes(LlmToolChannelProbePolicy.BuildRequestBody(request.Model)));
            webRequest.SetRequestHeader("Content-Type", "application/json");
            webRequest.timeout = Math.Max(1, request.TimeoutSeconds);
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                webRequest.SetRequestHeader("Authorization", "Bearer " + request.ApiKey.Trim());
            }

            cancellationToken.ThrowIfCancellationRequested();
            UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
            await UnityWebRequestOpenAiTransport.AwaitCompletionAsync(operation, webRequest, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            int status = webRequest.responseCode > 0 ? (int)webRequest.responseCode : 0;
            string body = webRequest.downloadHandler?.text ?? "";
            string transportError = status > 0 ? "" : webRequest.error ?? "network error";
            return LlmToolChannelProbePolicy.Classify(status, body, transportError);
        }
    }
}
#endif
