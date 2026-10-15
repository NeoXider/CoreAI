#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Tests.EditMode
{
    public sealed class MeaiOpenAiChatClientSseEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY this fixture detaches: it asserts through Assert.ThrowsAsync/CatchAsync, which BLOCK
        /// the calling thread until the awaited delegate finishes — being inside an async test does
        /// not change that. Under Unity's SynchronizationContext the delegate's continuation is
        /// posted back to that same blocked thread, and the editor deadlocks with no results file.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        [Test]
        public async Task ToolOptions_ModeIsSentToProvider(
            [Values(false, true)] bool streaming,
            [Values(null, "auto", "none", "required", "specific")] string mode)
        {
            MEAI.ChatOptions options = new()
            {
                ToolMode = mode switch
                {
                    "auto" => MEAI.ChatToolMode.Auto,
                    "none" => MEAI.ChatToolMode.None,
                    "required" => MEAI.ChatToolMode.RequireAny,
                    "specific" => MEAI.ChatToolMode.RequireSpecific("lookup"),
                    _ => null
                },
                Tools = new List<MEAI.AITool>
                {
                    MEAI.AIFunctionFactory.Create((Func<string>)(() => "unused"),
                        new MEAI.AIFunctionFactoryOptions { Name = "lookup" })
                }
            };
            JObject request = await CaptureToolOptionsRequestAsync(streaming, options);
            Assert.AreEqual("lookup", request["tools"]?[0]?["function"]?["name"]?.Value<string>());
            if (mode == "specific")
            {
                Assert.AreEqual("function", request["tool_choice"]?["type"]?.Value<string>());
                Assert.AreEqual("lookup", request["tool_choice"]?["function"]?["name"]?.Value<string>());
            }
            else
            {
                Assert.AreEqual(mode, request["tool_choice"]?.Value<string>(),
                    "The provider must receive the caller's native tool-choice contract; null uses its default.");
            }
        }

        [Test]
        public async Task ToolOptions_MultipleCallsPolicyIsSentToProvider(
            [Values(false, true)] bool streaming, [Values(null, false, true)] bool? allowMultiple)
        {
            JObject request = await CaptureToolOptionsRequestAsync(streaming,
                new MEAI.ChatOptions { AllowMultipleToolCalls = allowMultiple });
            Assert.AreEqual(allowMultiple, request["parallel_tool_calls"]?.Value<bool?>(),
                "False constrains one provider response; null preserves the provider's own default.");
        }

        [Test]
        public async Task ToolOptions_ExplicitRestrictionsWinOverProviderDefaults_EvenWithoutTools(
            [Values(false, true)] bool streaming, [Values(false, true)] bool hasTools)
        {
            MEAI.ChatOptions options = new()
            {
                ToolMode = MEAI.ChatToolMode.None,
                AllowMultipleToolCalls = false,
                Tools = hasTools ? new List<MEAI.AITool>
                {
                    MEAI.AIFunctionFactory.Create((Func<string>)(() => "unused"),
                        new MEAI.AIFunctionFactoryOptions { Name = "lookup" })
                } : null
            };
            JObject request = await CaptureToolOptionsRequestAsync(streaming, options,
                "{\"tool_choice\":\"required\",\"parallel_tool_calls\":true}");
            Assert.AreEqual("none", request["tool_choice"]?.Value<string>());
            Assert.AreEqual(false, request["parallel_tool_calls"]?.Value<bool?>());
        }

        [Test]
        public async Task TextEndpoint_RemovesNativeFieldsAfterProviderOverrides(
            [Values(false, true)] bool streaming, [Values(false, true)] bool injected)
        {
            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool>
                {
                    MEAI.AIFunctionFactory.Create((Func<string>)(() => "unused"),
                        new MEAI.AIFunctionFactoryOptions { Name = "lookup" })
                },
                ToolMode = MEAI.ChatToolMode.RequireAny,
                AllowMultipleToolCalls = true
            };
            string extra = injected
                ? "{\"tools\":[{\"type\":\"function\"}],\"tool_choice\":\"required\",\"parallel_tool_calls\":true,\"top_k\":7}"
                : "";
            JObject request = await CaptureToolOptionsRequestAsync(streaming, options, extra,
                supportsNativeToolCalling: false);
            Assert.IsNull(request["tools"], "A text endpoint rejecting tools must not receive them again.");
            Assert.IsNull(request["tool_choice"]);
            Assert.IsNull(request["parallel_tool_calls"]);
            if (injected) Assert.AreEqual(7, request["top_k"].Value<int>());
            Assert.AreEqual(1, options.Tools.Count, "Local invocation bindings must remain available.");
            Assert.IsInstanceOf<MEAI.RequiredChatToolMode>(options.ToolMode);
        }

        private static async Task<JObject> CaptureToolOptionsRequestAsync(bool streaming,
            MEAI.ChatOptions options, string extraBodyJson = "", bool supportsNativeToolCalling = true)
        {
            ToolOptionsCaptureTransport transport = new();
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings { ExtraBodyJson = extraBodyJson }, transport, supportsNativeToolCalling);
            MEAI.ChatMessage[] messages = { new(MEAI.ChatRole.User, "hello") };
            if (streaming)
            {
                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options))
                {
                    Assert.IsNotNull(update);
                }
            }
            else
            {
                await client.GetResponseAsync(messages, options);
            }
            Assert.IsNotNull(transport.Request);
            Assert.AreEqual(streaming, transport.Request.AcceptEventStream);
            return JObject.Parse(transport.Request.JsonBody);
        }

        [Test]
        public async Task RequestHistory_ReasoningOnlyAssistantNeverBecomesPromptContent([Values(false, true)] bool streaming)
        {
            ToolOptionsCaptureTransport transport = new();
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            MEAI.ChatMessage[] messages =
            {
                new(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                {
                    new MEAI.TextReasoningContent("private diagnostic plan")
                }),
                new(MEAI.ChatRole.User, "Continue")
            };
            if (streaming)
            {
                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(messages)) { }
            }
            else
            {
                await client.GetResponseAsync(messages);
            }

            JObject body = JObject.Parse(transport.Request.JsonBody);
            Assert.AreEqual("", body["messages"]?[0]?["content"]?.Value<string>());
            StringAssert.DoesNotContain("private diagnostic plan", transport.Request.JsonBody,
                "Private reasoning must not inflate or change the next model prompt.");
            Assert.AreEqual("private diagnostic plan", messages[0].Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
        }

        [Test]
        public async Task GetStreamingResponseAsync_ResponseStateIsAvailableBeforeFirstDelta()
        {
            LlmProviderResponseState state = new();
            MEAI.ChatOptions options = new()
            {
                AdditionalProperties = new MEAI.AdditionalPropertiesDictionary
                {
                    [LlmProviderResponseState.MetadataKey] = state
                }
            };
            TaskCompletionSource<bool> readGate = new();
            AsyncChunkedSseTransport transport = new(new[]
            {
                "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\ndata: [DONE]\n\n"
            }, readGate: readGate.Task);
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            await using IAsyncEnumerator<MEAI.ChatResponseUpdate> enumerator = client.GetStreamingResponseAsync(
                new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }, options).GetAsyncEnumerator();
            Task<bool> first = enumerator.MoveNextAsync().AsTask();

            try
            {
                Assert.IsTrue(state.ResponseObserved, "HTTP success must be observable while generation is still awaited.");
                Assert.IsFalse(first.IsCompleted, "HTTP success must not yield a chunk that commits fallback routing.");
            }
            finally { readGate.TrySetResult(true); }
            Assert.IsTrue(await first);
            Assert.AreEqual("answer", enumerator.Current.Text);
            while (await enumerator.MoveNextAsync()) { }
        }

        [Test]
        public async Task RequestResponseState_IsResetBeforeTransportAttempt([Values(false, true)] bool streaming)
        {
            LlmProviderResponseState state = new();
            state.RecordResponse();
            ToolOptionsCaptureTransport transport = new()
            {
                BeforeRequest = () => Assert.IsFalse(state.ResponseObserved,
                    "A new provider attempt must not inherit success evidence from an earlier attempt.")
            };
            MEAI.ChatOptions options = new()
            {
                AdditionalProperties = new MEAI.AdditionalPropertiesDictionary
                {
                    [LlmProviderResponseState.MetadataKey] = state
                }
            };
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            MEAI.ChatMessage[] messages = { new(MEAI.ChatRole.User, "hi") };
            if (streaming)
            {
                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options)) { }
            }
            else
            {
                await client.GetResponseAsync(messages, options);
            }
            Assert.IsTrue(state.ResponseObserved);
        }

        [Test]
        public async Task GetStreamingResponseAsync_FailedRetryDoesNotKeepPreviousHttpSuccess()
        {
            LlmProviderResponseState state = new();
            int attempts = 0;
            ToolOptionsCaptureTransport transport = new()
            {
                SsePayload = "data: [DONE]\n\n",
                BeforeRequest = () =>
                {
                    Assert.IsFalse(state.ResponseObserved);
                    attempts++;
                    if (attempts == 2)
                        throw new LlmClientException("The retry failed before opening a provider response.", LlmErrorCode.BackendUnavailable);
                }
            };
            MEAI.ChatOptions options = new()
            {
                AdditionalProperties = new MEAI.AdditionalPropertiesDictionary
                {
                    [LlmProviderResponseState.MetadataKey] = state
                }
            };
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            // WHY: the empty-stream retry waits a real backoff on the editor-ticked host delay; a blocking
            // Assert.ThrowsAsync would starve that tick and hang the EditMode run.
            LlmClientException failure = null;
            try
            {
                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                                   new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }, options))
                    Assert.Fail("An empty HTTP-success attempt must not commit the stream by yielding telemetry.");
            }
            catch (LlmClientException exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure, "The failed retry must surface as an LlmClientException.");
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, failure.ErrorCode);
            Assert.AreEqual(2, attempts);
            Assert.IsFalse(state.ResponseObserved, "A failed retry must remain a transport failure.");
        }

        [Test]
        public async Task ResponseWait_FiveMinutesOfReasoning_RemainsOneRequest()
        {
            ScriptedTimeTransport transport = new();
            for (int index = 0; index < 10; index++)
                transport.Steps.Add((30, "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"thinking\"}}]}\n\n"));
            transport.Steps.Add((1, "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\ndata: [DONE]\n\n"));
            MeaiOpenAiChatClient client = TimedClient(transport);
            Assert.AreEqual("answer", await CollectTimedResponseAsync(client));
            Assert.AreEqual(301, transport.Seconds);
            Assert.AreEqual(1, transport.StreamOpens);
            Assert.AreEqual(0, transport.NonStreamingCalls);
            Assert.AreEqual(0, transport.Request.TransportTimeoutSeconds);
            Assert.AreEqual(180, transport.Request.ResponseWaitTimeoutSeconds);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResponseWait_SilenceTimesOutWithoutRestart(bool afterReasoning)
        {
            ScriptedTimeTransport transport = new();
            if (afterReasoning)
                transport.Steps.Add((1, "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"plan\"}}]}\n\n"));
            transport.Steps.Add((181, ": keep-alive\n\n"));
            MeaiOpenAiChatClient client = TimedClient(transport);
            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(() => CollectTimedResponseAsync(client));
            Assert.AreEqual(LlmErrorCode.Timeout, failure.ErrorCode);
            Assert.AreEqual(LlmTimeoutKind.ResponseWait, failure.TimeoutKind);
            Assert.AreEqual(1, transport.StreamOpens);
            Assert.AreEqual(0, transport.NonStreamingCalls, "A response-wait deadline must not silently restart a model turn.");
            Assert.IsTrue(transport.Stream.Disposed);
        }

        [TestCase(": keep-alive\n\n")]
        [TestCase("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n")]
        [TestCase("data: {\"generation_progress\":{\"phase\":\"processing\"},\"choices\":[]}\n\n")]
        [TestCase("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"\"}}]}\n\n")]
        public void ResponseWait_NonTokenFramesDoNotRefreshDeadline(string frame)
        {
            ScriptedTimeTransport transport = new();
            transport.Steps.Add((60, frame));
            transport.Steps.Add((60, frame));
            transport.Steps.Add((61, frame));
            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(() => CollectTimedResponseAsync(TimedClient(transport)));
            Assert.AreEqual(LlmErrorCode.Timeout, failure.ErrorCode);
            Assert.AreEqual(181, transport.Seconds);
            Assert.AreEqual(1, transport.StreamOpens);
        }

        [Test]
        public async Task ResponseWait_ZeroDisablesSemanticDeadline()
        {
            ScriptedTimeTransport transport = new();
            transport.Steps.Add((181, ": keep-alive\n\n"));
            transport.Steps.Add((600, "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\ndata: [DONE]\n\n"));
            Assert.AreEqual("answer", await CollectTimedResponseAsync(TimedClient(transport, responseWait: 0)));
            Assert.AreEqual(0, transport.Request.ResponseWaitTimeoutSeconds);
        }

        [Test]
        public async Task TotalTimeout_PositiveCeilingStopsActiveReasoning()
        {
            ScriptedTimeTransport transport = new();
            for (int index = 0; index < 10; index++)
                transport.Steps.Add((30, "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"thinking\"}}]}\n\n"));
            LlmProviderResponseState state = new();
            try
            {
                await CollectTimedResponseAsync(TimedClient(transport, totalTimeout: 120), state);
                Assert.Fail("The configured total deadline must stop the active stream.");
            }
            catch (LlmClientException failure)
            {
                Assert.AreEqual(LlmErrorCode.Timeout, failure.ErrorCode);
                Assert.AreEqual(LlmTimeoutKind.TotalDeadline, failure.TimeoutKind);
                Assert.AreEqual(LlmTimeoutKind.TotalDeadline, state.TimeoutKind);
                Assert.IsTrue(state.ResponseObserved);
                Assert.IsTrue(state.RetrySuppressed);
            }
            Assert.AreEqual(120, transport.Seconds);
            Assert.AreEqual(1, transport.StreamOpens);
        }

        [TestCase(120, 180, LlmTimeoutKind.TotalDeadline)]
        [TestCase(0, 180, LlmTimeoutKind.ResponseWait)]
        public async Task OwnedBudgetTimer_FiveMillisecondsEarlyRetainsBindingTimeoutKind(
            int totalSeconds, int responseSeconds, LlmTimeoutKind expected)
        {
            ScriptedTimeTransport transport = new() { BlockReads = true };
            ExpiringMarshaler marshaler = new(transport) { EarlySeconds = 0.005 };
            MeaiOpenAiChatClient client = new(new OpenAiHttpOptions
            {
                Model = "model", RequestTimeoutSeconds = totalSeconds, ResponseWaitTimeoutSeconds = responseSeconds
            }, transport, null, marshaler) { MonotonicSeconds = () => transport.Seconds };
            LlmProviderResponseState state = new();
            try
            {
                await CollectTimedResponseAsync(client, state);
                Assert.Fail("The owned budget timer must terminate the blocked read.");
            }
            catch (LlmClientException failure)
            {
                Assert.AreEqual(expected, failure.TimeoutKind);
                Assert.AreEqual(expected, state.TimeoutKind);
                Assert.IsTrue(state.RetrySuppressed);
            }
            Assert.AreEqual((totalSeconds > 0 ? totalSeconds : responseSeconds) - 0.005, transport.Seconds, 0.0001);
        }

        [TestCase(120, 180, LlmTimeoutKind.TotalDeadline)]
        [TestCase(0, 180, LlmTimeoutKind.ResponseWait)]
        public async Task TransportTimer_FiveMillisecondsBeforeBudgetRetainsTimeoutKind(
            int totalSeconds, int responseSeconds, LlmTimeoutKind expected)
        {
            double seconds = 0;
            ToolOptionsCaptureTransport transport = new()
            {
                BeforeRequest = () =>
                {
                    seconds = (totalSeconds > 0 ? totalSeconds : responseSeconds) - 0.005;
                    throw new OperationCanceledException("The configured transport timer fired slightly early.");
                }
            };
            MeaiOpenAiChatClient client = new(new OpenAiHttpOptions
            {
                Model = "model", RequestTimeoutSeconds = totalSeconds, ResponseWaitTimeoutSeconds = responseSeconds
            }, transport) { MonotonicSeconds = () => seconds };
            LlmProviderResponseState state = new();
            try
            {
                await CollectTimedResponseAsync(client, state);
                Assert.Fail("The transport timer must surface its binding budget.");
            }
            catch (LlmClientException failure)
            {
                Assert.AreEqual(expected, failure.TimeoutKind);
                Assert.AreEqual(expected, state.TimeoutKind);
            }
        }

        [Test]
        public void ResponseWait_HeaderTimeConsumesFirstTokenBudget()
        {
            ScriptedTimeTransport transport = new() { HeaderSeconds = 120 };
            transport.Steps.Add((61, "data: {\"choices\":[{\"delta\":{\"content\":\"late\"}}]}\n\n"));
            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(() => CollectTimedResponseAsync(TimedClient(transport)));
            Assert.AreEqual(LlmErrorCode.Timeout, failure.ErrorCode);
            Assert.AreEqual(181, transport.Seconds);
            Assert.AreEqual(1, transport.StreamOpens);
        }

        [TestCase(0, 180)]
        [TestCase(120, 0)]
        public void NonStreaming_WaitsForWholeResponseWithIndependentLimits(int totalTimeout, int responseWait)
        {
            ScriptedTimeTransport transport = new() { NonStreamingSeconds = 181 };
            MeaiOpenAiChatClient client = TimedClient(transport, totalTimeout, responseWait);
            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(async () =>
                await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hello") }));
            Assert.AreEqual(LlmErrorCode.Timeout, failure.ErrorCode);
            Assert.AreEqual(1, transport.NonStreamingCalls);
            Assert.AreEqual(0, transport.StreamOpens);
        }

        [Test]
        public async Task NonStreaming_ZeroLimitsPermitLongResponse()
        {
            ScriptedTimeTransport transport = new() { NonStreamingSeconds = 600 };
            MEAI.ChatResponse response = await TimedClient(transport, responseWait: 0).GetResponseAsync(
                new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hello") });
            Assert.AreEqual("answer", response.Text);
            Assert.AreEqual(0, transport.Request.TransportTimeoutSeconds);
            Assert.AreEqual(0, transport.Request.ResponseWaitTimeoutSeconds);
        }

        [Test]
        public async Task ResponseWait_PartialToolArgumentsRefreshDeadline()
        {
            ScriptedTimeTransport transport = new();
            string[] fragments = { "{", "\"value\":", "1}" };
            for (int index = 0; index < fragments.Length; index++)
            {
                JObject function = new() { ["arguments"] = fragments[index] };
                if (index == 0) function["name"] = "lookup";
                JObject root = new()
                {
                    ["choices"] = new JArray(new JObject
                    {
                        ["delta"] = new JObject
                        {
                            ["tool_calls"] = new JArray(new JObject
                            {
                                ["index"] = 0, ["id"] = "call_1", ["function"] = function
                            })
                        }
                    })
                };
                transport.Steps.Add((100, "data: " + root.ToString(Newtonsoft.Json.Formatting.None) + "\n\n"));
            }
            transport.Steps.Add((0, "data: [DONE]\n\n"));
            List<MEAI.FunctionCallContent> calls = new();
            await foreach (MEAI.ChatResponseUpdate update in TimedClient(transport).GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hello") }))
                calls.AddRange(update.Contents.OfType<MEAI.FunctionCallContent>());

            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual(1L, Convert.ToInt64(calls[0].Arguments["value"]));
            Assert.AreEqual(300, transport.Seconds);
            Assert.AreEqual(1, transport.StreamOpens);
        }

        [Test]
        public void ResponseWait_AbandonedReadIsCancelledAndDisposedThroughHostDelay()
        {
            ScriptedTimeTransport transport = new() { BlockReads = true };
            OpenAiHttpOptions settings = new() { Model = "model", ResponseWaitTimeoutSeconds = 180 };
            ExpiringMarshaler marshaler = new(transport);
            MeaiOpenAiChatClient client = new(settings, transport, null, marshaler)
            {
                MonotonicSeconds = () => transport.Seconds
            };
            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(() => CollectTimedResponseAsync(client));
            Assert.AreEqual(LlmErrorCode.Timeout, failure.ErrorCode);
            Assert.AreEqual(1, marshaler.Delays);
            Assert.AreEqual(1, transport.ReadCalls, "A timed-out read must not overlap a new read or request.");
            Assert.IsTrue(transport.ReadCancelled);
            Assert.IsTrue(transport.Stream.Disposed);
            Assert.AreEqual(1, transport.StreamOpens);
        }

        [Test]
        public async Task HttpOptions_DefaultsAndSnapshotPreserveIndependentLimits()
        {
            OpenAiHttpOptions settings = new() { Model = "model" };
            Assert.AreEqual(0, settings.RequestTimeoutSeconds);
            Assert.AreEqual(262144, settings.MaxTokens);
            Assert.IsNull(settings.ResponseWaitTimeoutSeconds);
            float savedWait = CoreAISettings.LlmResponseWaitTimeoutSeconds;
            try
            {
                CoreAISettings.LlmResponseWaitTimeoutSeconds = 42f;
                ToolOptionsCaptureTransport transport = new();
                await new MeaiOpenAiChatClient(settings, transport).GetResponseAsync(
                    new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hello") });
                Assert.AreEqual(0, transport.Request.TransportTimeoutSeconds);
                Assert.AreEqual(42, transport.Request.ResponseWaitTimeoutSeconds);
                settings.RequestTimeoutSeconds = 75;
                settings.ResponseWaitTimeoutSeconds = 0;
                OpenAiHttpOptions snapshot = OpenAiHttpOptions.From(settings);
                Assert.AreEqual(75, snapshot.RequestTimeoutSeconds);
                Assert.AreEqual(0, snapshot.ResponseWaitTimeoutSeconds);
            }
            finally { CoreAISettings.LlmResponseWaitTimeoutSeconds = savedWait; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ResponseWait_OuterRetryDoesNotReplayDeadlineExpiry(bool mapFailureToChunk)
        {
            ScriptedTimeTransport transport = new() { HeaderSeconds = 181 };
            LlmCompletionRequest request = new() { UserPayload = "hello" };
            CoreStreamingAdapter adapter = new(TimedClient(transport), mapFailureToChunk);
            RetryingStreamingLlmClientDecorator client = new(adapter, 3);
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request))
                chunks.Add(chunk);
            Assert.AreEqual(LlmErrorCode.Timeout, chunks.Last().ErrorCode);
            Assert.AreEqual(1, transport.StreamOpens, "The semantic response-wait budget must not restart in an outer retry layer.");
            Assert.AreEqual(0, client.RetryCount);
            Assert.IsTrue(request.ProviderResponseState.RetrySuppressed);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ResponseWait_TransportCancellationRaceSuppressesReplayButTypedTransportTimeoutCanRetry(bool typedTimeout)
        {
            int attempts = 0;
            ToolOptionsCaptureTransport transport = new()
            {
                BeforeRequest = () =>
                {
                    attempts++;
                    if (typedTimeout)
                        throw new LlmClientException("A transport timeout was reported independently of the request budget.", LlmErrorCode.Timeout);
                    throw new OperationCanceledException("The transport's configured timer fired first.");
                }
            };
            MeaiOpenAiChatClient core = new(new OpenAiHttpOptions { Model = "model", ResponseWaitTimeoutSeconds = 180 }, transport);
            RetryingStreamingLlmClientDecorator client = new(new CoreStreamingAdapter(core, true), 1);
            LlmCompletionRequest request = new() { UserPayload = "hello" };
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request))
                chunks.Add(chunk);
            Assert.AreEqual(LlmErrorCode.Timeout, chunks.Last().ErrorCode);
            Assert.AreEqual(typedTimeout ? 2 : 1, attempts);
            Assert.AreEqual(!typedTimeout, request.ProviderResponseState.RetrySuppressed);
            Assert.AreEqual(typedTimeout ? LlmTimeoutKind.None : LlmTimeoutKind.Transport,
                request.ProviderResponseState.TimeoutKind);
        }

#if UNITY_EDITOR
        [TestCase(false)]
        [TestCase(true)]
        public async Task ResponseWait_MeaiPipelineSurfacesFirstTokenTimeoutWithoutRetryOrFallback(bool streaming)
        {
            ScriptedTimeTransport transport = new() { HeaderSeconds = 181, NonStreamingSeconds = 181 };
            MeaiLlmClient adapter = new(TimedClient(transport),
                CoreAI.Infrastructure.Logging.GameLoggerUnscopedFallback.Instance,
                new CoreAISettingsOptions(), supportsNativeToolCalling: true);
            RetryingStreamingLlmClientDecorator retry = new(adapter, 3);
            CoreStreamingAdapter unusedSecondary = new(TimedClient(new ScriptedTimeTransport()), true);
            FallbackLlmClientDecorator client = new(retry, unusedSecondary);
            LlmCompletionRequest request = new() { UserPayload = "hello" };
            if (streaming)
            {
                List<LlmStreamChunk> chunks = new();
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request))
                    chunks.Add(chunk);
                Assert.AreEqual(LlmErrorCode.Timeout, chunks.Last().ErrorCode);
                Assert.AreEqual(1, transport.StreamOpens);
            }
            else
            {
                LlmCompletionResult result = await client.CompleteAsync(request);
                Assert.AreEqual(LlmErrorCode.Timeout, result.ErrorCode);
                Assert.AreEqual(1, transport.NonStreamingCalls);
            }
            Assert.AreEqual(0, retry.RetryCount);
            Assert.AreEqual(0, client.FallbackCount);
            Assert.AreEqual(0, unusedSecondary.Calls);
        }
#endif

        private sealed class CoreStreamingAdapter : ILlmClient
        {
            private readonly MeaiOpenAiChatClient _core;
            private readonly bool _mapFailureToChunk;
            public int Calls { get; private set; }
            public CoreStreamingAdapter(MeaiOpenAiChatClient core, bool mapFailureToChunk)
            {
                _core = core;
                _mapFailureToChunk = mapFailureToChunk;
            }
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Calls++;
                request.ProviderResponseState ??= new LlmProviderResponseState();
                MEAI.ChatOptions options = new()
                {
                    AdditionalProperties = new MEAI.AdditionalPropertiesDictionary
                    {
                        [LlmProviderResponseState.MetadataKey] = request.ProviderResponseState
                    }
                };
                await using IAsyncEnumerator<MEAI.ChatResponseUpdate> enumerator = _core.GetStreamingResponseAsync(
                    new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, request.UserPayload) }, options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                LlmClientException failure = null;
                while (true)
                {
                    bool hasNext;
                    try { hasNext = await enumerator.MoveNextAsync(); }
                    catch (LlmClientException exception) { failure = exception; break; }
                    if (!hasNext) break;
                    yield return new LlmStreamChunk { Text = enumerator.Current.Text };
                }
                if (failure != null)
                {
                    if (!_mapFailureToChunk) throw failure;
                    yield return new LlmStreamChunk { IsDone = true, Error = failure.Message, ErrorCode = failure.ErrorCode };
                }
            }
        }

        private sealed class ExpiringMarshaler : ILlmAsyncMarshaler
        {
            private readonly ScriptedTimeTransport _clock;
            public int Delays { get; private set; }
            public double EarlySeconds { get; set; }
            public ExpiringMarshaler(ScriptedTimeTransport clock) => _clock = clock;
            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken) => factory();
            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
            {
                Delays++;
                _clock.Seconds += milliseconds / 1000.0 - EarlySeconds;
                return Task.CompletedTask;
            }
        }

        private static MeaiOpenAiChatClient TimedClient(ScriptedTimeTransport transport, int totalTimeout = 0,
            int? responseWait = null)
        {
            OpenAiHttpOptions settings = OpenAiHttpOptions.From(new DoneSentinelSettings());
            settings.RequestTimeoutSeconds = totalTimeout;
            settings.ResponseWaitTimeoutSeconds = responseWait;
            return new MeaiOpenAiChatClient(settings, transport) { MonotonicSeconds = () => transport.Seconds };
        }

        private static async Task<string> CollectTimedResponseAsync(MeaiOpenAiChatClient client,
            LlmProviderResponseState state = null)
        {
            MEAI.ChatOptions options = state == null ? null : new MEAI.ChatOptions
            {
                AdditionalProperties = new MEAI.AdditionalPropertiesDictionary
                {
                    [LlmProviderResponseState.MetadataKey] = state
                }
            };
            StringBuilder text = new();
            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hello") }, options))
                text.Append(update.Text);
            return text.ToString();
        }

        private sealed class ScriptedTimeTransport : IOpenAiHttpTransport
        {
            public List<(double Seconds, string Payload)> Steps { get; } = new();
            public double Seconds { get; set; }
            public double HeaderSeconds { get; set; }
            public double NonStreamingSeconds { get; set; }
            public bool BlockReads { get; set; }
            public int ReadCalls { get; set; }
            public bool ReadCancelled { get; set; }
            public int StreamOpens { get; private set; }
            public int NonStreamingCalls { get; private set; }
            public OpenAiHttpPostRequest Request { get; private set; }
            public ScriptedTimeStream Stream { get; private set; }
            public string DebugLabel => "ScriptedTime";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request, CancellationToken cancellationToken = default)
            {
                Request = request;
                NonStreamingCalls++;
                Seconds += NonStreamingSeconds;
                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"answer\"}}]}"
                });
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request, CancellationToken cancellationToken = default)
            {
                Request = request;
                StreamOpens++;
                Seconds += HeaderSeconds;
                Stream = new ScriptedTimeStream(this);
                return Task.FromResult(new OpenAiHttpSseOpenResult { StatusCode = 200 }.WithRawStream(Stream));
            }
        }

        private sealed class ScriptedTimeStream : Stream
        {
            private readonly ScriptedTimeTransport _owner;
            private readonly Queue<(double Seconds, string Payload)> _steps;
            public bool Disposed { get; private set; }
            public ScriptedTimeStream(ScriptedTimeTransport owner)
            {
                _owner = owner;
                _steps = new Queue<(double Seconds, string Payload)>(owner.Steps);
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_steps.Count == 0) return 0;
                (double Seconds, string Payload) step = _steps.Dequeue();
                _owner.Seconds += step.Seconds;
                byte[] bytes = Encoding.UTF8.GetBytes(step.Payload);
                Array.Copy(bytes, 0, buffer, offset, bytes.Length);
                return bytes.Length;
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                _owner.ReadCalls++;
                if (!_owner.BlockReads) return Read(buffer, offset, count);
                TaskCompletionSource<int> pending = new();
                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                {
                    _owner.ReadCancelled = true;
                    pending.TrySetCanceled(cancellationToken);
                });
                return await pending.Task;
            }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class ToolOptionsCaptureTransport : IOpenAiHttpTransport
        {
            public Action BeforeRequest { get; set; }
            public string SsePayload { get; set; } = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            public OpenAiHttpPostRequest Request { get; private set; }
            public string DebugLabel => "ToolOptionsCapture";
            public bool SupportsSseStreaming => true;
            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                BeforeRequest?.Invoke();
                Request = request;
                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}"
                });
            }
            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                BeforeRequest?.Invoke();
                Request = request;
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                        { ["Content-Type"] = new[] { "text/event-stream" } }
                };
                return Task.FromResult(result.WithRawStream(new MemoryStream(Encoding.UTF8.GetBytes(SsePayload))));
            }
        }

        [Test]
        public void ParseSseUpdates_MessageOnlyChunk_ParsesText()
        {
            const string sse =
                "data:{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"chunk\"}}]}\n";
            List<MEAI.ChatResponseUpdate> list = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("chunk", list[0].Text);
        }

        [Test]
        public void ParseSseUpdates_DataPrefixWithSpace_ParsesDelta()
        {
            const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n";
            List<MEAI.ChatResponseUpdate> list = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("hi", list[0].Text);
        }

        [Test]
        public void ParseSseUpdates_DataPrefixWithoutSpace_ParsesDelta()
        {
            const string sse = "data:{\"choices\":[{\"delta\":{\"content\":\"local\"}}]}\n";
            List<MEAI.ChatResponseUpdate> list = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("local", list[0].Text);
        }

        [Test]
        public void ParseSseDataLine_MessageOnly_InStreamChunk_EmitsText()
        {
            const string json =
                "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"from message\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("from message", u.Text);
        }

        [Test]
        public void ParseSseDataLine_LegacyChoicesText_EmitsText()
        {
            const string json = "{\"choices\":[{\"index\":0,\"text\":\"legacy stream\"}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("legacy stream", u.Text);
        }

        [Test]
        public void ParseSseDataLine_ReasoningOnly_EmitsTextReasoningContentNotAssistantText()
        {
            // A reasoning-only delta (DeepSeek/Qwen delta.reasoning_content with content=null) is a
            // REAL delta: it must surface as TextReasoningContent (so the empty-stream retry never
            // fires) while the visible assistant text stays empty.
            const string json = "{\"choices\":[{\"delta\":{\"reasoning_content\":\"think\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u, "Reasoning-only deltas must parse, not be dropped.");
            Assert.AreEqual("", u.Text ?? "", "Reasoning must never leak into the visible text.");
            MEAI.TextReasoningContent reasoning =
                u.Contents.OfType<MEAI.TextReasoningContent>().Single();
            Assert.AreEqual("think", reasoning.Text);
        }

        [Test]
        public void ParseSseDataLine_ReasoningAlternateSpellings_EmitTextReasoningContent()
        {
            const string bareJson = "{\"choices\":[{\"delta\":{\"reasoning\":\"bare\"}}]}";
            MEAI.ChatResponseUpdate bare = MeaiOpenAiChatClient.ParseSseDataLineForTests(bareJson);
            Assert.IsNotNull(bare);
            Assert.AreEqual("bare",
                bare.Contents.OfType<MEAI.TextReasoningContent>().Single().Text);

            const string camelJson = "{\"choices\":[{\"delta\":{\"reasoningContent\":\"camel\"}}]}";
            MEAI.ChatResponseUpdate camel = MeaiOpenAiChatClient.ParseSseDataLineForTests(camelJson);
            Assert.IsNotNull(camel);
            Assert.AreEqual("camel",
                camel.Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
        }

        [Test]
        public void ReasoningAlias_EmptyPrimaryFieldDoesNotHideReasoningText(
            [Values(false, true)] bool streaming, [Values(false, true)] bool nullPrimary)
        {
            JObject fields = new()
            {
                ["reasoning_content"] = nullPrimary ? JValue.CreateNull() : new JValue(""),
                ["reasoning_text"] = "alternate thinking"
            };
            JObject response = new()
            {
                ["choices"] = new JArray(new JObject { [streaming ? "delta" : "message"] = fields })
            };
            IList<MEAI.AIContent> contents = streaming
                ? MeaiOpenAiChatClient.ParseSseDataLineForTests(response.ToString()).Contents
                : MeaiOpenAiChatClient.ParseResponse(response.ToString()).Messages.Single().Contents;
            Assert.AreEqual("alternate thinking", contents.OfType<MEAI.TextReasoningContent>().Single().Text);
            Assert.IsFalse(contents.OfType<MEAI.TextContent>().Any(t => !string.IsNullOrEmpty(t.Text)));
        }

        [Test]
        public void ParseSseDataLine_ContentOnly_EmitsText()
        {
            const string json = "{\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("hi", u.Text);
        }

        [Test]
        public void ParseSseDataLine_ReasoningAndContent_EmitsContentAsTextAndReasoningAsReasoning()
        {
            const string json = "{\"choices\":[{\"delta\":{\"reasoning_content\":\"x\",\"content\":\"out\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("out", u.Text, "Only content may be visible text.");
            Assert.AreEqual("x", u.Contents.OfType<MEAI.TextReasoningContent>().Single().Text,
                "The reasoning delta must ride along as TextReasoningContent, not be dropped.");
        }

        [Test]
        public void ParseSseDataLine_GenerationProgressProcessing_EmitsMetadataOnlyUpdate()
        {
            const string json = "{\"generation_progress\":{\"phase\":\"processing\"},\"choices\":[]}";

            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);

            Assert.IsNotNull(u, "Whitelisted progress must parse, not be dropped.");
            Assert.AreEqual("", u.Text ?? "", "Progress must never fake visible text.");
            Assert.IsEmpty(u.Contents.OfType<MEAI.TextReasoningContent>(),
                "Progress must never carry reasoning.");
            Assert.AreEqual(MeaiOpenAiChatClient.GenerationProgressPhaseProcessing,
                u.AdditionalProperties[MeaiOpenAiChatClient.GenerationProgressMetadataKey]);
        }

        [Test]
        public void ParseSseDataLine_GenerationProgressUnknownPhase_IsIgnored()
        {
            const string json = "{\"generation_progress\":{\"phase\":\"dreaming\"},\"choices\":[]}";
            Assert.IsNull(MeaiOpenAiChatClient.ParseSseDataLineForTests(json),
                "Only the whitelisted processing phase is safe progress.");
        }

        [Test]
        public void ParseSseDataLine_GenerationProgressStripsEmbeddedReasoning()
        {
            const string json =
                "{\"generation_progress\":{\"phase\":\"processing\",\"reasoning_content\":\"secret plan\",\"reasoning\":\"x\"},\"choices\":[]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);

            Assert.IsNotNull(u);
            Assert.AreEqual("", u.Text ?? "", "Leaked reasoning must never become visible text.");
            Assert.IsEmpty(u.Contents.OfType<MEAI.TextReasoningContent>(),
                "Leaked reasoning must never become reasoning content either.");
        }

        [Test]
        public void ParseSseDataLine_RoleOnlyDelta_IsNotSemanticContent()
        {
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(
                "{\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}");
            Assert.IsNull(u);
        }

        [Test]
        public void ParseCompletion_EmptyContent_KeepsReasoningOutOfVisibleText()
        {
            // WHY: in RedoSchool reasoning_content was stored as a finished note for the learner; empty
            // content must stay empty, and the reasoning stays available for diagnostics only.
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":\"Hello from reasoning\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("", r.Text);
            Assert.AreEqual("Hello from reasoning",
                r.Messages[0].Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
        }

        [Test]
        public void ParseCompletion_ContentPresent_ReasoningExposedButNotPromoted()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"real answer\",\"reasoning_content\":\"hidden plan\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("real answer", r.Text, "Visible text must stay the provider content.");
            Assert.AreEqual("hidden plan",
                r.Messages[0].Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
        }

        [Test]
        public void ParseCompletion_EmptyContentWithToolCalls_DoesNotPromoteReasoning()
        {
            // A tool-call turn with empty content is NOT an empty answer: its reasoning must never
            // masquerade as visible assistant text next to the executing tool calls.
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":\"call plan\",\"tool_calls\":[" +
                "{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"go\",\"arguments\":\"{}\"}}]}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("", r.Text);
            Assert.AreEqual("call plan",
                r.Messages[0].Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
            Assert.AreEqual(1, r.Messages[0].Contents.OfType<MEAI.FunctionCallContent>().Count());
        }

        [Test]
        public void ParseCompletion_InlineThinkOnlyContent_KeepsThinkTextOutOfVisibleText()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"<think>inline plan</think>\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("", r.Text,
                "Think-only content must remain hidden reasoning, not an assistant answer.");
            Assert.AreEqual("inline plan",
                r.Messages[0].Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
        }

        [Test]
        public void ParseCompletion_ContentAsTextPartsArray_JoinsText()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("a\nb", r.Text);
        }

        [Test]
        public void ParseCompletion_ContentWithoutReasoning_ReturnsContentUnchanged()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"plain answer\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("plain answer", r.Text);
            Assert.IsEmpty(r.Messages[0].Contents.OfType<MEAI.TextReasoningContent>());
        }

        [Test]
        public void ParseCompletion_EmptyContent_KeepsCamelCaseReasoningOutOfVisibleText()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoningContent\":\"Hello camel\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("", r.Text);
            Assert.AreEqual("Hello camel",
                r.Messages[0].Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
        }

        [Test]
        public void ParseCompletion_MalformedToolCallArguments_DegradesOnlyThatCall()
        {
            // One bad tool call must NOT wipe the assistant text and the other calls: the good call
            // parses normally, the bad one surfaces via the shared parse-error markers (same
            // contract as the streaming accumulator; ToolExecutionPolicy fails just that call).
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"doing it\",\"tool_calls\":[" +
                "{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"good\",\"arguments\":\"{\\\"a\\\":1}\"}}," +
                "{\"id\":\"c2\",\"type\":\"function\",\"function\":{\"name\":\"bad\",\"arguments\":\"{\\\"broken\\\":\"}}" +
                "]}}]}";

            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);

            Assert.AreEqual("doing it", r.Text, "Assistant text must survive a bad tool call.");
            List<MEAI.FunctionCallContent> calls =
                r.Messages[0].Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count, "Both tool calls must survive; only the bad one degrades.");

            MEAI.FunctionCallContent good = calls.Single(c => c.Name == "good");
            Assert.AreEqual(1L, Convert.ToInt64(good.Arguments["a"]));
            Assert.IsFalse(good.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests));

            MEAI.FunctionCallContent bad = calls.Single(c => c.Name == "bad");
            Assert.IsTrue(bad.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests),
                "The malformed call must carry the parse-error marker, not silently vanish.");
            Assert.AreEqual("{\"broken\":",
                bad.Arguments[MeaiOpenAiChatClient.ToolCallRawArgumentsKeyForTests]);
        }

        [Test]
        public void ParseCompletion_WholeResponseMalformed_ReturnsEmptyMessage()
        {
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse("this is not json");
            Assert.AreEqual("", r.Text);
        }

        [Test]
        public void IsSseDoneLine_DataDone_ReturnsTrue()
        {
            Assert.IsTrue(MeaiOpenAiChatClient.IsSseDoneLineForTests("data: [DONE]"));
            Assert.IsTrue(MeaiOpenAiChatClient.IsSseDoneLineForTests("data:[DONE]"));
            Assert.IsFalse(MeaiOpenAiChatClient.IsSseDoneLineForTests("data: {\"done\":true}"));
        }

        [Test]
        public async Task GetStreamingResponseAsync_DoneSentinelStopsWithoutWaitingForStreamEof()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n" +
                "data: [DONE]\n\n";
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), new DoneSentinelTransport(sse));
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("hello", string.Concat(parts));
        }

        [Test]
        public async Task GetStreamingResponseAsync_AsyncChunkedReads_ContinuesAfterFirstChunk()
        {
            string[] chunks =
            {
                "data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}\n\n",
                "data: {\"choices\":[{\"delta\":{\"content\":\"B\"}}]}\n\n",
                "data: [DONE]\n\n"
            };
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), new AsyncChunkedSseTransport(chunks));
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("AB", string.Concat(parts));
        }

        [Test]
        public async Task GetStreamingResponseAsync_ReasoningOnlyStream_KeepsVisibleTextEmpty()
        {
            // WHY: in RedoSchool the reasoning stream reached the consumer as a note. reasoning_content
            // deltas count as real deltas, but are never promoted to visible text.
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"I am \"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"thinking\"}}]}\n\n" +
                "data: [DONE]\n\n";
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), new DoneSentinelTransport(sse));
            List<string> visibleParts = new();
            StringBuilder reasoningParts = new();

            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(async () =>
            {
            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    visibleParts.Add(update.Text);
                }

                if (update.Contents != null)
                {
                    foreach (MEAI.TextReasoningContent trc in
                             update.Contents.OfType<MEAI.TextReasoningContent>())
                    {
                        reasoningParts.Append(trc.Text);
                    }
                }
            }

            });
            Assert.AreEqual(LlmErrorCode.EmptyResponse, failure.ErrorCode);

            Assert.AreEqual("I am thinking", reasoningParts.ToString(),
                "Every reasoning delta must surface as TextReasoningContent.");
            Assert.AreEqual("", string.Concat(visibleParts),
                "A reasoning-only turn must not emit visible assistant text.");
        }

        [Test]
        public async Task GetStreamingResponseAsync_NativeToolOnly_IsUsableCompletion()
        {
            const string sse = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"test_tool\",\"arguments\":\"{}\"}}]}}]}\n\ndata: [DONE]\n\n";
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), new DoneSentinelTransport(sse));
            List<MEAI.ChatResponseUpdate> updates = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
                updates.Add(update);

            Assert.AreEqual(1, updates.SelectMany(u => u.Contents).OfType<MEAI.FunctionCallContent>().Count());
            Assert.IsTrue(updates.All(u => string.IsNullOrEmpty(u.Text)));
        }

        [Test]
        public async Task GetStreamingResponseAsync_ReasoningPlusContent_DoesNotPromoteReasoning()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"plan\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\n" +
                "data: [DONE]\n\n";
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), new DoneSentinelTransport(sse));
            List<string> visibleParts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    visibleParts.Add(update.Text);
                }
            }

            Assert.AreEqual("answer", string.Concat(visibleParts),
                "When real content arrived, reasoning must stay hidden (no promotion).");
        }

        [Test]
        public void FullResponseToSimulatedStreamingUpdates_ReEmitsUsageAsTrailingUpdate()
        {
            // WebGL non-native-streaming and the stream->non-stream fallback replay a full response
            // as simulated updates; without a trailing UsageContent every such turn reported 0 tokens.
            MEAI.ChatResponse full = new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "hi"))
            {
                Usage = new MEAI.UsageDetails
                {
                    InputTokenCount = 3,
                    OutputTokenCount = 5,
                    TotalTokenCount = 8
                }
            };

            List<MEAI.ChatResponseUpdate> updates =
                MeaiOpenAiChatClient.FullResponseToSimulatedStreamingUpdatesForTests(full);

            Assert.AreEqual("hi", string.Concat(updates.Select(u => u.Text ?? "")),
                "The visible text must still be replayed.");
            MEAI.UsageContent usage = updates
                .SelectMany(u => u.Contents ?? new List<MEAI.AIContent>())
                .OfType<MEAI.UsageContent>()
                .Single();
            Assert.AreEqual(3, (int)(usage.Details.InputTokenCount ?? 0));
            Assert.AreEqual(5, (int)(usage.Details.OutputTokenCount ?? 0));
            Assert.AreEqual(8, (int)(usage.Details.TotalTokenCount ?? 0));
            Assert.AreSame(updates.Last(),
                updates.Single(u => (u.Contents ?? new List<MEAI.AIContent>()).OfType<MEAI.UsageContent>().Any()),
                "Usage must ride the trailing update, mirroring OpenAI's final include_usage chunk.");
        }

        [Test]
        public void FullResponseToSimulatedStreamingUpdates_ReEmitsReasoningContent()
        {
            MEAI.ChatResponse full = new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent>
                {
                    new MEAI.TextReasoningContent("hidden plan"),
                    new MEAI.TextContent("visible answer")
                }));

            List<MEAI.ChatResponseUpdate> updates =
                MeaiOpenAiChatClient.FullResponseToSimulatedStreamingUpdatesForTests(full);

            Assert.AreEqual("visible answer", string.Concat(updates.Select(u => u.Text ?? "")));
            Assert.AreEqual("hidden plan", updates
                .SelectMany(u => u.Contents ?? new List<MEAI.AIContent>())
                .OfType<MEAI.TextReasoningContent>()
                .Single().Text);
        }

        [Test]
        public void FullResponseToSimulatedStreamingUpdates_PropagatesNativeContracts()
        {
            MEAI.ChatMessage msg = new(MEAI.ChatRole.Assistant, "hi")
            {
                MessageId = "msg-1"
            };
            MEAI.ChatResponse full = new(msg)
            {
                ResponseId = "resp-1",
                ModelId = "served-model",
                FinishReason = MEAI.ChatFinishReason.Length,
                Usage = new MEAI.UsageDetails
                {
                    InputTokenCount = 3,
                    OutputTokenCount = 5,
                    TotalTokenCount = 8,
                    AdditionalCounts = new MEAI.AdditionalPropertiesDictionary<long>
                    {
                        ["prompt_tokens_details.cached_tokens"] = 1,
                        ["completion_tokens_details.reasoning_tokens"] = 2
                    }
                }
            };

            List<MEAI.ChatResponseUpdate> updates =
                MeaiOpenAiChatClient.FullResponseToSimulatedStreamingUpdatesForTests(full);

            Assert.Greater(updates.Count, 1);
            foreach (MEAI.ChatResponseUpdate u in updates)
            {
                Assert.AreEqual("resp-1", u.ResponseId);
                Assert.AreEqual("msg-1", u.MessageId);
                Assert.AreEqual("served-model", u.ModelId);

            }

            Assert.AreEqual(MEAI.ChatFinishReason.Length, updates.Last().FinishReason);
            Assert.IsTrue(updates.Take(updates.Count - 1).All(update => !update.FinishReason.HasValue));

            MEAI.UsageContent usage = updates
                .SelectMany(u => u.Contents ?? new List<MEAI.AIContent>())
                .OfType<MEAI.UsageContent>()
                .Single();
            Assert.AreEqual(1L, usage.Details.AdditionalCounts["prompt_tokens_details.cached_tokens"]);
            Assert.AreEqual(2L, usage.Details.AdditionalCounts["completion_tokens_details.reasoning_tokens"]);
        }

        [Test]
        public void ParseSseUpdates_WireIdSharedAcrossChunks_BackfillsIdLessChunk()
        {
            const string sse =
                "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\n";
            List<MEAI.ChatResponseUpdate> list = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("chatcmpl-1", list[0].ResponseId);
            Assert.AreEqual("chatcmpl-1", list[0].MessageId);
            Assert.AreEqual("chatcmpl-1", list[1].ResponseId);
            Assert.AreEqual("chatcmpl-1", list[1].MessageId);
        }

        [Test]
        public void ParseSseUpdates_ChangedWireIdWithinAttempt_KeepsOriginalMessageIdentity()
        {
            const string sse =
                "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n" +
                "data: {\"id\":\"chatcmpl-2\",\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\n";
            List<MEAI.ChatResponseUpdate> list = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("chatcmpl-1", list[0].MessageId);
            Assert.AreEqual("chatcmpl-1", list[1].MessageId);
        }

        [Test]
        public void ParseSseUpdates_LateWireId_DoesNotInventAnotherAssistantMessage()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n" +
                "data: {\"id\":\"wire-id\",\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\n";
            List<MEAI.ChatResponseUpdate> updates = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(updates[0].MessageId, updates[1].MessageId);
            Assert.AreEqual("wire-id", updates[1].ResponseId);
            Assert.AreEqual("ab", string.Concat(updates.Select(update => update.Text)));
        }

        [Test]
        public void ParseSseUpdates_FinishOnlyFrame_PreservesTerminalSignal()
        {
            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.ParseSseDataLineForTests(
                "{\"id\":\"terminal\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}");
            Assert.IsNotNull(update);
            Assert.AreEqual(MEAI.ChatFinishReason.Stop, update.FinishReason);
            Assert.IsEmpty(update.Text);
        }

        [Test]
        public void ParseSseDataLine_IdLessChunk_GetsSyntheticMessageId()
        {
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(
                "{\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}");
            Assert.IsNotNull(u);
            Assert.IsNotEmpty(u.ResponseId);
            StringAssert.StartsWith("synth_", u.ResponseId);
            Assert.AreEqual(u.ResponseId, u.MessageId);
        }

        [Test]
        public void ParseSseDataLine_FinishReasonChunk_MapsToNativeContract()
        {
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(
                "{\"id\":\"chatcmpl-f\",\"choices\":[{\"finish_reason\":\"tool_calls\",\"delta\":{\"content\":\"done\"}}]}");
            Assert.IsNotNull(u);
            Assert.AreEqual("done", u.Text);
            Assert.AreEqual(MEAI.ChatFinishReason.ToolCalls, u.FinishReason);
            Assert.AreEqual("chatcmpl-f", u.MessageId);
        }

        [Test]
        public void DrainPerChunk_WireId_PropagatesToDrainedToolCallUpdate()
        {
            string[] chunks =
            {
                "{\"id\":\"chatcmpl-t\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"go\",\"arguments\":\"{\\\"a\\\":1}\"}}]}}]}"
            };
            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);
            Assert.IsNotNull(perChunk[0]);
            Assert.AreEqual("chatcmpl-t", perChunk[0].ResponseId);
            Assert.AreEqual("chatcmpl-t", perChunk[0].MessageId);
            Assert.IsNull(perChunk[1]);
        }

        [Test]
        public void ParseSseDataLine_TextAndUsageTogether_PreservesBoth()
        {
            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.ParseSseDataLineForTests(
                "{\"choices\":[{\"delta\":{\"content\":\"answer\"}}],\"usage\":{\"prompt_tokens_details\":{\"cached_tokens\":7}}}");
            Assert.AreEqual("answer", update.Text);
            MEAI.UsageContent usage = update.Contents.OfType<MEAI.UsageContent>().Single();
            Assert.AreEqual(7L, usage.Details.AdditionalCounts["prompt_tokens_details.cached_tokens"]);
        }

        [Test]
        public void ParseSseUpdates_UsageChunkWithDetails_PopulatesAdditionalCounts()
        {
            const string sse =
                "data: {\"id\":\"chatcmpl-u\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":7,\"total_tokens\":17," +
                "\"prompt_tokens_details\":{\"cached_tokens\":4},\"completion_tokens_details\":{\"reasoning_tokens\":6}}}\n\n";
            List<MEAI.ChatResponseUpdate> updates = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(1, updates.Count);
            Assert.AreEqual("chatcmpl-u", updates[0].ResponseId);
            Assert.AreEqual("chatcmpl-u", updates[0].MessageId);
            MEAI.UsageContent usage = updates[0].Contents?.OfType<MEAI.UsageContent>().FirstOrDefault();
            Assert.NotNull(usage);
            Assert.AreEqual(4L, usage.Details.AdditionalCounts["prompt_tokens_details.cached_tokens"]);
            Assert.AreEqual(6L, usage.Details.AdditionalCounts["completion_tokens_details.reasoning_tokens"]);
            Assert.AreEqual(17L, usage.Details.TotalTokenCount);
        }

        /// <summary>
        /// A provider delta reaches the consumer WHOLE — one SSE event in, one update out.
        /// <para>
        /// This replaces a test that asserted the opposite. A text delta over 24 characters used to be
        /// re-cut into word-sized pieces spaced 15 ms apart, so that a provider batching many tokens
        /// into one event would "look" like per-token streaming. That is an illusion built on the
        /// transport: it puts an artificial delay on the critical path, allocates per manufactured
        /// piece, and hands the consumer a stream whose shape never existed. Streaming means the
        /// consumer sees what arrived, when it arrived — a provider that batches is a provider that
        /// batches, and hiding it here hides it from whoever would otherwise fix it. This test exists
        /// so the smoothing cannot come back unnoticed.
        /// </para>
        /// </summary>
        [Test]
        public async Task GetStreamingResponseAsync_LargeTextDelta_ReachesTheConsumerWhole()
        {
            const string text = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJ";
            const string sse =
                "data: {\"id\":\"chatcmpl-s\",\"model\":\"router/smooth\",\"choices\":[{\"delta\":{\"content\":\"" + text + "\"}}]}\n\n" +
                "data: [DONE]\n\n";
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), new DoneSentinelTransport(sse));
            List<MEAI.ChatResponseUpdate> textUpdates = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                                new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    textUpdates.Add(update);
                }
            }

            Assert.AreEqual(1, textUpdates.Count,
                "One provider delta must produce exactly one update: re-cutting it is manufactured streaming.");
            Assert.AreEqual(text, textUpdates[0].Text);
            Assert.AreEqual("router/smooth", textUpdates[0].ModelId);
            Assert.AreEqual("chatcmpl-s", textUpdates[0].ResponseId);
            Assert.AreEqual("chatcmpl-s", textUpdates[0].MessageId);
        }

        [Test]
        public async Task GetStreamingResponseAsync_EmptyStreamRepeated_FallsBackToNonStreamingCompletion()
        {
            // An SSE 200 that carries only keep-alive comments (upstream rate limit hidden behind a
            // proxy) must NOT burn the full 10-attempt backoff budget: after a few empty streams the
            // turn falls back to ONE plain completion and the user still gets the answer.
            EmptyStreamThenNonStreamingTransport transport = new();
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("fallback answer", string.Concat(parts),
                "The non-streaming fallback answer must surface through the streaming iterator.");
            Assert.AreEqual(3, transport.StreamOpens,
                "An empty stream must retry only emptyStreamMaxAttempts (3) times.");
            Assert.AreEqual(1, transport.NonStreamingCalls,
                "After the empty-stream retries exactly one non-streaming completion runs.");
        }

        [Test]
        public async Task GetStreamingResponseAsync_PartialEof_PreservesPartialAndFailsWithoutFallback()
        {
            AsyncChunkedSseTransport transport = new(new[] { "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n" });
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            List<string> parts = new();

            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(async () =>
            {
                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                                   new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
                    parts.Add(update.Text);
            });

            Assert.AreEqual("partial", string.Concat(parts));
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, failure.ErrorCode);
        }

        [Test]
        public async Task GetStreamingResponseAsync_FinishReasonWithoutDone_IsTerminalCompletion()
        {
            AsyncChunkedSseTransport transport = new(new[] { "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\n" });
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
                parts.Add(update.Text);

            Assert.AreEqual("answer", string.Concat(parts));
        }

        [Test]
        public void GetStreamingResponseAsync_ErrorFrame_IsTypedAndRedacted()
        {
            ProgressOnlyTransport transport = new("data: {\"error\":{\"code\":502,\"message\":\"secret-provider-token\"}}\n\n");
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);

            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(async () =>
            {
                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                                   new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") })) { }
            });

            StringAssert.DoesNotContain("secret-provider-token", failure.ToString());
            Assert.IsEmpty(failure.ProviderErrorBody);
            Assert.AreEqual(502, failure.HttpStatus);
            Assert.AreEqual(1, transport.StreamOpens);
        }

        [Test]
        public async Task GetStreamingResponseAsync_ServerManagedKeepAlive_UsesConfiguredDeadline()
        {
            {
                AsyncChunkedSseTransport transport = new(new[] { ": keep-alive\n\n", "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\ndata: [DONE]\n\n" });
                DoneSentinelSettings settings = new() { ExecutionMode = LlmExecutionMode.ServerManagedApi };
                MeaiOpenAiChatClient client = new(settings, transport);
                List<string> parts = new();

                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                                   new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
                    parts.Add(update.Text);

                Assert.AreEqual("answer", string.Concat(parts), "A managed endpoint may legitimately omit reasoning progress frames.");
            }
        }

        [Test]
        public async Task GetStreamingResponseAsync_ProgressOnlyTerminal_FailsWithoutFallback()
        {
            // WHY: Progress proves generation, not a completed answer; it must not trigger a fresh fallback attempt.
            {
                const string sse =
                    "data: {\"generation_progress\":{\"phase\":\"processing\"},\"choices\":[]}\n\n" +
                    "data: {\"generation_progress\":{\"phase\":\"processing\"},\"choices\":[]}\n\n" +
                    "data: [DONE]\n\n";
                ProgressOnlyTransport transport = new(sse);
                MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
                List<MEAI.ChatResponseUpdate> updates = new();

                LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(async () =>
                {
                    await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                                       new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
                        updates.Add(update);
                });
                Assert.AreEqual(LlmErrorCode.EmptyResponse, failure.ErrorCode);

                Assert.GreaterOrEqual(updates.Count, 1,
                    "Progress frames must surface as parsed deltas, not vanish.");
                Assert.IsTrue(updates.All(u => string.IsNullOrEmpty(u.Text)),
                    "Progress carries no visible text.");
                Assert.IsTrue(updates.All(u =>
                    !(u.Contents ?? new List<MEAI.AIContent>()).OfType<MEAI.TextReasoningContent>().Any()),
                    "Progress carries no reasoning.");
                Assert.AreEqual(1, transport.StreamOpens,
                    "Progress cannot silently complete an empty answer or trigger a mixed fallback.");
            }
        }

        [Test]
        public async Task GetStreamingResponseAsync_RateLimited429Once_RetriesAndCompletes()
        {
            // Free-tier providers (OpenRouter :free and similar) reject bursts with 429 routinely;
            // the next window usually accepts. The single bounded retry must absorb that instead of
            // surfacing "Error: HTTP error 429" to the player on the first hit.
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
                "data: [DONE]\n\n";
            RateLimit429ThenSseTransport transport = new(1, sse);
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("ok", string.Concat(parts),
                "The stream must complete after a 429 absorbed by the rate-limit retry.");
            Assert.AreEqual(2, transport.StreamOpens,
                "One 429 must consume exactly the single retry (2 opens total).");
        }

        [Test]
        public async Task GetStreamingResponseAsync_RateLimitedPersists_FallsBackToNonStreaming()
        {
            // Request → retry → FALLBACK: when the stream keeps 429-ing after the retry budget,
            // the turn must try ONE plain completion before surfacing any error.
            RateLimit429ThenSseTransport transport = new(99, "data: [DONE]\n\n")
            {
                NonStreamingBody =
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"fallback answer\"}}]}"
            };
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("fallback answer", string.Concat(parts),
                "Persistent 429 on the stream must be rescued by the non-streaming fallback.");
            Assert.AreEqual(2, transport.StreamOpens,
                "1 initial attempt + 1 retry before the fallback.");
            Assert.AreEqual(1, transport.NonStreamingCalls,
                "Exactly one non-streaming fallback request.");
        }

        [Test]
        public async Task GetStreamingResponseAsync_RateLimited429Exhausted_ThrowsRateLimited()
        {
            // Stream 429s twice (initial + retry) AND the fallback completion 429s too:
            // only now the typed RateLimited error surfaces, with no hidden extra rounds.
            // (Deliberately NOT Assert.ThrowsAsync: its sync-over-async wait can deadlock EditMode.)
            RateLimit429ThenSseTransport transport = new(99, "data: [DONE]\n\n")
            {
                NonStreamingBody = null // non-streaming endpoint also answers 429
            };
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);

            LlmClientException caught = null;
            try
            {
                await foreach (MEAI.ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                                   new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
                {
                }
            }
            catch (LlmClientException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught, "Exhausted retries + failed fallback must surface a typed LlmClientException.");
            Assert.AreEqual(LlmErrorCode.RateLimited, caught.ErrorCode);
            Assert.AreEqual(2, transport.StreamOpens,
                "1 initial attempt + 1 retry on the stream.");
            Assert.AreEqual(1, transport.NonStreamingCalls,
                "The fallback completion runs exactly once (zero extra retries), then the error surfaces.");
        }

        [Test]
        public async Task GetStreamingResponseAsync_TransportSendFailsOnce_RetriesAndCompletes()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n" +
                "data: [DONE]\n\n";
            FailSendOnceSseTransport transport = new(sse);
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("hello", string.Concat(parts),
                "The stream must complete after retrying a transient transport send failure on open.");
            Assert.AreEqual(2, transport.Calls,
                "A transport send failure on stream-open must trigger exactly one retry (2 opens total).");
        }

        [Test]
        public async Task GetStreamingResponseAsync_SlowConsumerBetweenUpdates_DoesNotTripStallTimeout()
        {
            // The streaming iterator is pull-based: the consumer (MeaiLlmClient) executes tool
            // calls (up to tens of seconds) between MoveNexts. Here the wall time between SSE
            // lines exceeds the 1s transport timeout ONLY because of consumer-side delay, which
            // must not count against the stall budget - the stream is healthy.
            string[] chunks =
            {
                "data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}\n\n",
                "data: {\"choices\":[{\"delta\":{\"content\":\"B\"}}]}\n\n",
                "data: [DONE]\n\n"
            };
            OpenAiHttpOptions settings = OpenAiHttpOptions.From(new ShortTimeoutSettings());
            settings.RequestTimeoutSeconds = 0;
            settings.ResponseWaitTimeoutSeconds = 1;
            MeaiOpenAiChatClient client = new(settings, new AsyncChunkedSseTransport(chunks));
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                    await Task.Delay(1200); // simulated slow tool execution between MoveNexts
                }
            }

            Assert.AreEqual("AB", string.Concat(parts),
                "A healthy stream with a slow consumer must complete without 'LLM SSE stalled'.");
        }

        [Test]
        public void AccumulateToolCallDeltas_ArgumentsSplitAcrossChunks_Reassemble()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"search\",\"arguments\":\"{\\\"qu\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"ery\\\":\\\"cat\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"s\\\"}\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            MEAI.FunctionCallContent call = update.Contents.OfType<MEAI.FunctionCallContent>().Single();
            Assert.AreEqual("search", call.Name);
            Assert.AreEqual("call_1", call.CallId);
            Assert.AreEqual("cats", call.Arguments["query"]);
            Assert.IsFalse(call.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests));
        }

        [Test]
        public void AccumulateToolCallDeltas_NameInFirstChunk_ArgsInLaterChunks_Reassemble()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_2\",\"function\":{\"name\":\"add\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"a\\\":1,\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"b\\\":2}\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            MEAI.FunctionCallContent call = update.Contents.OfType<MEAI.FunctionCallContent>().Single();
            Assert.AreEqual("add", call.Name);
            Assert.AreEqual("call_2", call.CallId);
            Assert.AreEqual(1L, Convert.ToInt64(call.Arguments["a"]));
            Assert.AreEqual(2L, Convert.ToInt64(call.Arguments["b"]));
        }

        [Test]
        public void AccumulateToolCallDeltas_TwoParallelCalls_BothMaterialize()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_b\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"1}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"function\":{\"arguments\":\"2}\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            List<MEAI.FunctionCallContent> calls =
                update.Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count);

            MEAI.FunctionCallContent alpha = calls.Single(c => c.Name == "alpha");
            Assert.AreEqual("call_a", alpha.CallId);
            Assert.AreEqual(1L, Convert.ToInt64(alpha.Arguments["x"]));

            MEAI.FunctionCallContent beta = calls.Single(c => c.Name == "beta");
            Assert.AreEqual("call_b", beta.CallId);
            Assert.AreEqual(2L, Convert.ToInt64(beta.Arguments["y"]));
        }

        [Test]
        public void AccumulateToolCallDeltas_ReusedIndexWithDifferentId_DoesNotMergeCalls()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":1}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_b\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":2}\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            List<MEAI.FunctionCallContent> calls = update.Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count);

            MEAI.FunctionCallContent alpha = calls.Single(c => c.CallId == "call_a");
            Assert.AreEqual("alpha", alpha.Name);
            Assert.AreEqual(1L, Convert.ToInt64(alpha.Arguments["x"]));

            MEAI.FunctionCallContent beta = calls.Single(c => c.CallId == "call_b");
            Assert.AreEqual("beta", beta.Name);
            Assert.AreEqual(2L, Convert.ToInt64(beta.Arguments["y"]));
        }

        [Test]
        public void AccumulateToolCallDeltas_MissingIndexWhileAllPendingComplete_DropsFragmentKeepsCalls()
        {
            // Both pending calls already hold COMPLETE argument JSON, so neither can own the
            // id/index-less fragment: it is dropped, and both calls must survive UNPOISONED
            // (previously ALL pending calls were force-failed, breaking parallel tool calling).
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":1}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_b\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":2}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"function\":{\"arguments\":\"unowned\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            List<MEAI.FunctionCallContent> calls = update.Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count);
            Assert.IsFalse(calls.Any(c => c.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests)),
                "Calls with complete arguments cannot own the lost fragment and must not be poisoned.");
            Assert.AreEqual(1L, Convert.ToInt64(calls.Single(c => c.Name == "alpha").Arguments["x"]));
            Assert.AreEqual(2L, Convert.ToInt64(calls.Single(c => c.Name == "beta").Arguments["y"]));
        }

        [Test]
        public void AccumulateToolCallDeltas_MissingIndexWithSoleOpenCall_AttributesFragmentToIt()
        {
            // Exactly one pending call still has OPEN argument JSON: the id/index-less fragment can
            // only belong to it, so it must be attributed there and both calls must materialize clean.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":1}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_b\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"function\":{\"arguments\":\"2}\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            List<MEAI.FunctionCallContent> calls = update.Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count);
            Assert.IsFalse(calls.Any(c => c.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests)),
                "An unambiguous index-less fragment must be attributed, not poison the pending calls.");
            Assert.AreEqual(1L, Convert.ToInt64(calls.Single(c => c.Name == "alpha").Arguments["x"]));
            Assert.AreEqual(2L, Convert.ToInt64(calls.Single(c => c.Name == "beta").Arguments["y"]),
                "The fragment must complete the sole open call's arguments.");
        }

        [Test]
        public void AccumulateToolCallDeltas_MissingIndexWithMultipleOpenCalls_PoisonsOnlyOpenOnes()
        {
            // Two calls open, one complete: attribution is genuinely ambiguous between the OPEN
            // calls only - they get parse-error markers, the completed call must survive clean.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":1}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_b\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":2,\"id\":\"call_c\",\"function\":{\"name\":\"gamma\",\"arguments\":\"{\\\"z\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"function\":{\"arguments\":\"unowned\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            List<MEAI.FunctionCallContent> calls = update.Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(3, calls.Count);
            MEAI.FunctionCallContent alpha = calls.Single(c => c.Name == "alpha");
            Assert.IsFalse(alpha.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests),
                "The completed call cannot own the fragment and must not be poisoned.");
            Assert.AreEqual(1L, Convert.ToInt64(alpha.Arguments["x"]));
            Assert.IsTrue(calls.Where(c => c.Name != "alpha").All(c =>
                    c.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests)),
                "Both still-open calls are genuinely ambiguous owners and must surface parse errors.");
        }

        [Test]
        public void AccumulateToolCallDeltas_MalformedJson_SurfacesRawArgsNotSilentlyEmpty()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_3\",\"function\":{\"name\":\"broken\",\"arguments\":\"{\\\"q\\\":\\\"unclo\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"sed\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            MEAI.FunctionCallContent call = update.Contents.OfType<MEAI.FunctionCallContent>().Single();
            Assert.AreEqual("broken", call.Name);
            Assert.IsTrue(
                call.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests),
                "Malformed arguments must surface a parse-error marker rather than silently empty args.");
            Assert.AreEqual(
                "{\"q\":\"unclosed",
                call.Arguments[MeaiOpenAiChatClient.ToolCallRawArgumentsKeyForTests]);
        }

        // --- Execute-as-you-stream: DrainCompleted must surface each call the moment its JSON closes ---

        [Test]
        public void DrainCompleted_WholeCallPerChunk_SurfacesImmediately()
        {
            // A bridge/provider that sends one COMPLETE call per SSE chunk: each call must drain on
            // its own chunk, not wait for the final flush.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{\\\"n\\\":\\\"t1\\\"}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"c2\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{\\\"n\\\":\\\"t2\\\"}\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.AreEqual(3, perChunk.Count); // one per chunk + final flush
            Assert.IsNotNull(perChunk[0], "First complete call must drain on its own chunk.");
            Assert.AreEqual("t1",
                perChunk[0].Contents.OfType<MEAI.FunctionCallContent>().Single().Arguments["n"]);
            Assert.IsNotNull(perChunk[1], "Second complete call must drain on its own chunk.");
            Assert.AreEqual("t2",
                perChunk[1].Contents.OfType<MEAI.FunctionCallContent>().Single().Arguments["n"]);
            Assert.IsNull(perChunk[2], "Nothing must be left for the final flush.");
        }

        [Test]
        public void DrainCompleted_FragmentedArgs_DrainsOnlyWhenJsonCloses()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"search\",\"arguments\":\"{\\\"qu\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"ery\\\":\\\"cat\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"s\\\"}\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNull(perChunk[0], "Args still open - must not drain.");
            Assert.IsNull(perChunk[1], "Args still open - must not drain.");
            Assert.IsNotNull(perChunk[2], "Args JSON closed - must drain NOW, not at flush.");
            Assert.AreEqual("cats",
                perChunk[2].Contents.OfType<MEAI.FunctionCallContent>().Single().Arguments["query"]);
            Assert.IsNull(perChunk[3], "Nothing must be left for the final flush.");
        }

        [Test]
        public void DrainCompleted_InterleavedCalls_EarlierOpenCallBlocksLaterClosedCall()
        {
            // Contiguous-prefix contract: a later-indexed call whose JSON closes first must NOT
            // drain (and execute) before an earlier-indexed call that is still open - dependent
            // pairs (create -> configure) rely on provider index order across chunks.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"ca\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"cb\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":2}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"1}\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNull(perChunk[0], "alpha still open.");
            Assert.IsNull(perChunk[1],
                "beta is ready but index 0 (alpha) is still open - beta must stay blocked.");
            Assert.IsNotNull(perChunk[2], "alpha closed - the whole ready prefix drains now.");
            List<MEAI.FunctionCallContent> calls =
                perChunk[2].Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count, "alpha and beta must drain together once alpha closes.");
            Assert.AreEqual("alpha", calls[0].Name, "Drained calls must surface in index order.");
            Assert.AreEqual("beta", calls[1].Name, "Drained calls must surface in index order.");
            Assert.IsNull(perChunk[3], "Nothing must be left for the final flush.");
        }

        [Test]
        public void DrainCompleted_LaterIndexClosesFirst_NothingDrainsUntilPrefixReady()
        {
            // Both calls fragmented; index 1 closes before index 0. Nothing may drain until the
            // earlier index closes, then both drain in ONE update, in index order.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"ca\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"cb\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"function\":{\"arguments\":\"2}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"1}\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNull(perChunk[0], "alpha still open.");
            Assert.IsNull(perChunk[1], "both still open.");
            Assert.IsNull(perChunk[2],
                "beta closed but alpha (earlier index) is still open - nothing drains.");
            Assert.IsNotNull(perChunk[3], "alpha closed - both drain in one update.");
            List<MEAI.FunctionCallContent> calls =
                perChunk[3].Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count);
            Assert.AreEqual("alpha", calls[0].Name, "Index order must be preserved.");
            Assert.AreEqual("beta", calls[1].Name, "Index order must be preserved.");
            Assert.IsNull(perChunk[4], "Nothing must be left for the final flush.");
        }

        [Test]
        public void DrainCompleted_CumulativeResendAfterDrain_IsIgnored()
        {
            // Known OpenAI-compat server misbehavior: after a call's arguments closed (and the
            // call drained and EXECUTED), the provider re-sends the full cumulative argument
            // string. That must not create a fresh pending entry and run the call a second time.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{\\\"n\\\":\\\"t1\\\"}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{\\\"n\\\":\\\"t1\\\"}\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNotNull(perChunk[0], "The call drains once, at its close.");
            Assert.AreEqual("t1",
                perChunk[0].Contents.OfType<MEAI.FunctionCallContent>().Single().Arguments["n"]);
            Assert.IsNull(perChunk[1],
                "Cumulative re-send of a drained call must be ignored, not re-drained.");
            Assert.IsNull(perChunk[2], "Flush must emit nothing extra for the ignored re-send.");
        }

        [Test]
        public void DrainCompleted_TrailingEmptyDeltaForDrainedIndex_NoGhostPending()
        {
            // Trailing empty-arguments delta for an already-drained index must not create a ghost
            // pending entry that lingers into Flush.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{\\\"n\\\":\\\"t1\\\"}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNotNull(perChunk[0], "The call drains at its close.");
            Assert.IsNull(perChunk[1], "Trailing empty delta for a drained index must be ignored.");
            Assert.IsNull(perChunk[2], "Flush must emit nothing - no ghost entry may exist.");
        }

        [Test]
        public void DrainCompleted_MalformedNeverDrains_FlushSurfacesParseError()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"broken\",\"arguments\":\"{\\\"q\\\":\\\"unclo\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"sed\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNull(perChunk[0]);
            Assert.IsNull(perChunk[1], "Unclosed JSON must never drain early.");
            Assert.IsNotNull(perChunk[2], "Flush must still surface the malformed call with markers.");
            MEAI.FunctionCallContent call =
                perChunk[2].Contents.OfType<MEAI.FunctionCallContent>().Single();
            Assert.IsTrue(call.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests));
        }

        [Test]
        public void IsCompleteJsonObject_Detector_CoversStringsEscapesAndTrailingJunk()
        {
            Assert.IsTrue(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("{}"));
            Assert.IsTrue(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("  {\"a\": {\"b\": \"}\"}} \n"));
            Assert.IsTrue(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("{\"a\": \"x\\\"}\"}"));
            Assert.IsFalse(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests(""));
            Assert.IsFalse(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("{\"a\": 1"));
            Assert.IsFalse(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("{\"a\": \"un"));
            Assert.IsFalse(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("{} extra"));
            Assert.IsFalse(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("[1,2]"));
        }

        private sealed class DoneSentinelTransport : IOpenAiHttpTransport
        {
            private readonly string _sse;

            public DoneSentinelTransport(string sse)
            {
                _sse = sse;
            }

            public string DebugLabel => "DoneSentinel";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };

                return Task.FromResult(result.WithRawStream(new ThrowsAfterPayloadStream(_sse)));
            }
        }

        /// <summary>
        /// Streams always contain ONLY keep-alive comments (a starved upstream behind a proxy: SSE 200,
        /// zero data lines), while the plain completion endpoint answers normally — models the
        /// empty-stream → non-streaming fallback.
        /// </summary>
        private sealed class EmptyStreamThenNonStreamingTransport : IOpenAiHttpTransport
        {
            public int StreamOpens;
            public int NonStreamingCalls;

            public string DebugLabel => "EmptyStreamFallback";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                NonStreamingCalls++;
                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText =
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"fallback answer\"}}]}",
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>()
                });
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                StreamOpens++;
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };

                // Only comment keep-alives and the DONE sentinel — zero parsed deltas.
                return Task.FromResult(result.WithRawStream(
                    new ThrowsAfterPayloadStream(": keep-alive\n\n: keep-alive\n\ndata: [DONE]\n\n")));
            }
        }

        /// <summary>
        /// Streams a fixed SSE payload once, then closes. The plain completion endpoint throws:
        /// any empty-stream retry or non-streaming fallback surfaces immediately as a failure.
        /// </summary>
        private sealed class ProgressOnlyTransport : IOpenAiHttpTransport
        {
            private readonly string _sse;

            public ProgressOnlyTransport(string sse)
            {
                _sse = sse;
            }

            public int StreamOpens { get; private set; }
            public string DebugLabel => "ProgressOnly";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("ProgressOnlyTransport: fallback must never run.");
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                StreamOpens++;
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };
                return Task.FromResult(result.WithRawStream(new ThrowsAfterPayloadStream(_sse)));
            }
        }

        /// <summary>
        /// Streams NEVER close and never send a data line — an endless drip of ": keep-alive" SSE
        /// comments (a proxy holding a starved upstream connection open). Only the starved-stream
        /// watchdog can end an attempt. The plain completion endpoint answers normally.
        /// </summary>
        private sealed class EndlessKeepAliveTransport : IOpenAiHttpTransport
        {
            public int StreamOpens;
            public int NonStreamingCalls;

            private readonly string _prefix;
            public EndlessKeepAliveTransport(string prefix = "") { _prefix = prefix; }
            public string DebugLabel => "EndlessKeepAlive";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                NonStreamingCalls++;
                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText =
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"fallback answer\"}}]}",
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>()
                });
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                StreamOpens++;
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };

                return Task.FromResult(result.WithRawStream(new EndlessKeepAliveStream(_prefix)));
            }
        }

        private sealed class EndlessKeepAliveStream : Stream
        {
            private static readonly byte[] KeepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");
            private readonly byte[] _prefix;
            private int _prefixOffset;
            public EndlessKeepAliveStream(string prefix) { _prefix = Encoding.UTF8.GetBytes(prefix); }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return Read(buffer, offset, count);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_prefixOffset < _prefix.Length)
                {
                    int prefixCount = Math.Min(count, _prefix.Length - _prefixOffset);
                    Array.Copy(_prefix, _prefixOffset, buffer, offset, prefixCount);
                    _prefixOffset += prefixCount;
                    return prefixCount;
                }
                int toCopy = Math.Min(count, KeepAlive.Length);
                Array.Copy(KeepAlive, 0, buffer, offset, toCopy);
                return toCopy;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>
        /// Rejects the first N stream-opens with HTTP 429 (Retry-After: 0.001 so tests stay fast),
        /// then serves a normal SSE stream — models free-tier burst rate limiting.
        /// </summary>
        private sealed class RateLimit429ThenSseTransport : IOpenAiHttpTransport
        {
            private readonly int _failures;
            private readonly string _sse;

            public RateLimit429ThenSseTransport(int failures, string sse)
            {
                _failures = failures;
                _sse = sse;
            }

            public int StreamOpens { get; private set; }
            public int NonStreamingCalls { get; private set; }

            /// <summary>Non-streaming response body; null = the endpoint answers HTTP 429 as well.</summary>
            public string NonStreamingBody { get; set; }

            public string DebugLabel => "RateLimit429";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                NonStreamingCalls++;
                if (NonStreamingBody == null)
                {
                    return Task.FromResult(new OpenAiHttpPostResult
                    {
                        StatusCode = 429,
                        BodyText = "{\"error\":{\"message\":\"rate-limited upstream\",\"code\":429}}",
                        ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                        {
                            { "Retry-After", new[] { "0.001" } }
                        }
                    });
                }

                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText = NonStreamingBody,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>()
                });
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                StreamOpens++;
                if (StreamOpens <= _failures)
                {
                    OpenAiHttpSseOpenResult limited = new()
                    {
                        StatusCode = 429,
                        ErrorBodyText = "{\"error\":{\"message\":\"rate-limited upstream\",\"code\":429}}",
                        ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                        {
                            { "Retry-After", new[] { "0.001" } },
                            { "Content-Type", new[] { "application/json" } }
                        }
                    };
                    return Task.FromResult(limited);
                }

                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };
                return Task.FromResult(result.WithRawStream(new ThrowsAfterPayloadStream(_sse)));
            }
        }

        /// <summary>
        /// Fails the FIRST stream-open with a transport send error (as a stale pooled keep-alive
        /// connection does), then serves a normal SSE stream — models the bounded transport-send retry.
        /// </summary>
        private sealed class FailSendOnceSseTransport : IOpenAiHttpTransport
        {
            private readonly string _sse;
            private int _calls;

            public FailSendOnceSseTransport(string sse)
            {
                _sse = sse;
            }

            public int Calls => _calls;
            public string DebugLabel => "FailSendOnce";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                _calls++;
                if (_calls == 1)
                {
                    throw new System.Net.Http.HttpRequestException(
                        "An error occurred while sending the request");
                }

                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };

                return Task.FromResult(result.WithRawStream(new ThrowsAfterPayloadStream(_sse)));
            }
        }

        private sealed class AsyncChunkedSseTransport : IOpenAiHttpTransport
        {
            private readonly IReadOnlyList<string> _chunks;
            private readonly int _readDelayMilliseconds;
            private readonly Task _readGate;

            public AsyncChunkedSseTransport(IReadOnlyList<string> chunks, int readDelayMilliseconds = 0, Task readGate = null)
            {
                _chunks = chunks;
                _readDelayMilliseconds = readDelayMilliseconds;
                _readGate = readGate;
            }

            public string DebugLabel => "AsyncChunked";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };

                return Task.FromResult(result.WithRawStream(new AsyncChunkedReadStream(_chunks, _readDelayMilliseconds, _readGate)));
            }
        }

        private sealed class AsyncChunkedReadStream : Stream
        {
            private readonly Queue<byte[]> _chunks;
            private readonly int _readDelayMilliseconds;
            private readonly Task _readGate;

            public AsyncChunkedReadStream(IEnumerable<string> chunks, int readDelayMilliseconds = 0, Task readGate = null)
            {
                _chunks = new Queue<byte[]>(chunks.Select(Encoding.UTF8.GetBytes));
                _readDelayMilliseconds = readDelayMilliseconds;
                _readGate = readGate;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                await Task.Yield();
                if (_readGate != null)
                    await _readGate;
                if (_readDelayMilliseconds > 0)
                    await Task.Delay(_readDelayMilliseconds, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return Read(buffer, offset, count);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_chunks.Count == 0)
                {
                    return 0;
                }

                byte[] chunk = _chunks.Dequeue();
                int toCopy = Math.Min(count, chunk.Length);
                Array.Copy(chunk, 0, buffer, offset, toCopy);
                return toCopy;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class ThrowsAfterPayloadStream : Stream
        {
            private readonly byte[] _payload;
            private int _position;

            public ThrowsAfterPayloadStream(string payload)
            {
                _payload = Encoding.UTF8.GetBytes(payload);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _payload.Length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _payload.Length)
                {
                    throw new AssertionException("Client should stop on data: [DONE] without waiting for stream EOF.");
                }

                int toCopy = Math.Min(count, _payload.Length - _position);
                Array.Copy(_payload, _position, buffer, offset, toCopy);
                _position += toCopy;
                return toCopy;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        [Test]
        public void BuildMessagesPayload_ImageOnlyUserMessage_SendsOnlyTheImagePart()
        {
            // An image-only message has no TextContent; the wire must not carry the stringified
            // "Microsoft.Extensions.AI.DataContent" type name as a text part next to the image
            // (observed in the WebGL player on 2026-09-30).
            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            List<MEAI.ChatMessage> msgs = new()
            {
                AiUserMessageBuilder.BuildUserMessage(
                    "", new List<AiAttachment> { AiAttachment.Image(png, "image/png", "a.png") }),
                AiUserMessageBuilder.BuildUserMessage(
                    "describe", new List<AiAttachment> { AiAttachment.Image(png, "image/png", "b.png") })
            };

            List<Dictionary<string, object>> payload =
                MeaiOpenAiChatClient.BuildMessagesPayloadForTests(msgs);

            string imageOnly = JToken.FromObject(payload[0]["content"]).ToString();
            StringAssert.DoesNotContain("DataContent", imageOnly);
            JArray imageOnlyParts = JArray.Parse(imageOnly);
            Assert.AreEqual(1, imageOnlyParts.Count, imageOnly);
            Assert.AreEqual("image_url", (string)imageOnlyParts[0]["type"]);

            JArray withText = JArray.FromObject(payload[1]["content"]);
            Assert.AreEqual(2, withText.Count, withText.ToString());
            Assert.AreEqual("describe", (string)withText[0]["text"]);
            Assert.AreEqual("image_url", (string)withText[1]["type"]);
        }

        [Test]
        public void BuildMessagesPayload_ToolMessageWithMultipleResults_EmitsOneWireMessagePerResult()
        {
            // A Tool-role MEAI message carrying the whole turn's results must expand to one
            // OpenAI tool message per tool_call_id — serializing only the first result made
            // models re-issue the "unanswered" calls every round-trip (5 spawns became 15).
            List<MEAI.ChatMessage> msgs = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionCallContent("call_1", "world_command",
                        new Dictionary<string, object?> { ["action"] = "spawn" }),
                    new MEAI.FunctionCallContent("call_2", "world_command",
                        new Dictionary<string, object?> { ["action"] = "spawn" }),
                    new MEAI.FunctionCallContent("call_3", "world_command",
                        new Dictionary<string, object?> { ["action"] = "spawn" })
                }),
                new MEAI.ChatMessage(MEAI.ChatRole.Tool, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionResultContent("call_1", "ok-1"),
                    new MEAI.FunctionResultContent("call_2", "ok-2"),
                    new MEAI.FunctionResultContent("call_3", "ok-3")
                })
            };

            List<Dictionary<string, object>> payload =
                MeaiOpenAiChatClient.BuildMessagesPayloadForTests(msgs);

            List<Dictionary<string, object>> toolMessages =
                payload.Where(m => (string)m["role"] == "tool").ToList();
            Assert.AreEqual(3, toolMessages.Count,
                "Every FunctionResultContent must become its own tool-role wire message.");
            CollectionAssert.AreEqual(
                new[] { "call_1", "call_2", "call_3" },
                toolMessages.Select(m => (string)m["tool_call_id"]).ToArray());
            CollectionAssert.AreEqual(
                new[] { "ok-1", "ok-2", "ok-3" },
                toolMessages.Select(m => (string)m["content"]).ToArray());

            Dictionary<string, object> assistant =
                payload.First(m => (string)m["role"] == "assistant");
            Assert.AreEqual(3,
                ((List<Dictionary<string, object>>)assistant["tool_calls"]).Count,
                "All three calls must survive in the assistant message.");
        }

        [Test]
        public void BuildMessagesPayload_MissingCallId_UsesSameDeterministicIdOnEchoAndReply()
        {
            // F8: a call without a provider id must NOT get a random Guid in the assistant echo
            // while its tool reply omits tool_call_id - both sides must share one deterministic
            // synthetic id, or the model sees an unanswered call and re-issues it.
            List<MEAI.ChatMessage> msgs = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionCallContent("call_real", "world_command",
                        new Dictionary<string, object?> { ["action"] = "spawn" }),
                    new MEAI.FunctionCallContent("", "world_command",
                        new Dictionary<string, object?> { ["action"] = "move" })
                }),
                new MEAI.ChatMessage(MEAI.ChatRole.Tool, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionResultContent("call_real", "ok-real"),
                    new MEAI.FunctionResultContent("", "ok-synth")
                })
            };

            List<Dictionary<string, object>> payload =
                MeaiOpenAiChatClient.BuildMessagesPayloadForTests(msgs);

            Dictionary<string, object> assistant = payload.First(m => (string)m["role"] == "assistant");
            List<Dictionary<string, object>> toolCalls =
                (List<Dictionary<string, object>>)assistant["tool_calls"];
            Assert.AreEqual("call_real", (string)toolCalls[0]["id"]);
            string syntheticId = (string)toolCalls[1]["id"];
            Assert.IsNotEmpty(syntheticId, "The id-less call must still get an id in the echo");
            StringAssert.StartsWith("synth_", syntheticId, "Synthetic ids are deterministic, never random");

            List<Dictionary<string, object>> toolMessages =
                payload.Where(m => (string)m["role"] == "tool").ToList();
            Assert.AreEqual(2, toolMessages.Count);
            Assert.AreEqual("call_real", (string)toolMessages[0]["tool_call_id"]);
            Assert.IsTrue(toolMessages[1].ContainsKey("tool_call_id"),
                "The reply to the id-less call must carry the synthetic id, not omit it");
            Assert.AreEqual(syntheticId, (string)toolMessages[1]["tool_call_id"],
                "Echo and reply must use the SAME synthetic id");

            // Determinism: serializing the same history again yields the same synthetic id.
            List<Dictionary<string, object>> payloadAgain =
                MeaiOpenAiChatClient.BuildMessagesPayloadForTests(msgs);
            Dictionary<string, object> assistantAgain =
                payloadAgain.First(m => (string)m["role"] == "assistant");
            Assert.AreEqual(syntheticId,
                (string)((List<Dictionary<string, object>>)assistantAgain["tool_calls"])[1]["id"],
                "Synthetic ids must be stable across serializations of the same history");
        }

        [Test]
        public void BuildMessagesPayload_ParseErrorCall_EchoesRawArgumentsString()
        {
            // F9: a parse-error call's arguments carry internal marker keys; the assistant echo must
            // emit the model's ORIGINAL raw argument string, never the marker dictionary.
            const string rawArgs = "{\"action\":\"spawn\",\"prefab\":\"tre"; // truncated mid-stream
            List<MEAI.ChatMessage> msgs = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionCallContent("call_1", "world_command",
                        new Dictionary<string, object?>
                        {
                            [ToolCallArgumentMarkers.RawArgumentsKey] = rawArgs,
                            [ToolCallArgumentMarkers.ParseErrorKey] = true
                        })
                }),
                new MEAI.ChatMessage(MEAI.ChatRole.Tool, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionResultContent("call_1", "Error: arguments JSON was truncated")
                })
            };

            List<Dictionary<string, object>> payload =
                MeaiOpenAiChatClient.BuildMessagesPayloadForTests(msgs);

            Dictionary<string, object> assistant = payload.First(m => (string)m["role"] == "assistant");
            Dictionary<string, object> call =
                ((List<Dictionary<string, object>>)assistant["tool_calls"])[0];
            string arguments = (string)((Dictionary<string, object>)call["function"])["arguments"];
            Assert.AreEqual(rawArgs, arguments, "The raw argument string must be echoed verbatim");
            StringAssert.DoesNotContain(ToolCallArgumentMarkers.RawArgumentsKey, arguments,
                "Internal marker keys must never leak onto the wire");
            StringAssert.DoesNotContain(ToolCallArgumentMarkers.ParseErrorKey, arguments);
        }

        private sealed class DoneSentinelSettings : IOpenAiHttpSettings
        {
            public LlmExecutionMode ExecutionMode { get; set; }
            public string ExtraBodyJson { get; set; } = "";
            public string ApiBaseUrl => "https://example.invalid/v1";
            public string ApiKey => "";
            public string AuthorizationHeader => "";
            public string Model => "dummy";
            public float Temperature => 0f;
            public int RequestTimeoutSeconds => 30;
            public int MaxTokens => 256;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool EnableHttpDebugLogging => false;
            public IRequestHeaderProvider? HeaderProvider => null;
        }

        /// <summary>1-second transport timeout so consumer delays longer than the timeout are cheap to simulate.</summary>
        private sealed class ShortTimeoutSettings : IOpenAiHttpSettings
        {
            public string ApiBaseUrl => "https://example.invalid/v1";
            public string ApiKey => "";
            public string AuthorizationHeader => "";
            public string Model => "dummy";
            public float Temperature => 0f;
            public int RequestTimeoutSeconds => 1;
            public int MaxTokens => 256;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool EnableHttpDebugLogging => false;
            public IRequestHeaderProvider? HeaderProvider => null;
        }
    }
}
#endif
