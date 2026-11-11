using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// <see cref="LlmConnectionCheck"/>: what a player reads after Test connection, for each way a server can answer
    /// or not answer, each with its twin; and <see cref="LlmRememberedBackend"/>, the remembered server that never
    /// holds a key. Engine-free: the HTTP is a scripted port.
    /// </summary>
    public sealed class LlmConnectionCheckEditModeTests
    {
        private const string Base = "http://127.0.0.1:1234/v1";

        private sealed class ScriptedHttp : ILlmConnectionHttp
        {
            private readonly Dictionary<string, Queue<Func<LlmConnectionHttpRequest, LlmConnectionHttpResponse>>> _routes =
                new(StringComparer.Ordinal);

            public readonly List<LlmConnectionHttpRequest> Sent = new();

            public ScriptedHttp On(string route, Func<LlmConnectionHttpRequest, LlmConnectionHttpResponse> answer)
            {
                if (!_routes.TryGetValue(route, out Queue<Func<LlmConnectionHttpRequest, LlmConnectionHttpResponse>> queue))
                {
                    queue = new Queue<Func<LlmConnectionHttpRequest, LlmConnectionHttpResponse>>();
                    _routes[route] = queue;
                }

                queue.Enqueue(answer);
                return this;
            }

            public ScriptedHttp On(string route, int status, string body)
            {
                return On(route, _ => new LlmConnectionHttpResponse { StatusCode = status, Body = body });
            }

            public Task<LlmConnectionHttpResponse> SendAsync(LlmConnectionHttpRequest request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Sent.Add(request);
                string route = request.Url.EndsWith("/models", StringComparison.Ordinal) ? "models" : "chat";
                if (!_routes.TryGetValue(route, out Queue<Func<LlmConnectionHttpRequest, LlmConnectionHttpResponse>> queue) ||
                    queue.Count == 0)
                {
                    return Task.FromResult(new LlmConnectionHttpResponse { StatusCode = 404, Body = "" });
                }

                Func<LlmConnectionHttpRequest, LlmConnectionHttpResponse> answer =
                    queue.Count > 1 ? queue.Dequeue() : queue.Peek();
                return Task.FromResult(answer(request));
            }

            public int Count(string route)
            {
                int count = 0;
                foreach (LlmConnectionHttpRequest request in Sent)
                {
                    bool models = request.Url.EndsWith("/models", StringComparison.Ordinal);
                    count += (route == "models") == models ? 1 : 0;
                }

                return count;
            }
        }

        private static string Models(params string[] ids)
        {
            List<string> items = new();
            foreach (string id in ids)
            {
                items.Add("{\"id\":\"" + id + "\"}");
            }

            return "{\"object\":\"list\",\"data\":[" + string.Join(",", items) + "]}";
        }

        private static string Completion(string model)
        {
            return "{\"id\":\"x\",\"object\":\"chat.completion\",\"model\":\"" + model + "\",\"choices\":[{\"index\":0,"
                   + "\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";
        }

        private static LlmConnectionCheckResult Run(ScriptedHttp http, string baseUrl = Base, string model = "m",
            string key = "", bool browser = false, string page = "")
        {
            return LlmConnectionCheck.RunAsync(new LlmConnectionCheckRequest
            {
                BaseUrl = baseUrl,
                Model = model,
                ApiKey = key,
                InBrowser = browser,
                PageUrl = page
            }, http).GetAwaiter().GetResult();
        }

        [Test]
        public void AServerThatAnswers_IsConnected_AndNamesTheModel()
        {
            ScriptedHttp http = new ScriptedHttp().On("models", 200, Models("m")).On("chat", 200, Completion("m"));

            LlmConnectionCheckResult result = Run(http);

            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual(LlmConnectionProblem.None, result.Problem);
            StringAssert.StartsWith("Connected: 'm' answered in", result.Message);
            Assert.AreEqual("m", result.Model);
            Assert.AreEqual(Base, result.BaseUrl);
            Assert.AreEqual(1, http.Count("chat"), "one test completion");
            StringAssert.Contains("\"model\":\"m\"", http.Sent[1].JsonBody);
        }

        [Test]
        public void AServerThatFailsTheCompletion_IsNotConnected()
        {
            ScriptedHttp http = new ScriptedHttp().On("models", 200, Models("m"))
                .On("chat", 500, "{\"error\":{\"message\":\"out of memory\"}}");

            LlmConnectionCheckResult result = Run(http);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmConnectionProblem.ServerError, result.Problem);
            StringAssert.Contains("HTTP 500", result.Message);
            StringAssert.Contains("out of memory", result.Message);
        }

        [TestCase("", LlmConnectionProblem.MissingAddress)]
        [TestCase("   ", LlmConnectionProblem.MissingAddress)]
        [TestCase("localhost:1234/v1", LlmConnectionProblem.InvalidAddress)]
        [TestCase("ftp://host/v1", LlmConnectionProblem.InvalidAddress)]
        [TestCase("http://user:pw@host/v1", LlmConnectionProblem.InvalidAddress)]
        [TestCase("http://host/v1?key=1", LlmConnectionProblem.InvalidAddress)]
        public void ABadAddress_IsRefusedBeforeAnyRequest(string address, LlmConnectionProblem expected)
        {
            ScriptedHttp http = new();

            LlmConnectionCheckResult result = Run(http, address);

            Assert.AreEqual(expected, result.Problem);
            Assert.IsNotEmpty(result.Message);
            Assert.AreEqual(0, http.Sent.Count, "nothing is sent to an address that is not one");
        }

        [Test]
        public void APastedRoute_IsStrippedFromTheAddress()
        {
            Assert.AreEqual(Base, LlmConnectionCheck.NormalizeBaseUrl(" " + Base + "/chat/completions/ "));
            Assert.AreEqual(Base, LlmConnectionCheck.NormalizeBaseUrl(Base + "/models"));
            Assert.AreEqual(Base, LlmConnectionCheck.NormalizeBaseUrl(Base + "/"));
            Assert.AreEqual(Base + "/models", LlmConnectionCheck.RouteUrl(Base + "/", "/models"));
        }

        [Test]
        public void NothingAnswering_OnADesktop_SaysNoServerRunsThere()
        {
            ScriptedHttp http = new ScriptedHttp().On("models",
                _ => new LlmConnectionHttpResponse { TransportError = "Cannot connect to destination host" });

            LlmConnectionCheckResult result = Run(http);

            Assert.AreEqual(LlmConnectionProblem.Unreachable, result.Problem);
            StringAssert.Contains("Nothing answered at " + Base, result.Message);
            StringAssert.Contains("refused", result.Message);
            StringAssert.DoesNotContain("CORS", result.Message, "no browser rule on a desktop");
            Assert.AreEqual(0, http.Count("chat"));
        }

        [Test]
        public void NothingAnswering_InABrowser_NamesCorsAndTheProxy()
        {
            ScriptedHttp http = new ScriptedHttp().On("models",
                _ => new LlmConnectionHttpResponse { TransportError = "Unknown Error" });

            LlmConnectionCheckResult result = Run(http, browser: true, page: "http://localhost:8000/index.html");

            Assert.AreEqual(LlmConnectionProblem.BrowserBlockedOrUnreachable, result.Problem);
            StringAssert.Contains("CORS", result.Message);
            StringAssert.Contains("tools/G11Proxy", result.Message);
            StringAssert.Contains("--upstream " + Base, result.Message);
        }

        [Test]
        public void AnUnknownHost_IsNamed()
        {
            ScriptedHttp http = new ScriptedHttp().On("models",
                _ => new LlmConnectionHttpResponse { TransportError = "Cannot resolve destination host" });

            LlmConnectionCheckResult result = Run(http, "http://no-such-box:8080/v1");

            Assert.AreEqual(LlmConnectionProblem.HostNotFound, result.Problem);
            StringAssert.Contains("no-such-box", result.Message);
        }

        [Test]
        public void ACompletionThatTimesOut_SaysToTryAgain_AndTheModelListThatTimesOut_SaysNothingAnswered()
        {
            ScriptedHttp slowAnswer = new ScriptedHttp().On("models", 200, Models("m"))
                .On("chat", _ => new LlmConnectionHttpResponse { TimedOut = true, TransportError = "Request timeout" });
            ScriptedHttp slowList = new ScriptedHttp().On("models",
                _ => new LlmConnectionHttpResponse { TimedOut = true });

            LlmConnectionCheckResult answer = Run(slowAnswer);
            LlmConnectionCheckResult list = Run(slowList);

            Assert.AreEqual(LlmConnectionProblem.Timeout, answer.Problem);
            StringAssert.Contains("45 seconds", answer.Message);
            StringAssert.Contains("try again", answer.Message);
            Assert.AreEqual(LlmConnectionProblem.Timeout, list.Problem);
            StringAssert.Contains("Nothing answered at " + Base + " within 10 seconds", list.Message);
        }

        [Test]
        public void A401_WithoutAKey_AsksForOne_AndWithAKey_SaysItWasRefused()
        {
            ScriptedHttp noKey = new ScriptedHttp().On("models", 401, "{\"error\":\"unauthorized\"}");
            ScriptedHttp wrongKey = new ScriptedHttp().On("models", 401, "{\"error\":\"unauthorized\"}");

            LlmConnectionCheckResult asked = Run(noKey);
            LlmConnectionCheckResult refused = Run(wrongKey, key: "sk-wrong");

            Assert.AreEqual(LlmConnectionProblem.KeyRequired, asked.Problem);
            StringAssert.Contains("wants an API key", asked.Message);
            Assert.AreEqual(LlmConnectionProblem.KeyRefused, refused.Problem);
            StringAssert.Contains("refused the API key", refused.Message);
            StringAssert.Contains("HTTP 401", refused.Message);
        }

        [Test]
        public void AKey_IsSentOnBothRequests_AndNoneWhenNoneWasEntered()
        {
            ScriptedHttp withKey = new ScriptedHttp().On("models", 200, Models("m")).On("chat", 200, Completion("m"));
            ScriptedHttp withoutKey = new ScriptedHttp().On("models", 200, Models("m")).On("chat", 200, Completion("m"));

            Assert.IsTrue(Run(withKey, key: " sk-1 ").Ok);
            Assert.IsTrue(Run(withoutKey).Ok);

            Assert.AreEqual("sk-1", withKey.Sent[0].ApiKey);
            Assert.AreEqual("sk-1", withKey.Sent[1].ApiKey);
            Assert.AreEqual("", withoutKey.Sent[0].ApiKey);
            Assert.AreEqual("", withoutKey.Sent[1].ApiKey);
        }

        [Test]
        public void AModelTheServerDoesNotHave_IsNamedWithWhatItOffers()
        {
            ScriptedHttp http = new ScriptedHttp().On("models", 200, Models("a", "b"))
                .On("chat", 404, "{\"error\":{\"message\":\"model 'c' not found\"}}");

            LlmConnectionCheckResult result = Run(http, model: "c");

            Assert.AreEqual(LlmConnectionProblem.ModelNotFound, result.Problem);
            StringAssert.Contains("'c'", result.Message);
            StringAssert.Contains("'a', 'b'", result.Message);
            CollectionAssert.AreEqual(new[] { "a", "b" }, result.Models);
        }

        [Test]
        public void AModelMissingFromTheList_StillConnects_WhenTheServerAnswersForIt()
        {
            ScriptedHttp http = new ScriptedHttp().On("models", 200, Models("qwen.gguf"))
                .On("chat", 200, Completion("qwen.gguf"));

            LlmConnectionCheckResult result = Run(http, model: "anything");

            Assert.IsTrue(result.Ok, "a llama.cpp server answers for any name: " + result.Message);
            Assert.AreEqual("anything", result.Model);
            Assert.AreEqual("qwen.gguf", result.AnsweredModel);
        }

        [Test]
        public void NoModelEntered_UsesTheOnlyModel_AndAsksToChooseAmongSeveral()
        {
            ScriptedHttp one = new ScriptedHttp().On("models", 200, Models("only")).On("chat", 200, Completion("only"));
            ScriptedHttp several = new ScriptedHttp().On("models", 200, Models("a", "b", "c"));

            LlmConnectionCheckResult picked = Run(one, model: "");
            LlmConnectionCheckResult choose = Run(several, model: " ");

            Assert.IsTrue(picked.Ok, picked.Message);
            Assert.AreEqual("only", picked.Model);
            Assert.AreEqual(LlmConnectionProblem.ModelMissing, choose.Problem);
            StringAssert.Contains("'a', 'b', 'c'", choose.Message);
            Assert.AreEqual(3, choose.Models.Count);
            Assert.AreEqual(0, several.Count("chat"), "no completion without a model");
        }

        [Test]
        public void AnHttpsPage_MayNotCallAnHttpServerOnAnotherHost_ButMayCallThisComputer()
        {
            ScriptedHttp blocked = new();
            ScriptedHttp local = new ScriptedHttp().On("models", 200, Models("m")).On("chat", 200, Completion("m"));
            ScriptedHttp plainPage = new ScriptedHttp().On("models", 200, Models("m")).On("chat", 200, Completion("m"));

            LlmConnectionCheckResult mixed = Run(blocked, "http://192.168.1.154:8080/v1", browser: true,
                page: "https://games.example.com/blockworld/");
            LlmConnectionCheckResult loopback = Run(local, "http://127.0.0.1:1234/v1", browser: true,
                page: "https://games.example.com/blockworld/");
            LlmConnectionCheckResult http = Run(plainPage, "http://192.168.1.154:8080/v1", browser: true,
                page: "http://localhost:8000/");

            Assert.AreEqual(LlmConnectionProblem.MixedContent, mixed.Problem);
            StringAssert.Contains("mixed content", mixed.Message);
            Assert.AreEqual(0, blocked.Sent.Count);
            Assert.IsTrue(loopback.Ok, loopback.Message);
            Assert.IsTrue(http.Ok, http.Message);
            Assert.IsFalse(LlmConnectionCheck.IsMixedContent("https://a.example/", new Uri("https://b.example/v1")));
        }

        [Test]
        public void AServerWithoutAModelList_StillConnects_ThroughTheCompletion()
        {
            ScriptedHttp http = new ScriptedHttp().On("models", 404, "").On("chat", 200, Completion("m"));

            Assert.IsTrue(Run(http).Ok);
        }

        [Test]
        public void AnAddressWithoutV1_IsNotAnOpenAiApi_AndTheHintSaysV1()
        {
            ScriptedHttp http = new ScriptedHttp().On("models", 404, "Not Found").On("chat", 404, "Not Found");

            LlmConnectionCheckResult result = Run(http, "http://127.0.0.1:1234");

            Assert.AreEqual(LlmConnectionProblem.NotOpenAiCompatible, result.Problem);
            StringAssert.Contains("end in /v1", result.Message);
            StringAssert.Contains("http://127.0.0.1:1234/v1", result.Message);
        }

        [Test]
        public void AnAnswerThatIsNotAChatCompletion_IsNotAnOpenAiApi()
        {
            ScriptedHttp http = new ScriptedHttp().On("models", 200, "<html>hello</html>")
                .On("chat", 200, "<html>hello</html>");

            LlmConnectionCheckResult result = Run(http);

            Assert.AreEqual(LlmConnectionProblem.NotOpenAiCompatible, result.Problem);
        }

        [Test]
        public void AServerThatRefusesMaxTokens_IsAskedAgainWithoutIt()
        {
            ScriptedHttp http = new ScriptedHttp().On("models", 200, Models("m"))
                .On("chat", 400, "{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens'\"}}")
                .On("chat", 200, Completion("m"));

            LlmConnectionCheckResult result = Run(http);

            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual(2, http.Count("chat"));
            StringAssert.Contains("max_tokens", http.Sent[1].JsonBody);
            StringAssert.DoesNotContain("max_tokens", http.Sent[2].JsonBody);
        }

        [Test]
        public void A429_SaysBusy_AndATransportThatThrows_IsNothingAnswering()
        {
            ScriptedHttp busy = new ScriptedHttp().On("models", 429, "");
            ScriptedHttp throwing = new ScriptedHttp().On("models", _ => throw new InvalidOperationException("boom"));

            Assert.AreEqual(LlmConnectionProblem.ServerBusy, Run(busy).Problem);
            LlmConnectionCheckResult thrown = Run(throwing);
            Assert.AreEqual(LlmConnectionProblem.Unreachable, thrown.Problem);
            StringAssert.Contains("boom", thrown.Message);
        }

        [Test]
        public void ACancelledCheck_Throws()
        {
            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();

            Assert.CatchAsync<OperationCanceledException>(() => LlmConnectionCheck.RunAsync(
                new LlmConnectionCheckRequest { BaseUrl = Base, Model = "m" }, new ScriptedHttp(), cancelled.Token));
        }

        [Test]
        public void ListingModels_ReturnsThem_OrSaysWhyNot()
        {
            ScriptedHttp ok = new ScriptedHttp().On("models", 200, Models("a", "b", "a"));
            ScriptedHttp html = new ScriptedHttp().On("models", 200, "<html>502 Bad Gateway</html>");
            ScriptedHttp locked = new ScriptedHttp().On("models", 401, "");

            LlmModelListing listed = LlmConnectionCheck.ListModelsAsync(ok, Base, "", 5, false, "").GetAwaiter().GetResult();
            LlmModelListing odd = LlmConnectionCheck.ListModelsAsync(html, Base, "", 5, false, "").GetAwaiter().GetResult();
            LlmModelListing denied = LlmConnectionCheck.ListModelsAsync(locked, Base, "", 5, false, "").GetAwaiter().GetResult();

            Assert.IsTrue(listed.Ok);
            CollectionAssert.AreEqual(new[] { "a", "b" }, listed.Models);
            Assert.IsFalse(odd.Ok);
            Assert.AreEqual(LlmConnectionProblem.NotOpenAiCompatible, odd.Problem);
            Assert.AreEqual(LlmConnectionProblem.KeyRequired, denied.Problem);
        }

        [Test]
        public void NamingManyModels_StopsAtEight()
        {
            List<string> many = new();
            for (int index = 0; index < 11; index++)
            {
                many.Add("m" + index);
            }

            string named = LlmConnectionCheck.NameModels(many);

            StringAssert.Contains("'m7'", named);
            StringAssert.DoesNotContain("'m8'", named);
            StringAssert.EndsWith("and 3 more", named);
        }

        [Test]
        public void TheRememberedServer_RoundTrips_AndHasNoPlaceForAKey()
        {
            LlmRememberedBackend backend = new()
            {
                Mode = LlmExecutionMode.ClientOwnedApi,
                BaseUrl = Base,
                Model = "m",
                NeedsKey = true
            };

            string json = backend.ToJson();
            Assert.IsTrue(LlmRememberedBackend.TryParse(json, out LlmRememberedBackend back));

            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, back.Mode);
            Assert.AreEqual(Base, back.BaseUrl);
            Assert.AreEqual("m", back.Model);
            Assert.IsTrue(back.NeedsKey);
            StringAssert.DoesNotContain("key\"", json.Replace("needsKey\"", ""), "no key field is written");
        }

        [Test]
        public void ARememberedFileWithAKeyAddedByHand_DropsTheKey()
        {
            string json = "{\"schemaVersion\":1,\"mode\":\"ClientOwnedApi\",\"baseUrl\":\"" + Base
                          + "\",\"model\":\"m\",\"apiKey\":\"sk-secret\"}";

            Assert.IsTrue(LlmRememberedBackend.TryParse(json, out LlmRememberedBackend backend));

            StringAssert.DoesNotContain("sk-secret", backend.ToJson());
            Assert.IsFalse(backend.NeedsKey);
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{\"schemaVersion\":2,\"mode\":\"ClientOwnedApi\",\"baseUrl\":\"http://h/v1\",\"model\":\"m\"}")]
        [TestCase("{\"schemaVersion\":1,\"mode\":\"Offline\",\"baseUrl\":\"http://h/v1\",\"model\":\"m\"}")]
        [TestCase("{\"schemaVersion\":1,\"mode\":\"ClientOwnedApi\",\"baseUrl\":\"http://h/v1\",\"model\":\"\"}")]
        [TestCase("{\"schemaVersion\":1,\"mode\":\"ClientOwnedApi\",\"baseUrl\":\"not an address\",\"model\":\"m\"}")]
        [TestCase("{\"schemaVersion\":1,\"mode\":\"Bogus\",\"baseUrl\":\"http://h/v1\",\"model\":\"m\"}")]
        public void ABrokenOrUnusableRememberedFile_IsNotUsed(string json)
        {
            Assert.IsFalse(LlmRememberedBackend.TryParse(json, out LlmRememberedBackend backend));
            Assert.IsNull(backend);
        }

        [Test]
        public void TheInMemoryStore_SavesLoadsAndForgets()
        {
            InMemoryLlmRememberedBackendStore store = new();
            Assert.IsFalse(store.TryLoad(out _));

            Assert.IsTrue(store.Save(new LlmRememberedBackend { BaseUrl = Base, Model = "m" }));
            Assert.IsTrue(store.TryLoad(out LlmRememberedBackend loaded));
            Assert.AreEqual("m", loaded.Model);

            store.Clear();
            Assert.IsFalse(store.TryLoad(out _));
        }
    }
}
