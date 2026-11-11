using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.Npc;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Ai
{
    /// <summary>
    /// Lua-tier conformance of the AI surface (P12): Roblox's <c>TextGenerator</c> (mirror defaults and clamps, the
    /// yielding <c>GenerateTextAsync</c> and its result shape, <c>ContextToken</c>, <c>JsonSchema</c>, the world quota)
    /// and CoreAI's <c>AIService</c> agents (created by a mod, asked and heard through the NPC director, torn down with
    /// the mod), all behind the host's framework setting, whose refusal names it. Every model call goes to a scripted
    /// backend or a scripted orchestration service.
    /// </summary>
    [TestFixture]
    public sealed class P12TextGeneratorAndAiServiceEditModeTests
    {
        private SynchronizationContext _previousContext;

        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }

        [Test]
        public void P12_TextGenerator_HasTheMirrorDefaultsAndClamps()
        {
            using World world = new();
            world.Load(@"
                local g = Instance.new('TextGenerator')
                workspace:SetAttribute('class', g.ClassName)
                workspace:SetAttribute('isInstance', g:IsA('Instance'))
                workspace:SetAttribute('t0', g.Temperature)
                workspace:SetAttribute('p0', g.TopP)
                workspace:SetAttribute('s0', g.Seed)
                workspace:SetAttribute('sp0', g.SystemPrompt)
                g.Temperature = 5
                g.TopP = 0.1
                g.Seed = 3.9
                g.SystemPrompt = 'You are a beaver.'
                workspace:SetAttribute('t1', g.Temperature)
                workspace:SetAttribute('p1', g.TopP)
                workspace:SetAttribute('s1', g.Seed)
                workspace:SetAttribute('sp1', g.SystemPrompt)
                g.Temperature = 0
                workspace:SetAttribute('t2', g.Temperature)
            ");

            Assert.AreEqual("TextGenerator", world.Attribute("class"));
            Assert.AreEqual(true, world.Attribute("isInstance"));
            Assert.AreEqual(0.7d, Convert.ToDouble(world.Attribute("t0")), 1e-6);
            Assert.AreEqual(0.9d, Convert.ToDouble(world.Attribute("p0")), 1e-6);
            Assert.AreEqual(0d, Convert.ToDouble(world.Attribute("s0")));
            Assert.AreEqual("", world.Attribute("sp0"));
            Assert.AreEqual(1.0d, Convert.ToDouble(world.Attribute("t1")), 1e-6);
            Assert.AreEqual(0.5d, Convert.ToDouble(world.Attribute("p1")), 1e-6);
            Assert.AreEqual(3d, Convert.ToDouble(world.Attribute("s1")), "Non-integral seeds are truncated.");
            Assert.AreEqual("You are a beaver.", world.Attribute("sp1"));
            Assert.AreEqual(0.4d, Convert.ToDouble(world.Attribute("t2")), 1e-6);
        }

        [Test]
        public void P12_GenerateTextAsync_YieldsUntilTheModelAnswers_AndReturnsTheMirrorShape()
        {
            using World world = new();
            world.Text.Hold = true;
            world.Load(@"
                local g = Instance.new('TextGenerator')
                g.SystemPrompt = 'You are a very busy beaver.'
                g.Temperature = 0.5
                task.spawn(function()
                    local result = g:GenerateTextAsync({ UserPrompt = 'Where is the water?', MaxTokens = 40.6 })
                    workspace:SetAttribute('text', result.GeneratedText)
                    workspace:SetAttribute('token', result.ContextToken)
                    workspace:SetAttribute('model', result.Model)
                end)
                workspace:SetAttribute('after', 'spawned')
            ");
            world.Advance();

            Assert.AreEqual("spawned", world.Attribute("after"));
            Assert.IsNull(world.Attribute("text"), "The thread yields until the answer arrives.");
            Assert.AreEqual(1, world.Text.Requests.Count);
            RbxTextGenerationRequest sent = world.Text.Requests[0];
            Assert.AreEqual("You are a very busy beaver.", sent.SystemPrompt);
            Assert.AreEqual("Where is the water?", sent.UserPrompt);
            Assert.AreEqual(41, sent.MaxTokens, "MaxTokens is rounded to the nearest integer.");
            Assert.AreEqual(0.5f, sent.Temperature, 1e-6f);
            Assert.AreEqual("village", sent.ModId);

            world.Text.Release("Down by the river. Now back to my dam!");
            world.Advance();
            world.Advance();

            Assert.AreEqual("Down by the river. Now back to my dam!", world.Attribute("text"));
            StringAssert.StartsWith(RbxTextGenerationContexts.TokenPrefix, (string)world.Attribute("token"));
            Assert.AreEqual("scripted-model", world.Attribute("model"));
        }

        [Test]
        public void P12_ContextToken_ContinuesTheConversation_AndAnUnknownTokenIsRefused()
        {
            using World world = new();
            world.Text.Reply = request => "answer " + (request.History.Count + 1);
            world.Load(@"
                local g = Instance.new('TextGenerator')
                task.spawn(function()
                    local first = g:GenerateTextAsync({ UserPrompt = 'one' })
                    local second = g:GenerateTextAsync({ UserPrompt = 'two', ContextToken = first.ContextToken })
                    workspace:SetAttribute('second', second.GeneratedText)
                    workspace:SetAttribute('tokensDiffer', first.ContextToken ~= second.ContextToken)
                    local ok, message = pcall(function()
                        return g:GenerateTextAsync({ UserPrompt = 'three', ContextToken = 'made-up' })
                    end)
                    workspace:SetAttribute('forgedOk', ok)
                    workspace:SetAttribute('forgedError', tostring(message))
                end)
            ");
            world.AdvanceUntil(() => world.Attribute("forgedError") != null);

            Assert.AreEqual("answer 2", world.Attribute("second"));
            Assert.AreEqual(true, world.Attribute("tokensDiffer"));
            Assert.AreEqual(2, world.Text.Requests.Count, "The forged token never reaches the model.");
            Assert.AreEqual(1, world.Text.Requests[1].History.Count);
            Assert.AreEqual("one", world.Text.Requests[1].History[0].UserPrompt);
            Assert.AreEqual("answer 1", world.Text.Requests[1].History[0].GeneratedText);
            Assert.AreEqual(false, world.Attribute("forgedOk"));
            StringAssert.Contains("ContextToken", (string)world.Attribute("forgedError"));
        }

        [Test]
        public void P12_JsonSchema_TheAnswerParsesAgainstTheSchema_OrTheCallFailsTyped()
        {
            using World world = new();
            int call = 0;
            world.Text.Reply = _ => call++ == 0
                ? "Sure! ```json\n{\"mood\":\"happy\",\"score\":3}\n```"
                : "{\"mood\":5}";
            world.Load(@"
                local g = Instance.new('TextGenerator')
                local schema = '{""type"":""object"",""properties"":{""mood"":{""type"":""string""},""score"":{""type"":""integer""}},""required"":[""mood""]}'
                task.spawn(function()
                    local good = g:GenerateTextAsync({ UserPrompt = 'rate', JsonSchema = schema })
                    workspace:SetAttribute('good', good.GeneratedText)
                    local ok, message = pcall(function()
                        return g:GenerateTextAsync({ UserPrompt = 'rate again', JsonSchema = schema })
                    end)
                    workspace:SetAttribute('badOk', ok)
                    workspace:SetAttribute('badError', tostring(message))
                    local schemaOk, schemaMessage = pcall(function()
                        return g:GenerateTextAsync({ UserPrompt = 'x', JsonSchema = 'not json' })
                    end)
                    workspace:SetAttribute('schemaError', tostring(schemaMessage))
                end)
            ");
            world.AdvanceUntil(() => world.Attribute("schemaError") != null);

            Assert.AreEqual("{\"mood\":\"happy\",\"score\":3}", world.Attribute("good"));
            Assert.AreEqual(false, world.Attribute("badOk"));
            StringAssert.Contains("SchemaMismatch", (string)world.Attribute("badError"));
            StringAssert.Contains("JsonSchema", (string)world.Attribute("schemaError"));
            Assert.AreEqual(2, world.Text.Requests.Count, "An invalid schema is refused before the model is asked.");
        }

        [Test]
        public void P12_TheWorldQuota_RefusesRequestsBeyondTheLimit()
        {
            using World world = new(new CoreAiLuaAiOptions { AllowAgentsFromLua = true, TextGeneratorRequestsPerMinute = 1 });
            world.Load(@"
                local g = Instance.new('TextGenerator')
                task.spawn(function()
                    g:GenerateTextAsync({ UserPrompt = 'one' })
                    local ok, message = pcall(function() return g:GenerateTextAsync({ UserPrompt = 'two' }) end)
                    workspace:SetAttribute('quotaError', tostring(message))
                end)
            ");
            world.AdvanceUntil(() => world.Attribute("quotaError") != null);

            Assert.AreEqual(1, world.Text.Requests.Count);
            StringAssert.Contains("QuotaExceeded", (string)world.Attribute("quotaError"));
        }

        [Test]
        public void P12_WithTheFrameworkSettingOff_EveryAiCallIsRefusedNamingTheSetting()
        {
            using World world = new(new CoreAiLuaAiOptions { AllowAgentsFromLua = false });
            world.Load(@"
                local g = Instance.new('TextGenerator')
                local AIService = game:GetService('AIService')
                workspace:SetAttribute('serviceClass', AIService.ClassName)
                local okAgent, agentError = pcall(function() return AIService:CreateAgent({ Name = 'Bob' }) end)
                workspace:SetAttribute('agentError', tostring(agentError))
                task.spawn(function()
                    local okText, textError = pcall(function() return g:GenerateTextAsync({ UserPrompt = 'hi' }) end)
                    workspace:SetAttribute('textError', tostring(textError))
                end)
            ");
            world.AdvanceUntil(() => world.Attribute("textError") != null);

            Assert.AreEqual("AIService", world.Attribute("serviceClass"));
            StringAssert.Contains(CoreAiLuaAiOptions.SettingName, (string)world.Attribute("agentError"));
            StringAssert.Contains(CoreAiLuaAiOptions.SettingName, (string)world.Attribute("textError"));
            Assert.AreEqual(0, world.Text.Requests.Count);
            Assert.AreEqual(0, world.Director.Agents.Count);
        }

        [Test]
        public void P12_WithTheFrameworkSettingOn_TheSameCallsSucceed()
        {
            using World world = new();
            world.Load(@"
                local AIService = game:GetService('AIService')
                local bob = AIService:CreateAgent({ Name = 'Bob' })
                workspace:SetAttribute('agentName', bob.Name)
                local g = Instance.new('TextGenerator')
                task.spawn(function()
                    workspace:SetAttribute('text', g:GenerateTextAsync({ UserPrompt = 'hi' }).GeneratedText)
                end)
            ");
            world.AdvanceUntil(() => world.Attribute("text") != null);

            Assert.AreEqual("Bob", world.Attribute("agentName"));
            Assert.AreEqual("scripted text", world.Attribute("text"));
            Assert.AreEqual(1, world.Director.Agents.Count);
        }

        [Test]
        public void P12_TheAiCapability_IsRequired_WhenTheHostAsksForIt()
        {
            CoreAiLuaAiOptions options = new() { AllowAgentsFromLua = true, RequireAiCapability = true };
            using World world = new(options, LuaCapabilities.All | LuaAiCapability.Ai);
            const string source = @"
                local ok, message = pcall(function()
                    return game:GetService('AIService'):CreateAgent({ Name = 'Bob' })
                end)
                workspace:SetAttribute('ok', ok)
                workspace:SetAttribute('error', tostring(message))
            ";

            world.Load(source, LuaCapabilities.All);
            Assert.AreEqual(false, world.Attribute("ok"));
            StringAssert.Contains("Ai capability", (string)world.Attribute("error"));

            world.Load(source, LuaCapabilities.All | LuaAiCapability.Ai, "granted");
            Assert.AreEqual(true, world.Attribute("ok"));
            Assert.AreEqual(1, world.Director.Agents.Count);
        }

        [Test]
        public void P12_AnAgentCreatedByAMod_AnswersAsk_ThroughTheDirector()
        {
            using World world = new();
            world.Npc.Reply = request => request.Hint.Contains("bread") ? "Fresh loaves at dawn, Steve!" : "Hm?";
            world.Load(@"
                local bob = game:GetService('AIService'):CreateAgent({
                    Name = 'Bob',
                    Persona = 'a grumpy old baker',
                    VoiceRules = 'short sentences',
                    FallbackLines = { 'Not now, the oven!' },
                    CooldownSeconds = 0,
                })
                workspace:SetAttribute('id', bob.Id)
                workspace:SetAttribute('origin', bob.OriginTag)
                task.spawn(function()
                    local reply, fallback = bob:Ask('Any bread today?', 'Steve')
                    workspace:SetAttribute('reply', reply)
                    workspace:SetAttribute('fallback', fallback)
                end)
            ");
            world.AdvanceUntil(() => world.Attribute("reply") != null);

            Assert.AreEqual("Fresh loaves at dawn, Steve!", world.Attribute("reply"));
            Assert.AreEqual(false, world.Attribute("fallback"));
            Assert.AreEqual("village/Bob", world.Attribute("id"));
            Assert.AreEqual(OriginTag.FromAi("village"), world.Attribute("origin"));
            AiTaskRequest request = world.Npc.Requests[0];
            Assert.AreEqual("Mod:village", request.SourceTag);
            Assert.AreEqual("npc:village/Bob", request.ActorContext.Value.MemoryScope.TopicId);
            StringAssert.Contains("a grumpy old baker", request.RequestSystemInstructions);
            Assert.AreEqual("Steve: Any bread today?", request.Hint);
        }

        [Test]
        public void P12_AFailedAgentTurn_AnswersWithAFallbackLine_NeverTheProviderText()
        {
            using World world = new();
            world.Npc.FailWith = LlmErrorCode.ProviderError;
            world.Npc.FailText = "upstream 503 from model-x, request 9c1e";
            world.Load(@"
                local bob = game:GetService('AIService'):CreateAgent({
                    Name = 'Bob', FallbackLines = { 'Not now, the oven!' }, CooldownSeconds = 0 })
                task.spawn(function()
                    local reply, fallback = bob:Ask('hello')
                    workspace:SetAttribute('reply', reply)
                    workspace:SetAttribute('fallback', fallback)
                end)
            ");
            world.AdvanceUntil(() => world.Attribute("reply") != null);

            Assert.AreEqual("Not now, the oven!", world.Attribute("reply"));
            Assert.AreEqual(true, world.Attribute("fallback"));
        }

        [Test]
        public void P12_AChatLineHeardByTheDirector_FiresTheAgentsSignals()
        {
            using World world = new();
            world.Npc.Reply = _ => "Morning, Steve.";
            world.Load(@"
                local bob = game:GetService('AIService'):CreateAgent({ Name = 'Bob', CooldownSeconds = 0 })
                bob.Addressed:Connect(function(speaker, text, reason)
                    workspace:SetAttribute('addressedBy', speaker)
                    workspace:SetAttribute('reason', reason)
                end)
                bob.Replied:Connect(function(text, speaker, fallback)
                    workspace:SetAttribute('replied', text)
                    workspace:SetAttribute('repliedTo', speaker)
                end)
            ");

            world.Director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, good morning"));
            world.AdvanceUntil(() => world.Attribute("replied") != null);

            Assert.AreEqual("Steve", world.Attribute("addressedBy"));
            Assert.AreEqual("Name", world.Attribute("reason"));
            Assert.AreEqual("Morning, Steve.", world.Attribute("replied"));
            Assert.AreEqual("Steve", world.Attribute("repliedTo"));
        }

        [Test]
        public void P12_AIServiceHear_RoutesALineToTheNamedAgentOfTheMod()
        {
            using World world = new();
            world.Load(@"
                local AIService = game:GetService('AIService')
                local bob = AIService:CreateAgent({ Name = 'Bob', CooldownSeconds = 0, FollowUpSeconds = 0 })
                AIService:CreateAgent({ Name = 'Alice', CooldownSeconds = 0 })
                local taker = AIService:Hear('Steve', 'Bob, hi')
                workspace:SetAttribute('taker', taker and taker.Name or 'nobody')
                local none = AIService:Hear('Steve', 'lovely weather')
                workspace:SetAttribute('none', none == nil)
                workspace:SetAttribute('count', #AIService:GetAgents())
                workspace:SetAttribute('same', AIService:GetAgent('Bob') == bob)
            ");

            Assert.AreEqual("Bob", world.Attribute("taker"));
            Assert.AreEqual(true, world.Attribute("none"));
            Assert.AreEqual(2d, Convert.ToDouble(world.Attribute("count")));
            Assert.AreEqual(true, world.Attribute("same"));
        }

        [Test]
        public void P12_UnloadingTheMod_RemovesItsAgents_AndAReloadMayReuseTheName()
        {
            using World world = new();
            const string source = "game:GetService('AIService'):CreateAgent({ Name = 'Bob' })";
            world.Load(source);
            Assert.AreEqual(1, world.Director.Agents.Count);

            world.Stack.Runtime.ReloadMod("village", source);
            world.Advance();
            Assert.AreEqual(1, world.Director.Agents.Count, "The reload replaced the agent instead of failing on the name.");

            world.Stack.Runtime.UnloadMod("village");
            world.Advance();
            Assert.AreEqual(0, world.Director.Agents.Count);
            Assert.AreEqual(0, world.Adapter.AgentCount);
        }

        [Test]
        public void P12_AReplacementWorld_RecreatesItsAgents_WhileTheOutgoingWorldStillHoldsThem()
        {
            const string source = "game:GetService('AIService'):CreateAgent({ Name = 'Bob' })";
            using World outgoing = new();
            outgoing.Load(source);
            using (World replacement = new(sharedDirector: outgoing.Director))
            {
                replacement.Load(source);

                Assert.AreEqual(2, outgoing.Director.Agents.Count, "Both worlds hold a Bob for a moment.");
                outgoing.Rbx.Dispose();
                Assert.AreEqual(1, outgoing.Director.Agents.Count, "The outgoing world's surface removed its agent.");
                Assert.AreEqual(1, replacement.Adapter.AgentCount);
            }

            Assert.AreEqual(0, outgoing.Director.Agents.Count, "Disposing a world removes its agents.");
        }

        [Test]
        public void P12_CreateAgent_RefusesUnknownKeysDuplicatesAndAMissingName()
        {
            using World world = new();
            world.Load(@"
                local AIService = game:GetService('AIService')
                local function try(config)
                    local ok, message = pcall(function() return AIService:CreateAgent(config) end)
                    return tostring(message)
                end
                workspace:SetAttribute('unknown', try({ Name = 'Bob', Persnoa = 'typo' }))
                workspace:SetAttribute('missing', try({ Persona = 'no name' }))
                AIService:CreateAgent({ Name = 'Bob' })
                workspace:SetAttribute('duplicate', try({ Name = 'Bob' }))
            ");

            StringAssert.Contains("Persnoa", (string)world.Attribute("unknown"));
            StringAssert.Contains("Name", (string)world.Attribute("missing"));
            StringAssert.Contains("already exists", (string)world.Attribute("duplicate"));
            Assert.AreEqual(1, world.Director.Agents.Count);
        }

        [Test]
        public void P12_TheMirrorsNpcExample_RunsUnchanged_OnTheDefaultBackend()
        {
            ScriptedNpcService npc = new() { Reply = request => "The forest is north. I must get back to my dam!" };
            using World world = new(useDefaultTextBackend: true, npc: npc);
            world.Load(@"
                local TextGenerator = Instance.new('TextGenerator')
                TextGenerator.SystemPrompt = 'You are a very busy beaver. You end all statements by mentioning how you need to get back to work on your dam.'
                local contextToken = nil
                local function ask(prompt)
                    local ok, result = pcall(function()
                        return TextGenerator:GenerateTextAsync({ UserPrompt = prompt, ContextToken = contextToken })
                    end)
                    if ok then
                        contextToken = result.ContextToken
                        return result.GeneratedText
                    end
                    return nil
                end
                task.spawn(function()
                    workspace:SetAttribute('first', ask('Where is the forest?'))
                    workspace:SetAttribute('second', ask('And the river?'))
                end)
            ");
            world.AdvanceUntil(() => world.Attribute("second") != null);

            Assert.AreEqual("The forest is north. I must get back to my dam!", world.Attribute("first"));
            Assert.AreEqual(2, npc.Requests.Count);
            AiTaskRequest second = npc.Requests[1];
            StringAssert.StartsWith("You are a very busy beaver.", second.SystemPrompt);
            Assert.AreEqual(AiTaskHistoryMode.None, second.HistoryMode, "The context token, not role history, carries the conversation.");
            Assert.AreEqual(AiTaskMemoryMode.None, second.MemoryMode);
            CollectionAssert.IsEmpty(second.AllowedToolNames);
            StringAssert.Contains("Where is the forest?", second.Hint);
            StringAssert.Contains("And the river?", second.Hint);
            Assert.Less(second.Priority, 0);
        }

        /// <summary>A mod stack with the AI surface attached through its own one-line hook, over scripted models.</summary>
        private sealed class World : IDisposable
        {
            private readonly bool _ownsDirector;

            public World(CoreAiLuaAiOptions options = null, LuaCapabilities ceiling = LuaCapabilities.All,
                bool useDefaultTextBackend = false, ScriptedNpcService npc = null, NpcDirector sharedDirector = null)
            {
                Rbx = new LuaCsRbxApiBindings();
                Npc = npc ?? new ScriptedNpcService();
                Text = new ScriptedTextBackend();
                _ownsDirector = sharedDirector == null;
                Director = sharedDirector ?? new NpcDirector(Npc, null, new NpcDirectorOptions(), new ManualClock());
                Action<LuaCsApiRegistry, LuaCapabilities> hook = CoreAiLuaAiBindings.Attach(Rbx,
                    options ?? new CoreAiLuaAiOptions { AllowAgentsFromLua = true }, Npc, Director, null, null,
                    new NpcSpeechAnchorRegistry(), null, useDefaultTextBackend ? null : Text, null,
                    out LuaCsRbxAiServiceAdapter adapter);
                Adapter = adapter;
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    RbxApi = Rbx,
                    Capabilities = ceiling,
                    OneOffCapabilities = ceiling,
                    AdditionalGameplayBindings = hook
                });
            }

            public LuaCsRbxApiBindings Rbx { get; }

            public LuaCsModStack Stack { get; }

            public ScriptedNpcService Npc { get; }

            public ScriptedTextBackend Text { get; }

            public NpcDirector Director { get; }

            public LuaCsRbxAiServiceAdapter Adapter { get; }

            public void Load(string source, LuaCapabilities capabilities = LuaCapabilities.All, string modId = "village")
            {
                Stack.Runtime.LoadMod(modId, source, capabilities, false);
            }

            public object Attribute(string name)
            {
                return Rbx.Game.FindFirstChildOfClass("Workspace").GetAttribute(name);
            }

            public void Advance()
            {
                Rbx.Scheduler.Advance(0d);
            }

            public void AdvanceUntil(Func<bool> done)
            {
                for (int frame = 0; frame < 200 && !done(); frame++)
                {
                    Advance();
                }

                Assert.IsTrue(done(), "The world did not settle within 200 frames.");
            }

            public void Dispose()
            {
                if (_ownsDirector)
                {
                    Director.Dispose();
                }

                Rbx.Dispose();
            }
        }

        private sealed class ManualClock : INpcClock
        {
            public double NowSeconds => 0d;
        }

        /// <summary>A text backend that answers synchronously, or holds every request until released.</summary>
        private sealed class ScriptedTextBackend : IRbxTextGenerationBackend
        {
            private readonly List<TaskCompletionSource<RbxTextGenerationResult>> _held = new();

            public readonly List<RbxTextGenerationRequest> Requests = new();

            public Func<RbxTextGenerationRequest, string> Reply = _ => "scripted text";

            public bool Hold;

            public Task<RbxTextGenerationResult> GenerateAsync(RbxTextGenerationRequest request,
                CancellationToken cancellationToken)
            {
                Requests.Add(request);
                if (Hold)
                {
                    TaskCompletionSource<RbxTextGenerationResult> held = new();
                    _held.Add(held);
                    return held.Task;
                }

                return Task.FromResult(Answer(Reply(request)));
            }

            public void Release(string text)
            {
                foreach (TaskCompletionSource<RbxTextGenerationResult> held in _held)
                {
                    held.TrySetResult(Answer(text));
                }

                _held.Clear();
            }

            private static RbxTextGenerationResult Answer(string text)
            {
                return new RbxTextGenerationResult { Ok = true, GeneratedText = text, Model = "scripted-model" };
            }
        }

        /// <summary>A scripted orchestration service: answers every turn synchronously, by script or with a failure.</summary>
        private sealed class ScriptedNpcService : IAiOrchestrationService, IAiTaskResultService
        {
            public readonly List<AiTaskRequest> Requests = new();

            public Func<AiTaskRequest, string> Reply = _ => "Hello.";

            public LlmErrorCode FailWith = LlmErrorCode.None;

            public string FailText = "";

            public bool SupportsTaskResults => true;

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("Use the typed path.");
            }

            public Task<LlmCompletionResult> RunTaskResultAsync(AiTaskRequest task,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(task);
                return Task.FromResult(FailWith != LlmErrorCode.None
                    ? new LlmCompletionResult { Ok = false, Error = FailText, ErrorCode = FailWith }
                    : new LlmCompletionResult { Ok = true, Content = Reply(task), Model = "scripted-npc" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(AiTaskRequest task,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Requests.Add(task);
                await Task.CompletedTask;
                if (FailWith != LlmErrorCode.None)
                {
                    yield return new LlmStreamChunk { IsDone = true, Error = FailText, ErrorCode = FailWith };
                    yield break;
                }

                yield return new LlmStreamChunk { Text = Reply(task) };
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }
    }
}
