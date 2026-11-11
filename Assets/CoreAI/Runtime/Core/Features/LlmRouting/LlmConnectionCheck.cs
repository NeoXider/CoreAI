using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>One HTTP request a connection check sends.</summary>
    public sealed class LlmConnectionHttpRequest
    {
        /// <summary>"GET" or "POST".</summary>
        public string Method { get; set; } = "GET";

        /// <summary>Absolute URL of the request.</summary>
        public string Url { get; set; } = "";

        /// <summary>Sent as a Bearer token when not empty.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>JSON body of a POST; null for a GET.</summary>
        public string JsonBody { get; set; }

        /// <summary>Seconds the request may take before it counts as timed out.</summary>
        public int TimeoutSeconds { get; set; } = 10;
    }

    /// <summary>What a request brought back: an HTTP status and body, or no HTTP answer at all.</summary>
    public sealed class LlmConnectionHttpResponse
    {
        /// <summary>HTTP status; 0 when no HTTP answer could be read (network error, refused, blocked by a browser).</summary>
        public int StatusCode { get; set; }

        /// <summary>Response body as text; empty when there is none.</summary>
        public string Body { get; set; } = "";

        /// <summary>The transport's own words when there is no HTTP status; empty otherwise.</summary>
        public string TransportError { get; set; } = "";

        /// <summary>True when the request ran out of its time.</summary>
        public bool TimedOut { get; set; }
    }

    /// <summary>
    /// The HTTP a connection check sends through. The host supplies one that works on its platform: a browser
    /// player has no sockets, so Unity's adapter goes through <c>UnityWebRequest</c>. An implementation returns
    /// HTTP errors as a status, never as an exception; it may throw <see cref="OperationCanceledException"/> when
    /// the caller cancels.
    /// </summary>
    public interface ILlmConnectionHttp
    {
        /// <summary>Sends <paramref name="request"/> and reports what came back.</summary>
        Task<LlmConnectionHttpResponse> SendAsync(LlmConnectionHttpRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Why a connection check failed, as a player would act on it.</summary>
    public enum LlmConnectionProblem
    {
        /// <summary>The check passed.</summary>
        None = 0,

        /// <summary>No server address was entered.</summary>
        MissingAddress = 1,

        /// <summary>The address is not an http:// or https:// web address.</summary>
        InvalidAddress = 2,

        /// <summary>An https page may not call an http:// address (the browser's mixed-content rule).</summary>
        MixedContent = 3,

        /// <summary>Nothing answered: no server there, a wrong port, or the connection was refused.</summary>
        Unreachable = 4,

        /// <summary>The host name could not be found.</summary>
        HostNotFound = 5,

        /// <summary>In a browser: no server there, or it does not allow calls from a web page (CORS).</summary>
        BrowserBlockedOrUnreachable = 6,

        /// <summary>The server did not answer in time.</summary>
        Timeout = 7,

        /// <summary>The server wants an API key and none was entered.</summary>
        KeyRequired = 8,

        /// <summary>The server refused the API key that was entered.</summary>
        KeyRefused = 9,

        /// <summary>No model name was entered and the server offers several.</summary>
        ModelMissing = 10,

        /// <summary>The server does not have the model that was entered.</summary>
        ModelNotFound = 11,

        /// <summary>Something answered, but not as an OpenAI-compatible API.</summary>
        NotOpenAiCompatible = 12,

        /// <summary>The server is busy or the key ran out of requests (HTTP 429).</summary>
        ServerBusy = 13,

        /// <summary>The server failed or refused the request for another reason.</summary>
        ServerError = 14
    }

    /// <summary>What a connection check is asked to test.</summary>
    public sealed class LlmConnectionCheckRequest
    {
        /// <summary>OpenAI-compatible base URL, usually ending in <c>/v1</c>.</summary>
        public string BaseUrl { get; set; } = "";

        /// <summary>Optional API key; a local server usually needs none.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Model name; empty lets the check pick the server's only model.</summary>
        public string Model { get; set; } = "";

        /// <summary>True when the check runs in a web page, where CORS and mixed content apply.</summary>
        public bool InBrowser { get; set; }

        /// <summary>Address of the page the player runs in (only read in a browser).</summary>
        public string PageUrl { get; set; } = "";

        /// <summary>Seconds the model list may take.</summary>
        public int ModelsTimeoutSeconds { get; set; } = 10;

        /// <summary>Seconds the one test completion may take (a model can take that long to load).</summary>
        public int CompletionTimeoutSeconds { get; set; } = 45;
    }

    /// <summary>The outcome of a connection check, with one sentence a player can act on.</summary>
    public sealed class LlmConnectionCheckResult
    {
        /// <summary>True when the server answered a test completion for the model.</summary>
        public bool Ok { get; set; }

        /// <summary>Why it failed; <see cref="LlmConnectionProblem.None"/> when <see cref="Ok"/>.</summary>
        public LlmConnectionProblem Problem { get; set; }

        /// <summary>What happened, in words a player understands.</summary>
        public string Message { get; set; } = "";

        /// <summary>The base URL that was tested, without a trailing slash or a pasted route.</summary>
        public string BaseUrl { get; set; } = "";

        /// <summary>The model to use: the one entered, or the server's only model when none was entered.</summary>
        public string Model { get; set; } = "";

        /// <summary>The model the server said answered; empty when it did not say.</summary>
        public string AnsweredModel { get; set; } = "";

        /// <summary>Models the server listed; empty when it lists none or has no model list.</summary>
        public IReadOnlyList<string> Models { get; set; } = Array.Empty<string>();

        /// <summary>HTTP status of the request that decided the outcome; 0 when nothing answered.</summary>
        public int StatusCode { get; set; }

        /// <summary>Milliseconds the test completion took; 0 when none ran.</summary>
        public double LatencyMs { get; set; }
    }

    /// <summary>Result of listing a server's models.</summary>
    public sealed class LlmModelListing
    {
        /// <summary>True when the server returned a model list with at least one model.</summary>
        public bool Ok { get; set; }

        /// <summary>The model ids in server order, without duplicates.</summary>
        public IReadOnlyList<string> Models { get; set; } = Array.Empty<string>();

        /// <summary>Why the list is empty or missing, in a player's words; empty when <see cref="Ok"/>.</summary>
        public string Message { get; set; } = "";

        /// <summary>Why it failed; <see cref="LlmConnectionProblem.None"/> when <see cref="Ok"/>.</summary>
        public LlmConnectionProblem Problem { get; set; }
    }

    /// <summary>
    /// Tests an OpenAI-compatible server the way a player needs it tested: is there a server at the address, does
    /// it take the key, does it have the model, and does the model answer. It lists the models
    /// (<c>GET {base}/models</c>) and sends one tiny completion (<c>POST {base}/chat/completions</c>), and turns
    /// every failure into one sentence that says what to do: unreachable, a host not found, a browser refusing the
    /// call (CORS) or an https page calling http (mixed content), a missing or refused key, a model the server does
    /// not have, a timeout, or an address that is not an OpenAI-compatible API. Engine-free: the HTTP is a port.
    /// </summary>
    /// <remarks>
    /// WHY the completion decides and not the model list: a llama.cpp server lists only its loaded file but answers
    /// for any model name, so a name missing from the list is reported only when the completion fails too.
    /// </remarks>
    public static class LlmConnectionCheck
    {
        /// <summary>How many model names a message lists before it says how many more there are.</summary>
        public const int MaxModelsNamed = 8;

        /// <summary>The prompt of the test completion.</summary>
        public const string ProbePrompt = "Reply with the single word: ok";

        private const int ProbeMaxTokens = 16;
        private const int MaxBodyExcerpt = 160;

        /// <summary>Runs the check. Never throws for a server problem; throws only when the caller cancels.</summary>
        public static async Task<LlmConnectionCheckResult> RunAsync(
            LlmConnectionCheckRequest request,
            ILlmConnectionHttp http,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (http == null)
            {
                throw new ArgumentNullException(nameof(http));
            }

            string baseUrl = NormalizeBaseUrl(request.BaseUrl);
            string model = (request.Model ?? "").Trim();
            string key = (request.ApiKey ?? "").Trim();
            if (!TryValidateBaseUrl(baseUrl, out Uri target, out LlmConnectionProblem addressProblem,
                    out string addressMessage))
            {
                return Fail(addressProblem, addressMessage, baseUrl, model, 0);
            }

            if (request.InBrowser && IsMixedContent(request.PageUrl, target))
            {
                return Fail(LlmConnectionProblem.MixedContent,
                    "This page is served over https, and the browser does not let it call the http:// address " + baseUrl
                    + " (mixed content). Give the server an https address, or open the game from an http:// page "
                    + "such as http://localhost.", baseUrl, model, 0);
            }

            LlmConnectionHttpResponse listed = await SendQuietlyAsync(http, new LlmConnectionHttpRequest
            {
                Method = "GET",
                Url = RouteUrl(baseUrl, "models"),
                ApiKey = key,
                TimeoutSeconds = Math.Max(1, request.ModelsTimeoutSeconds)
            }, cancellationToken);

            List<string> models = new();
            bool listReadable = false;
            if (listed.StatusCode == 0)
            {
                return FailNoAnswer(listed, request, baseUrl, model, Math.Max(1, request.ModelsTimeoutSeconds), false);
            }

            if (listed.StatusCode is >= 200 and < 300)
            {
                listReadable = TryParseModelIds(listed.Body, out models);
            }
            else if (IsStopStatus(listed.StatusCode))
            {
                return FailStatus(listed, key, baseUrl, model, "models", models);
            }

            if (model.Length == 0)
            {
                if (listReadable && models.Count == 1)
                {
                    model = models[0];
                }
                else
                {
                    LlmConnectionCheckResult missing = Fail(LlmConnectionProblem.ModelMissing,
                        models.Count > 1
                            ? "Choose a model: the server offers " + NameModels(models) + "."
                            : "Enter the name of the model to use.",
                        baseUrl, model, listed.StatusCode);
                    missing.Models = models;
                    return missing;
                }
            }

            Stopwatch watch = Stopwatch.StartNew();
            LlmConnectionHttpResponse answered = await SendCompletionAsync(http, baseUrl, key, model,
                Math.Max(1, request.CompletionTimeoutSeconds), true, cancellationToken);
            if (answered.StatusCode == 400 && Mentions(answered.Body, "max_tokens"))
            {
                // WHY a second try: newer OpenAI models refuse max_tokens; the cap only keeps a local model brief.
                answered = await SendCompletionAsync(http, baseUrl, key, model,
                    Math.Max(1, request.CompletionTimeoutSeconds), false, cancellationToken);
            }

            watch.Stop();
            if (answered.StatusCode == 0)
            {
                LlmConnectionCheckResult silent = FailNoAnswer(answered, request, baseUrl, model,
                    Math.Max(1, request.CompletionTimeoutSeconds), true);
                silent.Models = models;
                return silent;
            }

            if (answered.StatusCode is >= 200 and < 300)
            {
                if (!TryReadCompletion(answered.Body, out string answeredModel))
                {
                    LlmConnectionCheckResult odd = Fail(LlmConnectionProblem.NotOpenAiCompatible,
                        "Something answered at " + RouteUrl(baseUrl, "chat/completions")
                        + ", but not as an OpenAI-compatible chat API." + V1Hint(target), baseUrl, model,
                        answered.StatusCode);
                    odd.Models = models;
                    return odd;
                }

                string shown = answeredModel.Length > 0 ? answeredModel : model;
                return new LlmConnectionCheckResult
                {
                    Ok = true,
                    Problem = LlmConnectionProblem.None,
                    Message = string.Format(CultureInfo.InvariantCulture, "Connected: {0} answered in {1:0} ms.",
                        Quote(shown), watch.Elapsed.TotalMilliseconds),
                    BaseUrl = baseUrl,
                    Model = model,
                    AnsweredModel = answeredModel,
                    Models = models,
                    StatusCode = answered.StatusCode,
                    LatencyMs = watch.Elapsed.TotalMilliseconds
                };
            }

            bool listedButMissing = listReadable && models.Count > 0 && !models.Contains(model);
            LlmConnectionCheckResult failed = FailStatus(answered, key, baseUrl, model, "chat/completions", models);
            if (listedButMissing && failed.Problem is LlmConnectionProblem.ServerError
                    or LlmConnectionProblem.NotOpenAiCompatible)
            {
                failed.Problem = LlmConnectionProblem.ModelNotFound;
                failed.Message = "The server has no model " + Quote(model) + ". It offers " + NameModels(models) + ".";
            }

            failed.Models = models;
            return failed;
        }

        /// <summary>
        /// Lists the models of the server at <paramref name="baseUrl"/>: the same request and the same failure
        /// sentences as <see cref="RunAsync"/>, without the test completion.
        /// </summary>
        public static async Task<LlmModelListing> ListModelsAsync(
            ILlmConnectionHttp http,
            string baseUrl,
            string apiKey,
            int timeoutSeconds,
            bool inBrowser,
            string pageUrl,
            CancellationToken cancellationToken = default)
        {
            if (http == null)
            {
                throw new ArgumentNullException(nameof(http));
            }

            string normalized = NormalizeBaseUrl(baseUrl);
            if (!TryValidateBaseUrl(normalized, out Uri target, out LlmConnectionProblem problem, out string message))
            {
                return new LlmModelListing { Problem = problem, Message = message };
            }

            if (inBrowser && IsMixedContent(pageUrl, target))
            {
                return new LlmModelListing
                {
                    Problem = LlmConnectionProblem.MixedContent,
                    Message = "This page is served over https, and the browser does not let it call the http:// "
                              + "address " + normalized + " (mixed content)."
                };
            }

            int seconds = Math.Max(1, timeoutSeconds);
            string key = (apiKey ?? "").Trim();
            LlmConnectionHttpResponse response = await SendQuietlyAsync(http, new LlmConnectionHttpRequest
            {
                Method = "GET",
                Url = RouteUrl(normalized, "models"),
                ApiKey = key,
                TimeoutSeconds = seconds
            }, cancellationToken);
            if (response.StatusCode == 0)
            {
                LlmConnectionCheckResult silent = FailNoAnswer(response, new LlmConnectionCheckRequest
                {
                    InBrowser = inBrowser,
                    PageUrl = pageUrl ?? ""
                }, normalized, "", seconds, false);
                return new LlmModelListing { Problem = silent.Problem, Message = silent.Message };
            }

            if (response.StatusCode is < 200 or >= 300)
            {
                LlmConnectionCheckResult failed = FailStatus(response, key, normalized, "", "models",
                    Array.Empty<string>());
                return new LlmModelListing { Problem = failed.Problem, Message = failed.Message };
            }

            if (!TryParseModelIds(response.Body, out List<string> models))
            {
                return new LlmModelListing
                {
                    Problem = LlmConnectionProblem.NotOpenAiCompatible,
                    Message = "Something answered at " + RouteUrl(normalized, "models")
                              + ", but not with a model list." + V1Hint(target)
                };
            }

            return models.Count == 0
                ? new LlmModelListing
                {
                    Problem = LlmConnectionProblem.ModelMissing,
                    Message = "The server lists no models. Load a model in it, then try again."
                }
                : new LlmModelListing { Ok = true, Models = models };
        }

        /// <summary>
        /// The base URL as it is stored: trimmed, without a trailing slash, and without a pasted
        /// <c>/models</c> or <c>/chat/completions</c> route.
        /// </summary>
        public static string NormalizeBaseUrl(string baseUrl)
        {
            string value = (baseUrl ?? "").Trim().TrimEnd('/');
            foreach (string route in new[] { "/chat/completions", "/models" })
            {
                if (value.EndsWith(route, StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(0, value.Length - route.Length).TrimEnd('/');
                }
            }

            return value;
        }

        /// <summary>
        /// True when <paramref name="baseUrl"/> is an absolute http or https URL without credentials, a query or a
        /// fragment; otherwise the problem and the sentence that says so.
        /// </summary>
        public static bool TryValidateBaseUrl(string baseUrl, out Uri uri, out LlmConnectionProblem problem,
            out string message)
        {
            uri = null;
            string value = (baseUrl ?? "").Trim();
            if (value.Length == 0)
            {
                problem = LlmConnectionProblem.MissingAddress;
                message = "Enter the server's address first, for example http://127.0.0.1:1234/v1 for LM Studio.";
                return false;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri parsed) ||
                (parsed.Scheme != "http" && parsed.Scheme != "https") ||
                string.IsNullOrEmpty(parsed.Host))
            {
                problem = LlmConnectionProblem.InvalidAddress;
                message = Quote(value) + " is not a web address: it must start with http:// or https://, "
                          + "for example http://127.0.0.1:1234/v1.";
                return false;
            }

            if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) ||
                !string.IsNullOrEmpty(parsed.Fragment))
            {
                problem = LlmConnectionProblem.InvalidAddress;
                message = "Enter the server's base address only, without a user name, '?' or '#', "
                          + "for example http://127.0.0.1:1234/v1. Put a key in the API key field.";
                return false;
            }

            uri = parsed;
            problem = LlmConnectionProblem.None;
            message = "";
            return true;
        }

        /// <summary>
        /// True when a page at <paramref name="pageUrl"/> may not call <paramref name="target"/>: the page is https
        /// and the target is http on a host other than this computer (browsers let a secure page call
        /// <c>http://localhost</c> and <c>http://127.0.0.1</c>).
        /// </summary>
        public static bool IsMixedContent(string pageUrl, Uri target)
        {
            if (target == null || target.Scheme != "http" ||
                !Uri.TryCreate((pageUrl ?? "").Trim(), UriKind.Absolute, out Uri page) || page.Scheme != "https")
            {
                return false;
            }

            return !IsLoopbackHost(target.Host);
        }

        /// <summary><c>{baseUrl}/{route}</c> with exactly one slash between them.</summary>
        public static string RouteUrl(string baseUrl, string route)
        {
            return NormalizeBaseUrl(baseUrl) + "/" + (route ?? "").TrimStart('/');
        }

        /// <summary>
        /// Reads model ids from an OpenAI-compatible <c>/models</c> body: the <c>{"data":[{"id":...}]}</c> envelope
        /// or a bare array of objects or strings, in server order, without duplicates. False when the body is not
        /// such a list (an HTML page, an error object, nothing).
        /// </summary>
        public static bool TryParseModelIds(string json, out List<string> ids)
        {
            ids = new List<string>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                JToken root = JToken.Parse(json);
                JToken array = root is JArray ? root : root is JObject envelope ? envelope["data"] : null;
                if (array is not JArray items)
                {
                    return false;
                }

                HashSet<string> seen = new(StringComparer.Ordinal);
                foreach (JToken item in items)
                {
                    string id = item is JValue ? item.ToString() : item is JObject entry ? (string)entry["id"] ?? "" : "";
                    id = id.Trim();
                    if (id.Length > 0 && seen.Add(id))
                    {
                        ids.Add(id);
                    }
                }

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>"a, b, c" for up to <see cref="MaxModelsNamed"/> names, then how many more there are.</summary>
        public static string NameModels(IReadOnlyList<string> models)
        {
            if (models == null || models.Count == 0)
            {
                return "no models";
            }

            List<string> named = new();
            for (int index = 0; index < models.Count && index < MaxModelsNamed; index++)
            {
                named.Add(Quote(models[index]));
            }

            string list = string.Join(", ", named);
            return models.Count > MaxModelsNamed
                ? list + " and " + (models.Count - MaxModelsNamed).ToString(CultureInfo.InvariantCulture) + " more"
                : list;
        }

        private static async Task<LlmConnectionHttpResponse> SendCompletionAsync(ILlmConnectionHttp http,
            string baseUrl, string key, string model, int timeoutSeconds, bool capTokens,
            CancellationToken cancellationToken)
        {
            JObject body = new()
            {
                ["model"] = model,
                ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = ProbePrompt }),
                ["stream"] = false
            };
            if (capTokens)
            {
                body["max_tokens"] = ProbeMaxTokens;
            }

            return await SendQuietlyAsync(http, new LlmConnectionHttpRequest
            {
                Method = "POST",
                Url = RouteUrl(baseUrl, "chat/completions"),
                ApiKey = key,
                JsonBody = body.ToString(Formatting.None),
                TimeoutSeconds = timeoutSeconds
            }, cancellationToken);
        }

        private static async Task<LlmConnectionHttpResponse> SendQuietlyAsync(ILlmConnectionHttp http,
            LlmConnectionHttpRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                LlmConnectionHttpResponse response = await http.SendAsync(request, cancellationToken);
                return response ?? new LlmConnectionHttpResponse { TransportError = "no response" };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new LlmConnectionHttpResponse { TimedOut = true, TransportError = "timed out" };
            }
            catch (Exception ex)
            {
                // WHY contained: a transport that throws instead of answering is still "nothing answered" to a player.
                return new LlmConnectionHttpResponse { TransportError = ex.Message ?? ex.GetType().Name };
            }
        }

        private static bool IsStopStatus(int status)
        {
            return status is 401 or 403 or 429 || status >= 500;
        }

        private static LlmConnectionCheckResult FailNoAnswer(LlmConnectionHttpResponse response,
            LlmConnectionCheckRequest request, string baseUrl, string model, int seconds, bool completion)
        {
            string error = (response.TransportError ?? "").Trim();
            string lower = error.ToLowerInvariant();
            if (response.TimedOut || lower.Contains("timeout") || lower.Contains("timed out"))
            {
                return Fail(LlmConnectionProblem.Timeout, completion
                        ? "The server took longer than " + seconds.ToString(CultureInfo.InvariantCulture)
                          + " seconds to answer the test. A model can take that long to load the first time: "
                          + "try again in a moment."
                        : "Nothing answered at " + baseUrl + " within " + seconds.ToString(CultureInfo.InvariantCulture)
                          + " seconds. Check that the server is running and the address is right.",
                    baseUrl, model, 0);
            }

            if (request.InBrowser)
            {
                return Fail(LlmConnectionProblem.BrowserBlockedOrUnreachable,
                    "No answer from " + baseUrl + " that this page may read. Either no server runs there (or the "
                    + "address or port is wrong), or the server does not allow calls from a web page: a browser only "
                    + "lets a page call a server that sends CORS headers (LM Studio: turn on Enable CORS in its server "
                    + "settings). A proxy that adds the headers works too, for example tools/G11Proxy in the CoreAI "
                    + "repository: python tools/G11Proxy/g11_proxy.py --port 8811 --upstream " + baseUrl
                    + ", then enter http://127.0.0.1:8811/v1 here.", baseUrl, model, 0);
            }

            if (lower.Contains("resolve") || lower.Contains("no such host") || lower.Contains("name or service"))
            {
                string host = Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri parsed) ? parsed.Host : baseUrl;
                return Fail(LlmConnectionProblem.HostNotFound,
                    "The address " + host + " could not be found. Check how it is spelled.", baseUrl, model, 0);
            }

            return Fail(LlmConnectionProblem.Unreachable,
                "Nothing answered at " + baseUrl + ": no server runs there, or the address or port is wrong, or the "
                + "connection was refused" + (error.Length > 0 ? " (" + error + ")" : "") + ".", baseUrl, model, 0);
        }

        private static LlmConnectionCheckResult FailStatus(LlmConnectionHttpResponse response, string key,
            string baseUrl, string model, string route, IReadOnlyList<string> models)
        {
            int status = response.StatusCode;
            string excerpt = ErrorExcerpt(response.Body);
            string code = " (HTTP " + status.ToString(CultureInfo.InvariantCulture) + ")";
            if (status is 401 or 403)
            {
                return key.Length == 0
                    ? Fail(LlmConnectionProblem.KeyRequired,
                        "The server wants an API key" + code + ". Enter it in API key.", baseUrl, model, status)
                    : Fail(LlmConnectionProblem.KeyRefused,
                        "The server refused the API key" + code + ". Check the key.", baseUrl, model, status);
            }

            if (status == 429)
            {
                return Fail(LlmConnectionProblem.ServerBusy,
                    "The server is busy, or this key has run out of requests" + code + ". Try again later.", baseUrl,
                    model, status);
            }

            if (status >= 500)
            {
                return Fail(LlmConnectionProblem.ServerError,
                    "The server failed" + code + (excerpt.Length > 0 ? ": " + excerpt : "."), baseUrl, model, status);
            }

            Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri target);
            bool aboutModel = model.Length > 0 &&
                              (Mentions(response.Body, "model") || (model.Length >= 3 && Mentions(response.Body, model)));
            if (status == 404 && !aboutModel)
            {
                return Fail(LlmConnectionProblem.NotOpenAiCompatible,
                    "No OpenAI-compatible API answered at " + RouteUrl(baseUrl, route) + code + "." + V1Hint(target),
                    baseUrl, model, status);
            }

            if ((status is 400 or 404 or 422) && aboutModel)
            {
                return Fail(LlmConnectionProblem.ModelNotFound,
                    "The server refused the model " + Quote(model) + code
                    + (excerpt.Length > 0 ? ": " + excerpt : ".")
                    + (models.Count > 0 ? " It offers " + NameModels(models) + "." : ""), baseUrl, model, status);
            }

            return Fail(LlmConnectionProblem.ServerError,
                "The server refused the test request" + code + (excerpt.Length > 0 ? ": " + excerpt : "."), baseUrl,
                model, status);
        }

        private static bool TryReadCompletion(string body, out string answeredModel)
        {
            answeredModel = "";
            if (string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            try
            {
                if (JToken.Parse(body) is not JObject root || root["choices"] is not JArray)
                {
                    return false;
                }

                answeredModel = ((string)root["model"] ?? "").Trim();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string ErrorExcerpt(string body)
        {
            string text = (body ?? "").Trim();
            if (text.Length == 0)
            {
                return "";
            }

            try
            {
                JToken root = JToken.Parse(text);
                JToken error = root is JObject obj ? obj["error"] ?? obj["message"] ?? obj["detail"] : null;
                if (error is JObject nested)
                {
                    error = nested["message"] ?? nested;
                }

                if (error is JValue value && value.Value != null)
                {
                    text = value.Value.ToString();
                }
            }
            catch (JsonException)
            {
            }

            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= MaxBodyExcerpt ? text : text.Substring(0, MaxBodyExcerpt) + "...";
        }

        private static string V1Hint(Uri target)
        {
            if (target == null)
            {
                return "";
            }

            string path = target.AbsolutePath.TrimEnd('/');
            return path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? " Check the address and that the server is an OpenAI-compatible one."
                : " Most servers want the address to end in /v1, for example " + target.Scheme + "://"
                  + target.Authority + "/v1.";
        }

        private static bool Mentions(string body, string word)
        {
            return !string.IsNullOrEmpty(body) && !string.IsNullOrEmpty(word) &&
                   body.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsLoopbackHost(string host)
        {
            string value = (host ?? "").Trim('[', ']').ToLowerInvariant();
            return value == "localhost" || value == "::1" || value.EndsWith(".localhost", StringComparison.Ordinal) ||
                   value.StartsWith("127.", StringComparison.Ordinal);
        }

        private static string Quote(string value)
        {
            return "'" + (value ?? "") + "'";
        }

        private static LlmConnectionCheckResult Fail(LlmConnectionProblem problem, string message, string baseUrl,
            string model, int status)
        {
            return new LlmConnectionCheckResult
            {
                Ok = false,
                Problem = problem,
                Message = message,
                BaseUrl = baseUrl ?? "",
                Model = model ?? "",
                StatusCode = status
            };
        }
    }
}
