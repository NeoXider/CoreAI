using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using UnityEngine;
using UnityEngine.Networking;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Клиент к эндпоинту <c>/chat/completions</c> в формате OpenAI (совместим с многими прокси: vLLM, LiteLLM, LM Studio и т.д.).
    /// Вызовы <see cref="CompleteAsync"/> должны идти с главного потока Unity (как и <see cref="LlmUnityLlmClient"/>).
    /// </summary>
    public sealed class OpenAiChatLlmClient : ILlmClient
    {
        private readonly OpenAiHttpLlmSettings _settings;

        /// <param name="settings">Asset с URL, ключом и именем модели.</param>
        public OpenAiChatLlmClient(OpenAiHttpLlmSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <inheritdoc />
        public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
        {
            var url = _settings.ApiBaseUrl + "/chat/completions";
            var body = BuildJsonBody(request);
            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            var bodyRaw = Encoding.UTF8.GetBytes(body);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(_settings.ApiKey))
                req.SetRequestHeader("Authorization", "Bearer " + _settings.ApiKey);
            req.timeout = _settings.RequestTimeoutSeconds;

            var tcs = new TaskCompletionSource<LlmCompletionResult>();
            CancellationTokenRegistration ctr = default;
            if (cancellationToken.CanBeCanceled)
            {
                ctr = cancellationToken.Register(() =>
                {
                    req.Abort();
                    tcs.TrySetResult(new LlmCompletionResult { Ok = false, Error = "Cancelled" });
                });
            }

            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    ctr.Dispose();
                    if (tcs.Task.IsCompleted)
                        return;
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        tcs.TrySetResult(new LlmCompletionResult
                        {
                            Ok = false,
                            Error = string.IsNullOrEmpty(req.error) ? req.downloadHandler.text : req.error
                        });
                        return;
                    }

                    var text = req.downloadHandler.text ?? "";
                    if (text.IndexOf("\"error\"", StringComparison.Ordinal) >= 0)
                    {
                        tcs.TrySetResult(new LlmCompletionResult { Ok = false, Error = text });
                        return;
                    }

                    var parsed = TryParseChatResponse(text);
                    if (parsed.Content == null)
                        tcs.TrySetResult(new LlmCompletionResult { Ok = false, Error = "Bad chat/completions JSON: " + text });
                    else
                    {
                        var ok = new LlmCompletionResult { Ok = true, Content = parsed.Content };
                        if (parsed.Usage != null)
                        {
                            ok.PromptTokens = parsed.Usage.prompt_tokens;
                            ok.CompletionTokens = parsed.Usage.completion_tokens;
                            ok.TotalTokens = parsed.Usage.total_tokens;
                        }

                        tcs.TrySetResult(ok);
                    }
                }
                finally
                {
                    req.Dispose();
                }
            };

            return tcs.Task;
        }

        private string BuildJsonBody(LlmCompletionRequest request)
        {
            var sys = JsonEscape(request.SystemPrompt ?? "");
            var user = JsonEscape(request.UserPayload ?? "");
            var model = JsonEscape(_settings.Model);
            var maxOutputTokens = request.MaxOutputTokens.GetValueOrDefault(
                Math.Max(128, Math.Min(2048, request.ContextWindowTokens / 4)));
            return "{\"model\":\"" + model + "\",\"temperature\":" +
                   _settings.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                   ",\"max_tokens\":" + maxOutputTokens +
                   ",\"messages\":[" +
                   "{\"role\":\"system\",\"content\":\"" + sys + "\"}," +
                   "{\"role\":\"user\",\"content\":\"" + user + "\"}" +
                   "]}";
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        private static (string Content, OaiUsage Usage) TryParseChatResponse(string json)
        {
            if (string.IsNullOrEmpty(json))
                return (null, null);
            if (!LlmResponseSanitizer.TryPrepareJsonObject(json, out var body) || string.IsNullOrEmpty(body))
                body = json.Trim();
            try
            {
                var dto = JsonUtility.FromJson<OaiChatResponse>(body);
                if (dto?.choices == null || dto.choices.Length == 0)
                    return (null, null);
                var m = dto.choices[0].message;
                var content = m?.content ?? "";
                return (content, dto.usage);
            }
            catch
            {
                return (null, null);
            }
        }

        [Serializable]
        private class OaiChatResponse
        {
            public OaiChoice[] choices;
            public OaiUsage usage;
        }

        [Serializable]
        private class OaiUsage
        {
            public int prompt_tokens;
            public int completion_tokens;
            public int total_tokens;
        }

        [Serializable]
        private class OaiChoice
        {
            public OaiMessage message;
        }

        [Serializable]
        private class OaiMessage
        {
            public string content;
        }
    }
}
