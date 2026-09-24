using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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

        private const string ForgedClientActorId = "forged-client-actor";
        private const string ServerEventProbeModId = "server-event-probe";
        private const string ServerInvokeProbeModId = "server-invoke-probe";
        private const string InvokeClientProbeModId = "invoke-client-probe";

        private const string DescribeLua = @"
                local function describe(value)
                    if value == nil then
                        return 'nil'
                    end
                    return value.Name
                end";

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

        [Test]
        public void ClientEvent_NamingAServerStorageChild_ReachesOnServerEventAsNil()
        {
            ForgingNetworkBridge bridge = new ForgingNetworkBridge();
            using ProductionHarness harness = new ProductionHarness(bridge);
            RbxInstance vault = harness.CreatePart("AdminVault", harness.ServerStorage);
            RbxInstance remote = LoadServerEventProbe(harness);
            ActorContext client = harness.Actor(ForgedClientActorId);
            harness.Bindings.ConnectActor(client);
            harness.Pump();

            bridge.Emit(ClientEvent(remote, client.ActorId, vault));
            harness.Pump();

            Assert.AreEqual("nil", harness.Store.Get(ServerEventProbeModId, "first"),
                "a client cannot name a ServerStorage object: the handler must receive nil, as Roblox passes it");
        }

        [Test]
        public void ClientEvent_NamingAWorkspacePart_ReachesOnServerEventAsThatPart()
        {
            ForgingNetworkBridge bridge = new ForgingNetworkBridge();
            using ProductionHarness harness = new ProductionHarness(bridge);
            RbxInstance crate = harness.CreatePart("VisibleCrate", harness.Registry.WorldRoot);
            RbxInstance remote = LoadServerEventProbe(harness);
            ActorContext client = harness.Actor(ForgedClientActorId);
            harness.Bindings.ConnectActor(client);
            harness.Pump();

            bridge.Emit(ClientEvent(remote, client.ActorId, crate));
            harness.Pump();

            Assert.AreEqual("VisibleCrate", harness.Store.Get(ServerEventProbeModId, "first"),
                "a replicated Workspace part must still reach the handler as that part");
        }

        [Test]
        public void ClientEvent_NamingAnotherPlayersBackpackTool_IsNil_ButTheSendersOwnToolResolves()
        {
            ForgingNetworkBridge bridge = new ForgingNetworkBridge();
            using ProductionHarness harness = new ProductionHarness(bridge);
            RbxInstance remote = LoadServerEventProbe(harness);
            ActorContext client = harness.Actor(ForgedClientActorId);
            ActorContext other = harness.Actor("other-client-actor");
            RbxPlayer senderPlayer = harness.Bindings.ConnectActor(client);
            RbxPlayer otherPlayer = harness.Bindings.ConnectActor(other);
            RbxInstance ownTool = harness.CreatePart(
                "OwnTool", senderPlayer.FindFirstChild("Backpack"));
            RbxInstance otherTool = harness.CreatePart(
                "OtherTool", otherPlayer.FindFirstChild("Backpack"));
            harness.Pump();

            bridge.Emit(ClientEvent(remote, client.ActorId, ownTool, otherTool));
            harness.Pump();

            Assert.AreEqual("OwnTool", harness.Store.Get(ServerEventProbeModId, "first"),
                "the admitted sender sees its own Backpack, so its own tool must resolve");
            Assert.AreEqual("nil", harness.Store.Get(ServerEventProbeModId, "second"),
                "another player's Backpack is replicated to its owner only, so the sender cannot name it");
        }

        [Test]
        public void ClientInvoke_NamingAServerStorageChild_ReachesOnServerInvokeAsNil()
        {
            ForgingNetworkBridge bridge = new ForgingNetworkBridge();
            using ProductionHarness harness = new ProductionHarness(bridge);
            RbxInstance vault = harness.CreatePart("AdminVault", harness.ServerStorage);
            RbxInstance remote = LoadServerInvokeProbe(harness);
            ActorContext client = harness.Actor(ForgedClientActorId);
            harness.Bindings.ConnectActor(client);
            harness.Pump();
            RbxNetworkResponse answer = null;

            bridge.EmitRequest(ClientRequest(remote, client.ActorId, vault),
                response => answer = response);
            harness.Pump();

            Assert.IsNotNull(answer, "OnServerInvoke must answer the forged request");
            Assert.IsTrue(answer.Succeeded, answer.Error);
            Assert.AreEqual("nil", harness.Store.Get(ServerInvokeProbeModId, "first"),
                "a client cannot name a ServerStorage object: OnServerInvoke must receive nil");
        }

        [Test]
        public void ClientInvoke_NamingAWorkspacePart_ReachesOnServerInvokeAsThatPart()
        {
            ForgingNetworkBridge bridge = new ForgingNetworkBridge();
            using ProductionHarness harness = new ProductionHarness(bridge);
            RbxInstance crate = harness.CreatePart("VisibleCrate", harness.Registry.WorldRoot);
            RbxInstance remote = LoadServerInvokeProbe(harness);
            ActorContext client = harness.Actor(ForgedClientActorId);
            harness.Bindings.ConnectActor(client);
            harness.Pump();
            RbxNetworkResponse answer = null;

            bridge.EmitRequest(ClientRequest(remote, client.ActorId, crate),
                response => answer = response);
            harness.Pump();

            Assert.IsNotNull(answer, "OnServerInvoke must answer the forged request");
            Assert.IsTrue(answer.Succeeded, answer.Error);
            Assert.AreEqual("VisibleCrate", harness.Store.Get(ServerInvokeProbeModId, "first"),
                "a replicated Workspace part must still reach OnServerInvoke as that part");
        }

        [Test]
        public void InvokeClientResponse_NamingAServerStorageChild_ReturnsNilToTheServerCaller()
        {
            ForgingNetworkBridge bridge = new ForgingNetworkBridge();
            using ProductionHarness harness = new ProductionHarness(bridge);
            RbxInstance vault = harness.CreatePart("AdminVault", harness.ServerStorage);

            string returned = InvokeClientAndAnswer(harness, bridge, vault);

            Assert.AreEqual("nil", returned,
                "an InvokeClient answer is client-authored: a ServerStorage object it names must return nil");
        }

        [Test]
        public void InvokeClientResponse_NamingAWorkspacePart_ReturnsThatPartToTheServerCaller()
        {
            ForgingNetworkBridge bridge = new ForgingNetworkBridge();
            using ProductionHarness harness = new ProductionHarness(bridge);
            RbxInstance crate = harness.CreatePart("VisibleCrate", harness.Registry.WorldRoot);

            string returned = InvokeClientAndAnswer(harness, bridge, crate);

            Assert.AreEqual("VisibleCrate", returned,
                "a replicated Workspace part in an InvokeClient answer must still return as that part");
        }

        [Test]
        public void HiddenClientReference_IsReportedThroughTheBindingsLog_AsExactlyOneLine()
        {
            List<string> log = new();
            ForgingNetworkBridge bridge = new ForgingNetworkBridge();
            using ProductionHarness harness = new ProductionHarness(bridge, log.Add);
            RbxInstance vault = harness.CreatePart("AdminVault", harness.ServerStorage);
            RbxInstance remote = harness.Registry.Create("RemoteEvent");
            remote.Name = "LoggedRemote";
            remote.Parent = harness.Registry.WorldRoot;
            ActorContext client = harness.Actor(ForgedClientActorId);
            harness.Bindings.ConnectActor(client);
            int before = log.Count;

            bridge.Emit(ClientEvent(remote, client.ActorId, vault));

            List<string> emitted = log.GetRange(before, log.Count - before);
            Assert.AreEqual(1, emitted.Count,
                "one hidden reference must produce exactly one line in the bindings' log; got: "
                + string.Join(" | ", emitted));
            StringAssert.Contains("'" + ForgedClientActorId + "'", emitted[0]);
            StringAssert.Contains(
                "InstanceId " + vault.Id.Value.ToString(CultureInfo.InvariantCulture), emitted[0]);
            StringAssert.Contains("cannot see", emitted[0]);
        }

        private static RbxInstance LoadServerEventProbe(ProductionHarness harness)
        {
            harness.LoadServerMod(ServerEventProbeModId, DescribeLua + @"
                local remote = Instance.new('RemoteEvent')
                remote.Name = 'ServerEventProbe'
                remote.Parent = workspace
                remote.OnServerEvent:Connect(function(player, first, second)
                    store_set('first', describe(first))
                    store_set('second', describe(second))
                end)");
            return RequireWorkspaceChild(harness, "ServerEventProbe");
        }

        private static RbxInstance LoadServerInvokeProbe(ProductionHarness harness)
        {
            harness.LoadServerMod(ServerInvokeProbeModId, DescribeLua + @"
                local remote = Instance.new('RemoteFunction')
                remote.Name = 'ServerInvokeProbe'
                remote.Parent = workspace
                remote.OnServerInvoke = function(player, first)
                    store_set('first', describe(first))
                    return 'handled'
                end");
            return RequireWorkspaceChild(harness, "ServerInvokeProbe");
        }

        /// <summary>
        /// Admits a client, has a server mod call InvokeClient on it, answers that request with a
        /// client-authored payload naming <paramref name="named"/>, and returns what the server-side
        /// caller received (the value's Name, or "nil").
        /// </summary>
        private static string InvokeClientAndAnswer(ProductionHarness harness,
            ForgingNetworkBridge bridge, RbxInstance named)
        {
            ActorContext client = harness.Actor(ForgedClientActorId);
            RbxPlayer player = harness.Bindings.ConnectActor(client);
            harness.LoadServerMod(InvokeClientProbeModId, DescribeLua + @"
                local Players = game:GetService('Players')
                local remote = Instance.new('RemoteFunction')
                remote.Name = 'InvokeClientProbe'
                remote.Parent = workspace
                task.spawn(function()
                    local target = Players:GetPlayerByUserId("
                + player.UserId.ToString(CultureInfo.InvariantCulture) + @")
                    local ok, returned = pcall(function()
                        return remote:InvokeClient(target, 'probe')
                    end)
                    if ok then
                        store_set('returned', describe(returned))
                    else
                        store_set('returned', 'error: ' .. tostring(returned))
                    end
                end)");
            harness.Pump();

            Assert.AreEqual(1, bridge.SentRequests.Count,
                "the server mod must have sent exactly one InvokeClient request; mod said: "
                + harness.Store.Get(InvokeClientProbeModId, "returned"));
            SentRequest sent = bridge.SentRequests[0];
            Assert.AreEqual(RbxNetworkDirection.ServerToClient, sent.Message.Direction);
            Assert.AreEqual(client.ActorId, sent.Message.RecipientActorId);
            Assert.AreEqual("", harness.Store.Get(InvokeClientProbeModId, "returned"),
                "the caller must still be waiting for the client's answer");

            sent.Respond(RbxNetworkResponse.Success(InstancePayload(named)));
            harness.Pump();

            return harness.Store.Get(InvokeClientProbeModId, "returned");
        }

        private static RbxInstance RequireWorkspaceChild(ProductionHarness harness, string name)
        {
            RbxInstance child = harness.Registry.WorldRoot.FindFirstChild(name);
            Assert.IsNotNull(child, name + " must have been created by the server mod");
            return child;
        }

        private static RbxNetworkEventMessage ClientEvent(RbxInstance remote, string senderActorId,
            params RbxInstance[] named)
        {
            return new RbxNetworkEventMessage(
                remote.Id,
                RbxNetworkDirection.ClientToServer,
                RbxNetworkReliability.ReliableOrdered,
                senderActorId,
                null,
                InstancePayload(named));
        }

        private static RbxNetworkRequestMessage ClientRequest(RbxInstance remote,
            string senderActorId, params RbxInstance[] named)
        {
            return new RbxNetworkRequestMessage(
                remote.Id,
                RbxNetworkDirection.ClientToServer,
                senderActorId,
                null,
                InstancePayload(named));
        }

        /// <summary>
        /// The wire form a modified client writes by hand: one <c>{"$rbx":"Instance","id":"N"}</c>
        /// tag per argument, with ids read off the server's registry.
        /// </summary>
        private static byte[] InstancePayload(params RbxInstance[] named)
        {
            StringBuilder json = new("[");
            for (int index = 0; index < named.Length; index++)
            {
                if (index > 0)
                {
                    json.Append(',');
                }

                json.Append("{\"$rbx\":\"Instance\",\"id\":\"")
                    .Append(named[index].Id.Value.ToString(CultureInfo.InvariantCulture))
                    .Append("\"}");
            }

            json.Append(']');
            return Encoding.UTF8.GetBytes(json.ToString());
        }

        private sealed class ProductionHarness : IDisposable
        {
            public ProductionHarness(INetworkBridge bridge = null, Action<string> log = null)
            {
                Registry = new InstanceRegistry(
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                    worldId: "envelope-world");
                Game = DataModelBootstrap.CreateGame(Registry);
                Bindings = new LuaCsRbxApiBindings(
                    Registry, Game, log: log, networkBridge: bridge);
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    Capabilities = Capabilities,
                    OneOffCapabilities = Capabilities,
                    RbxApi = Bindings
                });
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public RbxInstance ServerStorage => Game.GetService("ServerStorage");

            public LuaCsRbxApiBindings Bindings { get; }

            public MemoryStore Store { get; }

            public LuaCsModStack Stack { get; }

            public void LoadServerMod(string modId, string source)
            {
                ActorContext host = CoreAI.Composition.CoreServicesInstaller
                    .DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                Stack.Runtime.LoadMod(host, modId, source, persistToStore: false);
            }

            public RbxInstance CreatePart(string name, RbxInstance parent)
            {
                Assert.IsNotNull(parent, "the parent container must exist for " + name);
                RbxInstance part = Registry.Create("Part");
                part.Name = name;
                part.Parent = parent;
                return part;
            }

            /// <summary>
            /// Runs a few zero-length scheduler frames: deferred remote dispatch, the spawned
            /// RemoteFunction callback and a resumed InvokeClient waiter each need one drain.
            /// </summary>
            public void Pump()
            {
                for (int frame = 0; frame < 4; frame++)
                {
                    Bindings.Scheduler.Advance(0d);
                }
            }

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
                RequestReceived;

            /// <summary>Requests the world sent outward (InvokeClient), held for the test to answer.</summary>
            public List<SentRequest> SentRequests { get; } = new();

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
                SentRequests.Add(new SentRequest(message, response));
            }

            public void Emit(RbxNetworkEventMessage message)
            {
                EventReceived?.Invoke(message);
            }

            /// <summary>Delivers a request as if a client had sent it over the wire.</summary>
            public void EmitRequest(RbxNetworkRequestMessage message,
                Action<RbxNetworkResponse> response)
            {
                RequestReceived?.Invoke(message, new RbxNetworkRequestResponder(response));
            }
        }

        private sealed class SentRequest
        {
            public SentRequest(RbxNetworkRequestMessage message, Action<RbxNetworkResponse> respond)
            {
                Message = message;
                Respond = respond;
            }

            public RbxNetworkRequestMessage Message { get; }

            public Action<RbxNetworkResponse> Respond { get; }
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
