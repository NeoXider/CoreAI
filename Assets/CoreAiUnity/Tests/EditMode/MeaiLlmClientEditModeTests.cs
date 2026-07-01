using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
#if !COREAI_NO_LLM
    public sealed class MeaiLlmClientEditModeTests
    {
        [Test]
        public void CreateHttp_WithOpenAiSettings_ShouldNotThrow()
        {
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.SetRuntimeConfiguration(true, "http://localhost:1234/v1", "", "test-model");

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings,
                ScriptableObject.CreateInstance<CoreAISettingsAsset>(), logger);

            Assert.IsNotNull(client);
            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public void CreateHttp_WithCoreAiSettings_ShouldNotThrow()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi("http://localhost:1234/v1", "", "test-model");

            OpenAiChatLlmClient client = new(settings);

            Assert.IsNotNull(client);
            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public void CreateLlmUnity_RequiresAgent()
        {
            Exception ex = Assert.Catch<Exception>(() =>
            {
                MeaiLlmClient.CreateLlmUnity(null, GameLoggerUnscopedFallback.Instance,
                    ScriptableObject.CreateInstance<CoreAISettingsAsset>());
            });

#if UNITY_WEBGL || !COREAI_HAS_LLMUNITY
            Assert.That(ex, Is.TypeOf<NotSupportedException>());
#else
            Assert.That(ex, Is.TypeOf<ArgumentNullException>());
#endif
        }

        [Test]
        public void BuildAIFunctions_ShouldCreateMemoryTool()
        {
            OpenAiHttpLlmSettings settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.SetRuntimeConfiguration(true, "http://localhost:1234/v1", "", "test-model");

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            TestMemoryStore memoryStore = new();

            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings,
                ScriptableObject.CreateInstance<CoreAISettingsAsset>(), logger, memoryStore);

            List<ILlmTool> tools = new() { new MemoryLlmTool() };
            client.SetTools(tools);

            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public async Task CompleteAsync_BindsExplicitAIFunctionToolContract()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = new List<ILlmTool> { new ExplicitFunctionTool("explicit_tool") }
            }, CancellationToken.None);

            Assert.IsNotNull(inner.LastOptions);
            Assert.IsNotNull(inner.LastOptions.Tools);
            Assert.That(inner.LastOptions.Tools.Select(t => t.Name), Does.Contain("explicit_tool"));
        }

        [Test]
        public async Task CompleteAsync_NativeTools_AreCanonicalOrdinalByName()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = new List<ILlmTool>
                {
                    new ExplicitFunctionTool("z_tool"),
                    new ExplicitFunctionTool("a_tool")
                }
            }, CancellationToken.None);

            Assert.IsNotNull(inner.LastOptions?.Tools);
            CollectionAssert.AreEqual(
                new[] { "a_tool", "z_tool" },
                inner.LastOptions.Tools.Select(t => t.Name).ToArray());
        }

        [Test]
        public async Task CompleteAsync_DoesNotBindLegacyDuckTypedCreateAIFunctionTool()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = new List<ILlmTool> { new LegacyDuckTypedFunctionTool("legacy_tool") }
            }, CancellationToken.None);

            Assert.IsTrue(inner.LastOptions == null ||
                          inner.LastOptions.Tools == null ||
                          inner.LastOptions.Tools.All(t => t.Name != "legacy_tool"),
                "Tools must opt into IAIFunctionLlmTool/IAIFunctionsLlmTool; CreateAIFunction duck typing should not bind.");
        }

        [Test]
        public async Task CompleteAsync_ReusesIdempotencyKey_OnSameRequestInstance()
        {
            HelloOnceChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi"
            };

            await client.CompleteAsync(request, CancellationToken.None);
            string firstKey = request.IdempotencyKey;
            Assert.IsNotEmpty(firstKey);

            await client.CompleteAsync(request, CancellationToken.None);
            Assert.AreEqual(firstKey, request.IdempotencyKey);
        }

        [Test]
        public async Task CompleteAsync_KeepsCallerProvidedIdempotencyKey()
        {
            HelloOnceChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);
            const string preset = "deadbeefcafebabe1122334455667788";
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi",
                IdempotencyKey = preset
            };

            await client.CompleteAsync(request, CancellationToken.None);
            Assert.AreEqual(preset, request.IdempotencyKey);
        }

        [Test]
        public async Task CompleteAsync_NormalizesTailSystemMessages_ForProviderCompatibility()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);

            await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "root system",
                UserPayload = "current user",
                ChatHistory = new List<MEAI.ChatMessage>
                {
                    new(MEAI.ChatRole.User, "previous user"),
                    new(MEAI.ChatRole.System, "## Memory\nstable facts"),
                    new(MEAI.ChatRole.Assistant, "previous assistant")
                }
            }, CancellationToken.None);

            Assert.IsNotNull(inner.LastMessages);
            Assert.AreEqual(MEAI.ChatRole.System, inner.LastMessages[0].Role);
            Assert.AreEqual("root system", inner.LastMessages[0].Text);
            Assert.IsFalse(inner.LastMessages.Skip(1).Any(m => m.Role == MEAI.ChatRole.System),
                "OpenAI-compatible chat templates may reject system messages outside the first position.");
            Assert.AreEqual(MEAI.ChatRole.User, inner.LastMessages[2].Role);
            StringAssert.Contains("System context update:", inner.LastMessages[2].Text);
            StringAssert.Contains("## Memory", inner.LastMessages[2].Text);
            Assert.AreEqual("current user", inner.LastMessages[^1].Text);
        }

        [Test]
        public async Task CompleteStreamingAsync_NormalizesTailSystemMessages_ForProviderCompatibility()
        {
            CapturingChatClient inner = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);

            await foreach (LlmStreamChunk _ in client.CompleteStreamingAsync(new LlmCompletionRequest
                           {
                               AgentRoleId = "Role",
                               SystemPrompt = "root system",
                               UserPayload = "current user",
                               ChatHistory = new List<MEAI.ChatMessage>
                               {
                                   new(MEAI.ChatRole.System, "## World State\nnear shop")
                               }
                           }, CancellationToken.None))
            {
            }

            Assert.IsNotNull(inner.LastMessages);
            Assert.AreEqual(MEAI.ChatRole.System, inner.LastMessages[0].Role);
            Assert.IsFalse(inner.LastMessages.Skip(1).Any(m => m.Role == MEAI.ChatRole.System),
                "Streaming must keep the same provider-safe message contract as CompleteAsync.");
            Assert.AreEqual(MEAI.ChatRole.User, inner.LastMessages[1].Role);
            StringAssert.Contains("## World State", inner.LastMessages[1].Text);
            Assert.AreEqual("current user", inner.LastMessages[^1].Text);
        }

        [Test]
        public void ExtractCacheTokenCounts_MatchesProviderKeyVariants()
        {
            (int nullRead, int nullWrite) = MeaiLlmClient.ExtractCacheTokenCounts(null);
            Assert.AreEqual(0, nullRead);
            Assert.AreEqual(0, nullWrite);

            MEAI.AdditionalPropertiesDictionary<long> counts = new()
            {
                ["cache_read_input_tokens"] = 11,
                ["CachedTokens"] = 7,
                ["cache_creation_input_tokens"] = 13,
                ["cache_create_tokens"] = 3,
                ["cache_write_tokens"] = 5,
                ["input_tokens"] = 999,
                ["prompt_cache_miss_tokens"] = 17
            };

            (int cacheRead, int cacheWrite) = MeaiLlmClient.ExtractCacheTokenCounts(counts);

            Assert.AreEqual(18, cacheRead);
            Assert.AreEqual(21, cacheWrite);
        }

        [Test]
        public async Task CompleteAsync_MapsAdditionalCountsCacheTokens()
        {
            UsageChatClient inner = new(
                promptTokens: 100,
                completionTokens: 12,
                totalTokens: 112,
                cacheReadTokens: 80,
                cacheWriteTokens: 20);
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);

            LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Role",
                SystemPrompt = "sys",
                UserPayload = "hi"
            }, CancellationToken.None);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(100, result.PromptTokens);
            Assert.AreEqual(12, result.CompletionTokens);
            Assert.AreEqual(112, result.TotalTokens);
            Assert.AreEqual(80, result.CacheReadTokens);
            Assert.AreEqual(20, result.CacheWriteTokens);
        }

        [Test]
        public async Task CompleteStreamingAsync_NoTools_YieldsOneChunkPerInnerUpdateBeforeTerminal()
        {
            StreamingScriptedChatClient inner = new(new[] { "a", "bb", "ccc" });
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "PlainChat",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = null
            };

            List<string> texts = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    texts.Add(chunk.Text);
                }
            }

            CollectionAssert.AreEqual(new[] { "a", "bb", "ccc" }, texts);
        }

        [Test]
        public async Task CompleteStreamingAsync_SingleLargeInnerDelta_FansOutToMultipleTextChunks()
        {
            const int len = 150;
            string blob = new('z', len);
            StreamingScriptedChatClient inner = new(new[] { blob });
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "PlainChat",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = null
            };

            List<string> texts = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    texts.Add(chunk.Text);
                }
            }

            Assert.GreaterOrEqual(texts.Count, 4,
                "One 150-char SSE-style blob should fan out into multiple UI chunks.");
            Assert.AreEqual(len, string.Concat(texts).Length);
            Assert.AreEqual(blob, string.Concat(texts));
        }

        [Test]
        public async Task CompleteStreamingAsync_ToolJsonInStream_ExecutesToolAndReturnsFinalText()
        {
            StreamingScriptedChatClient inner = new(
                new[]
                {
                    "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"Saved from stream\"}}"
                },
                new[] { "Quiz created successfully." });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Create quiz",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            List<string> textChunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    textChunks.Add(chunk.Text);
                }
            }

            string full = string.Concat(textChunks);
            Assert.IsTrue(memoryStore.TryLoad("Teacher", out AgentMemoryState state));
            Assert.That(state.Memory, Does.Contain("Saved from stream"));
            Assert.That(full, Does.Contain("Quiz created successfully."));
            // Live streaming may surface the raw tool JSON in intermediate chunks before extraction finishes.
            Assert.GreaterOrEqual(inner.StreamCalls, 2, "Tool cycle should trigger second stream call.");
        }

        [Test]
        public async Task CompleteAsync_ToolExecutedThenFinalTextEmpty_StillReturnsExecutedToolCalls()
        {
            // Regression: MeaiLlmClient.CompleteAsync used to return early on an empty final assistant
            // response BEFORE copying functionClient.LastExecutedToolCalls into the result - so a turn
            // that successfully ran a tool but then trailed off into an empty response silently lost all
            // evidence that the tool ran (Ok=false, ExecutedToolCalls empty). Every terminal streaming
            // chunk and the success non-streaming path already carry these traces; the empty-response
            // non-streaming path must too.
            ScriptedNonStreamChatClient inner = new(
                new[]
                {
                    "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"Saved before going blank\"}}",
                    ""
                });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Create quiz",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            LlmCompletionResult result = await client.CompleteAsync(request, CancellationToken.None);

            Assert.IsFalse(result.Ok, "Final empty assistant text should still surface as a failed turn.");
            Assert.IsTrue(memoryStore.TryLoad("Teacher", out AgentMemoryState state));
            Assert.That(state.Memory, Does.Contain("Saved before going blank"),
                "The tool call must have actually executed despite the empty final response.");
            Assert.IsNotNull(result.ExecutedToolCalls);
            Assert.IsTrue(result.ExecutedToolCalls.Any(t => t.Name == "memory" && t.Success),
                "ExecutedToolCalls must carry the successful tool trace even when the wrapping turn errors.");
        }

        [Test]
        public async Task CompleteStreamingAsync_ToolJsonWithVisiblePrefix_KeepsPrefixAndHidesJson()
        {
            StreamingScriptedChatClient inner = new(
                new[]
                {
                    "Working... {\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"Prefix persisted\"}}"
                },
                new[] { "Done." });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Create quiz",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            List<string> textChunks = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    textChunks.Add(chunk.Text);
                }
            }

            string full = string.Concat(textChunks);
            Assert.That(full, Does.Contain("Working..."));
            Assert.That(full, Does.Contain("Done."));
            // Intermediate chunks may still include the tool JSON shape before the text-only pass completes.
            Assert.IsTrue(memoryStore.TryLoad("Teacher", out AgentMemoryState state));
            Assert.That(state.Memory, Does.Contain("Prefix persisted"));
        }

        [Test]
        public async Task CompleteStreamingAsync_UnboundToolsRequested_ChunkedInner_YieldsPrefixThenStripsJson()
        {
            // Two prose deltas before the JSON so hybrid streaming yields two Text chunks; a single "Saved! "
            // delta would already be a full safe prefix and the final strip step dedupes to one chunk.
            StreamingScriptedChatClient inner = new(
                new[]
                {
                    "Saved", "! ", "{\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"foo\"}}"
                });
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, new StubCoreSettings(), null);
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "sys",
                UserPayload = "hi",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            List<string> texts = new();
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    texts.Add(chunk.Text);
                }
            }

            Assert.GreaterOrEqual(texts.Count, 2,
                "Multiple inner prose deltas should surface as multiple streamed chunks before JSON.");
            string full = string.Concat(texts);
            Assert.AreEqual("Saved! ", full);
            Assert.That(full, Does.Not.Contain("\"name\":\"memory\""));
        }

        [Test]
        public async Task CompleteStreamingAsync_TooManyToolIterations_ReturnsTerminalError()
        {
            string toolJson = "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"loop\"}}";
            StreamingScriptedChatClient inner = new(
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson },
                new[] { toolJson });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Create quiz",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            LlmStreamChunk last = null;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                last = chunk;
            }

            Assert.IsNotNull(last);
            Assert.IsTrue(last.IsDone);
            // ToolExecutionPolicy detects duplicate tool calls and increments consecutive errors,
            // so the error comes from the policy's max-errors guard rather than the loop counter.
            Assert.IsTrue(
                last.Error.Contains("max consecutive tool errors") ||
                last.Error.Contains("tool loop exceeded"),
                $"Unexpected error: {last.Error}");
        }

        [Test]
        public async Task
            CompleteStreamingAsync_TooManySuccessfulToolIterations_WithVisibleText_CompletesWithoutUserError()
        {
            StreamingScriptedChatClient inner = new(
                new[] { "Saved. ", MemoryToolJson("append", "loop-1") },
                new[] { "Still saved. ", MemoryToolJson("append", "loop-2") },
                new[] { "Progress saved. ", MemoryToolJson("append", "loop-3") },
                new[] { "Summary saved. ", MemoryToolJson("append", "loop-4") },
                new[] { "Done saved. ", MemoryToolJson("append", "loop-5") },
                new[] { "Final saved. ", MemoryToolJson("append", "loop-6") });

            StatefulMemoryStore memoryStore = new();
            StubCoreSettings settings = new();
            MeaiLlmClient client = new(inner, GameLoggerUnscopedFallback.Instance, settings, memoryStore);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Teacher",
                SystemPrompt = "You are test agent.",
                UserPayload = "Save memory and summarize",
                Tools = new List<ILlmTool> { new MemoryLlmTool() }
            };

            List<string> texts = new();
            LlmStreamChunk last = null;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    texts.Add(chunk.Text);
                }

                last = chunk;
            }

            Assert.IsNotNull(last);
            Assert.IsTrue(last.IsDone);
            Assert.IsTrue(string.IsNullOrEmpty(last.Error), $"Unexpected user-visible error: {last.Error}");
            Assert.IsTrue(last.ExecutedToolCalls.Any(t => t.Success));
            Assert.That(string.Concat(texts), Does.Contain("Saved."));
        }

        private static string MemoryToolJson(string action, string content)
        {
            return "{\"name\":\"memory\",\"arguments\":{\"action\":\"" + action +
                   "\",\"content\":\"" + content + "\"}}";
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = new AgentMemoryState { Memory = "" };
                return true;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
            }

            public void Clear(string roleId)
            {
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
            }
        }

        private sealed class StatefulMemoryStore : IAgentMemoryStore
        {
            private readonly Dictionary<string, AgentMemoryState> _states = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return _states.TryGetValue(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                _states[roleId] = state;
            }

            public void Clear(string roleId)
            {
                _states.Remove(roleId);
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
            }
        }

        private sealed class StubCoreSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 4096;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }

        private sealed class ExplicitFunctionTool : ILlmTool, IAIFunctionLlmTool
        {
            public ExplicitFunctionTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "Explicit MEAI function test tool.";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;

            public MEAI.AIFunction CreateAIFunction()
            {
                return MEAI.AIFunctionFactory.Create(
                    (Func<string>)(() => "{\"Success\":true}"),
                    new MEAI.AIFunctionFactoryOptions
                    {
                        Name = Name,
                        Description = Description
                    });
            }
        }

        private sealed class LegacyDuckTypedFunctionTool : ILlmTool
        {
            public LegacyDuckTypedFunctionTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "Legacy duck-typed MEAI function test tool.";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;

            public MEAI.AIFunction CreateAIFunction()
            {
                return MEAI.AIFunctionFactory.Create(
                    (Func<string>)(() => "{\"Success\":true}"),
                    new MEAI.AIFunctionFactoryOptions
                    {
                        Name = Name,
                        Description = Description
                    });
            }
        }

        private sealed class CapturingChatClient : MEAI.IChatClient
        {
            public MEAI.ChatOptions LastOptions { get; private set; }
            public List<MEAI.ChatMessage> LastMessages { get; private set; }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                LastOptions = options;
                LastMessages = chatMessages.ToList();
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "ok")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                LastOptions = options;
                LastMessages = chatMessages.ToList();
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "ok");
                await Task.Yield();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Minimal MEAI client for non-streaming completion tests.</summary>
        private sealed class HelloOnceChatClient : MEAI.IChatClient
        {
            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "ok")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "x");
                await Task.Yield();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Minimal MEAI client that returns usage details with provider-specific cache counts.</summary>
        private sealed class UsageChatClient : MEAI.IChatClient
        {
            private readonly int _promptTokens;
            private readonly int _completionTokens;
            private readonly int _totalTokens;
            private readonly int _cacheReadTokens;
            private readonly int _cacheWriteTokens;

            public UsageChatClient(
                int promptTokens,
                int completionTokens,
                int totalTokens,
                int cacheReadTokens,
                int cacheWriteTokens)
            {
                _promptTokens = promptTokens;
                _completionTokens = completionTokens;
                _totalTokens = totalTokens;
                _cacheReadTokens = cacheReadTokens;
                _cacheWriteTokens = cacheWriteTokens;
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                MEAI.ChatResponse response = new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "ok"))
                {
                    Usage = new MEAI.UsageDetails
                    {
                        InputTokenCount = _promptTokens,
                        OutputTokenCount = _completionTokens,
                        TotalTokenCount = _totalTokens,
                        AdditionalCounts = new MEAI.AdditionalPropertiesDictionary<long>
                        {
                            ["cache_read_input_tokens"] = _cacheReadTokens,
                            ["cache_creation_input_tokens"] = _cacheWriteTokens
                        }
                    }
                };
                return Task.FromResult(response);
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "x");
                await Task.Yield();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Non-streaming scripted client: returns each queued text response in order, one per call.</summary>
        private sealed class ScriptedNonStreamChatClient : MEAI.IChatClient
        {
            private readonly Queue<string> _responses;

            public ScriptedNonStreamChatClient(params string[] responses)
            {
                _responses = new Queue<string>(responses ?? Array.Empty<string>());
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                string text = _responses.Count > 0 ? _responses.Dequeue() : "";
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, text)));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages, MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class StreamingScriptedChatClient : MEAI.IChatClient
        {
            private readonly Queue<string[]> _streamScripts;
            public int StreamCalls { get; private set; }

            public StreamingScriptedChatClient(params string[][] streamScripts)
            {
                _streamScripts = new Queue<string[]>(streamScripts ?? Array.Empty<string[]>());
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                StreamCalls++;
                if (_streamScripts.Count == 0)
                {
                    yield break;
                }

                string[] chunks = _streamScripts.Dequeue();
                foreach (string chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, chunk);
                    await Task.Yield();
                }
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// Tests for the hardened TryExtractToolCallsFromText parser.
    /// Covers: multi-tool, code block false-positives, partial JSON, edge cases.
    /// </summary>
    [TestFixture]
    public sealed class TryExtractToolCallsFromTextTests
    {
        [Test]
        public void SingleToolCall_ExtractedCorrectly()
        {
            string text =
                "Here is the result: {\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"hello\"}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("memory", calls[0].Name);
            Assert.That(cleaned, Does.Contain("Here is the result:"));
            Assert.That(cleaned, Does.Not.Contain("\"name\":\"memory\""));
        }

        [Test]
        public void PseudoActionWrite_QwenStyle_ExtractedAsMemory()
        {
            string text =
                "Action=write content=\"Final exam is on June 15th.\" memory_type=\"text\" action=\"write\"";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("memory", calls[0].Name);
            Assert.AreEqual("write", calls[0].Arguments["action"]?.ToString());
            Assert.That(calls[0].Arguments["content"]?.ToString(),
                Does.Contain("June 15"));
            Assert.That(cleaned, Does.Not.Contain("Action=write"));
        }

        [Test]
        public void PseudoActionWrite_WithProsePrefix_StripsPseudoTailOnly()
        {
            string text = "Okay. Action=write content=\"hello\"";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.That(cleaned, Does.Contain("Okay."));
            Assert.That(cleaned, Does.Not.Contain("Action=write"));
        }

        [Test]
        public void MultipleToolCalls_AllExtracted()
        {
            string text =
                "{\"name\":\"tool_a\",\"arguments\":{\"x\":1}} some text {\"name\":\"tool_b\",\"arguments\":{\"y\":2}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(2, calls.Count);
            Assert.AreEqual("tool_a", calls[0].Name);
            Assert.AreEqual("tool_b", calls[1].Name);
            Assert.That(cleaned, Does.Contain("some text"));
        }

        [Test]
        public void JsonInCodeBlock_NotExtracted()
        {
            string text =
                "Here is an example:\n```json\n{\"name\":\"memory\",\"arguments\":{\"action\":\"read\"}}\n```\nDone.";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsFalse(found, "JSON inside code blocks should be ignored");
            Assert.AreEqual(0, calls.Count);
        }

        [Test]
        public void MalformedJson_GracefullySkipped()
        {
            string text = "Partial: {\"name\":\"tool\",\"arguments\":{\"broken";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsFalse(found, "Unclosed JSON should not produce tool calls");
            Assert.AreEqual(0, calls.Count);
        }

        [Test]
        public void JsonWithoutNameAndArguments_NotExtracted()
        {
            string text = "Config: {\"key\":\"value\",\"count\":42}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsFalse(found, "Regular JSON without name+arguments keys should be ignored");
        }

        [Test]
        public void EmptyText_ReturnsFalse()
        {
            Assert.IsFalse(MeaiLlmClient.TryExtractToolCallsFromText("", out _, out _));
            Assert.IsFalse(MeaiLlmClient.TryExtractToolCallsFromText(null, out _, out _));
            Assert.IsFalse(MeaiLlmClient.TryExtractToolCallsFromText("   ", out _, out _));
        }

        [Test]
        public void NestedBracesInArguments_HandledCorrectly()
        {
            string text = "{\"name\":\"config\",\"arguments\":{\"data\":{\"nested\":true}}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("config", calls[0].Name);
        }

        [Test]
        public void StripCodeBlocks_PreservesPositions()
        {
            string text = "Before ```code``` After";
            string stripped = MeaiLlmClient.StripCodeBlocks(text);

            Assert.AreEqual(text.Length, stripped.Length, "Stripped text should have same length");
            Assert.That(stripped, Does.StartWith("Before "));
            Assert.That(stripped, Does.EndWith(" After"));
        }

        [Test]
        public void IsValidToolCallJson_RequiresBothKeys()
        {
            Assert.IsTrue(MeaiLlmClient.IsValidToolCallJson("{\"name\":\"x\",\"arguments\":{}}"));
            Assert.IsFalse(MeaiLlmClient.IsValidToolCallJson("{\"name\":\"x\"}"));
            Assert.IsFalse(MeaiLlmClient.IsValidToolCallJson("{\"arguments\":{}}"));
            Assert.IsFalse(MeaiLlmClient.IsValidToolCallJson(""));
        }

        [Test]
        public void FindToolCallJsonSpans_MultipleSpans()
        {
            string text = "A {\"name\":\"a\",\"arguments\":{}} B {\"name\":\"b\",\"arguments\":{\"x\":1}}";
            List<MeaiLlmClient.JsonSpan> spans = MeaiLlmClient.FindToolCallJsonSpans(text);

            Assert.AreEqual(2, spans.Count);
        }

        [Test]
        public void GetExclusiveEndForSafeUnboundRawStreaming_StopsBeforeCompleteToolJson()
        {
            string text = "Saved! {\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"foo\"}} tail";
            int brace = text.IndexOf('{');
            Assert.AreEqual(brace, MeaiLlmClient.GetExclusiveEndForSafeUnboundRawStreaming(text));
        }

        [Test]
        public void GetExclusiveEndForSafeUnboundRawStreaming_IncompleteBraceAtEofHoldsFromOpen()
        {
            string text = "Saved! {";
            int brace = text.IndexOf('{');
            Assert.AreEqual(brace, MeaiLlmClient.GetExclusiveEndForSafeUnboundRawStreaming(text));
        }

        [Test]
        public void GetExclusiveEndForSafeUnboundRawStreaming_NonToolClosedObjectEmitsFullLength()
        {
            string text = "Use { \"a\": 1 } ok";
            Assert.AreEqual(text.Length, MeaiLlmClient.GetExclusiveEndForSafeUnboundRawStreaming(text));
        }

        [Test]
        public void GetCleanedTextSuffixAfterHybridPrefix_SkipsPrefixAlreadyStreamedToConsumer()
        {
            string visible = "Hello ";
            string cleaned = "Hello world";
            int hybridEnd = visible.Length;
            string? suffix = MeaiLlmClient.GetCleanedTextSuffixAfterHybridPrefix(cleaned, visible, hybridEnd);
            Assert.IsNotNull(suffix);
            Assert.AreEqual("world", suffix);
        }

        [Test]
        public void GetCleanedTextSuffixAfterHybridPrefix_ReturnsNullWhenNothingWasStreamed()
        {
            Assert.IsNull(MeaiLlmClient.GetCleanedTextSuffixAfterHybridPrefix("only cleaned", "visible", 0));
        }

        [Test]
        public void GetCleanedTextSuffixAfterHybridPrefix_UsesTrimmedRawPrefixWhenCleanedOmitsTrailingSpaces()
        {
            string visible = "OK  ";
            string cleaned = "OK done";
            int hybridEnd = visible.Length;
            string? suffix = MeaiLlmClient.GetCleanedTextSuffixAfterHybridPrefix(cleaned, visible, hybridEnd);
            Assert.IsNotNull(suffix);
            Assert.AreEqual(" done", suffix);
        }

        [Test]
        public void ToolCallWithStringContainingBraces_HandledCorrectly()
        {
            string text = "{\"name\":\"tool\",\"arguments\":{\"code\":\"function() { return {}; }\"}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("tool", calls[0].Name);
        }

        [Test]
        public void ToolCallInMiddleOfLongText_PrefixAndSuffixPreserved()
        {
            // Real-world pattern: model writes text, then tool call JSON, then nothing
            string prefix = "I will save this to memory now. ";
            string json = "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"save me\"}}";
            string suffix = " Done processing.";
            string text = prefix + json + suffix;

            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.That(cleaned, Does.Contain("I will save this to memory now."));
            Assert.That(cleaned, Does.Contain("Done processing."));
            Assert.That(cleaned, Does.Not.Contain("\"name\":\"memory\""));
        }

        [Test]
        public void CodeBlockFollowedByRealToolCall_OnlyRealCallExtracted()
        {
            // Model shows an example in code block, then makes a real call
            string text =
                "Here is an example:\n```json\n{\"name\":\"memory\",\"arguments\":{\"action\":\"read\"}}\n```\n" +
                "Now I will actually call it:\n" +
                "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"real\"}}";

            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found, "Should extract the real call outside the code block");
            Assert.AreEqual(1, calls.Count, "Only the non-code-block call should be extracted");
            Assert.AreEqual("write", calls[0].Arguments?["action"]?.ToString());
        }

        [Test]
        public void ToolCallWithArrayArguments_ExtractedCorrectly()
        {
            string text = "{\"name\":\"batch_tool\",\"arguments\":{\"items\":[1,2,3],\"mode\":\"sync\"}}";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("batch_tool", calls[0].Name);
        }

        [Test]
        public void CleanedText_IsTrimmable_NoLeadingTrailingJson()
        {
            // Tool call at very start of text — cleaned should not start with JSON
            string text =
                "{\"name\":\"memory\",\"arguments\":{\"action\":\"write\",\"content\":\"x\"}} Here is my answer.";
            bool found = MeaiLlmClient.TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> calls,
                out string cleaned);

            Assert.IsTrue(found);
            Assert.That(cleaned.TrimStart(), Does.Not.StartWith("{\"name\""),
                "Cleaned text should not start with JSON tool call");
            Assert.That(cleaned, Does.Contain("Here is my answer."));
        }
    }

    /// <summary>
    /// Unit coverage for the Kilo/Cline-style on-the-fly hybrid hold helpers
    /// (<see cref="MeaiLlmClient.GetHybridSafeSegments"/> / <see cref="MeaiLlmClient.GetHybridUnemittedSuffix"/>),
    /// which keep prose streaming live before AND after a tool call instead of buffering the whole turn.
    /// </summary>
    public sealed class HybridSafeSegmentsTests
    {
        private const string ToolJson = "{\"name\":\"memory\",\"arguments\":{\"action\":\"read\"}}";

        [Test]
        public void GetHybridSafeSegments_ProseToolProse_SplitsIntoThreeSegmentsAndStreamsTrailingProse()
        {
            string text = "P " + ToolJson + " Q";
            List<MeaiLlmClient.HybridProseSegment> segments =
                MeaiLlmClient.GetHybridSafeSegments(text, out int safeEnd);

            Assert.AreEqual(3, segments.Count, "Expected prose / tool-json / prose.");
            Assert.IsFalse(segments[0].IsToolJson);
            Assert.AreEqual("P ", text.Substring(segments[0].Start, segments[0].Length));
            Assert.IsTrue(segments[1].IsToolJson, "Middle span is the hidden tool-call JSON.");
            Assert.IsFalse(segments[2].IsToolJson, "Trailing prose must resume live after the tool call.");
            Assert.AreEqual(" Q", text.Substring(segments[2].Start, segments[2].Length));
            Assert.AreEqual(text.Length, safeEnd, "A fully-closed turn has no pending hold.");
        }

        [Test]
        public void GetHybridSafeSegments_IncompleteBraceAtEnd_HoldsFromOpenBrace()
        {
            string text = "Hi {\"name\":\"x\",\"argu";
            List<MeaiLlmClient.HybridProseSegment> segments =
                MeaiLlmClient.GetHybridSafeSegments(text, out int safeEnd);

            Assert.AreEqual(1, segments.Count);
            Assert.IsFalse(segments[0].IsToolJson);
            Assert.AreEqual("Hi ", text.Substring(segments[0].Start, segments[0].Length));
            Assert.AreEqual(text.IndexOf('{'), safeEnd, "Output is held from the first still-open brace.");
        }

        [Test]
        public void GetHybridSafeSegments_NonToolClosedObject_StreamsWholeTextAsProse()
        {
            string text = "Use { \"a\": 1 } now";
            List<MeaiLlmClient.HybridProseSegment> segments =
                MeaiLlmClient.GetHybridSafeSegments(text, out int safeEnd);

            Assert.AreEqual(1, segments.Count, "A non-tool {...} must not be hidden.");
            Assert.IsFalse(segments[0].IsToolJson);
            Assert.AreEqual(text, text.Substring(segments[0].Start, segments[0].Length));
            Assert.AreEqual(text.Length, safeEnd);
        }

        [Test]
        public void GetHybridSafeSegments_TwoToolCalls_PreservesProseBetweenThem()
        {
            string text = "A " + ToolJson + " B " + ToolJson + " C";
            List<MeaiLlmClient.HybridProseSegment> segments =
                MeaiLlmClient.GetHybridSafeSegments(text, out int safeEnd);

            int toolSpans = segments.Count(s => s.IsToolJson);
            Assert.AreEqual(2, toolSpans, "Both text-shaped tool calls are hidden.");

            string prose = string.Concat(segments
                .Where(s => !s.IsToolJson)
                .Select(s => text.Substring(s.Start, s.Length)));
            Assert.AreEqual("A  B  C", prose, "Prose between two tool calls must not be lost.");
            Assert.AreEqual(text.Length, safeEnd);
        }

        [Test]
        public void GetHybridSafeSegments_Empty_ReturnsNoSegments()
        {
            List<MeaiLlmClient.HybridProseSegment> segments =
                MeaiLlmClient.GetHybridSafeSegments("", out int safeEnd);
            Assert.IsEmpty(segments);
            Assert.AreEqual(0, safeEnd);
        }

        [Test]
        public void GetHybridUnemittedSuffix_StripsHeldToolJson_ReturnsTrailingProse()
        {
            string visible = "ok " + ToolJson + " bye";
            string? suffix = MeaiLlmClient.GetHybridUnemittedSuffix(visible, 3);

            Assert.IsNotNull(suffix);
            Assert.That(suffix, Does.Contain("bye"));
            Assert.That(suffix, Does.Not.Contain("\"name\""), "Held tool-call JSON must be stripped.");
        }

        [Test]
        public void GetHybridUnemittedSuffix_CursorAtOrPastEnd_ReturnsNull()
        {
            Assert.IsNull(MeaiLlmClient.GetHybridUnemittedSuffix("abc", 3));
            Assert.IsNull(MeaiLlmClient.GetHybridUnemittedSuffix("abc", 9));
        }

        [Test]
        public void GetHybridUnemittedSuffix_WhitespaceOnlyTail_ReturnsNull()
        {
            Assert.IsNull(MeaiLlmClient.GetHybridUnemittedSuffix("ok    ", 2));
        }
    }
#endif
}
