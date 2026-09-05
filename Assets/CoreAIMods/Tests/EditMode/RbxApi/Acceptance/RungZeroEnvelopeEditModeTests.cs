using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Lua;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>Real production-stack coverage for server-issued mutation envelopes.</summary>
    [TestFixture]
    public sealed class RungZeroEnvelopeEditModeTests
    {
        private const LuaCapabilities Capabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit;

        [Test]
        public async Task PlainExecuteLua_ServerEnvelope_AppliesOneMutationBatch()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("execute-lua-actor");
            LuaTool tool = new(
                harness.Stack.ToolExecutor,
                new StubSettings(),
                new RecordingLog(),
                new LuaGenerationRateLimiter(),
                new FixedActorIdentityProvider(actor),
                BuiltInAgentRoleIds.Programmer);
            int before = harness.Registry.RetainedMutationOperationCount;

            string result = await tool.ExecuteAsync(
                "workspace.Name = 'ToolEnvelopeWorkspace'",
                CancellationToken.None);

            StringAssert.Contains("\"Success\":true", result);
            Assert.AreEqual("ToolEnvelopeWorkspace", harness.Registry.WorldRoot.Name);
            Assert.AreEqual(
                before + 1, harness.Registry.RetainedMutationOperationCount);
        }

        [Test]
        public async Task ExplicitDuplicateOperationId_AppliesOnceThroughRealExecutor()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("duplicate-actor");
            RbxInstance target = harness.Registry.Create(
                "Folder",
                ownerActorId: actor.ActorId,
                accessScope: InstanceAccessScope.Owned);
            target.Name = "DuplicateTarget";
            target.Parent = harness.Registry.WorldRoot;
            InstanceRecord record = harness.Registry.GetRecord(target.Id);
            MutationEnvelope envelope = new(
                actor.ActorId, target.Id, "duplicate-operation", record.Revision);
            string code = @"
                local target = workspace:FindFirstChild('DuplicateTarget')
                local count = target:GetAttribute('DuplicateCount') or 0
                target:SetAttribute('DuplicateCount', count + 1)
                return target:GetAttribute('DuplicateCount')";

            LuaTool.LuaResult first = await harness.Stack.ToolExecutor.ExecuteAsync(
                code, actor, envelope, CancellationToken.None);
            LuaTool.LuaResult replay = await harness.Stack.ToolExecutor.ExecuteAsync(
                code, actor, envelope, CancellationToken.None);

            Assert.IsTrue(first.Success, first.Error);
            Assert.IsTrue(replay.Success, replay.Error);
            Assert.AreEqual("1", first.Output);
            Assert.AreEqual("1", replay.Output);
            Assert.AreEqual(1d, target.GetAttribute("DuplicateCount"));
            Assert.AreEqual(1, harness.Registry.RetainedMutationOperationCount);
        }

        [Test]
        public async Task MutationOutsideEnvelope_InAclWorld_IsRefused()
        {
            using ProductionHarness harness = new ProductionHarness();

            LuaTool.LuaResult result = await harness.Stack.ToolExecutor.ExecuteAsync(
                "workspace.Name = 'BareMutation'", CancellationToken.None);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("envelope", result.Error.ToLowerInvariant());
            Assert.AreEqual(0, harness.Registry.RetainedMutationOperationCount);
        }

        [Test]
        public void LuaToolSchema_ExposesOnlyServerIndependentCode()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("schema-actor");
            LuaLlmTool wrapper = new(
                harness.Stack.ToolExecutor,
                new StubSettings(),
                new RecordingLog(),
                new LuaGenerationRateLimiter(),
                new FixedActorIdentityProvider(actor),
                BuiltInAgentRoleIds.Programmer);

            string schema = wrapper.ParametersSchema;
            StringAssert.Contains("\"code\"", schema);
            Assert.IsFalse(schema.Contains("operation_id"));
            Assert.IsFalse(schema.Contains("target_instance_id"));
            Assert.IsFalse(schema.Contains("expected_revision"));
        }

        [Test]
        public void NetworkCodec_FrozenPayloadCap_IsCheckedBeforeDecode()
        {
            InstanceRegistry registry = new InstanceRegistry();
            LuaCsRbxNetworkCodec codec = new(
                registry, RbxEnumRegistry.CreateWithBuiltins(), null);
            Assert.AreEqual(65_536, LuaCsRbxNetworkCodec.MaxPayloadBytes);
            byte[] oversized = new byte[LuaCsRbxNetworkCodec.MaxPayloadBytes + 1];
            for (int index = 0; index < oversized.Length; index++)
            {
                oversized[index] = 0xff;
            }

            RbxError error = Assert.Throws<RbxError>(
                () => codec.DecodeArguments(oversized));
            Assert.AreEqual(RbxErrorCode.PayloadTooLarge, error.Code);
            StringAssert.Contains("65536", error.RawMessage);
            StringAssert.Contains("65537", error.RawMessage);
        }

        [Test]
        public void NetworkCodec_OversizedOutboundEnvelope_IsRefused()
        {
            InstanceRegistry registry = new InstanceRegistry();
            LuaCsRbxNetworkCodec codec = new(
                registry, RbxEnumRegistry.CreateWithBuiltins(), null);
            string oversized = new string(
                'x', LuaCsRbxNetworkCodec.MaxPayloadBytes);

            RbxError error = Assert.Throws<RbxError>(() => codec.EncodeArguments(
                new List<LuaValue> { oversized }));

            Assert.AreEqual(RbxErrorCode.PayloadTooLarge, error.Code);
        }

        [Test]
        public void DeliverNetworkEvent_UnknownSender_ReturnsErrorWithoutIdentityAllocation()
        {
            ForgingNetworkBridge bridge = new ForgingNetworkBridge();
            using ProductionHarness harness = new ProductionHarness(bridge);
            ActorContext admitted = harness.Actor("admitted-actor");
            harness.Bindings.ConnectActor(admitted);
            RbxRemoteEvent remote =
                (RbxRemoteEvent)harness.Registry.Create("RemoteEvent");
            LuaCsRbxNetworkCodec codec = new(
                harness.Registry, RbxEnumRegistry.CreateWithBuiltins(), null);
            byte[] payload = codec.EncodeArguments(new List<LuaValue>());
            int playerCount = harness.Bindings.Players.GetPlayers().Count;
            int instanceCount = harness.Registry.GetLiveInstances().Count;

            RbxError error = Assert.Throws<RbxError>(() => bridge.Emit(
                new RbxNetworkEventMessage(
                    remote.Id,
                    RbxNetworkDirection.ClientToServer,
                    RbxNetworkReliability.ReliableOrdered,
                    "forged-actor",
                    null,
                    payload)));

            Assert.AreEqual(RbxErrorCode.NotAuthority, error.Code);
            StringAssert.Contains("forged-actor", error.RawMessage);
            Assert.AreEqual(
                playerCount, harness.Bindings.Players.GetPlayers().Count);
            Assert.AreEqual(instanceCount, harness.Registry.GetLiveInstances().Count);
            Assert.IsFalse(harness.Bindings.Players.TryGetByActorId(
                "forged-actor", out _));
            CollectionAssert.DoesNotContain(bridge.ActorIds, "forged-actor");
        }

        private sealed class ProductionHarness : IDisposable
        {
            public ProductionHarness(INetworkBridge bridge = null)
            {
                Registry = new InstanceRegistry(
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                    worldId: "envelope-world");
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bindings = new LuaCsRbxApiBindings(
                    Registry, game, networkBridge: bridge);
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = new MemoryStore(),
                    Capabilities = Capabilities,
                    OneOffCapabilities = Capabilities,
                    RbxApi = Bindings
                });
            }

            public InstanceRegistry Registry { get; }

            public LuaCsRbxApiBindings Bindings { get; }

            public LuaCsModStack Stack { get; }

            public ActorContext Actor(string actorId)
            {
                return new LocalActorIdentityProvider(
                        actorId,
                        "session-" + actorId,
                        Registry.WorldId,
                        ActorGrantSet.None,
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
            }

            public void Dispose()
            {
                Bindings.Dispose();
            }
        }

        private sealed class FixedActorIdentityProvider : IActorIdentityProvider
        {
            private readonly ActorContext _actorContext;

            public FixedActorIdentityProvider(ActorContext actorContext)
            {
                _actorContext = actorContext;
            }

            public ActorContext GetActorContext(string roleId)
            {
                return _actorContext;
            }
        }

        private sealed class ForgingNetworkBridge : INetworkBridge
        {
            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public event System.Action<RbxNetworkPeerDisconnected> PeerDisconnected
            {
                add { }
                remove { }
            }

            private readonly List<string> _actorIds = new();

            public RbxNetworkTopology Topology => RbxNetworkTopology.Host;

            public IReadOnlyList<string> ActorIds => _actorIds;

            public event Action<RbxNetworkEventMessage> EventReceived;

            public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder>
                RequestReceived
            {
                add
                {
                }
                remove
                {
                }
            }

            public void RegisterActor(string actorId)
            {
                if (!_actorIds.Contains(actorId))
                {
                    _actorIds.Add(actorId);
                }
            }

            public void UnregisterActor(string actorId)
            {
                _actorIds.Remove(actorId);
            }

            public void SendEvent(RbxNetworkEventMessage message)
            {
                EventReceived?.Invoke(message);
            }

            public void SendRequest(RbxNetworkRequestMessage message,
                Action<RbxNetworkResponse> response)
            {
                throw new NotSupportedException();
            }

            public void Emit(RbxNetworkEventMessage message)
            {
                EventReceived?.Invoke(message);
            }
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue(
                    (modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> removed = new();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (string.Equals(key.ModId, modId,
                            StringComparison.Ordinal))
                    {
                        removed.Add(key);
                    }
                }

                for (int index = 0; index < removed.Count; index++)
                {
                    _values.Remove(removed[index]);
                }
            }
        }

        private sealed class StubSettings : ICoreAISettings
        {
            public int MaxLuaRepairRetries => 0;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 0;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public int ContextWindowTokens => 0;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0f;
            public int MaxToolCallRetries => 0;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => false;
            public bool AllowWorldPrimitives => true;
        }

        private sealed class RecordingLog : CoreAI.Logging.ILog
        {
            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
            }

            public void Error(string message, string tag = null)
            {
            }
        }

        private sealed class SilentGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }
        }
    }
}
