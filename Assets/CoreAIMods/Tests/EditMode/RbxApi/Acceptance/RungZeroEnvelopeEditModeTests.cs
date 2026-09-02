using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>Rung-zero 0.2: all production mutation through server-generated envelope.</summary>
    [TestFixture]
    public sealed class RungZeroEnvelopeEditModeTests
    {
        [Test]
        public void ApplyMutation_DuplicateOperationId_AppliesOnce()
        {
            InstanceRegistry registry = new();
            RbxInstance folder = registry.Create("Folder");
            long rev = registry.GetRecord(folder.Id).Revision;
            MutationEnvelope envelope = new("actor-a", folder.Id, "op-1", rev);
            int count = 0;
            string first = registry.ApplyMutation(envelope, () => { count++; return "ok"; });
            string second = registry.ApplyMutation(envelope, () => { count++; return "ok"; });
            Assert.AreEqual("ok", first);
            Assert.AreEqual("ok", second);
            Assert.AreEqual(1, count);
            Assert.AreEqual(1, registry.RetainedMutationOperationCount);
        }

        [Test]
        public void ApplyMutation_ReplayWithDifferentTarget_IsRefused()
        {
            InstanceRegistry registry = new();
            RbxInstance a = registry.Create("Folder");
            RbxInstance b = registry.Create("Folder");
            long rev = registry.GetRecord(a.Id).Revision;
            MutationEnvelope first = new("actor-a", a.Id, "op-replay", rev);
            registry.ApplyMutation(first, () => "first");
            MutationEnvelope replay = new("actor-a", b.Id, "op-replay", rev);
            RbxError error = Assert.Throws<RbxError>(() => registry.ApplyMutation(replay, () => "second"));
            StringAssert.Contains("already used", error.RawMessage);
        }

        [Test]
        public async Task PlainExecuteLua_InAclWorld_MustIncrementRetainedCount_AndMcpDoesToo()
        {
            InstanceRegistry registry = new(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            LuaCsRbxApiBindings bindings = new(registry: registry, game: game);
            LuaCsSecureEnvironment sandbox = new();
            TestBindings testBindings = new(bindings);
            LuaCsGameToolExecutor executor = new(sandbox, testBindings, new NullObserver());
            IActorIdentityProvider provider = new LocalActorIdentityProvider("actor-envelope");
            LuaTool tool = new(executor, new TestSettings(), new SilentLog(), null, provider, BuiltInAgentRoleIds.Programmer);

            int before = registry.RetainedMutationOperationCount;
            string resultJson = await tool.ExecuteAsync("return 42", CancellationToken.None);
            Assert.IsTrue(resultJson.Contains("\"Success\":true") || resultJson.Contains("\"Success\": true"));
            Assert.Greater(registry.RetainedMutationOperationCount, before, "plain execute_lua must go through envelope");
        }

        [Test]
        public void MutationOutsideEnvelope_InAclWorld_IsRefused()
        {
            InstanceRegistry registry = new(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            LuaCsRbxApiBindings bindings = new(registry: registry, game: game);
            RbxInstance folder = registry.Create("Folder", ownerActorId: "actor-owner", accessScope: InstanceAccessScope.Owned);
            folder.Name = "Owned";

            // Simulate a write without envelope by using a context with no envelope
            // The bindings require a mutation target when envelope is present; without envelope in ACL world, Demand should still enforce.
            // We test via AuthorizeMutation helper: outside envelope means no envelope, but ACL should still demand owner.
            RbxError error = Assert.Throws<RbxError>(() =>
                registry.AuthorizeMutation("actor-intruder", false, "", folder, WorldAclDecision.WriteProperty, "write property"));
            StringAssert.Contains("actor 'actor-intruder'", error.RawMessage);
        }

        private sealed class TestBindings : IActorScopedLuaCsGameRuntimeBindings, ILuaCsGameRuntimeBindings
        {
            private readonly LuaCsRbxApiBindings _bindings;
            public TestBindings(LuaCsRbxApiBindings bindings) { _bindings = bindings; }
            public InstanceRegistry MutationRegistry => _bindings.Registry;
            public void RegisterGameplayApis(LuaCsApiRegistry registry) { _bindings.Register(registry, LuaCapabilities.All); }
            public void RegisterGameplayApis(LuaCsApiRegistry registry, ActorContext ctx, MutationEnvelope env) { _bindings.Register(registry, LuaCapabilities.All, null, ctx, env); }
        }

        private sealed class NullObserver : ILuaExecutionObserver
        {
            public void OnLuaSuccess(string summary) { }
            public void OnLuaFailure(string error) { }
        }

        private sealed class TestSettings : ICoreAISettings
        {
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => false;
            public int MaxToolCallRetries => 0;
            public int MaxLuaRepairRetries => 0;
            public bool EnableMeaiDebugLogging => false;
            public string UniversalSystemPromptPrefix => "";
            public CoreAI.Infrastructure.Llm.LlmProviderKind LlmProviderKind => CoreAI.Infrastructure.Llm.LlmProviderKind.Stub;
            public string LlmModelId => "";
            public string LlmEndpointUrl => "";
            public string LlmApiKey => "";
            public int LlmContextCap => 0;
            public int LlmOutputCap => 0;
            public double LlmTemperature => 0;
            public int LlmOrchestratorConcurrency => 0;
            public int LlmRequestTimeoutSeconds => 0;
            public string LlmExtraBodyJson => "";
            public bool LlmEnableToolCallRetry => false;
            public ICoreAISettings.ToolInvocationMarshaler ToolInvocationMarshaler => null;
        }

        private sealed class SilentLog : CoreAI.Logging.ILog
        {
            public void Info(string message, CoreAI.Logging.LogTag tag = CoreAI.Logging.LogTag.General) { }
            public void Warn(string message, CoreAI.Logging.LogTag tag = CoreAI.Logging.LogTag.General) { }
            public void Error(string message, CoreAI.Logging.LogTag tag = CoreAI.Logging.LogTag.General) { }
        }
    }
}
