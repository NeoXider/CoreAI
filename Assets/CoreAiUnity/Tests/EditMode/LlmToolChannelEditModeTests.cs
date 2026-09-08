using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
#if COREAI_LLM
    /// <summary>Endpoint capability selection, cancellation and diagnostics through public behavior.</summary>
    public sealed class LlmToolChannelEditModeTests
    {
        private const string JinjaRejectionBody =
            "{\"error\":{\"code\":500,\"message\":\"tools param requires --jinja flag\",\"type\":\"server_error\"}}";

        private const string AcceptedBody =
            "{\"choices\":[{\"finish_reason\":\"length\",\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"I\"}}]," +
            "\"object\":\"chat.completion\"}";

        private sealed class ScriptedToolChannelProbe : ILlmToolChannelProbe
        {
            public LlmToolChannelProbeResult Result { get; set; }
            public Exception Throws { get; set; }
            public Action OnProbe { get; set; }
            public int Calls { get; private set; }
            public LlmToolChannelProbeRequest LastRequest { get; private set; }

            public Task<LlmToolChannelProbeResult> ProbeAsync(
                LlmToolChannelProbeRequest request,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                OnProbe?.Invoke();
                LastRequest = request;
                if (Throws != null)
                {
                    throw Throws;
                }

                return Task.FromResult(Result);
            }
        }

        private sealed class ReadyReadinessProbe : ILlmEndpointReadinessProbe
        {
            public Task<LlmEndpointReadinessResult> Response { get; set; }
            public Action OnProbe { get; set; }
            public Task<LlmEndpointReadinessResult> ProbeAsync(
                LlmEndpointReadinessRequest request,
                CancellationToken cancellationToken = default)
            {
                OnProbe?.Invoke();
                return Response ?? Task.FromResult(new LlmEndpointReadinessResult { IsReady = true, StatusCode = 200 });
            }
        }

        private static LlmToolChannelProbeResult Outcome(LlmToolChannelProbeOutcome outcome, string detail = "d")
        {
            return new LlmToolChannelProbeResult { Outcome = outcome, Detail = detail };
        }

        // ---------------------------------------------------------------- проба: чтение ответа сервера

        [TestCase(500)]
        [TestCase(400)]
        public void Classify_JinjaRejection_IsRejectedByMessageNotByStatus(int status)
        {
            // Живой llama.cpp без --jinja отвечает HTTP 500 (server_error); другие сборки — 400.
            LlmToolChannelProbeResult result = LlmToolChannelProbePolicy.Classify(status, JinjaRejectionBody, "");

            Assert.AreEqual(LlmToolChannelProbeOutcome.Rejected, result.Outcome);
            Assert.AreEqual(status, result.StatusCode);
            StringAssert.Contains("requires --jinja", result.Detail);
        }

        [Test]
        public void Classify_ServerAcceptsTools_IsAccepted()
        {
            LlmToolChannelProbeResult result = LlmToolChannelProbePolicy.Classify(200, AcceptedBody, "");

            Assert.AreEqual(LlmToolChannelProbeOutcome.Accepted, result.Outcome);
            Assert.AreEqual(200, result.StatusCode);
        }

        [Test]
        public void Classify_TransportFailure_IsInconclusive()
        {
            LlmToolChannelProbeResult result = LlmToolChannelProbePolicy.Classify(0, "", "Cannot connect to destination host");

            Assert.AreEqual(LlmToolChannelProbeOutcome.Inconclusive, result.Outcome);
            StringAssert.Contains("Cannot connect", result.Detail);
        }

        [Test]
        public void Classify_UnrelatedServerError_IsInconclusive()
        {
            LlmToolChannelProbeResult json = LlmToolChannelProbePolicy.Classify(
                503, "{\"error\":{\"message\":\"Loading model\"}}", "");
            LlmToolChannelProbeResult html = LlmToolChannelProbePolicy.Classify(
                502, "<html><body>Bad gateway</body></html>", "");

            Assert.AreEqual(LlmToolChannelProbeOutcome.Inconclusive, json.Outcome);
            StringAssert.Contains("Loading model", json.Detail);
            Assert.AreEqual(LlmToolChannelProbeOutcome.Inconclusive, html.Outcome);
            StringAssert.Contains("Bad gateway", html.Detail);
        }

        [Test]
        public void BuildRequestBody_DeclaresOneToolAndAsksForOneToken()
        {
            JObject body = JObject.Parse(LlmToolChannelProbePolicy.BuildRequestBody("qwen"));

            Assert.AreEqual("qwen", body["model"].Value<string>());
            Assert.AreEqual(1, body["max_tokens"].Value<int>());
            Assert.IsFalse(body["stream"].Value<bool>());
            Assert.AreEqual(1, ((JArray)body["messages"]).Count);
            Assert.AreEqual(1, ((JArray)body["tools"]).Count);
            Assert.AreEqual(
                LlmToolChannelProbePolicy.ProbeToolName,
                body["tools"][0]["function"]["name"].Value<string>());
        }

        // ---------------------------------------------------------------- решение: настройка > проба

        [Test]
        public void Resolve_ExplicitSettingWinsOverProbe()
        {
            LlmToolChannelDecision native = LlmToolChannelResolution.Resolve(
                LlmToolChannel.Native, Outcome(LlmToolChannelProbeOutcome.Rejected));
            LlmToolChannelDecision text = LlmToolChannelResolution.Resolve(
                LlmToolChannel.Text, Outcome(LlmToolChannelProbeOutcome.Accepted));

            Assert.IsTrue(native.Native);
            Assert.AreEqual(LlmToolChannelDecisionSource.Declared, native.Source);
            Assert.IsFalse(text.Native);
            Assert.AreEqual(LlmToolChannelDecisionSource.Declared, text.Source);
        }

        [Test]
        public void Resolve_Auto_FollowsProbeAndFallsBackToTextConservatively()
        {
            LlmToolChannelDecision accepted = LlmToolChannelResolution.Resolve(
                LlmToolChannel.Auto, Outcome(LlmToolChannelProbeOutcome.Accepted, "HTTP 200"));
            LlmToolChannelDecision rejected = LlmToolChannelResolution.Resolve(
                LlmToolChannel.Auto, Outcome(LlmToolChannelProbeOutcome.Rejected, "HTTP 500: tools param requires --jinja flag"));
            LlmToolChannelDecision inconclusive = LlmToolChannelResolution.Resolve(
                LlmToolChannel.Auto, Outcome(LlmToolChannelProbeOutcome.Inconclusive, "transport: timeout"));
            LlmToolChannelDecision missing = LlmToolChannelResolution.Resolve(LlmToolChannel.Auto, null);

            Assert.IsTrue(accepted.Native);
            Assert.AreEqual(LlmToolChannelDecisionSource.Probe, accepted.Source);
            Assert.IsFalse(rejected.Native);
            Assert.AreEqual(LlmToolChannelDecisionSource.Probe, rejected.Source);
            StringAssert.Contains("--jinja", rejected.Reason);
            Assert.IsFalse(inconclusive.Native);
            Assert.AreEqual(LlmToolChannelDecisionSource.ProbeInconclusive, inconclusive.Source);
            StringAssert.Contains("timeout", inconclusive.Reason);
            Assert.IsFalse(missing.Native);
            Assert.AreEqual(LlmToolChannelDecisionSource.ProbeInconclusive, missing.Source);
        }

        [Test]
        public void ResolveWithoutProbe_AutoIsNativeWithTheGivenReason()
        {
            LlmToolChannelDecision auto = LlmToolChannelResolution.ResolveWithoutProbe(
                LlmToolChannel.Auto, LlmToolChannelResolution.BundledLlamaLibReason);
            LlmToolChannelDecision text = LlmToolChannelResolution.ResolveWithoutProbe(
                LlmToolChannel.Text, LlmToolChannelResolution.BundledLlamaLibReason);

            Assert.IsTrue(auto.Native);
            Assert.AreEqual(LlmToolChannelDecisionSource.Known, auto.Source);
            Assert.AreEqual(LlmToolChannelResolution.BundledLlamaLibReason, auto.Reason);
            Assert.IsFalse(text.Native);
            Assert.AreEqual(LlmToolChannelDecisionSource.Declared, text.Source);
            Assert.IsTrue(LlmToolChannelResolution.RequiresProbe(LlmToolChannel.Auto));
            Assert.IsFalse(LlmToolChannelResolution.RequiresProbe(LlmToolChannel.Native));
            Assert.IsFalse(LlmToolChannelResolution.RequiresProbe(LlmToolChannel.Text));
        }

        // ---------------------------------------------------------------- фабрика: локальный эндпойнт

        [Test]
        public async Task LocalEndpoint_ServerWithNativeChannel_DoesNotFallToProseParsing()
        {
            // Дефект: локальный llama.cpp с jinja получал `false` и разбор прозы. Проба говорит «принял» —
            // канал нативный, и запрос ушёл на тот адрес и с той моделью, что у эндпойнта.
            ScriptedToolChannelProbe probe = new() { Result = Outcome(LlmToolChannelProbeOutcome.Accepted, "HTTP 200") };

            LlmToolChannelDecision decision = await LlmEndpointClientFactory.DecideToolChannelAsync(
                LlmToolChannel.Auto,
                probe,
                new LlmToolChannelProbeRequest { BaseUrl = "http://localhost:13333/v1", Model = "qwen.gguf" },
                CancellationToken.None);

            Assert.IsTrue(decision.Native);
            Assert.AreEqual(1, probe.Calls);
            Assert.AreEqual("http://localhost:13333/v1", probe.LastRequest.BaseUrl);
            Assert.AreEqual("qwen.gguf", probe.LastRequest.Model);
        }

        [Test]
        public async Task LocalEndpoint_ServerWithoutJinja_UsesTextChannelAndSaysWhy()
        {
            ScriptedToolChannelProbe probe = new()
            {
                Result = LlmToolChannelProbePolicy.Classify(500, JinjaRejectionBody, "")
            };

            LlmToolChannelDecision decision = await LlmEndpointClientFactory.DecideToolChannelAsync(
                LlmToolChannel.Auto, probe, new LlmToolChannelProbeRequest(), CancellationToken.None);

            Assert.IsFalse(decision.Native);
            Assert.AreEqual(LlmToolChannelDecisionSource.Probe, decision.Source);
            StringAssert.Contains("requires --jinja", decision.Reason);
        }

        [Test]
        public async Task LocalEndpoint_ProbeFailure_DoesNotBreakActivation()
        {
            ScriptedToolChannelProbe probe = new() { Throws = new InvalidOperationException("socket closed") };

            LlmToolChannelDecision decision = await LlmEndpointClientFactory.DecideToolChannelAsync(
                LlmToolChannel.Auto, probe, new LlmToolChannelProbeRequest(), CancellationToken.None);

            Assert.IsFalse(decision.Native, "A failed probe must fall back to the channel that works on any server.");
            Assert.AreEqual(LlmToolChannelDecisionSource.ProbeInconclusive, decision.Source);
            StringAssert.Contains("socket closed", decision.Reason);
        }

        [TestCase(LlmToolChannel.Auto)]
        [TestCase(LlmToolChannel.Native)]
        [TestCase(LlmToolChannel.Text)]
        public async Task Endpoint_PreCancelled_DoesNotProbeOrReturnAnActivation(LlmToolChannel setting)
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            ScriptedToolChannelProbe probe = new() { Result = Outcome(LlmToolChannelProbeOutcome.Accepted) };
            OperationCanceledException caught = null;
            try
            {
                await LlmEndpointClientFactory.DecideToolChannelAsync(
                    setting, probe, new LlmToolChannelProbeRequest(), cancellation.Token);
            }
            catch (OperationCanceledException exception) { caught = exception; }
            Assert.IsNotNull(caught);
            Assert.AreEqual(cancellation.Token, caught.CancellationToken);
            Assert.AreEqual(0, probe.Calls);
        }

        [Test]
        public async Task LocalEndpoint_ExplicitSetting_SkipsTheProbeRoundtrip()
        {
            ScriptedToolChannelProbe probe = new() { Result = Outcome(LlmToolChannelProbeOutcome.Rejected) };

            LlmToolChannelDecision native = await LlmEndpointClientFactory.DecideToolChannelAsync(
                LlmToolChannel.Native, probe, new LlmToolChannelProbeRequest(), CancellationToken.None);
            LlmToolChannelDecision text = await LlmEndpointClientFactory.DecideToolChannelAsync(
                LlmToolChannel.Text, probe, new LlmToolChannelProbeRequest(), CancellationToken.None);

            Assert.AreEqual(0, probe.Calls, "An explicit setting must not cost a request.");
            Assert.IsTrue(native.Native);
            Assert.IsFalse(text.Native);
        }

        // ---------------------------------------------------------------- фабрика: HTTP-эндпойнт

        [TestCase(LlmToolChannelProbeOutcome.Accepted, true)]
        [TestCase(LlmToolChannelProbeOutcome.Rejected, false)]
        [TestCase(LlmToolChannelProbeOutcome.Inconclusive, false)]
        public async Task HttpEndpoint_AutoProbesOnceAfterReadiness(
            LlmToolChannelProbeOutcome outcome, bool expectedNative)
        {
            TaskCompletionSource<LlmEndpointReadinessResult> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ScriptedToolChannelProbe probe = new() { Result = Outcome(outcome, "server capability response") };
            RecordingLogger logger = new();
            LlmEndpointClientFactory factory = new(new CoreAISettingsOptions(), logger, null,
                new ReadyReadinessProbe { Response = ready.Task }, probe);
            Task<LlmEndpointClientActivation> pending = factory.ActivateAsync(
                HttpDescriptor(LlmToolChannel.Auto), "session-key", CancellationToken.None);
            Assert.IsFalse(pending.IsCompleted);
            Assert.AreEqual(0, probe.Calls, "An unready endpoint must not consume its capability probe.");
            ready.SetResult(new LlmEndpointReadinessResult { IsReady = true, StatusCode = 200 });
            LlmEndpointClientActivation activation = await pending;
            Assert.AreEqual(expectedNative, activation.Client.SupportsNativeToolCalling);
            Assert.AreEqual(1, probe.Calls);
            Assert.AreEqual("https://example.test/v1", probe.LastRequest.BaseUrl);
            Assert.AreEqual("test", probe.LastRequest.Model);
            Assert.AreEqual("session-key", probe.LastRequest.ApiKey);
            Assert.AreEqual(outcome == LlmToolChannelProbeOutcome.Inconclusive, logger.Warnings.Count > 0);
            Assert.That(logger.Messages, Has.Some.Contains("server capability response"));
        }

        [TestCase(LlmToolChannel.Native, true)]
        [TestCase(LlmToolChannel.Text, false)]
        public async Task HttpEndpoint_ExplicitChannel_DoesNotProbe(LlmToolChannel channel, bool native)
        {
            ScriptedToolChannelProbe probe = new() { Throws = new InvalidOperationException("must not be called") };
            LlmEndpointClientFactory factory = new(new CoreAISettingsOptions(), new RecordingLogger(), null,
                new ReadyReadinessProbe(), probe);
            LlmEndpointClientActivation activation = await factory.ActivateAsync(
                HttpDescriptor(channel), "", CancellationToken.None);
            Assert.AreEqual(native, activation.Client.SupportsNativeToolCalling);
            Assert.AreEqual(0, probe.Calls);
        }

        [Test]
        public async Task HttpEndpoint_ProbeThrows_ActivationSurvivesWithTextAndDiagnostic()
        {
            RecordingLogger logger = new();
            ScriptedToolChannelProbe probe = new() { Throws = new InvalidOperationException("socket closed") };
            LlmEndpointClientFactory factory = new(new CoreAISettingsOptions(), logger, null,
                new ReadyReadinessProbe(), probe);
            LlmEndpointClientActivation activation = await factory.ActivateAsync(
                HttpDescriptor(LlmToolChannel.Auto), "", CancellationToken.None);
            Assert.IsFalse(activation.Client.SupportsNativeToolCalling);
            Assert.AreEqual(1, probe.Calls);
            Assert.That(logger.Warnings, Has.Some.Contains("socket closed"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HttpEndpoint_ProbeIgnoresCancellation_CallerStillReceivesCancellation(bool duringToolProbe)
        {
            using CancellationTokenSource cancellation = new();
            ScriptedToolChannelProbe probe = new()
            {
                Result = Outcome(LlmToolChannelProbeOutcome.Accepted),
                OnProbe = duringToolProbe ? cancellation.Cancel : null
            };
            ReadyReadinessProbe readiness = new() { OnProbe = duringToolProbe ? null : cancellation.Cancel };
            LlmEndpointClientFactory factory = new(new CoreAISettingsOptions(), new RecordingLogger(), null, readiness, probe);
            OperationCanceledException caught = null;
            try
            {
                await factory.ActivateAsync(HttpDescriptor(LlmToolChannel.Auto), "", cancellation.Token);
            }
            catch (OperationCanceledException exception) { caught = exception; }
            Assert.IsNotNull(caught);
            Assert.AreEqual(cancellation.Token, caught.CancellationToken);
            Assert.AreEqual(duringToolProbe ? 1 : 0, probe.Calls);
        }

        [TestCase("tool")]
        [TestCase("readiness")]
        [TestCase("transport")]
        public async Task UnityHttpAdapters_PreCancelled_DoNotStartRequest(string adapter)
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            OperationCanceledException caught = null;
            try
            {
                if (adapter == "tool")
                    await new UnityWebRequestToolChannelProbe().ProbeAsync(
                        new LlmToolChannelProbeRequest { BaseUrl = "invalid" }, cancellation.Token);
                else if (adapter == "readiness")
                    await new UnityWebRequestOpenAiReadinessProbe().ProbeAsync(
                        new LlmEndpointReadinessRequest { BaseUrl = "invalid" }, cancellation.Token);
                else
                    await new UnityWebRequestOpenAiTransport().PostNonStreamingAsync(
                        new OpenAiHttpPostRequest { Url = "invalid" }, cancellation.Token);
            }
            catch (OperationCanceledException exception) { caught = exception; }
            Assert.IsNotNull(caught, "Cancellation must win before URL validation or native request creation.");
            Assert.AreEqual(cancellation.Token, caught.CancellationToken);
        }

        [TestCase("tool")]
        [TestCase("readiness")]
        [TestCase("transport")]
        public async Task UnityHttpAdapters_RealLoopbackResponse_CompletesWithoutPolling(string adapter)
        {
            using HttpListener listener = StartLoopback(out string baseUrl);
            Task reply = ReplyOnceAsync(listener, adapter == "tool" ? 500 : 200,
                adapter == "tool" ? JinjaRejectionBody : "{}");
            Task request = StartAdapterRequest(adapter, baseUrl, CancellationToken.None);
            await CompleteWithinAsync(request);
            await CompleteWithinAsync(reply);
            if (adapter == "tool")
                Assert.AreEqual(LlmToolChannelProbeOutcome.Rejected,
                    (await ((Task<LlmToolChannelProbeResult>)request)).Outcome);
            else if (adapter == "readiness")
                Assert.IsTrue((await ((Task<LlmEndpointReadinessResult>)request)).IsReady);
            else
                Assert.AreEqual(200, (await ((Task<OpenAiHttpPostResult>)request)).StatusCode);
        }

        [TestCase("tool")]
        [TestCase("readiness")]
        [TestCase("transport")]
        public async Task UnityHttpAdapters_CancelFromWorker_AbortAndResumeOnUnityContext(string adapter)
        {
            using HttpListener listener = StartLoopback(out string baseUrl);
            using CancellationTokenSource cancellation = new();
            Task<HttpListenerContext> admission = listener.GetContextAsync();
            int unityThread = Thread.CurrentThread.ManagedThreadId;
            Task request = StartAdapterRequest(adapter, baseUrl, cancellation.Token);
            await CompleteWithinAsync(admission);
            HttpListenerContext context = await admission;
            try
            {
                // WHY: only cancel the token on the worker; UnityWebRequest.Abort must remain on Unity.
                await Task.Run(() => cancellation.Cancel());
                OperationCanceledException caught = null;
                try { await CompleteWithinAsync(request); }
                catch (OperationCanceledException exception) { caught = exception; }
                Assert.IsNotNull(caught);
                Assert.AreEqual(cancellation.Token, caught.CancellationToken);
                Assert.AreEqual(unityThread, Thread.CurrentThread.ManagedThreadId);
            }
            finally { context.Response.Abort(); }
        }

        [Test]
        public async Task UnityHttpAwait_AlreadyCompletedRequest_DoesNotWaitForAnotherEvent()
        {
            using HttpListener listener = StartLoopback(out string baseUrl);
            Task reply = ReplyOnceAsync(listener, 200, "{}");
            using UnityWebRequest request = UnityWebRequest.Get(baseUrl);
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            await CompleteWithinAsync(UnityWebRequestOpenAiTransport.AwaitCompletionAsync(operation, request, CancellationToken.None));
            await CompleteWithinAsync(reply);
            await CompleteWithinAsync(UnityWebRequestOpenAiTransport.AwaitCompletionAsync(operation, request, CancellationToken.None));
            Assert.AreEqual(200, request.responseCode);
        }

        private static Task StartAdapterRequest(string adapter, string baseUrl, CancellationToken cancellationToken)
        {
            if (adapter == "tool")
                return new UnityWebRequestToolChannelProbe().ProbeAsync(
                    new LlmToolChannelProbeRequest { BaseUrl = baseUrl, Model = "test" }, cancellationToken);
            if (adapter == "readiness")
                return new UnityWebRequestOpenAiReadinessProbe().ProbeAsync(
                    new LlmEndpointReadinessRequest { BaseUrl = baseUrl }, cancellationToken);
            return new UnityWebRequestOpenAiTransport().PostNonStreamingAsync(
                new OpenAiHttpPostRequest { Url = baseUrl, JsonBody = "{}" }, cancellationToken);
        }

        private static HttpListener StartLoopback(out string baseUrl)
        {
            TcpListener reservation = new(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            baseUrl = "http://127.0.0.1:" + port + "/";
            HttpListener listener = new();
            listener.Prefixes.Add(baseUrl);
            listener.Start();
            return listener;
        }

        private static async Task ReplyOnceAsync(HttpListener listener, int status, string body)
        {
            HttpListenerContext context = await listener.GetContextAsync();
            using (context.Response)
            {
                await context.Request.InputStream.CopyToAsync(Stream.Null);
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
        }

        private static async Task CompleteWithinAsync(Task operation)
        {
            Task completed = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreSame(operation, completed, "The asynchronous request must complete without holding the Unity frame.");
            await operation;
        }

        private sealed class RecordingLogger : IGameLogger
        {
            public List<string> Messages { get; } = new();
            public List<string> Warnings { get; } = new();
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null) { Messages.Add(message); }
            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            { Warnings.Add(message); Messages.Add(message); }
            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null) { Messages.Add(message); }
        }

        private static LlmEndpointDescriptor HttpDescriptor(LlmToolChannel channel)
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = "external",
                DisplayName = "External",
                Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = "https://example.test/v1",
                Model = "test",
                ToolChannel = channel
            };
        }

        // ---------------------------------------------------------------- лог: выбор не молчит

        [Test]
        public void ActivationLog_ToolChannelPhase_NamesChannelAndReason()
        {
            LlmUnityActivationLogContext context = new(
                "local-main", "Local Main", @"D:\Models\qwen3.5-0.8b.gguf", "Qwen Agent", 13333);
            LlmToolChannelDecision decision = new(
                false, LlmToolChannelDecisionSource.Probe, "server rejected \"tools\"\nHTTP 500");

            string line = LlmUnityActivationLog.ToolChannel(context, decision);

            StringAssert.Contains("local-main", line);
            StringAssert.Contains("text", line);
            StringAssert.Contains("server rejected", line);
            StringAssert.Contains("HTTP 500", line);
            StringAssert.DoesNotContain("\n", line);
            StringAssert.DoesNotContain("\r", line);
        }

        [Test]
        public void ToolChannelLog_ReadsAsOnePlainSentence()
        {
            LlmToolChannelDecision decision = LlmToolChannelResolution.ResolveWithoutProbe(
                LlmToolChannel.Auto, LlmToolChannelResolution.OpenAiHttpReason);

            string line = LlmToolChannelLog.Format("cloud", "Cloud", decision);

            StringAssert.Contains("Cloud", line);
            StringAssert.Contains("cloud", line);
            StringAssert.Contains(decision.ChannelName, line);
            StringAssert.Contains(decision.Reason, line);
        }

        // ---------------------------------------------------------------- настройка: хранится и читается

        [Test]
        public void Persistence_KeepsToolChannelAcrossSaveAndLoad()
        {
            string path = Path.Combine(Path.GetTempPath(), "coreai-toolchannel-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                FileLlmEndpointRegistryStore store = new(path);
                store.Save(new LlmEndpointRegistryState
                {
                    Endpoints = new[]
                    {
                        new LlmEndpointDescriptor
                        {
                            EndpointId = "local",
                            Kind = LlmEndpointKind.LlmUnity,
                            ToolChannel = LlmToolChannel.Text
                        }
                    }
                });

                LlmEndpointRegistryState loaded = store.Load();

                Assert.AreEqual(1, loaded.Endpoints.Count);
                Assert.AreEqual(LlmToolChannel.Text, loaded.Endpoints[0].ToolChannel);
                Assert.AreEqual(
                    LlmToolChannel.Text,
                    FileLlmEndpointRegistryStore.CloneDescriptor(loaded.Endpoints[0]).ToolChannel);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [TestCase(LlmToolChannel.Auto, true)]
        [TestCase(LlmToolChannel.Native, true)]
        [TestCase(LlmToolChannel.Text, false)]
        public void BundledLlmUnity_ExplicitOverrideWinsOverKnownCapability(LlmToolChannel setting, bool native)
        {
            LlmToolChannelDecision decision = LlmToolChannelResolution.ResolveWithoutProbe(
                setting, LlmToolChannelResolution.BundledLlamaLibReason);
            Assert.AreEqual(native, decision.Native);
            Assert.AreEqual(setting == LlmToolChannel.Auto ? LlmToolChannelDecisionSource.Known :
                LlmToolChannelDecisionSource.Declared, decision.Source);
        }
    }
#endif
}
