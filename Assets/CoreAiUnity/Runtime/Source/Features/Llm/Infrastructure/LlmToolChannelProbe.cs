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
    /// <summary>Чем сервер ответил на пробный запрос с объявленным инструментом.</summary>
    public enum LlmToolChannelProbeOutcome
    {
        /// <summary>Сервер принял <c>tools</c> и ответил успешно — канал есть.</summary>
        Accepted = 0,

        /// <summary>Сервер отверг <c>tools</c> внятной ошибкой про jinja — канала нет.</summary>
        Rejected = 1,

        /// <summary>Ответ не позволяет судить о канале (транспорт, таймаут, другая ошибка сервера).</summary>
        Inconclusive = 2
    }

    /// <summary>Вход пробы канала инструментов.</summary>
    public sealed class LlmToolChannelProbeRequest
    {
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "";

        /// <summary>
        /// Проба просит один токен, но перед ним сервер прогоняет промпт с описанием инструмента, поэтому
        /// потолок выше, чем у пробы готовности: на CPU с тяжёлой моделью префилл занимает секунды.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 15;
    }

    /// <summary>Исход пробы канала инструментов без хостовых HTTP-типов.</summary>
    public sealed class LlmToolChannelProbeResult
    {
        public LlmToolChannelProbeOutcome Outcome { get; set; }
        public int StatusCode { get; set; }
        public string Detail { get; set; } = "";
    }

    /// <summary>Хостовая проба: принимает ли OpenAI-совместимый сервер параметр <c>tools</c>.</summary>
    public interface ILlmToolChannelProbe
    {
        Task<LlmToolChannelProbeResult> ProbeAsync(
            LlmToolChannelProbeRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Тело пробного запроса и правило чтения ответа — общие для любого транспорта и проверяемые без сети.
    /// </summary>
    public static class LlmToolChannelProbePolicy
    {
        public const string ProbeToolName = "coreai_tool_channel_probe";

        /// <summary>
        /// Минимальный запрос с одним объявленным инструментом: один токен на выходе, чтобы проба стоила
        /// префилл, а не генерацию. Сам инструмент модель вызывать не обязана — вопрос к серверу, а не к ней.
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
        /// Читает ответ сервера. Отказ распознаётся по ТЕКСТУ ошибки, а не по коду: llama.cpp отдаёт
        /// «tools param requires --jinja flag» как HTTP 500 (`server_error`), проверено живым прогоном
        /// 2026-09-06; код 400 у других сборок читается так же. Всё, что не успех и не этот отказ, —
        /// не доказательство ни в одну сторону.
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
                // WHY: тело не JSON (HTML-страница прокси, plain text) — читаем как есть, обрезав.
            }

            return SingleLine(text, 200);
        }

        private static string SingleLine(string value, int maxLength)
        {
            string flat = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return flat.Length <= maxLength ? flat : flat.Substring(0, maxLength) + "…";
        }
    }

    /// <summary>Откуда взялось решение о канале — от этого зависит уровень записи в логе.</summary>
    public enum LlmToolChannelDecisionSource
    {
        /// <summary>Явная настройка эндпойнта.</summary>
        Declared = 0,

        /// <summary>Известно из вида эндпойнта или из установленного факта о сервере, без пробы.</summary>
        Known = 1,

        /// <summary>Проба дала однозначный ответ.</summary>
        Probe = 2,

        /// <summary>Проба не удалась — взят консервативный канал.</summary>
        ProbeInconclusive = 3
    }

    /// <summary>Решение о канале вместе с причиной — причина уходит в лог дословно.</summary>
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
    /// Единственное место, где настройка и проба складываются в решение. Порядок: явная настройка —
    /// без пробы и без лишнего запроса; иначе проба; неудачная проба — текстовый канал, потому что он
    /// работает на любом сервере (нативные <c>tool_calls</c> клиент исполняет и в нём), а нативный на
    /// сервере без jinja роняет каждый запрос с инструментами.
    /// </summary>
    public static class LlmToolChannelResolution
    {
        /// <summary>
        /// Причина для путей без пробы под LLMUnity. Факт установлен живым прогоном 2026-09-06: сервер
        /// LlamaLib v2.0.5 (комплект LLMUnity 3.0.3), поднятый тем же <c>LLMService_Construct</c> +
        /// <c>LLM_Start_Server</c>, что и компонент <c>LLM</c>, принимает <c>tools</c> и возвращает
        /// <c>tool_calls</c> (в том числе по SSE) — его llama.cpp собран с jinja по умолчанию.
        /// </summary>
        public const string BundledLlamaLibReason =
            "LLMUnity's bundled LlamaLib v2.0.5 server starts with jinja chat templates on and returns native " +
            "tool_calls (verified 2026-09-06); no probe is possible on this path because the client is built " +
            "before the server exists — set LlmUnityToolChannel=Text for a LlamaLib build that rejects tools";

        /// <summary>Описание явно выбранного legacy OpenAI-адаптера; runtime Auto использует пробу.</summary>
        public const string OpenAiHttpReason =
            "Legacy OpenAI adapter declares native tool calling; runtime endpoints use ToolChannel=Auto " +
            "to probe the actual server capability";

        public static bool RequiresProbe(LlmToolChannel setting)
        {
            return setting == LlmToolChannel.Auto;
        }

        /// <summary>Решение там, где пробы нет: явная настройка либо известный ответ для <c>Auto</c>.</summary>
        public static LlmToolChannelDecision ResolveWithoutProbe(LlmToolChannel setting, string autoReason)
        {
            return setting == LlmToolChannel.Auto
                ? new LlmToolChannelDecision(true, LlmToolChannelDecisionSource.Known, autoReason)
                : Declared(setting);
        }

        /// <summary>Решение по пробе; явная настройка по-прежнему выигрывает, проба тогда не нужна.</summary>
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

    /// <summary>Одна строка лога о выбранном канале — чтобы выбор никогда не был молчаливым.</summary>
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

    /// <summary>Unity-адаптер пробы канала: один POST на <c>/chat/completions</c> с объявленным инструментом.</summary>
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
