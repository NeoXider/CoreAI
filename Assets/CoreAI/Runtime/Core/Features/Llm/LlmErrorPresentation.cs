using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Превращает сбой LLM в две разные строки: одну для ИГРОКА, другую для ЛОГА.
    ///
    /// ПОЧЕМУ: чат-UI когда-то вставлял <c>exception.Message</c> прямо в ленту, и игрок видел
    /// <c>HTTP error 403: {"error":{"message":"..."}}</c> — технично, часто обрезано и бесполезно, —
    /// а лог получал ту же короткую строку и терял тело ответа провайдера. Этот тип разводит аудитории:
    /// <list type="bullet">
    /// <item><see cref="ToUserMessage(Exception, string)"/> — одно читаемое предложение. Побеждает фраза,
    ///   которую НАШ бэкенд написал для игрока (шлюз, уже сказавший «учитель недоступен, попробуй через
    ///   минуту», знает продукт лучше библиотеки); иначе — фраза по <see cref="LlmErrorCode"/>.</item>
    /// <item><see cref="ToDiagnosticText"/> — всё, что стоит сохранить: код ошибки, HTTP-статус,
    ///   подсказка о повторе и сырое тело ответа провайдера.</item>
    /// </list>
    /// <para>
    /// <b>Авторство проверяется, а не предполагается.</b> Текст показывается игроку только когда тело
    /// ошибки несёт канонический конверт бэкенда — строковые поля верхнего уровня <c>error_code</c>,
    /// <c>request_id</c> и <c>message</c> (зеркало <c>error.message</c> принимается как носитель текста).
    /// Сырой провайдер (OpenAI, Anthropic, Groq, OpenRouter, HTML-страница прокси) такой конверт не
    /// эмитит, поэтому его <c>error.message</c> — на языке провайдера, с именами моделей, id организаций
    /// и ссылками на биллинг — никогда не принимается за фразу для игрока. Голое
    /// <c>HTTP error 503: …</c> из сообщения исключения — транспортный текст, он тоже не показывается.
    /// Даже сообщение из конверта проходит фильтр шума (<see cref="IsPresentableToPlayer"/>): трасса
    /// стека или traceback, просочившиеся в сообщение бэкенда, в ленту не попадают.
    /// </para>
    /// Портативное ядро: без типов Unity, одно и то же отображение доступно любому хосту и headless-тесту.
    /// </summary>
    public static class LlmErrorPresentation
    {
        /// <summary>Запасная фраза, когда ничего конкретнее не известно.</summary>
        public const string DefaultUserMessage =
            "The assistant is unavailable right now. Please try again in a moment.";

        /// <summary>Самое длинное сообщение бэкенда, которое показывается игроку как есть.</summary>
        public const int MaxUserMessageLength = 400;

        // ПОЧЕМУ: настоящая диагностика приходит в нескольких форматах, и ни один из них не «\n at »
        // с одним пробелом:
        //  - фреймы .NET — «\r\n   at Namespace.Type.Method(...)», ТРИ пробела после перевода строки;
        //  - фреймы JavaScript — «\n    at fn (file.js:12:3)»;
        //  - Python-traceback — «Traceback (most recent call last):» и «File "x.py", line 3»;
        //  - сама строка исключения — «ValueError: …», «TypeError: Failed to fetch»,
        //    «System.Net.Http.HttpRequestException: …».
        // Любой из этих признаков означает, что текст написан для инженера, а не для игрока.
        private static readonly Regex StackFramePattern = new(
            @"(?:^|[\r\n])[ \t]*at[ \t]+\S",
            RegexOptions.CultureInvariant);

        private static readonly Regex PythonTracebackPattern = new(
            @"Traceback \(most recent call last\)|File ""[^""]*"", line \d+",
            RegexOptions.CultureInvariant);

        private static readonly Regex ExceptionLinePattern = new(
            @"(?:^|[\s(\[])[A-Za-z_][\w.]*(?:Exception|Error)\s*:",
            RegexOptions.CultureInvariant);

        /// <summary>Одно читаемое предложение для пузыря чата. Никогда не возвращает null или пустоту.</summary>
        public static string ToUserMessage(Exception exception, string fallback = null)
        {
            if (exception is LlmClientException llmException)
            {
                return ToUserMessage(llmException, fallback);
            }

            if (exception is LlmOperationTimeoutException)
            {
                return ForErrorCode(LlmErrorCode.Timeout);
            }

            if (exception is OperationCanceledException)
            {
                return ForErrorCode(LlmErrorCode.Cancelled);
            }

            return Coalesce(fallback, DefaultUserMessage);
        }

        /// <summary>Одно читаемое предложение для пузыря чата из типизированного сбоя LLM.</summary>
        public static string ToUserMessage(LlmClientException exception, string fallback = null)
        {
            if (exception == null)
            {
                return Coalesce(fallback, DefaultUserMessage);
            }

            // ПОЧЕМУ: тело 401 может вернуть отправленный ключ/токен обратно (провайдеры так делают),
            // поэтому его текст в ленту не попадает — игрок получает фразу «войдите заново».
            // То же правило, что и редактирование в HTTP-адаптерах; см. MeaiOpenAiChatClient.BuildHttpException.
            if (IsAuthFailure(exception))
            {
                return Coalesce(fallback, ForErrorCode(LlmErrorCode.AuthExpired));
            }

            // Победить встроенную фразу может только предложение, которое наш бэкенд написал для игрока.
            string authored = ExtractBackendAuthoredMessage(exception.ProviderErrorBody);
            if (IsPresentableToPlayer(authored))
            {
                return authored.Trim();
            }

            return Coalesce(fallback, ForErrorCode(exception.ErrorCode, exception.RetryAfterSeconds));
        }

        /// <summary>Встроенная фраза для категории сбоя; используется, когда никто не написал лучше.</summary>
        public static string ForErrorCode(LlmErrorCode errorCode, int? retryAfterSeconds = null)
        {
            string retryHint = retryAfterSeconds.HasValue && retryAfterSeconds.Value > 0
                ? $" Try again in {retryAfterSeconds.Value} s."
                : "";

            switch (errorCode)
            {
                case LlmErrorCode.Timeout:
                    return "The assistant took too long to answer. Please try again.";
                case LlmErrorCode.Cancelled:
                    return "The request was stopped.";
                case LlmErrorCode.EmptyResponse:
                    return "The assistant returned an empty answer. Please try again.";
                case LlmErrorCode.AuthExpired:
                    return "The session has expired. Please sign in again.";
                case LlmErrorCode.QuotaExceeded:
                    return "The assistant quota for this account is used up.";
                case LlmErrorCode.ClientLimitExceeded:
                    return "This session has reached its local assistant limit: too many requests, or a message that is too long.";
                case LlmErrorCode.PaymentRequired:
                    return "The assistant account has run out of credit. Please tell the maintainer.";
                case LlmErrorCode.PermanentProviderError:
                    return "The assistant provider refused this request. Please tell the maintainer.";
                case LlmErrorCode.RateLimited:
                    return "Too many requests to the assistant right now." + retryHint;
                case LlmErrorCode.BackendUnavailable:
                    return "The assistant is unavailable right now. Please try again in a moment.";
                case LlmErrorCode.InvalidRequest:
                    return "The assistant could not process this request.";
                case LlmErrorCode.RoutingError:
                    return "No assistant backend is configured. Please tell the maintainer.";
                case LlmErrorCode.ContextLengthExceeded:
                    return "The conversation got too long for the model. Start a new chat or shorten the message.";
                default:
                    return DefaultUserMessage;
            }
        }

        /// <summary>
        /// Всё, что стоит записать в лог: категория, HTTP-статус, подсказка о повторе и сырое тело
        /// ответа провайдера. Логировать ВМЕСТЕ с самим исключением (у него трасса стека).
        /// </summary>
        public static string ToDiagnosticText(Exception exception)
        {
            if (!(exception is LlmClientException llmException))
            {
                return exception == null ? "" : exception.ToString();
            }

            string status = llmException.HttpStatus.HasValue ? $" http={llmException.HttpStatus.Value}" : "";
            string retry = llmException.RetryAfterSeconds.HasValue
                ? $" retryAfter={llmException.RetryAfterSeconds.Value}s"
                : "";
            // То же правило редактирования, что в HTTP-адаптерах: тело auth-ошибки может содержать
            // только что отправленный ключ, а логи уезжают дальше процесса.
            string body;
            if (string.IsNullOrWhiteSpace(llmException.ProviderErrorBody))
            {
                body = "";
            }
            else if (IsAuthFailure(llmException))
            {
                body = " body=[redacted auth error body]";
            }
            else
            {
                body = $" body={llmException.ProviderErrorBody}";
            }

            string message = IsAuthFailure(llmException) ? "[redacted auth error message]" : llmException.Message;
            return $"code={llmException.ErrorCode}{status}{retry} message={message}{body}";
        }

        /// <summary>
        /// Предложение, которое наш бэкенд написал для игрока, либо "" — когда тело не является
        /// каноническим конвертом бэкенда. Конверт узнаётся по строковым полям верхнего уровня
        /// <c>error_code</c>, <c>request_id</c> и <c>message</c> (OpenAI-подобное зеркало
        /// <c>error.message</c> тоже принимается как носитель текста). Голое
        /// <c>{"error":{"message":…}}</c> возвращает любой провайдер и об авторстве не говорит ничего —
        /// для него результат "".
        /// </summary>
        public static string ExtractBackendAuthoredMessage(string providerErrorBody)
        {
            if (string.IsNullOrWhiteSpace(providerErrorBody))
            {
                return "";
            }

            try
            {
                JObject parsed = JObject.Parse(providerErrorBody);
                if (!IsNonEmptyString(parsed["error_code"]) || !IsNonEmptyString(parsed["request_id"]))
                {
                    return "";
                }

                JToken message = parsed["message"];
                if (!IsNonEmptyString(message))
                {
                    message = parsed["error"]?["message"];
                }

                return IsNonEmptyString(message) ? message.ToString().Trim() : "";
            }
            catch (Exception)
            {
                // Не JSON (HTML-страница ошибки, текст прокси, обрезанное тело): для игрока никто
                // ничего не писал, а сбой разбора не должен всплыть второй ошибкой.
                return "";
            }
        }

        /// <summary>
        /// Снимает префикс <c>HTTP error 403: </c>, который HTTP-адаптеры ставят перед текстом
        /// провайдера. Диагностический помощник: остаток — текст провайдера, сам по себе он не для игрока.
        /// </summary>
        public static string StripHttpErrorPrefix(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "";
            }

            const string marker = "HTTP error ";
            if (!message.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                return message.Trim();
            }

            int separator = message.IndexOf(':', marker.Length);
            return separator < 0 || separator + 1 >= message.Length
                ? message.Trim()
                : message.Substring(separator + 1).Trim();
        }

        /// <summary>
        /// Можно ли это показать игроку? JSON-дампы, трассы стека, traceback'и, строки исключений и
        /// тела размером с повесть — диагностика, а не текст UI; для них берётся встроенная фраза.
        /// Публичный, чтобы хосты с собственным рендером ошибок применяли тот же фильтр.
        /// </summary>
        public static bool IsPresentableToPlayer(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || message.Length > MaxUserMessageLength)
            {
                return false;
            }

            string trimmed = message.Trim();
            char first = trimmed[0];
            if (first == '{' || first == '[' || first == '<')
            {
                return false;
            }

            return !StackFramePattern.IsMatch(trimmed)
                   && !PythonTracebackPattern.IsMatch(trimmed)
                   && !ExceptionLinePattern.IsMatch(trimmed);
        }

        /// <summary>
        /// Сбой класса 401: его тело везде считается носителем секрета. Достаточно ЛЮБОГО из двух
        /// признаков — адаптер может классифицировать 401 как <see cref="LlmErrorCode.AuthExpired"/>
        /// без статуса или передать статус под другим кодом.
        /// </summary>
        private static bool IsAuthFailure(LlmClientException exception)
        {
            return exception.HttpStatus == 401 || exception.ErrorCode == LlmErrorCode.AuthExpired;
        }

        private static bool IsNonEmptyString(JToken token)
        {
            return token != null && token.Type == JTokenType.String &&
                   !string.IsNullOrWhiteSpace(token.ToString());
        }

        private static string Coalesce(string preferred, string fallback)
        {
            return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
        }
    }
}
