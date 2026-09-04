using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using CoreAI.Unity.Logging;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// End-to-end proof of the Roblox MVP1 Lua surface (roadmap §5.1.3) through the REAL mod
    /// runtime: corpus-style snippets loaded via <see cref="LuaCsModRuntimeFactory"/> exercising
    /// datatype constructors/operators, Enum access, Instance.new over the registry whitelist,
    /// game/workspace navigation, §5.2.7 error texts, ownership/origin attribution, and
    /// capability gating. Test names cite rule ids where one applies (§6.6).
    /// </summary>
    [TestFixture]
    public sealed class RbxApiLuaBindingsEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>Same sync-over-async hazard as LuaCsModRuntimeEditModeTests: detach Unity's
        /// SynchronizationContext so VM continuations complete on the thread pool.</summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
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
                List<(string ModId, string Key)> keys = new();
                foreach ((string storedModId, string key) in _values.Keys)
                {
                    if (storedModId == modId)
                    {
                        keys.Add((storedModId, key));
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class FakeGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private sealed class EmptySourceStore : ILuaModSourceStore
        {
            public void Save(string id, string source, LuaModManifest manifest)
            {
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                source = null;
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                return Array.Empty<LuaModManifest>();
            }

            public void SetActive(string id, bool active)
            {
            }

            public void Delete(string id)
            {
            }
        }

        private sealed class TrackingNetworkBridge : INetworkBridge
        {
            private readonly List<string> _actors = new();
            private Action<RbxNetworkEventMessage> _eventReceived;
            private Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> _requestReceived;

            public RbxNetworkTopology Topology => RbxNetworkTopology.Solo;

            public IReadOnlyList<string> ActorIds => _actors;

            public int EventSubscriberCount => _eventReceived?.GetInvocationList().Length ?? 0;

            public int RequestSubscriberCount => _requestReceived?.GetInvocationList().Length ?? 0;

            public bool DropRequests { get; set; }

            public Action<RbxNetworkResponse> LastResponse { get; private set; }

            public event Action<RbxNetworkEventMessage> EventReceived
            {
                add => _eventReceived += value;
                remove => _eventReceived -= value;
            }

            public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived
            {
                add => _requestReceived += value;
                remove => _requestReceived -= value;
            }

            public void RegisterActor(string actorId)
            {
                if (!_actors.Contains(actorId))
                {
                    _actors.Add(actorId);
                }
            }

            public void UnregisterActor(string actorId)
            {
                _actors.Remove(actorId);
            }

            public void SendEvent(RbxNetworkEventMessage message)
            {
                _eventReceived?.Invoke(message);
            }

            public void SendRequest(RbxNetworkRequestMessage message,
                Action<RbxNetworkResponse> response)
            {
                LastResponse = response;
                if (DropRequests)
                {
                    return;
                }

                Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> receiver =
                    _requestReceived;
                if (receiver != null)
                {
                    receiver(message, new RbxNetworkRequestResponder(response));
                }
            }
        }

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox,
            MemoryStore store = null, LuaCapabilities caps = LuaCapabilities.All)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store ?? new MemoryStore(),
                Capabilities = caps,
                OneOffCapabilities = caps,
                RbxApi = roblox
            });
        }

        private static Exception LoadFails(LuaCsModStack stack, string modId, string code)
        {
            Exception ex = Assert.Catch(() => stack.Runtime.LoadMod(modId, code));
            return ex;
        }

        private static string FullText(Exception ex)
        {
            return ex.ToString();
        }

        private static ActorContext Actor(string actorId)
        {
            return new LocalActorIdentityProvider(actorId)
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private static LuaCsRbxApiBindings StrictWorld(out InstanceRegistry instanceRegistry)
        {
            instanceRegistry = new InstanceRegistry(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            return new LuaCsRbxApiBindings(registry: instanceRegistry);
        }

        private static RbxInstance CreateActorInstance(InstanceRegistry registry,
            ActorContext actorContext, string modId, string className)
        {
            string originTag = OriginTag.FromMod(modId);
            registry.BindActorAttribution(modId, originTag, actorContext.ActorId);
            return registry.CreateScripted(className, modId, originTag);
        }

        private static InstanceRecord Record(InstanceRegistry registry, RbxInstance instance)
        {
            Assert.IsTrue(registry.TryGetRecord(instance.Id, out InstanceRecord record));
            return record;
        }

        private static LuaTool.LuaResult ExecuteMutation(LuaCsModStack stack,
            ActorContext actorContext, MutationEnvelope envelope, string source)
        {
            return stack.ToolExecutor.ExecuteAsync(
                    source, actorContext, envelope, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        private static void RunActorLua(LuaCsRbxApiBindings bindings, ActorContext actorContext,
            string modId, string source,
            params (string Name, RbxInstance Instance)[] instanceGlobals)
        {
            LuaCsRbxModContext context = new(
                bindings, LuaCapabilities.All, modId, OriginTag.FromMod(modId), actorContext);
            LuaCsScriptEngine engine = new();
            LuaCsApiRegistry registry = (LuaCsApiRegistry)engine.CreateFunctionRegistry();
            registry.RegisterValue("CFrame", LuaCsRbxDatatypeBindings.BuildCFrameGlobal);
            for (int index = 0; index < instanceGlobals.Length; index++)
            {
                (string Name, RbxInstance Instance) global = instanceGlobals[index];
                registry.RegisterValue(global.Name, () => context.WrapInstance(global.Instance));
            }

            IScriptState state = engine.CreateState();
            registry.ApplyTo(state);
            engine.RunChunk(state, source);
        }

        private sealed class ProductionNetworkHarness : IDisposable
        {
            private const string MissingWorldHostLog =
                "[CoreAI] [Core] [CoreAiMods] RbxWorldHost NOT resolved — mods run headless. " +
                "Instance.new / workspace mutations produce no GameObjects. " +
                "Check: (1) RbxWorldHost component exists in the scene, " +
                "(2) CoreAiModsLifetimeScope.robloxWorldHost is wired to it, " +
                "(3) link.xml preserves CoreAI.RbxApi.Binding assembly.";

            public ProductionNetworkHarness(INetworkBridge networkBridge = null)
            {
                ContainerBuilder builder = new();
                builder.RegisterInstance<IGameLogger>(new FakeGameLogger());
                GameLogSettingsOptions logSettings = new();
                IGameLogger compositionLogger = new FilteringGameLogger(
                    new UnityGameLogSink(logSettings), logSettings);
                builder.RegisterInstance<CoreAI.Logging.ILog>(new UnityLog(compositionLogger));
                builder.Register<NoopCommandSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
                builder.RegisterCoreAiMods(applicationIsPlayingProvider: () => false);
                Store = new MemoryStore();
                builder.RegisterInstance<ILuaModStore>(Store);
                builder.RegisterInstance<ILuaModSourceStore>(new EmptySourceStore());
                if (networkBridge != null)
                {
                    builder.RegisterInstance<INetworkBridge>(networkBridge);
                }

                Container = builder.Build();
                LogAssert.Expect(LogType.Error, MissingWorldHostLog);
                Runtime = Container.Resolve<ILuaModRuntime>();
                LuaCsModStack stack = Container.Resolve<LuaCsModStack>();
                Bindings = stack.GameplayBindings.RbxApi;
            }

            public IObjectResolver Container { get; }

            public ILuaModRuntime Runtime { get; }

            public LuaCsRbxApiBindings Bindings { get; }

            public MemoryStore Store { get; }

            public void PumpFrames(int frameCount)
            {
                ActorContext hostActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                for (int frame = 0; frame < frameCount; frame++)
                {
                    Bindings.Scheduler.Advance(0.016d);
                    Runtime.Tick(hostActor, 0.016d);
                }
            }

            public void Dispose()
            {
                Container.Dispose();
            }
        }

        private sealed class NoopCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        [Test]
        public void ExecuteLua_ProductionToolEntry_UsesServerGeneratedEnvelope()
        {
            const string actorId = "production-envelope-actor";
            CoreAISettingsOptions settings = new();
            ContainerBuilder builder = new();
            builder.RegisterInstance<IGameLogger>(new FakeGameLogger());
            builder.RegisterInstance<CoreAI.Logging.ILog>(CoreAI.Logging.NullLog.Instance);
            builder.Register<NoopCommandSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
            builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
            builder.RegisterInstance<ICoreAISettings>(settings);
            builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
            builder.RegisterInstance<IActorIdentityProvider>(new LocalActorIdentityProvider(
                actorId, "production-envelope-session", "", ActorGrantSet.None,
                AgentMemoryScope.Empty));
            builder.RegisterCoreAiMods(
                applicationIsPlayingProvider: () => false,
                skillTextProvider: _ => null);
            builder.RegisterInstance<ILuaModStore>(new MemoryStore());
            builder.RegisterInstance<ILuaModSourceStore>(new EmptySourceStore());

            IObjectResolver container = builder.Build();
            try
            {
                LuaCsModStack stack = container.Resolve<LuaCsModStack>();
                InstanceRegistry registry = stack.GameplayBindings.RbxApi.Registry;
                RbxInstance target = registry.Create(
                    "Folder", accessScope: InstanceAccessScope.SharedWritable);
                target.Name = "ProductionEnvelopeTarget";
                target.Parent = registry.WorldRoot;
                long initialRevision = Record(registry, target).Revision;
                ILlmTool executeLua = null;
                foreach (ILlmTool tool in container.Resolve<AgentMemoryPolicy>()
                             .GetToolsForRole(BuiltInAgentRoleIds.Programmer))
                {
                    if (tool.Name == LuaTool.ExecuteLuaToolName)
                    {
                        executeLua = tool;
                        break;
                    }
                }

                Assert.IsNotNull(executeLua,
                    "The shipped Programmer composition must expose execute_lua.");
                Assert.IsInstanceOf<IAIFunctionLlmTool>(executeLua);
                AIFunction function = ((IAIFunctionLlmTool)executeLua).CreateAIFunction();
                AIFunctionArguments arguments = new()
                {
                    ["code"] = @"
                        local target = workspace:FindFirstChild('ProductionEnvelopeTarget')
                        local count = target:GetAttribute('Count') or 0
                        target:SetAttribute('Count', count + 1)
                        return target:GetAttribute('Count')"
                };

                object resultRaw = function.InvokeAsync(arguments).GetAwaiter().GetResult();
                JObject result = JObject.Parse(resultRaw.ToString());

                Assert.IsTrue(result.Value<bool>("Success"), result.ToString());
                Assert.AreEqual("1", result.Value<string>("Output"));
                Assert.AreEqual(1d, target.GetAttribute("Count"));
                Assert.AreEqual(initialRevision + 1L, Record(registry, target).Revision);
                Assert.AreEqual(1, registry.RetainedMutationOperationCount);
            }
            finally
            {
                container.Dispose();
            }
        }

        [Test]
        public void MutationEnvelope_ResultRetention_IsBoundedPerActor()
        {
            const int expectedPerActorLimit =
                InstanceRegistry.DefaultMutationReplayCapacityPerActor;
            const string actorId = "bounded-cache-actor";
            InstanceRegistry registry = new();
            RbxInstance target = registry.Create(
                "Folder", accessScope: InstanceAccessScope.SharedWritable);
            long revision = Record(registry, target).Revision;

            for (int index = 0; index <= expectedPerActorLimit; index++)
            {
                MutationEnvelope envelope = new(
                    actorId,
                    target.Id,
                    "bounded-operation-" + index,
                    revision);
                registry.ApplyMutation(envelope, () =>
                {
                    revision = registry.AdvanceRevision(target.Id);
                    return index;
                });
            }

            Assert.LessOrEqual(registry.RetainedMutationOperationCount, expectedPerActorLimit);
        }

        [Test]
        public void MutationEnvelope_UnparentedInstanceNew_AdvancesCreationAnchorRevision()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            LuaCsModStack stack = BuildStack(bindings);
            RbxInstance anchor = registry.Create(
                "Folder", accessScope: InstanceAccessScope.SharedWritable);
            anchor.Name = "CreationAnchor";
            anchor.Parent = registry.WorldRoot;
            ActorContext actor = Actor("create-actor");
            long initialRevision = Record(registry, anchor).Revision;
            int initialCount = registry.Count;
            MutationEnvelope firstEnvelope = new(
                actor.ActorId, anchor.Id, "create-operation-1", initialRevision);

            LuaTool.LuaResult first = ExecuteMutation(
                stack, actor, firstEnvelope,
                "local created=Instance.new('Folder'); return created.ClassName");

            Assert.IsTrue(first.Success, first.Error);
            Assert.AreEqual("Folder", first.Output);
            Assert.Greater(registry.Count, initialCount);
            int countAfterFirst = registry.Count;
            Assert.AreEqual(initialRevision + 1L, Record(registry, anchor).Revision);

            MutationEnvelope staleEnvelope = new(
                actor.ActorId, anchor.Id, "create-operation-2", initialRevision);
            LuaTool.LuaResult stale = ExecuteMutation(
                stack, actor, staleEnvelope,
                "Instance.new('Folder'); return 'unexpected'");

            Assert.IsFalse(stale.Success);
            StringAssert.Contains("stale expected revision", stale.Error);
            Assert.AreEqual(countAfterFirst, registry.Count);
        }

        [Test]
        public void MutationEnvelope_Clone_AdvancesSourceRevision()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            LuaCsModStack stack = BuildStack(bindings);
            RbxInstance source = registry.Create(
                "Folder", accessScope: InstanceAccessScope.SharedWritable);
            source.Name = "CloneRevisionSource";
            source.Parent = registry.WorldRoot;
            ActorContext actor = Actor("clone-actor");
            long initialRevision = Record(registry, source).Revision;
            int initialCount = registry.Count;
            MutationEnvelope firstEnvelope = new(
                actor.ActorId, source.Id, "clone-operation-1", initialRevision);
            const string cloneSource = @"
                local source = workspace:FindFirstChild('CloneRevisionSource')
                local copy = source:Clone()
                return copy.Name";

            LuaTool.LuaResult first = ExecuteMutation(
                stack, actor, firstEnvelope, cloneSource);

            Assert.IsTrue(first.Success, first.Error);
            Assert.AreEqual("CloneRevisionSource", first.Output);
            Assert.Greater(registry.Count, initialCount);
            int countAfterFirst = registry.Count;
            Assert.AreEqual(initialRevision + 1L, Record(registry, source).Revision);

            MutationEnvelope staleEnvelope = new(
                actor.ActorId, source.Id, "clone-operation-2", initialRevision);
            LuaTool.LuaResult stale = ExecuteMutation(
                stack, actor, staleEnvelope, cloneSource);

            Assert.IsFalse(stale.Success);
            StringAssert.Contains("stale expected revision", stale.Error);
            Assert.AreEqual(countAfterFirst, registry.Count);
        }

        [Test]
        public void Lua_NetworkProductionPath_ClientRegistersBeforeServerWithoutPhantomPlayer()
        {
            using ProductionNetworkHarness harness = new();
            ActorContext clientActor = Actor("delivery-actor");
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            harness.Runtime.LoadMod(clientActor, "network-delivery-client", @"
                local Players = game:GetService('Players')
                local localPlayer = Players.LocalPlayer
                local listed = Players:GetPlayers()
                assert(localPlayer ~= nil)
                assert(#listed == 1 and listed[1] == localPlayer)
                assert(Players.PlayerAdded ~= nil and Players.PlayerRemoving ~= nil)
                task.defer(function()
                    task.wait()
                    local remote = workspace:WaitForChild('DeliveryRemote')
                    local clientValues = {}
                    remote.OnClientEvent:Connect(function(payload)
                        table.insert(clientValues, payload)
                        store_set('client', table.concat(clientValues, ','))
                    end)
                    remote:FireServer('server-payload')
                end)", persistToStore: false);

            harness.Runtime.LoadMod(serverActor, "network-delivery-server", @"
                local Players = game:GetService('Players')
                assert(Players.LocalPlayer == nil)
                local remote = Instance.new('RemoteEvent')
                remote.Name = 'DeliveryRemote'
                remote.Parent = workspace
                remote.OnServerEvent:Connect(function(player, payload)
                    store_set('server', tostring(player.UserId) .. ':' .. payload)
                end)
                task.defer(function()
                    task.wait()
                    task.wait()
                    local listed = Players:GetPlayers()
                    store_set('server_player_count', tostring(#listed))
                    local player = listed[#listed]
                    remote:FireClient(player, 'one')
                    remote:FireAllClients('all')
                end)", persistToStore: false);

            harness.PumpFrames(4);

            Assert.IsInstanceOf<NullNetworkBridge>(harness.Bindings.NetworkBridge);
            Assert.AreEqual("1:server-payload",
                harness.Store.Get("network-delivery-server", "server"));
            Assert.AreEqual("1",
                harness.Store.Get("network-delivery-server", "server_player_count"));
            Assert.AreEqual("one,all",
                harness.Store.Get("network-delivery-client", "client"));
        }

        [Test]
        public void NetworkActorRegistration_PlayerFailureDoesNotLeaveBridgeActor()
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            TrackingNetworkBridge bridge = new();
            LuaCsRbxApiBindings bindings = new(
                registry: registry, game: game, networkBridge: bridge);
            ActorContext actor = Actor("rejected-player-actor");
            LuaCsRbxModContext context = new(
                bindings, LuaCapabilities.All, "rejected-player-mod",
                OriginTag.FromMod("rejected-player-mod"), actor);
            registry.Registered += record =>
            {
                if (record.Instance is RbxPlayer)
                {
                    record.Instance.Destroy();
                    throw new InvalidOperationException("synthetic Player registration rejected");
                }
            };

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => bindings.GetLocalPlayer(context));

            StringAssert.Contains("registration rejected", error.Message);
            Assert.IsEmpty(bridge.ActorIds);
            Assert.IsEmpty(bindings.Players.GetPlayers());
        }

        [Test]
        public void Lua_NetworkProductionPath_ForeignActorCannotMutatePlayerIdentity()
        {
            using ProductionNetworkHarness harness = new();
            ActorContext ownerActor = Actor("player-owner-actor");
            ActorContext foreignActor = Actor("player-foreign-actor");
            harness.Runtime.LoadMod(ownerActor, "player-owner-mod", @"
                local player = game:GetService('Players').LocalPlayer
                assert(player ~= nil)", persistToStore: false);
            harness.Runtime.LoadMod(foreignActor, "player-foreign-mod", @"
                local Players = game:GetService('Players')
                local foreign = Players:GetPlayers()[1]
                assert(foreign ~= Players.LocalPlayer)
                local function capture(name, callback)
                    local ok, failure = pcall(callback)
                    store_set(name .. '_ok', tostring(ok))
                    store_set(name .. '_error', tostring(failure))
                end
                capture('name', function() foreign.Name = 'Hijacked' end)
                capture('attribute', function() foreign:SetAttribute('Admin', true) end)
                capture('tag', function() foreign:AddTag('Impersonated') end)",
                persistToStore: false);

            Assert.IsTrue(harness.Bindings.Players.TryGetByActorId(
                ownerActor.ActorId, out RbxPlayer ownerPlayer));
            Assert.AreEqual("Player1", ownerPlayer.Name);
            Assert.IsNull(ownerPlayer.GetAttribute("Admin"));
            Assert.IsFalse(ownerPlayer.HasTag("Impersonated"));
            Assert.IsTrue(harness.Bindings.Registry.TryGetRecord(
                ownerPlayer.Id, out InstanceRecord ownerRecord));
            Assert.IsTrue(ownerRecord.IsRuntimeInfrastructure);
            Assert.AreEqual(ownerActor.ActorId, ownerRecord.OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.Owned, ownerRecord.AccessScope);
            string[] mutations = { "name", "attribute", "tag" };
            for (int index = 0; index < mutations.Length; index++)
            {
                string mutation = mutations[index];
                Assert.AreEqual("false",
                    harness.Store.Get("player-foreign-mod", mutation + "_ok"));
                StringAssert.Contains("Owned by actor '" + ownerActor.ActorId + "'",
                    harness.Store.Get("player-foreign-mod", mutation + "_error"));
            }
        }

        [Test]
        public void Lua_NetworkProductionPath_DirectionAuthorityRefusesWrongSideWithActorAndReason()
        {
            using ProductionNetworkHarness harness = new();
            ActorContext clientActor = Actor("direction-client-actor");
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            harness.Runtime.LoadMod(clientActor, "network-direction-client", @"
                local Players = game:GetService('Players')
                local player = Players.LocalPlayer
                local event = Instance.new('RemoteEvent')
                local remote = Instance.new('RemoteFunction')
                remote.OnClientInvoke = function()
                    return 'client'
                end
                local function capture(name, callback)
                    local ok, failure = pcall(callback)
                    store_set(name .. '_ok', tostring(ok))
                    store_set(name .. '_error', tostring(failure))
                end
                capture('fire_client', function() event:FireClient(player) end)
                capture('fire_all_clients', function() event:FireAllClients() end)
                capture('on_server_event', function()
                    return event.OnServerEvent
                end)
                capture('on_server_invoke', function()
                    remote.OnServerInvoke = function() return 'server' end
                end)
                capture('invoke_client', function()
                    return remote:InvokeClient(player)
                end)", persistToStore: false);

            harness.Runtime.LoadMod(serverActor, "network-direction-server", @"
                local event = Instance.new('RemoteEvent')
                local remote = Instance.new('RemoteFunction')
                remote.OnServerInvoke = function()
                    return 'server'
                end
                local function capture(name, callback)
                    local ok, failure = pcall(callback)
                    store_set(name .. '_ok', tostring(ok))
                    store_set(name .. '_error', tostring(failure))
                end
                capture('fire_server', function() event:FireServer() end)
                capture('on_client_event', function()
                    return event.OnClientEvent
                end)
                capture('on_client_invoke', function()
                    remote.OnClientInvoke = function() return 'client' end
                end)
                capture('invoke_server', function()
                    return remote:InvokeServer()
                end)", persistToStore: false);

            harness.PumpFrames(8);

            string[] clientServerOnlyMembers =
            {
                "fire_client", "fire_all_clients", "on_server_event",
                "on_server_invoke", "invoke_client"
            };
            for (int index = 0; index < clientServerOnlyMembers.Length; index++)
            {
                string member = clientServerOnlyMembers[index];
                Assert.AreEqual("false",
                    harness.Store.Get("network-direction-client", member + "_ok"));
                string refusal = harness.Store.Get(
                    "network-direction-client", member + "_error");
                StringAssert.Contains("actor '" + clientActor.ActorId + "'", refusal);
                StringAssert.Contains("server-only", refusal);
            }

            string[] serverClientOnlyMembers =
            {
                "fire_server", "on_client_event", "on_client_invoke", "invoke_server"
            };
            for (int index = 0; index < serverClientOnlyMembers.Length; index++)
            {
                string member = serverClientOnlyMembers[index];
                Assert.AreEqual("false",
                    harness.Store.Get("network-direction-server", member + "_ok"));
                string refusal = harness.Store.Get(
                    "network-direction-server", member + "_error");
                StringAssert.Contains("actor '" + serverActor.ActorId + "'", refusal);
                StringAssert.Contains("client-only", refusal);
            }
        }

        [Test]
        public void Lua_NetworkProductionPath_Tac020ServerScriptObservesNilLocalPlayer()
        {
            string fixturePath = Path.Combine(
                Directory.GetCurrentDirectory(), "Assets", "CoreAIMods", "Tests", "EditMode",
                "RbxApi", "CompatibilityCorpus", "Fixtures",
                "TAC-020-players-localplayer.lua");
            string source = File.ReadAllText(fixturePath);
            using ProductionNetworkHarness harness = new();
            ActorContext hostActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            Exception exception = Assert.Catch(() => harness.Runtime.LoadMod(
                hostActor, "TAC-020-players-localplayer", source, persistToStore: false));
            StringAssert.Contains("attempt to index a nil value (local 'player')",
                FullText(exception));
        }

        [Test]
        public void Lua_NetworkProductionPath_ReliableRemoteEventPreservesOrdering()
        {
            using ProductionNetworkHarness harness = new();
            ActorContext clientActor = Actor("ordering-actor");
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            harness.Runtime.LoadMod(serverActor, "network-ordering-server", @"
                local remote = Instance.new('RemoteEvent')
                remote.Name = 'OrderingRemote'
                remote.Parent = workspace
                local received = ''
                remote.OnServerEvent:Connect(function(_, value)
                    received = received .. tostring(value)
                    store_set('received', received)
                end)
            ", persistToStore: false);
            harness.Runtime.LoadMod(clientActor, "network-ordering-client", @"
                local remote = workspace:FindFirstChild('OrderingRemote')
                for value = 1, 6 do
                    remote:FireServer(value)
                end", persistToStore: false);

            harness.PumpFrames(1);

            Assert.AreEqual("123456",
                harness.Store.Get("network-ordering-server", "received"));
        }

        [Test]
        public void Lua_NetworkProductionPath_RateRefusalNamesActorAndReason()
        {
            NullNetworkBridge bridge = new(2, () => 0d);
            using ProductionNetworkHarness harness = new(bridge);
            ActorContext actorContext = Actor("rate-actor");

            harness.Runtime.LoadMod(actorContext, "network-rate", @"
                local remote = Instance.new('RemoteEvent')
                remote:FireServer(1)
                remote:FireServer(2)
                local ok, refusal = pcall(function()
                    remote:FireServer(3)
                end)
                store_set('ok', tostring(ok))
                store_set('refusal', tostring(refusal))", persistToStore: false);

            Assert.AreSame(bridge, harness.Bindings.NetworkBridge);
            Assert.AreEqual("false", harness.Store.Get("network-rate", "ok"));
            string refusal = harness.Store.Get("network-rate", "refusal");
            StringAssert.Contains("actor 'rate-actor'", refusal);
            StringAssert.Contains(
                "network request rate quota reached (limit 2 requests/s)", refusal);
        }

        [Test]
        public void Lua_NetworkProductionPath_CodecRejectsDepthCyclesAndAggregateOverflow()
        {
            using ProductionNetworkHarness harness = new();
            ActorContext actorContext = Actor("codec-limits-actor");

            harness.Runtime.LoadMod(actorContext, "network-codec-limits", @"
                local remote = Instance.new('RemoteEvent')
                local function capture(name, value)
                    local ok, failure = pcall(function()
                        remote:FireServer(value)
                    end)
                    store_set(name .. '_ok', tostring(ok))
                    store_set(name .. '_error', tostring(failure))
                end

                local deep = {}
                local cursor = deep
                for index = 1, 65 do
                    local child = {}
                    cursor.child = child
                    cursor = child
                end
                capture('depth', deep)

                local cyclic = {}
                cyclic.self = cyclic
                capture('cycle', cyclic)

                local oversized = {}
                for index = 1, 100001 do
                    oversized[tostring(index)] = index
                end
                capture('entries', oversized)", persistToStore: false);

            Assert.AreEqual("false",
                harness.Store.Get("network-codec-limits", "depth_ok"));
            StringAssert.Contains("64 level limit",
                harness.Store.Get("network-codec-limits", "depth_error"));
            Assert.AreEqual("false",
                harness.Store.Get("network-codec-limits", "cycle_ok"));
            StringAssert.Contains("cyclic table",
                harness.Store.Get("network-codec-limits", "cycle_error"));
            Assert.AreEqual("false",
                harness.Store.Get("network-codec-limits", "entries_ok"));
            StringAssert.Contains("100000 aggregate entry limit",
                harness.Store.Get("network-codec-limits", "entries_error"));
        }

        [Test]
        public void Lua_NetworkProductionPath_R510SanitizesTablesAndInstancesAcrossBoundary()
        {
            using ProductionNetworkHarness harness = new();
            ActorContext clientActor = Actor("sanitization-actor");
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            harness.Runtime.LoadMod(serverActor, "network-sanitization-server", @"
                local part = Instance.new('Part')
                part.Name = 'BoundaryPart'
                part.Parent = workspace
                local remote = Instance.new('RemoteFunction')
                remote.Name = 'SanitizationRemote'
                remote.Parent = workspace
                remote.OnServerInvoke = function(_, payload, instance)
                    store_set('instance_same', tostring(instance == part))
                    store_set('instance_key_stringified',
                        tostring(payload.BoundaryPart == 'keyed'))
                    store_set('metatable_lost', tostring(getmetatable(payload) == nil))
                    store_set('function_removed', tostring(payload.callback == nil))
                    payload.server_mutation = 'received'
                    return payload, instance
                end", persistToStore: false);

            harness.Runtime.LoadMod(clientActor, "network-sanitization-client", @"
                local part = workspace:FindFirstChild('BoundaryPart')
                local remote = workspace:FindFirstChild('SanitizationRemote')
                local payload = setmetatable({
                    [part] = 'keyed',
                    direct = 'value',
                    callback = function() return 'not replicated' end
                }, {
                    __index = { inherited = 'metatable-only' }
                })
                local returned, returnedInstance = remote:InvokeServer(payload, part)
                store_set('copy_identity', tostring(returned ~= payload))
                store_set('sender_unchanged', tostring(payload.server_mutation == nil))
                store_set('returned_mutation', tostring(returned.server_mutation))
                store_set('returned_instance_same', tostring(returnedInstance == part))

                local cyclic = {}
                cyclic.self = cyclic
                local ok, failure = pcall(function()
                    return remote:InvokeServer(cyclic, part)
                end)
                store_set('cycle_ok', tostring(ok))
                store_set('cycle_error', tostring(failure))", persistToStore: false);

            harness.PumpFrames(8);

            Assert.AreEqual("true", harness.Store.Get(
                "network-sanitization-server", "instance_same"));
            Assert.AreEqual("true", harness.Store.Get(
                "network-sanitization-server", "instance_key_stringified"));
            Assert.AreEqual("true", harness.Store.Get(
                "network-sanitization-server", "metatable_lost"));
            Assert.AreEqual("true", harness.Store.Get(
                "network-sanitization-server", "function_removed"));
            Assert.AreEqual("true", harness.Store.Get(
                "network-sanitization-client", "copy_identity"));
            Assert.AreEqual("true", harness.Store.Get(
                "network-sanitization-client", "sender_unchanged"));
            Assert.AreEqual("received", harness.Store.Get(
                "network-sanitization-client", "returned_mutation"));
            Assert.AreEqual("true", harness.Store.Get(
                "network-sanitization-client", "returned_instance_same"));
            Assert.AreEqual("false", harness.Store.Get(
                "network-sanitization-client", "cycle_ok"));
            StringAssert.Contains("cyclic table", harness.Store.Get(
                "network-sanitization-client", "cycle_error"));
        }

        [Test]
        public void Lua_NetworkProductionPath_RemoteFunctionYieldsAndPropagatesReturnsAndErrors()
        {
            using ProductionNetworkHarness harness = new();
            ActorContext clientActor = Actor("function-actor");
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            harness.Runtime.LoadMod(serverActor, "network-function-server", @"
                local Players = game:GetService('Players')
                local server = Instance.new('RemoteFunction')
                server.Name = 'ServerFunction'
                server.Parent = workspace
                server.OnServerInvoke = function(sender, left, right)
                    task.wait()
                    return left + right, 'server'
                end

                local client = Instance.new('RemoteFunction')
                client.Name = 'ClientFunction'
                client.Parent = workspace

                local failing = Instance.new('RemoteFunction')
                failing.Name = 'FailingFunction'
                failing.Parent = workspace
                failing.OnServerInvoke = function()
                    task.wait()
                    error('receiver exploded')
                end

                task.defer(function()
                    task.wait()
                    local listed = Players:GetPlayers()
                    local player = listed[#listed]
                    local doubled, clientLabel = client:InvokeClient(player, 11)
                    store_set('client', tostring(doubled) .. ':' .. clientLabel)
                end)", persistToStore: false);

            harness.Runtime.LoadMod(clientActor, "network-function-client", @"
                local server = workspace:FindFirstChild('ServerFunction')
                local client = workspace:FindFirstChild('ClientFunction')
                local failing = workspace:FindFirstChild('FailingFunction')

                client.OnClientInvoke = function(value)
                    task.wait()
                    return value * 2, 'client'
                end

                local total, serverLabel = server:InvokeServer(8, 13)
                store_set('server', tostring(total) .. ':' .. serverLabel)
                local ok, failure = pcall(function()
                    return failing:InvokeServer()
                end)
                store_set('failure_ok', tostring(ok))
                store_set('failure', tostring(failure))", persistToStore: false);

            harness.PumpFrames(12);

            Assert.AreEqual("21:server",
                harness.Store.Get("network-function-client", "server"));
            Assert.AreEqual("22:client",
                harness.Store.Get("network-function-server", "client"));
            Assert.AreEqual("false",
                harness.Store.Get("network-function-client", "failure_ok"));
            StringAssert.Contains(
                "receiver exploded",
                harness.Store.Get("network-function-client", "failure"));
        }

        [Test]
        public void Lua_NetworkProductionPath_RemoteFunctionNoResponderReleasesWaitState()
        {
            const string modId = "network-no-responder";
            using ProductionNetworkHarness harness = new();
            ActorContext actorContext = Actor("no-responder-actor");

            harness.Runtime.LoadMod(actorContext, modId, @"
                local remote = Instance.new('RemoteFunction')
                local ok, failure = pcall(function()
                    return remote:InvokeServer()
                end)
                store_set('ok', tostring(ok))
                store_set('failure', tostring(failure))", persistToStore: false);
            harness.PumpFrames(4);

            int liveThreadsBeforeUnload = harness.Bindings.Scheduler.LiveThreadCount;
            int liveConnectionsBeforeUnload =
                harness.Bindings.Connections.GetOwnedBy(modId).Count;
            int waitsBeforeUnload =
                harness.Bindings.CountRemoteFunctionWaitsOwnedBy(modId);
            bool unloaded = harness.Runtime.UnloadMod(actorContext, modId);

            Assert.IsTrue(unloaded);
            Assert.AreEqual("false", harness.Store.Get(modId, "ok"));
            StringAssert.Contains("OnServerInvoke is not set",
                harness.Store.Get(modId, "failure"));
            Assert.AreEqual(0, liveThreadsBeforeUnload);
            Assert.AreEqual(0, liveConnectionsBeforeUnload);
            Assert.AreEqual(0, waitsBeforeUnload,
                "A terminal missing-callback response must release its caller wait generation.");
            Assert.AreEqual(0, harness.Bindings.CountRemoteFunctionWaitsOwnedBy(modId));
        }

        [Test]
        public void Lua_NetworkProductionPath_RemoteFunctionDroppedRequestTimesOutAndIgnoresLateResponse()
        {
            const string modId = "network-dropped-request";
            const string actorId = "dropped-request-actor";
            TrackingNetworkBridge bridge = new() { DropRequests = true };
            LuaCsRbxApiBindings bindings = new(networkBridge: bridge);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            ActorContext actorContext = Actor(actorId);

            stack.Runtime.LoadMod(actorContext, modId, @"
                local remote = Instance.new('RemoteFunction')
                remote.Name = 'DroppedRequestRemote'
                remote.Parent = workspace
                local ok, failure = pcall(function()
                    return remote:InvokeServer()
                end)
                store_set('ok', tostring(ok))
                store_set('failure', tostring(failure))", persistToStore: false);

            Assert.AreEqual("", store.Get(modId, "ok"));
            Assert.AreEqual(1, bindings.CountRemoteFunctionWaitsOwnedBy(modId));

            bindings.Scheduler.Advance(
                LuaCsRbxApiBindings.RemoteFunctionInvokeTimeoutSeconds);

            Assert.AreEqual("false", store.Get(modId, "ok"));
            StringAssert.Contains(actorId, store.Get(modId, "failure"));
            StringAssert.Contains("DroppedRequestRemote", store.Get(modId, "failure"));
            StringAssert.Contains("timed out after 30 seconds", store.Get(modId, "failure"));
            Assert.AreEqual(0, bindings.CountRemoteFunctionWaitsOwnedBy(modId));
            Assert.AreEqual(0, bindings.Scheduler.LiveThreadCount);

            bridge.LastResponse(RbxNetworkResponse.Failure("late response"));

            Assert.AreEqual("false", store.Get(modId, "ok"));
            StringAssert.DoesNotContain("late response", store.Get(modId, "failure"));
            Assert.AreEqual(0, bindings.CountRemoteFunctionWaitsOwnedBy(modId));
        }

        [Test]
        public void Lua_NetworkProductionPath_RemoteFunctionThrowingResponderReleasesLifecycleState()
        {
            const string serverModId = "network-throwing-responder-server";
            const string clientModId = "network-throwing-responder-client";
            using ProductionNetworkHarness harness = new();
            ActorContext clientActor = Actor("throwing-responder-actor");
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            harness.Runtime.LoadMod(serverActor, serverModId, @"
                local remote = Instance.new('RemoteFunction')
                remote.Name = 'ThrowingLifecycleRemote'
                remote.Parent = workspace
                remote.OnServerInvoke = function()
                    task.wait()
                    error('hostile responder threw')
                end", persistToStore: false);
            harness.Runtime.LoadMod(clientActor, clientModId, @"
                local remote = workspace:FindFirstChild('ThrowingLifecycleRemote')
                local ok, failure = pcall(function()
                    return remote:InvokeServer()
                end)
                store_set('ok', tostring(ok))
                store_set('failure', tostring(failure))", persistToStore: false);
            harness.PumpFrames(8);

            int liveThreadsBeforeUnload = harness.Bindings.Scheduler.LiveThreadCount;
            int liveConnectionsBeforeUnload =
                harness.Bindings.Connections.GetOwnedBy(clientModId).Count;
            int waitsBeforeUnload =
                harness.Bindings.CountRemoteFunctionWaitsOwnedBy(clientModId);
            int callbacksBeforeUnload =
                harness.Bindings.CountRemoteFunctionCallbacksOwnedBy(serverModId);
            bool clientUnloaded = harness.Runtime.UnloadMod(clientActor, clientModId);
            bool serverUnloaded = harness.Runtime.UnloadMod(serverActor, serverModId);

            Assert.IsTrue(clientUnloaded);
            Assert.IsTrue(serverUnloaded);
            Assert.AreEqual("false", harness.Store.Get(clientModId, "ok"));
            StringAssert.Contains("hostile responder threw",
                harness.Store.Get(clientModId, "failure"));
            Assert.AreEqual(0, liveThreadsBeforeUnload);
            Assert.AreEqual(0, liveConnectionsBeforeUnload);
            Assert.AreEqual(0, waitsBeforeUnload,
                "A throwing callback must release its caller wait generation.");
            Assert.AreEqual(1, callbacksBeforeUnload,
                "The live mod keeps its assigned callback until lifecycle teardown.");
            Assert.AreEqual(0,
                harness.Bindings.CountRemoteFunctionWaitsOwnedBy(clientModId));
            Assert.AreEqual(0,
                harness.Bindings.CountRemoteFunctionCallbacksOwnedBy(serverModId));
        }

        [Test]
        public void Lua_NetworkProductionPath_RemoteFunctionNeverAnsweringResponderIsReleasedOnTeardown()
        {
            const string serverModId = "network-never-answering-responder-server";
            const string clientModId = "network-never-answering-responder-client";
            using ProductionNetworkHarness harness = new();
            ActorContext clientActor = Actor("never-answering-responder-actor");
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            harness.Runtime.LoadMod(serverActor, serverModId, @"
                local remote = Instance.new('RemoteFunction')
                remote.Name = 'NeverAnsweringLifecycleRemote'
                remote.Parent = workspace
                remote.OnServerInvoke = function()
                    task.wait(1000000)
                    return 'unreachable'
                end", persistToStore: false);
            harness.Runtime.LoadMod(clientActor, clientModId, @"
                local remote = workspace:FindFirstChild('NeverAnsweringLifecycleRemote')
                remote:InvokeServer()
                store_set('unexpected', 'resumed')", persistToStore: false);
            harness.PumpFrames(2);

            int liveThreadsBeforeUnload = harness.Bindings.Scheduler.LiveThreadCount;
            int liveConnectionsBeforeUnload =
                harness.Bindings.Connections.GetOwnedBy(clientModId).Count;
            int waitsBeforeUnload =
                harness.Bindings.CountRemoteFunctionWaitsOwnedBy(clientModId);
            int callbacksBeforeUnload =
                harness.Bindings.CountRemoteFunctionCallbacksOwnedBy(serverModId);
            bool clientUnloaded = harness.Runtime.UnloadMod(clientActor, clientModId);
            bool serverUnloaded = harness.Runtime.UnloadMod(serverActor, serverModId);

            Assert.IsTrue(clientUnloaded);
            Assert.IsTrue(serverUnloaded);
            Assert.AreEqual("", harness.Store.Get(clientModId, "unexpected"));
            Assert.AreEqual(2, liveThreadsBeforeUnload,
                "The caller and never-returning callback remain live until teardown.");
            Assert.AreEqual(1, liveConnectionsBeforeUnload);
            Assert.AreEqual(1, waitsBeforeUnload);
            Assert.AreEqual(1, callbacksBeforeUnload);
            Assert.AreEqual(0, harness.Bindings.Scheduler.LiveThreadCount);
            Assert.AreEqual(0,
                harness.Bindings.Connections.GetOwnedBy(clientModId).Count);
            Assert.AreEqual(0,
                harness.Bindings.CountRemoteFunctionWaitsOwnedBy(clientModId));
            Assert.AreEqual(0,
                harness.Bindings.CountRemoteFunctionCallbacksOwnedBy(serverModId));
        }

        [Test]
        public void Lua_NetworkProductionPath_RemoteFunctionReloadDoesNotRetainOutgoingCallback()
        {
            const string serverModId = "network-reload-callback-server";
            const string clientModId = "network-reload-callback-client";
            using ProductionNetworkHarness harness = new();
            ActorContext clientActor = Actor("reload-callback-actor");
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            harness.Runtime.LoadMod(serverActor, serverModId, @"
                local remote = Instance.new('RemoteFunction')
                remote.Name = 'ReloadRemote'
                remote.Parent = workspace
                remote.OnServerInvoke = function()
                    return 'outgoing callback'
                end", persistToStore: false);
            Assert.AreEqual(1,
                harness.Bindings.CountRemoteFunctionCallbacksOwnedBy(serverModId));

            harness.Runtime.ReloadMod(serverActor, serverModId, @"
                local remote = workspace:FindFirstChild('ReloadRemote')");
            Assert.AreEqual(0,
                harness.Bindings.CountRemoteFunctionCallbacksOwnedBy(serverModId));

            harness.Runtime.LoadMod(clientActor, clientModId, @"
                local remote = workspace:FindFirstChild('ReloadRemote')
                local ok, failure = pcall(function()
                    return remote:InvokeServer()
                end)
                store_set('ok', tostring(ok))
                store_set('failure', tostring(failure))", persistToStore: false);

            harness.PumpFrames(4);

            Assert.AreEqual("false", harness.Store.Get(clientModId, "ok"));
            StringAssert.Contains("OnServerInvoke is not set",
                harness.Store.Get(clientModId, "failure"));
            Assert.AreEqual(0,
                harness.Bindings.CountRemoteFunctionWaitsOwnedBy(clientModId));
        }

        [Test]
        public void Lua_NetworkProductionPath_BindingDisposalUnsubscribesInjectedBridge()
        {
            TrackingNetworkBridge bridge = new();
            ProductionNetworkHarness harness = new(bridge);

            Assert.AreEqual(1, bridge.EventSubscriberCount);
            Assert.AreEqual(1, bridge.RequestSubscriberCount);

            harness.Dispose();

            Assert.AreEqual(0, bridge.EventSubscriberCount);
            Assert.AreEqual(0, bridge.RequestSubscriberCount);
        }

        [Test]
        public void Lua_NetworkProductionPath_UnreliableRemoteEventMayDropAndReorder()
        {
            NullNetworkBridge droppingBridge = new(
                unreliableBehavior: RbxNullNetworkUnreliableBehavior.DropAll);
            using ProductionNetworkHarness droppingHarness = new(droppingBridge);
            ActorContext droppingClientActor = Actor("drop-actor");
            ActorContext droppingServerActor =
                CoreServicesInstaller.DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);

            droppingHarness.Runtime.LoadMod(
                droppingServerActor, "network-drop-server", @"
                local remote = Instance.new('UnreliableRemoteEvent')
                remote.Name = 'DroppingRemote'
                remote.Parent = workspace
                store_set('received', '0')
                remote.OnServerEvent:Connect(function()
                    store_set('received', '1')
                end)", persistToStore: false);
            droppingHarness.Runtime.LoadMod(
                droppingClientActor, "network-drop-client", @"
                local remote = workspace:FindFirstChild('DroppingRemote')
                remote:FireServer('drop-me')", persistToStore: false);
            droppingHarness.PumpFrames(1);

            Assert.AreSame(droppingBridge, droppingHarness.Bindings.NetworkBridge);
            Assert.AreEqual("0",
                droppingHarness.Store.Get("network-drop-server", "received"));

            NullNetworkBridge reorderingBridge = new(
                unreliableBehavior: RbxNullNetworkUnreliableBehavior.ReverseAdjacentPairs);
            using ProductionNetworkHarness reorderingHarness = new(reorderingBridge);
            ActorContext reorderingClientActor = Actor("reorder-actor");
            ActorContext reorderingServerActor =
                CoreServicesInstaller.DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);

            reorderingHarness.Runtime.LoadMod(
                reorderingServerActor, "network-reorder-server", @"
                local remote = Instance.new('UnreliableRemoteEvent')
                remote.Name = 'ReorderingRemote'
                remote.Parent = workspace
                local received = ''
                remote.OnServerEvent:Connect(function(_, value)
                    received = received .. tostring(value)
                    store_set('received', received)
                end)", persistToStore: false);
            reorderingHarness.Runtime.LoadMod(
                reorderingClientActor, "network-reorder-client", @"
                local remote = workspace:FindFirstChild('ReorderingRemote')
                remote:FireServer(1)
                remote:FireServer(2)", persistToStore: false);
            reorderingHarness.PumpFrames(1);

            Assert.AreSame(reorderingBridge, reorderingHarness.Bindings.NetworkBridge);
            Assert.AreEqual("21",
                reorderingHarness.Store.Get("network-reorder-server", "received"));
        }

        // ---- Datatypes ----------------------------------------------------------------------

        [Test]
        public void Lua_Vector3_ConstructorsOperatorsAndTostring()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local p = Vector3.new(1, 2, 3)
                assert(p.X == 1 and p.Y == 2 and p.Z == 3)
                assert(tostring(p) == '1, 2, 3')
                assert(p == Vector3.new(1, 2, 3))
                assert(p ~= Vector3.new(3, 2, 1))
                local q = p + Vector3.new(1, 1, 1)
                assert(tostring(q) == '2, 3, 4')
                assert((p - p) == Vector3.zero)
                assert((p * 2).Y == 4)
                assert((2 * p).Z == 6)
                assert((p * Vector3.new(2, 2, 2)).X == 2)
                assert((p / 2).X == 0.5)
                assert((-p).X == -1)
                assert(p:Dot(Vector3.new(1, 0, 0)) == 1)
                assert(Vector3.xAxis:Cross(Vector3.yAxis) == Vector3.zAxis)
                assert(Vector3.new(3, 4, 0).Magnitude == 5)
                assert(Vector3.new(10, 0, 0).Unit == Vector3.xAxis)
                assert(Vector3.zero:Lerp(Vector3.one, 0.5) == Vector3.new(0.5, 0.5, 0.5))
                assert(Vector3.FromNormalId(Enum.NormalId.Front) == Vector3.new(0, 0, -1))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_CFrame_MathMatchesPureSpec()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-4 end
                local cf = CFrame.new(0, 5, 0) * CFrame.Angles(0, math.pi / 2, 0)
                assert(cf.Position == Vector3.new(0, 5, 0))
                -- WHY: right-handed spec — yaw of +90deg turns LookVector (-Z) onto -X.
                assert(near(cf.LookVector.X, -1) and near(cf.LookVector.Z, 0))
                local moved = CFrame.new(1, 2, 3) * Vector3.new(0, 0, -1)
                assert(moved == Vector3.new(1, 2, 2))
                local roundtrip = cf:ToObjectSpace(cf:ToWorldSpace(CFrame.new(7, 8, 9)))
                assert(near(roundtrip.X, 7) and near(roundtrip.Y, 8) and near(roundtrip.Z, 9))
                local look = CFrame.lookAt(Vector3.zero, Vector3.new(0, 0, -10))
                assert(near(look.LookVector.Z, -1))
                local x, y, z, r00 = CFrame.identity:GetComponents()
                assert(x == 0 and y == 0 and z == 0 and r00 == 1)
                assert(CFrame.new() == CFrame.identity)
                assert((CFrame.new(1, 1, 1) + Vector3.new(0, 1, 0)).Y == 2)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_Color3_UDim2_Vector2_ConstructorsAndMembers()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local c = Color3.fromRGB(255, 0, 0)
                assert(c.R == 1 and c.G == 0 and c.B == 0)
                assert(Color3.new(0.5, 0.25, 1).B == 1)
                assert(Color3.fromHex('#FF0000') == Color3.fromRGB(255, 0, 0))
                local h, s, v = Color3.fromRGB(255, 0, 0):ToHSV()
                assert(h == 0 and s == 1 and v == 1)
                local u = UDim.new(0.5, 10) + UDim.new(0.25, 5)
                assert(u.Scale == 0.75 and u.Offset == 15)
                local u2 = UDim2.fromScale(1, 0.5)
                assert(u2.X.Scale == 1 and u2.Y.Scale == 0.5 and u2.X.Offset == 0)
                assert(UDim2.fromOffset(200, 100).Y.Offset == 100)
                assert(UDim2.new(1, 0, 0.5, 20) == UDim2.new(UDim.new(1, 0), UDim.new(0.5, 20)))
                local v = Vector2.new(3, 4)
                assert(v.Magnitude == 5)
                assert((v + Vector2.one).X == 4)
                assert(tostring(Vector2.new(1, 2)) == '1, 2')");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_Enum_AccessIdentityAndGetEnumItems()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                assert(Enum.Material.Wood.Value == 512)
                assert(Enum.Material.Wood.Name == 'Wood')
                assert(tostring(Enum.PartType.Ball) == 'Enum.PartType.Ball')
                assert(Enum.Material.Wood == Enum.Material.Wood)
                assert(Enum.Material.Wood ~= Enum.Material.Metal)
                assert(Enum.Material.Wood.EnumType == Enum.Material)
                assert(tostring(Enum) == 'Enum')
                local items = Enum.Axis:GetEnumItems()
                assert(#items == 3 and items[1] == Enum.Axis.X)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_Enum_UnknownEnum_RaisesLoudStub()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            // WHY: KeyCode shipped with the MVP1 input slice; EasingStyle stays unimplemented
            // until TweenService (MVP8), so it is the loud-stub probe now.
            Exception ex = LoadFails(stack, "m", "local k = Enum.EasingStyle");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(ex));
            StringAssert.Contains("Enum.EasingStyle", FullText(ex));
        }

        [Test]
        public void Lua_Enum_UnknownItem_RaisesBadArgument()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", "local k = Enum.Material.Bogus");
            StringAssert.Contains("BAD_ARGUMENT", FullText(ex));
            StringAssert.Contains("'Bogus' is not a valid member of Enum.Material", FullText(ex));
        }

        [Test]
        public void Lua_Random_DeterministicFromSeed()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local a = Random.new(42)
                local b = Random.new(42)
                for _ = 1, 8 do
                    assert(a:NextNumber() == b:NextNumber())
                end
                local c = Random.new(7)
                for _ = 1, 32 do
                    local n = c:NextInteger(1, 6)
                    assert(n >= 1 and n <= 6)
                end
                local d = Random.new(5)
                local clone = d:Clone()
                assert(d:NextNumber() == clone:NextNumber())
                assert(Random.new(1):NextUnitVector():FuzzyEq(Random.new(1):NextUnitVector()))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        // ---- Instance surface ---------------------------------------------------------------

        [Test]
        public void Lua_R1_1_ProductionPath_ScriptGlobalIsItsExecutingRegistryInstance()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod(Actor("script-global-actor-a"), "script-global-a", @"
                assert(script ~= nil)
                assert(script.ClassName == 'Script')
                assert(script:IsA('BaseScript'))
                assert(script:IsA('LuaSourceContainer'))
                assert(script.Name == 'script-global-a')
                assert(script.Parent.ClassName == 'Folder')
                assert(script.Parent.Parent == game:GetService('ServerScriptService'))
                script:SetAttribute('Executing', true)
                local child = Instance.new('Folder', script.Parent)
                child.Name = 'AuthoredChild'", persistToStore: false);
            stack.Runtime.LoadMod(Actor("script-global-actor-b"), "script-global-b", @"
                assert(script.Name == 'script-global-b')
                assert(script:GetAttribute('Executing') == nil)", persistToStore: false);

            RbxInstance serverScripts = roblox.Game.FindFirstChildOfClass("ServerScriptService");
            RbxInstance firstContainer = serverScripts.FindFirstChild("script-global-a");
            RbxInstance secondContainer = serverScripts.FindFirstChild("script-global-b");
            RbxInstance first = firstContainer?.FindFirstChildOfClass("Script");
            RbxInstance second = secondContainer?.FindFirstChildOfClass("Script");

            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.AreNotSame(first, second);
            Assert.AreEqual(true, first.GetAttribute("Executing"));
            Assert.AreEqual(1, roblox.Registry.GetOwnedBy("script-global-a").Count,
                "runtime infrastructure must not appear as mod-authored content");
            Assert.IsTrue(roblox.Registry.TryGetRecord(first.Id, out InstanceRecord firstRecord));
            Assert.IsTrue(firstRecord.IsRuntimeInfrastructure);
            Assert.IsNull(firstRecord.OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.SharedWritable, firstRecord.AccessScope,
                "the proxy must not become a foreign ACL container for its actor");
        }

        [Test]
        public void Lua_ProductionPath_Tac015ScriptParentFixturePassesUnmodified()
        {
            string fixturePath = Path.Combine(
                Directory.GetCurrentDirectory(), "Assets", "CoreAIMods", "Tests", "EditMode",
                "RbxApi", "CompatibilityCorpus", "Fixtures",
                "TAC-015-script-parent-property-signal.lua");
            string source = File.ReadAllText(fixturePath);
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);

            stack.Runtime.LoadMod("TAC-015-script-parent-property-signal", source,
                persistToStore: false);
            roblox.Scheduler.Advance(0d);

            RbxInstance workspace = roblox.Game.FindFirstChildOfClass("Workspace");
            Assert.AreEqual("TAC-015-script-parent-property-signal",
                workspace.GetAttribute("TierACorpusResult"));
        }

        [Test]
        public void Lua_ProductionPath_Tac016GeneralizedIterationFixturePassesUnmodified()
        {
            string fixturePath = Path.Combine(
                Directory.GetCurrentDirectory(), "Assets", "CoreAIMods", "Tests", "EditMode",
                "RbxApi", "CompatibilityCorpus", "Fixtures",
                "TAC-016-generic-for-descendants.lua");
            string source = File.ReadAllText(fixturePath);
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);

            stack.Runtime.LoadMod("TAC-016-generic-for-descendants", source,
                persistToStore: false);

            RbxInstance workspace = roblox.Game.FindFirstChildOfClass("Workspace");
            Assert.AreEqual("TAC-016-generic-for-descendants",
                workspace.GetAttribute("TierACorpusResult"));
        }

        [Test]
        public void Lua_ProductionPath_GeneralizedIterationHonorsIterMetamethod()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);
            stack.Runtime.LoadMod("generalized-iteration", @"
                local values = { 3, 5, 7 }
                setmetatable(values, { __iter = function(t)
                    local i = #t + 1
                    return function()
                        i = i - 1
                        if i > 0 then return i, t[i] end
                    end
                end })
                local result = ''
                for i, value in values do
                    result = result .. i .. ':' .. value .. ';'
                end
                store_set('result', result)");

            Assert.AreEqual("3:7;2:5;1:3;",
                store.Get("generalized-iteration", "result"));
        }

        [Test]
        public void Lua_InstanceNew_CreatesParentsAndNavigates()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("m", @"
                local f = Instance.new('Folder')
                f.Name = 'F'
                assert(f.Parent == nil)
                f.Parent = workspace
                assert(f.Parent == workspace)
                assert(workspace:FindFirstChild('F') == f)
                assert(workspace.F == f)
                assert(f:GetFullName() == 'Workspace.F')
                assert(f.ClassName == 'Folder')
                assert(f:IsA('Folder') and f:IsA('Instance') and not f:IsA('BasePart'))
                assert(f:IsDescendantOf(workspace) and workspace:IsAncestorOf(f))
                local kids = workspace:GetChildren()
                assert(kids[#kids] == f)
                assert(tostring(f) == 'F')
                local m = Instance.new('Model')
                m.Parent = f
                assert(workspace:FindFirstChildWhichIsA('Model', true) == m)
                assert(#f:GetChildren() == 1)
                assert(game.Workspace == workspace)
                assert(game.ReplicatedStorage.ClassName == 'ReplicatedStorage')
                assert(f:WaitForChild('Model') == m)");

            Assert.IsTrue(roblox.Registry.TryGetByWorldName("missing", out _) == false);
            RbxInstance folder = roblox.Game.FindFirstChildOfClass("Workspace").FindFirstChild("F");
            Assert.IsNotNull(folder);
        }

        [Test]
        public void Lua_InstanceNew_DeprecatedParentArgument_WorksAndLogsOnce()
        {
            List<string> log = new();
            LuaCsRbxApiBindings roblox = new(log: log.Add);
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("m", @"
                local a = Instance.new('Folder', workspace)
                local b = Instance.new('Folder', workspace)
                assert(a.Parent == workspace and b.Parent == workspace)");

            int deprecationNotes = 0;
            foreach (string line in log)
            {
                if (line.Contains("deprecated"))
                {
                    deprecationNotes++;
                }
            }

            Assert.AreEqual(1, deprecationNotes,
                "the Instance.new(className, parent) deprecation note must fire once per mod");
        }

        [Test]
        public void Lua_InstanceNew_NonCreatableClass_RaisesRobloxErrorShape()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", "Instance.new('Workspace')");
            StringAssert.Contains("Unable to create an Instance of type 'Workspace'", FullText(ex));
            StringAssert.Contains("BAD_ARGUMENT", FullText(ex));
        }

        [Test]
        public void Lua_GetService_PlannedService_DefersPhaseNamingStubUntilMemberAccess()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            // WHY: top-of-file GetService calls must not block unrelated script initialization;
            // the loud failure belongs to the line that first uses the missing service surface.
            stack.Runtime.LoadMod("resolve-only", @"
                local TweenService = game:GetService('TweenService')
                assert(TweenService ~= nil)");
            Assert.IsTrue(stack.Runtime.IsLoaded("resolve-only"));

            Exception ex = LoadFails(stack, "member-access", @"
                local TweenService = game:GetService('TweenService')
                TweenService:Create()");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(ex));
            StringAssert.Contains("TweenService:Create", FullText(ex));
            StringAssert.Contains("MVP8", FullText(ex));
        }

        [Test]
        public void Lua_PlannedStub_ProductionPathCarriesModIdAndSourceLine()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "stub-context", @"
                local missing = 'NeverThere'
                Instance.fromExisting(missing)");
            string fullText = FullText(ex);
            StringAssert.Contains("[mod:stub-context script:main.lua line:3]", fullText);
            StringAssert.Contains("NOT_IMPLEMENTED", fullText);
            StringAssert.Contains("backlog", fullText);
        }

        [Test]
        public void Lua_GetService_UnknownService_RaisesExactRobloxText()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", "game:GetService('Bogus')");
            StringAssert.Contains("Bogus is not a valid Service name", FullText(ex));
            StringAssert.Contains("UNKNOWN_SERVICE", FullText(ex));
        }

        [Test]
        public void Lua_UnknownMember_RaisesValidMemberError()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", "local x = workspace.NoSuchChildHere");
            StringAssert.Contains(
                "NoSuchChildHere is not a valid member of Workspace \"Workspace\"", FullText(ex));
            StringAssert.DoesNotContain("NOT_IMPLEMENTED", FullText(ex));
        }

        [TestCase("GetService")]
        [TestCase("FindService")]
        [TestCase("BindToClose")]
        public void Lua_Folder_ServiceProviderMember_RemainsInvalidMember(string member)
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m",
                "local folder = Instance.new('Folder'); local value = folder." + member);
            string fullText = FullText(ex);
            StringAssert.Contains(
                member + " is not a valid member of Folder \"Folder\"", fullText);
            StringAssert.DoesNotContain("NOT_IMPLEMENTED", fullText);
        }

        [Test]
        public void Lua_ModOwnership_OriginTagAndOwnerRecorded()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("mymod", @"
                local f = Instance.new('Folder')
                f.Name = 'Owned'
                f.Parent = workspace");

            RbxInstance owned = null;
            foreach (RbxInstance candidate in roblox.Registry.GetOwnedBy("mymod"))
            {
                owned = candidate;
            }

            Assert.IsNotNull(owned, "instances created from a mod must be owner-attributed");
            Assert.AreEqual("Owned", owned.Name);
            Assert.IsTrue(roblox.Registry.TryGetRecord(owned.Id, out InstanceRecord record));
            Assert.AreEqual("mymod", record.OwnerModId);
            Assert.AreEqual(OriginTag.FromMod("mymod"), record.OriginTag);
        }

        [Test]
        public void Lua_WorldAcl_OwnerCanMutate_ButCrossActorWritesAndDestroyAreDenied()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            ActorContext actorA = Actor("actor-a");
            ActorContext actorB = Actor("actor-b");
            RbxInstance ownedA = CreateActorInstance(registry, actorA, "mod-a", "Folder");
            RbxInstance ownedB = CreateActorInstance(registry, actorB, "mod-b", "Folder");
            RbxInstance shared = registry.Create("Folder");

            Assert.AreEqual("actor-a", Record(registry, ownedA).OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.Owned, Record(registry, ownedA).AccessScope);
            Assert.AreEqual(InstanceAccessScope.SharedWritable, Record(registry, shared).AccessScope);

            RunActorLua(bindings, actorA, "mod-a", "own.Name = 'OwnedByA'",
                ("own", ownedA));
            Assert.AreEqual("OwnedByA", ownedA.Name);

            Exception writeError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "mod-a", "foreign.Name = 'stolen'", ("foreign", ownedB)));
            StringAssert.Contains("actor 'actor-a'", FullText(writeError));
            StringAssert.Contains("Owned by actor 'actor-b'", FullText(writeError));
            Assert.AreEqual("Folder", ownedB.Name);

            Exception destroyError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "mod-a", "foreign:Destroy()", ("foreign", ownedB)));
            StringAssert.Contains("actor 'actor-a'", FullText(destroyError));
            StringAssert.Contains("Owned by actor 'actor-b'", FullText(destroyError));
            Assert.IsFalse(ownedB.IsDestroyed);

            RunActorLua(bindings, actorA, "mod-a", "shared.Name = 'Writable'", ("shared", shared));
            Exception sharedDestroyError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "mod-a", "shared:Destroy()", ("shared", shared)));
            StringAssert.Contains("SharedWritable destruction", FullText(sharedDestroyError));
            Assert.IsFalse(shared.IsDestroyed);
        }

        [Test]
        public void Lua_WorldAcl_ProductionRuntimeInstanceNewAndMutationUseActorAcl()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            LuaCsModStack stack = BuildStack(bindings);
            ActorContext actorA = Actor("runtime-actor-a");
            ActorContext actorB = Actor("runtime-actor-b");
            registry.BindActorAttribution(
                "runtime-owner-b", OriginTag.FromMod("runtime-owner-b"), actorB.ActorId);

            stack.Runtime.LoadMod(actorB, "runtime-owner-b", @"
                local owned = Instance.new('Folder')
                owned.Name = 'RuntimeOwnedByB'
                owned.Parent = workspace", persistToStore: false);

            RbxInstance ownedB = bindings.Registry.WorldRoot.FindFirstChild("RuntimeOwnedByB");
            Assert.IsNotNull(ownedB);
            Assert.AreEqual(actorB.ActorId, Record(registry, ownedB).OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.Owned, Record(registry, ownedB).AccessScope);

            registry.BindActorAttribution(
                "runtime-write-a", OriginTag.FromMod("runtime-write-a"), actorA.ActorId);
            Exception writeError = Assert.Catch(() => stack.Runtime.LoadMod(
                actorA,
                "runtime-write-a",
                "workspace:FindFirstChild('RuntimeOwnedByB').Name = 'stolen'",
                persistToStore: false));
            StringAssert.Contains("Owned by actor 'runtime-actor-b'", FullText(writeError));

            registry.BindActorAttribution(
                "runtime-destroy-a", OriginTag.FromMod("runtime-destroy-a"), actorA.ActorId);
            Exception destroyError = Assert.Catch(() => stack.Runtime.LoadMod(
                actorA,
                "runtime-destroy-a",
                "workspace:FindFirstChild('RuntimeOwnedByB'):Destroy()",
                persistToStore: false));
            StringAssert.Contains("actor 'runtime-actor-a'", FullText(destroyError));
            Assert.IsFalse(ownedB.IsDestroyed);
        }

        [Test]
        public void Lua_WorldAcl_HostProtectedCameraIsWritable_ButSingletonLifecycleIsHostOnly()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            ActorContext actor = Actor("camera-mod-owner");
            RbxInstance workspace = bindings.Game.FindFirstChildOfClass("Workspace");
            RbxInstance camera = workspace.FindFirstChildOfClass("Camera");
            RbxInstance lighting = bindings.Game.FindFirstChildOfClass("Lighting");

            Assert.AreEqual(InstanceAccessScope.HostProtected, Record(registry, camera).AccessScope);
            Assert.AreEqual(InstanceAccessScope.HostProtected, Record(registry, lighting).AccessScope);
            RunActorLua(bindings, actor, "camera-mod",
                "camera.CFrame = CFrame.new(3, 4, 5)", ("camera", camera));
            Assert.AreEqual(3f, bindings.CameraRig.GetCFrame().Position.X, 0.0001f);

            Exception actorDestroyError = Assert.Catch(() => RunActorLua(
                bindings, actor, "camera-mod", "service:Destroy()", ("service", lighting)));
            StringAssert.Contains("HostProtected", FullText(actorDestroyError));
            Assert.IsFalse(lighting.IsDestroyed);

            ActorContext host = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            Exception hostDestroyError = Assert.Catch(() => RunActorLua(
                bindings, host, "host", "service:Destroy()", ("service", lighting)));
            StringAssert.Contains("including for unrestricted actors", FullText(hostDestroyError));
            Assert.IsFalse(lighting.IsDestroyed);
        }

        [Test]
        public void Lua_WorldAcl_CloneSubtreeBelongsToCloningActor()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            ActorContext actorA = Actor("clone-source-owner");
            ActorContext actorB = Actor("clone-caller");
            RbxInstance workspace = bindings.Game.FindFirstChildOfClass("Workspace");
            RbxInstance source = CreateActorInstance(registry, actorA, "source-mod", "Folder");
            RbxInstance sourceChild = CreateActorInstance(registry, actorA, "source-mod", "Part");
            sourceChild.Parent = source;
            source.Parent = workspace;

            RunActorLua(bindings, actorB, "clone-mod", @"
                local copy = source:Clone()
                copy.Name = 'CloneByB'
                copy.Parent = workspace",
                ("source", source), ("workspace", workspace));

            RbxInstance clone = workspace.FindFirstChild("CloneByB");
            Assert.IsNotNull(clone);
            RbxInstance cloneChild = clone.FindFirstChildOfClass("Part");
            Assert.IsNotNull(cloneChild);
            Assert.AreEqual("clone-caller", Record(registry, clone).OwnerActorId);
            Assert.AreEqual("clone-caller", Record(registry, cloneChild).OwnerActorId);
            Assert.AreEqual("clone-mod", Record(registry, clone).OwnerModId);
            Assert.AreEqual("clone-mod", Record(registry, cloneChild).OwnerModId);
            Assert.AreEqual(OriginTag.FromMod("clone-mod"), Record(registry, clone).OriginTag);
            Assert.AreEqual(OriginTag.FromMod("clone-mod"), Record(registry, cloneChild).OriginTag);
            Assert.AreEqual(InstanceAccessScope.Owned, Record(registry, clone).AccessScope);
            Assert.AreEqual(InstanceAccessScope.Owned, Record(registry, cloneChild).AccessScope);
        }

        [Test]
        public void Lua_WorldAcl_ReparentChecksMovedObjectAndDestinationBeforeMutation()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            ActorContext actorA = Actor("reparent-a");
            ActorContext actorB = Actor("reparent-b");
            RbxInstance workspace = bindings.Game.FindFirstChildOfClass("Workspace");
            RbxInstance childA = CreateActorInstance(registry, actorA, "reparent-mod-a", "Folder");
            RbxInstance parentB = CreateActorInstance(registry, actorB, "reparent-mod-b", "Folder");

            Exception sourceError = Assert.Catch(() => RunActorLua(
                bindings, actorB, "reparent-mod-b", "child.Parent = workspace",
                ("child", childA), ("workspace", workspace)));
            StringAssert.Contains("reparent source", FullText(sourceError));
            Assert.IsNull(childA.Parent);

            Exception destinationError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "reparent-mod-a", "child.Parent = destination",
                ("child", childA), ("destination", parentB)));
            StringAssert.Contains("reparent destination", FullText(destinationError));
            StringAssert.Contains("Owned by actor 'reparent-b'", FullText(destinationError));
            Assert.IsNull(childA.Parent);

            childA.Parent = parentB;
            Exception sourceContainerError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "reparent-mod-a", "child.Parent = workspace",
                ("child", childA), ("workspace", workspace)));
            StringAssert.Contains("reparent source container", FullText(sourceContainerError));
            StringAssert.Contains("Owned by actor 'reparent-b'", FullText(sourceContainerError));
            Assert.AreSame(parentB, childA.Parent);
        }

        [Test]
        public void Lua_WorldAcl_InstanceNewParentChecksForeignContainerBeforeMutation()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            LuaCsModStack stack = BuildStack(bindings);
            ActorContext actorA = Actor("instance-new-parent-a");
            ActorContext actorB = Actor("instance-new-parent-b");
            RbxInstance workspace = bindings.Game.FindFirstChildOfClass("Workspace");
            RbxInstance containerA = CreateActorInstance(
                registry, actorA, "instance-new-container-a", "Folder");
            containerA.Name = "ForeignContainer";
            containerA.Parent = workspace;
            registry.BindActorAttribution(
                "instance-new-mod-b", OriginTag.FromMod("instance-new-mod-b"), actorB.ActorId);

            Exception error = null;
            try
            {
                stack.Runtime.LoadMod(
                    actorB,
                    "instance-new-mod-b",
                    "Instance.new('Folder', workspace:FindFirstChild('ForeignContainer'))",
                    persistToStore: false);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            Assert.AreEqual(0, containerA.GetChildren().Count);
            Assert.AreEqual(0, registry.GetOwnedBy("instance-new-mod-b").Count);
            Assert.IsNotNull(error, "foreign-container creation must be denied");
            StringAssert.Contains(
                "cannot create child on Folder 'Workspace.ForeignContainer'", FullText(error));
            StringAssert.Contains("Owned by actor 'instance-new-parent-a'", FullText(error));
        }

        [Test]
        public void Lua_WorldAcl_RecursiveDestroyAndClearPreflightEveryDescendantAtomically()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            ActorContext actorA = Actor("destroy-a");
            ActorContext actorB = Actor("destroy-b");
            RbxInstance rootA = CreateActorInstance(registry, actorA, "destroy-mod-a", "Folder");
            RbxInstance descendantB = CreateActorInstance(registry, actorB, "destroy-mod-b", "Folder");
            descendantB.Parent = rootA;

            Assert.Catch(() => RunActorLua(
                bindings, actorA, "destroy-mod-a", "root:Destroy()", ("root", rootA)));
            Assert.IsFalse(rootA.IsDestroyed);
            Assert.IsFalse(descendantB.IsDestroyed);
            Assert.AreSame(rootA, descendantB.Parent);

            RbxInstance containerA = CreateActorInstance(
                registry, actorA, "destroy-mod-a", "Folder");
            RbxInstance childA = CreateActorInstance(registry, actorA, "destroy-mod-a", "Folder");
            RbxInstance childB = CreateActorInstance(registry, actorB, "destroy-mod-b", "Folder");
            childA.Parent = containerA;
            childB.Parent = containerA;

            Assert.Catch(() => RunActorLua(
                bindings, actorA, "destroy-mod-a", "container:ClearAllChildren()",
                ("container", containerA)));
            Assert.AreSame(containerA, childA.Parent);
            Assert.AreSame(containerA, childB.Parent);
            Assert.IsFalse(childA.IsDestroyed);
            Assert.IsFalse(childB.IsDestroyed);

            RbxInstance containerB = CreateActorInstance(
                registry, actorB, "destroy-mod-b", "Folder");
            RbxInstance callerOwnedChild = CreateActorInstance(
                registry, actorA, "destroy-mod-a", "Folder");
            callerOwnedChild.Parent = containerB;
            Exception containerError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "destroy-mod-a", "container:ClearAllChildren()",
                ("container", containerB)));
            StringAssert.Contains("clear descendants container", FullText(containerError));
            StringAssert.Contains("Owned by actor 'destroy-b'", FullText(containerError));
            Assert.AreSame(containerB, callerOwnedChild.Parent);
            Assert.IsFalse(callerOwnedChild.IsDestroyed);
        }

        [Test]
        public void Lua_WorldAcl_LegacyWorldKeepsExistingCrossActorDestroyBehavior()
        {
            InstanceRegistry registry = new();
            LuaCsRbxApiBindings bindings = new(registry: registry);
            ActorContext actorA = Actor("legacy-a");
            ActorContext actorB = Actor("legacy-b");
            RbxInstance ownedB = CreateActorInstance(registry, actorB, "legacy-mod-b", "Folder");

            Assert.IsNull(registry.WorldAclVersion);
            RunActorLua(bindings, actorA, "legacy-mod-a",
                "target.Name = 'LegacyWritable'; target:Destroy()", ("target", ownedB));
            Assert.IsTrue(ownedB.IsDestroyed);
        }

        [Test]
        public void Lua_OneOffExecutor_GetsConsoleOrigin()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync("local f = Instance.new('Folder', workspace) f.Name = 'FromConsole'",
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.Success, result.Error);
            RbxInstance created = roblox.Game.FindFirstChildOfClass("Workspace")
                .FindFirstChild("FromConsole");
            Assert.IsNotNull(created);
            Assert.IsTrue(roblox.Registry.TryGetRecord(created.Id, out InstanceRecord record));
            Assert.IsNull(record.OwnerModId, "console instances are world-owned (no teardown owner)");
            StringAssert.StartsWith(OriginTag.ConsolePrefix, record.OriginTag);
        }

        [Test]
        public void Lua_CapabilityGating_ReadTierHasNoInstanceNewAndCannotMutate()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCapabilities readOnly =
                LuaCapabilities.Read | LuaCapabilities.Gameplay | LuaCapabilities.LogicOverride;
            LuaCsModStack stack = BuildStack(roblox, caps: readOnly);

            stack.Runtime.LoadMod("reader", @"
                assert(Instance == nil, 'Instance.new must be absent without WorldEdit')
                assert(workspace.ClassName == 'Workspace', 'navigation stays available on Read tier')");

            Exception ex = LoadFails(stack, "writer", "workspace.Name = 'Hacked'");
            StringAssert.Contains("WorldEdit", FullText(ex));
            Assert.AreEqual("Workspace", roblox.Game.FindFirstChildOfClass("Workspace").Name);
        }

        [Test]
        public void Lua_R6_7_R6_8_AttributesAndTags_RoundTrip()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local f = Instance.new('Folder', workspace)
                f:SetAttribute('Health', 100)
                f:SetAttribute('Label', 'boss')
                f:SetAttribute('Alive', true)
                assert(f:GetAttribute('Health') == 100)
                assert(f:GetAttribute('Label') == 'boss')
                assert(f:GetAttribute('Alive') == true)
                assert(f:GetAttribute('Missing') == nil)
                local attrs = f:GetAttributes()
                assert(attrs.Health == 100 and attrs.Label == 'boss')
                f:SetAttribute('Health', nil)
                assert(f:GetAttribute('Health') == nil)
                f:AddTag('Enemy')
                assert(f:HasTag('Enemy'))
                assert(not f:HasTag('Friend'))
                assert(f:GetTags()[1] == 'Enemy')
                f:RemoveTag('Enemy')
                assert(not f:HasTag('Enemy'))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_AttributeTable_RejectedWithBadArgument()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m",
                "Instance.new('Folder'):SetAttribute('Data', { x = 1 })");
            StringAssert.Contains("BAD_ARGUMENT", FullText(ex));
            StringAssert.Contains("table", FullText(ex));
        }

        [Test]
        public void Lua_R6_2_DestroyedInstance_MemberAccessAndReparentRaiseContractErrors()
        {
            // WHY: the mod's own store is the read-back channel, matching the runtime harness style.
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);
            stack.Runtime.LoadMod("m", @"
                local f = Instance.new('Folder', workspace)
                f:Destroy()
                local ok, err = pcall(function() return f.Name end)
                store_set('nameErr', tostring(err))
                local ok2, err2 = pcall(function() f.Parent = workspace end)
                store_set('parentErr', tostring(err2))");
            StringAssert.Contains("INSTANCE_DESTROYED", store.Get("m", "nameErr"));
            StringAssert.Contains("PARENT_LOCKED", store.Get("m", "parentErr"));
            StringAssert.Contains("The Parent property of Folder is locked",
                store.Get("m", "parentErr"));
        }

        [Test]
        public void Lua_R6_5_Clone_DeepCopiesWithFreshIdentity()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("m", @"
                local src = Instance.new('Folder', workspace)
                src.Name = 'Src'
                src:SetAttribute('Level', 3)
                local child = Instance.new('Model')
                child.Name = 'Child'
                child.Parent = src
                local copy = src:Clone()
                assert(copy ~= src)
                assert(copy.Parent == nil)
                assert(copy.Name == 'Src')
                assert(copy:GetAttribute('Level') == 3)
                assert(copy:FindFirstChild('Child') ~= nil)
                assert(copy:FindFirstChild('Child') ~= src:FindFirstChild('Child'))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        // ---- Loud stubs (§5.1.6) ------------------------------------------------------------

        [Test]
        public void Lua_BasePartSpatialWrites_ReflectInPartProperties()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("m", @"
                local p = Instance.new('Part')
                p.Name = 'Spatial'
                p.Parent = workspace
                p.Position = Vector3.new(1, 2, 3)
                p.Size = Vector3.new(5, 6, 7)
                p.Color = Color3.fromRGB(255, 128, 0)
                p.Transparency = 0.25
                p.Anchored = true
                p.CanCollide = false");

            RbxInstance part = roblox.Game.FindFirstChildOfClass("Workspace").FindFirstChild("Spatial");
            Assert.IsNotNull(part);
            PartProperties props = roblox.PartSink.GetPartPropertiesOrDefault(part.Id);
            Assert.AreEqual(new RbxVector3(1f, 2f, 3f), props.Position);
            Assert.AreEqual(new RbxVector3(5f, 6f, 7f), props.Size);
            Assert.AreEqual(RbxColor3.FromRGB(255f, 128f, 0f), props.Color);
            Assert.AreEqual(0.25f, props.Transparency, 1e-5f);
            Assert.IsTrue(props.Anchored);
            Assert.IsFalse(props.CanCollide);
        }

        [Test]
        public void Lua_BasePartCFrame_SetsBoth_Position_SetKeepsOrientation()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-4 end
                local p = Instance.new('Part')
                p.Name = 'Oriented'
                p.Parent = workspace
                p.CFrame = CFrame.new(0, 5, 0) * CFrame.Angles(0, math.pi / 2, 0)
                assert(p.CFrame.Position == Vector3.new(0, 5, 0))
                assert(near(p.CFrame.LookVector.X, -1))
                -- setting Position preserves rotation (Roblox Part semantics)
                p.Position = Vector3.new(9, 9, 9)
                assert(p.Position == Vector3.new(9, 9, 9))
                assert(near(p.CFrame.LookVector.X, -1))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartOrientation_RoundTripsDegreesYxz()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-3 end
                local p = Instance.new('Part')
                p.Orientation = Vector3.new(20, 30, 40)
                local value = p.Orientation
                assert(near(value.X, 20) and near(value.Y, 30) and near(value.Z, 40))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartOrientation_SetMatchesCFrameFromOrientationYxz()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-4 end
                local p = Instance.new('Part')
                p.Orientation = Vector3.new(20, 30, 40)
                local actual = { p.CFrame:GetComponents() }
                local expected = {
                    CFrame.fromOrientation(math.rad(20), math.rad(30), math.rad(40)):GetComponents()
                }
                for i = 1, 12 do
                    assert(near(actual[i], expected[i]))
                end");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartOrientation_SetPreservesPosition()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local p = Instance.new('Part')
                p.Position = Vector3.new(7, 8, 9)
                p.Orientation = Vector3.new(15, 25, 35)
                assert(p.Position == Vector3.new(7, 8, 9))
                assert(p.CFrame.Position == Vector3.new(7, 8, 9))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartOrientation_Yaw90_MatchesDocumentedAxes()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-4 end
                local p = Instance.new('Part')
                p.Orientation = Vector3.new(0, 90, 0)
                local look = p.CFrame.LookVector
                local right = p.CFrame.RightVector
                assert(near(look.X, -1) and near(look.Y, 0) and near(look.Z, 0))
                assert(near(right.X, 0) and near(right.Y, 0) and near(right.Z, -1))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartRotation_RoundTripsDegreesXyz_AndMatchesOrientationOnSingleAxis()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-3 end
                local p = Instance.new('Part')
                p.Rotation = Vector3.new(10, 20, 30)
                local value = p.Rotation
                assert(near(value.X, 10) and near(value.Y, 20) and near(value.Z, 30))
                p.Orientation = Vector3.new(30, 0, 0)
                local orientationCFrame = p.CFrame
                p.Rotation = Vector3.new(30, 0, 0)
                assert(p.CFrame == orientationCFrame)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartRotation_SetPreservesPosition()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local p = Instance.new('Part')
                p.Position = Vector3.new(7, 8, 9)
                p.Rotation = Vector3.new(15, 25, 35)
                assert(p.Position == Vector3.new(7, 8, 9))
                assert(p.CFrame.Position == Vector3.new(7, 8, 9))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartRotation_MultiAxisDiffersFromOrientation_AndMatchesCFrameAnglesXyz()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-4 end
                local p = Instance.new('Part')
                local rotation = Vector3.new(20, 30, 40)
                p.Rotation = rotation
                local orientation = p.Orientation
                assert(not (
                    near(orientation.X, rotation.X)
                    and near(orientation.Y, rotation.Y)
                    and near(orientation.Z, rotation.Z)))
                local actual = { p.CFrame:GetComponents() }
                local expected = {
                    CFrame.Angles(math.rad(20), math.rad(30), math.rad(40)):GetComponents()
                }
                for i = 1, 12 do
                    assert(near(actual[i], expected[i]))
                end");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartPreset_SetInCSharp_ReadableFromLua()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            RbxInstance part = roblox.Registry.Create("Part");
            part.Name = "Preset";
            part.Parent = roblox.Registry.WorldRoot;
            roblox.PartSink.SetSize(part.Id, new RbxVector3(8f, 9f, 10f));
            roblox.PartSink.SetAnchored(part.Id, true);

            stack.Runtime.LoadMod("m", @"
                local p = workspace:FindFirstChild('Preset')
                assert(p.Size == Vector3.new(8, 9, 10))
                assert(p.Anchored == true)
                -- an untouched fresh Part reads Roblox defaults
                local q = Instance.new('Part')
                assert(q.Size == Vector3.new(4, 1, 2))
                assert(q.Transparency == 0)
                assert(q.CanCollide == true)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartMaterial_SetAndReadBackEnumMaterialThroughProductionPath()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local p = Instance.new('Part')
                p.Material = Enum.Material.Wood
                assert(p.Material == Enum.Material.Wood)
                assert(p.Material.Name == 'Wood')
                assert(p.Material.Value == 512)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [TestCase(
            "local value = workspace.Gravity",
            "Workspace.Gravity", "MVP8")]
        [TestCase(
            "local value = workspace.Raycast",
            "WorldRoot:Raycast", "MVP8")]
        public void Lua_PlannedUnimplementedMember_RaisesExactPhaseNamingStub(
            string code, string feature, string phase)
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", code);
            string fullText = FullText(ex);
            StringAssert.Contains("NOT_IMPLEMENTED", fullText);
            StringAssert.Contains(feature + " is planned for " + phase + ".", fullText);
            StringAssert.Contains("| fix: ", fullText);
        }

        [TestCase(
            "local part = Instance.new('Part'); local value = part.AssemblyLinearVelocity",
            "BasePart.AssemblyLinearVelocity")]
        [TestCase(
            "local part = Instance.new('Part'); part.Massless = true",
            "BasePart.Massless")]
        [TestCase(
            "local value = game:GetService('Lighting').ClockTime",
            "Lighting.ClockTime")]
        public void Lua_BacklogMember_RaisesUnassignedRungStatus(string code, string feature)
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", code);
            string fullText = FullText(ex);
            StringAssert.Contains("NOT_IMPLEMENTED", fullText);
            StringAssert.Contains(
                feature + " is a known Rbx member, but no roadmap rung is assigned.", fullText);
            StringAssert.DoesNotContain("is planned for", fullText);
        }

        [Test]
        public void Lua_UnsupportedTerrain_RaisesDistinctUnsupportedStatus()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", "local value = workspace.Terrain");
            string fullText = FullText(ex);
            StringAssert.Contains("NOT_IMPLEMENTED", fullText);
            StringAssert.Contains(
                "Workspace.Terrain is a known Rbx member deliberately unsupported by CoreAI.",
                fullText);
            StringAssert.DoesNotContain("is planned for", fullText);
        }

        [Test]
        public void Lua_WorkspaceSignalBehavior_ReadsDeferred()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                assert(workspace.SignalBehavior == Enum.SignalBehavior.Deferred)
                assert(tostring(workspace.SignalBehavior) == 'Enum.SignalBehavior.Deferred')");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_WorkspaceSignalBehavior_WriteIsUnsupported()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(
                stack, "m", "workspace.SignalBehavior = Enum.SignalBehavior.Immediate");
            string fullText = FullText(ex);
            StringAssert.Contains("NOT_IMPLEMENTED", fullText);
            StringAssert.Contains(
                "Workspace.SignalBehavior is a known Rbx member deliberately unsupported by CoreAI.",
                fullText);
            StringAssert.Contains("signal mode is Deferred-only", fullText);
            StringAssert.DoesNotContain("is planned for", fullText);
        }

        [TestCase("PivotTo")]
        [TestCase("GetPivot")]
        public void Lua_Folder_PvInstanceMember_RemainsInvalidMember(string member)
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m",
                "local folder = Instance.new('Folder'); local value = folder." + member);
            string fullText = FullText(ex);
            StringAssert.Contains(
                member + " is not a valid member of Folder \"Folder\"", fullText);
            StringAssert.DoesNotContain("NOT_IMPLEMENTED", fullText);
        }

        [Test]
        public void Lua_ModelPivotTo_PreservesEveryDescendantPvInstanceOffset()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function nearCFrame(a, b)
                    local ac = { a:GetComponents() }
                    local bc = { b:GetComponents() }
                    for i = 1, 12 do
                        if math.abs(ac[i] - bc[i]) >= 1e-4 then
                            return false
                        end
                    end
                    return true
                end

                local model = Instance.new('Model')
                model.Parent = workspace
                local direct = Instance.new('Part')
                direct.CFrame = CFrame.new(-4, 1, 3) * CFrame.Angles(0.1, 0.2, 0.3)
                direct.Parent = model
                local folder = Instance.new('Folder')
                folder.Parent = model
                local throughFolder = Instance.new('Part')
                throughFolder.CFrame = CFrame.new(5, -2, 7) * CFrame.Angles(-0.2, 0.4, 0.1)
                throughFolder.Parent = folder
                local nested = Instance.new('Model')
                nested.WorldPivot = CFrame.new(8, 3, -6) * CFrame.Angles(0.3, -0.1, 0.2)
                nested.Parent = folder
                local deep = Instance.new('Part')
                deep.CFrame = CFrame.new(11, 4, -9) * CFrame.Angles(0.5, 0.25, -0.15)
                deep.Parent = nested
                model.WorldPivot = CFrame.new(1, 2, 3) * CFrame.Angles(0.2, -0.3, 0.4)

                local oldPivot = model:GetPivot()
                local directOffset = oldPivot:ToObjectSpace(direct.CFrame)
                local folderOffset = oldPivot:ToObjectSpace(throughFolder.CFrame)
                local deepOffset = oldPivot:ToObjectSpace(deep.CFrame)
                local nestedOffset = oldPivot:ToObjectSpace(nested:GetPivot())
                local target = CFrame.new(30, -7, 12) * CFrame.Angles(-0.4, 0.6, 0.25)
                model:PivotTo(target)

                assert(nearCFrame(model:GetPivot(), target), 'model pivot missed target')
                assert(nearCFrame(target:ToObjectSpace(direct.CFrame), directOffset),
                    'direct descendant offset changed')
                assert(nearCFrame(target:ToObjectSpace(throughFolder.CFrame), folderOffset),
                    'folder-nested descendant offset changed')
                assert(nearCFrame(target:ToObjectSpace(deep.CFrame), deepOffset),
                    'nested-model descendant offset changed')
                assert(nearCFrame(target:ToObjectSpace(nested:GetPivot()), nestedOffset),
                    'descendant Model pivot offset changed')");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_ModelGetPivot_UsesBoundingBoxWithoutPrimaryPart_AndPrimaryPartCFrameWhenSet()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local model = Instance.new('Model')
                local left = Instance.new('Part')
                left.Size = Vector3.new(2, 2, 2)
                left.CFrame = CFrame.new(0, 0, 0)
                left.Parent = model
                local right = Instance.new('Part')
                right.Size = Vector3.new(4, 2, 2)
                right.CFrame = CFrame.new(10, 0, 0)
                right.Parent = model

                assert(model.PrimaryPart == nil)
                assert(model:GetPivot().Position == Vector3.new(5.5, 0, 0),
                    'fresh Model pivot must be its world-axis bounding-box center')
                model.PrimaryPart = right
                assert(model.PrimaryPart == right)
                assert(model:GetPivot() == right.CFrame)
                right.CFrame = CFrame.new(14, 3, -2) * CFrame.Angles(0.2, 0.4, 0.6)
                assert(model:GetPivot() == right.CFrame,
                    'PrimaryPart-driven pivot must follow the part')
                model.PrimaryPart = nil
                assert(model.PrimaryPart == nil)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_ModelPrimaryPart_NonDescendantSurvivesAssignmentThenClearsAtSimulationStep()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("create", @"
                local model = Instance.new('Model')
                model.Name = 'PivotModel'
                model.Parent = workspace
                local child = Instance.new('Part')
                child.Name = 'Child'
                child.Parent = model
                local external = Instance.new('Part')
                external.Name = 'ExternalPrimary'
                external.Parent = workspace

                assert(model.PrimaryPart == nil)
                model.PrimaryPart = external
                assert(model.PrimaryPart == external,
                    'legacy assignment must remain visible until simulation')");
            roblox.PumpPreSimulation(0.016f);
            stack.Runtime.LoadMod("verify", @"
                local model = workspace.PivotModel
                assert(model.PrimaryPart == nil,
                    'non-descendant PrimaryPart must clear at the next simulation step')
                model.PrimaryPart = model.Child
                assert(model.PrimaryPart == model.Child)
                model.PrimaryPart = nil
                assert(model.PrimaryPart == nil)");
            Assert.IsTrue(stack.Runtime.IsLoaded("create"));
            Assert.IsTrue(stack.Runtime.IsLoaded("verify"));
        }

        [Test]
        public void Lua_ModelWorldPivot_RoundTripsAndStaysFixedWhenPartsMove()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function nearCFrame(a, b)
                    local ac = { a:GetComponents() }
                    local bc = { b:GetComponents() }
                    for i = 1, 12 do
                        if math.abs(ac[i] - bc[i]) >= 1e-4 then
                            return false
                        end
                    end
                    return true
                end

                local model = Instance.new('Model')
                local part = Instance.new('Part')
                part.CFrame = CFrame.new(2, 3, 4)
                part.Parent = model
                local pivot = CFrame.new(-8, 6, 12) * CFrame.Angles(0.3, -0.5, 0.7)
                model.WorldPivot = pivot
                assert(nearCFrame(model.WorldPivot, pivot))
                assert(nearCFrame(model:GetPivot(), pivot))
                local partBefore = part.CFrame
                local replacement = CFrame.new(9, -4, 2) * CFrame.Angles(-0.2, 0.1, 0.8)
                model.WorldPivot = replacement
                assert(nearCFrame(model.WorldPivot, replacement))
                assert(nearCFrame(model:GetPivot(), replacement))
                assert(part.CFrame == partBefore, 'setting WorldPivot must not move descendants')
                part.CFrame = CFrame.new(100, 200, 300)
                assert(nearCFrame(model:GetPivot(), replacement),
                    'explicit WorldPivot must stay fixed when descendants move')");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_DataModelBindToClose_ValidatesFunctionBeforeMvp5Stub()
        {
            LuaCsModStack badArgumentStack = BuildStack(new LuaCsRbxApiBindings());
            Exception badArgument = LoadFails(
                badArgumentStack, "bad", "game:BindToClose('not a function')");
            StringAssert.Contains("BAD_ARGUMENT", FullText(badArgument));
            StringAssert.Contains(
                "game:BindToClose expects a function at argument 1, got string",
                FullText(badArgument));
            StringAssert.Contains("pass the function to run at shutdown", FullText(badArgument));

            LuaCsModStack notImplementedStack = BuildStack(new LuaCsRbxApiBindings());
            Exception notImplemented = LoadFails(
                notImplementedStack, "stub", "game:BindToClose(function() end)");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(notImplemented));
            StringAssert.Contains("game:BindToClose", FullText(notImplemented));
            StringAssert.Contains("MVP5", FullText(notImplemented));
        }

        [Test]
        public void Lua_BasePartPosition_RoundTripsThroughBinder_NoScaleOrChiralityDistortion()
        {
            // WHY: golden — a Lua Position write must survive Roblox→Unity→(read) with no double
            // conversion: GameObject lands 0.28-scaled/Z-mirrored, Lua/registry keeps pure Roblox studs
            // (mirrors PositionGolden in the binder tests, driven end-to-end through the Lua surface).
            RbxSpace.ResetForTests(0.28f);
            GameObject root = new("GoldenRoot");
            try
            {
                InstanceGameObjectBinder binder = new(root.transform);
                InstanceRegistry registry = new(null, binder);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                LuaCsRbxApiBindings roblox = new(registry, game, partSink: binder);
                LuaCsModStack stack = BuildStack(roblox);

                RbxInstance part = registry.Create("Part");
                part.Name = "Golden";
                part.Parent = registry.WorldRoot;

                stack.Runtime.LoadMod("m", @"
                    local p = workspace:FindFirstChild('Golden')
                    p.Position = Vector3.new(10, 5, -4)
                    assert(p.Position == Vector3.new(10, 5, -4), 'Lua must read pure Roblox studs')");

                PartProperties props = binder.GetPartPropertiesOrDefault(part.Id);
                Assert.AreEqual(10f, props.Position.X, 1e-4f);
                Assert.AreEqual(5f, props.Position.Y, 1e-4f);
                Assert.AreEqual(-4f, props.Position.Z, 1e-4f);

                Assert.IsTrue(binder.TryGetBoundObject(part.Id, out GameObject go));
                Assert.AreEqual(2.8f, go.transform.position.x, 1e-4f);
                Assert.AreEqual(1.4f, go.transform.position.y, 1e-4f);
                Assert.AreEqual(1.12f, go.transform.position.z, 1e-4f, "mod-space z = -Unity z (D2)");

                game.Destroy();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                RbxSpace.ResetForTests();
            }
        }

        [Test]
        public void Lua_R6_7_DatatypeAttribute_Vector3RoundTrip()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local f = Instance.new('Folder', workspace)
                f:SetAttribute('Spawn', Vector3.new(1, 2, 3))
                f:SetAttribute('Tint', Color3.fromRGB(255, 0, 0))
                local v = f:GetAttribute('Spawn')
                assert(v == Vector3.new(1, 2, 3))
                assert(v.X == 1 and v.Y == 2 and v.Z == 3)
                assert(f:GetAttribute('Tint') == Color3.fromRGB(255, 0, 0))
                local attrs = f:GetAttributes()
                assert(attrs.Spawn == Vector3.new(1, 2, 3))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_UnsupportedDatatypeAttribute_RejectedWithSupportedList()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m",
                "Instance.new('Folder'):SetAttribute('Bad', CFrame.new(1, 2, 3))");
            StringAssert.Contains("BAD_ARGUMENT", FullText(ex));
            StringAssert.Contains("CFrame", FullText(ex));
            StringAssert.Contains("Vector3, Vector2, Color3, or UDim", FullText(ex));
        }

        [Test]
        public void Lua_SignalConnect_UsesGeneralDeferredSurface()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", "workspace.ChildAdded:Connect(function() end)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_TaskWait_TopLevelSuspendsAndResumes_AndParallelSwitchesAreNoOps()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            stack.Runtime.LoadMod("noop", @"
                task.synchronize()
                task.desynchronize()");
            Assert.IsTrue(stack.Runtime.IsLoaded("noop"), "DEV-5: parallel switches must be no-ops");

            stack.Runtime.LoadMod("waiter", @"
                store_set('phase', 'waiting')
                task.wait(1)
                store_set('phase', 'resumed')");
            Assert.IsTrue(stack.Runtime.IsLoaded("waiter"));
            Assert.AreEqual("waiting", store.Get("waiter", "phase"));

            bindings.Scheduler.Advance(0.5d);
            Assert.AreEqual("waiting", store.Get("waiter", "phase"));

            bindings.Scheduler.Advance(0.5d);
            Assert.AreEqual("resumed", store.Get("waiter", "phase"));
        }

        [Test]
        public void Lua_WaitForChild_TimeoutZeroReturnsNilImmediately()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m",
                "assert(workspace:WaitForChild('NeverThere', 0) == nil)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        // ---- Shared world -------------------------------------------------------------------

        [Test]
        public void Lua_TwoMods_ShareOneInstanceWorld()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("producer", @"
                local f = Instance.new('Folder', workspace)
                f.Name = 'SharedNode'");
            stack.Runtime.LoadMod("consumer", @"
                local f = workspace:FindFirstChild('SharedNode')
                assert(f ~= nil, 'mods must share one Roblox world')
                assert(f.Name == 'SharedNode')");
            Assert.IsTrue(stack.Runtime.IsLoaded("consumer"));
        }
    }
}
