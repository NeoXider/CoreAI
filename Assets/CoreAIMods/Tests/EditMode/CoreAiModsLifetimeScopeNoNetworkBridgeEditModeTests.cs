using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Unity.Logging;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The empty-field contract of the network transport seam on <see cref="CoreAiModsLifetimeScope"/>:
    /// with no bridge provider assigned, the scope registers no <see cref="INetworkBridge"/> and the
    /// installer's world falls back to <see cref="NullNetworkBridge"/> - the behaviour every scene had
    /// before the field existed.
    /// </summary>
    /// <remarks>
    /// WHY this lives here and not next to the provider's own fixture: the only other test of the field
    /// compiles under the MIRROR define, and Assets/Mirror is gitignored, so a clone without Mirror keeps a
    /// green suite while losing every line that touches the seam. This assembly always compiles,
    /// references nothing from Mirror, and drives the scope's real Configure the way that fixture does -
    /// a copy of the registration would pass under a scope that had stopped consulting the field.
    /// </remarks>
    [TestFixture]
    public sealed class CoreAiModsLifetimeScopeNoNetworkBridgeEditModeTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _scopeGo;
        private CoreAiModsLifetimeScope _scope;

        [SetUp]
        public void CreateScope()
        {
            _scopeGo = new GameObject("CoreAiModsLifetimeScope_NoBridge");
            _scope = _scopeGo.AddComponent<CoreAiModsLifetimeScope>();
        }

        [TearDown]
        public void DestroyScope()
        {
            UnityEngine.Object.DestroyImmediate(_scopeGo);
        }

        [Test]
        public void Configure_WithNoBridgeProvider_RegistersNoNetworkBridge()
        {
            Assert.IsNull(GetField(_scope, "networkBridgeProvider"),
                "a freshly added scope must start with the transport field empty");
            ContainerBuilder builder = new();

            Configure(_scope, builder);

            Assert.IsFalse(builder.Exists(typeof(INetworkBridge), includeInterfaceTypes: true),
                "an empty provider field must leave the container without any transport registration");
            using IObjectResolver container = builder.Build();
            Assert.IsNull(container.ResolveOrDefault<INetworkBridge>(),
                "the installer's ResolveOrDefault<INetworkBridge>() must see nothing - that is what selects NullNetworkBridge");
        }

        [Test]
        public void Configure_WithNoBridgeProvider_WorldRunsOnTheNullBridge()
        {
            ContainerBuilder builder = new();
            Configure(_scope, builder);
            RegisterMinimalHostServices(builder);

            using IObjectResolver container = builder.Build();
            LuaCsModStack stack = container.Resolve<LuaCsModStack>();

            Assert.IsInstanceOf<NullNetworkBridge>(stack.GameplayBindings.RbxApi.NetworkBridge,
                "with nothing registered the installer must build the Rbx world on the in-process null bridge");
        }

        /// <summary>
        /// The services the installer's world factory resolves unconditionally and a scope normally
        /// inherits from its CoreAI parent; the null log keeps the headless-world diagnostic out of the
        /// Unity console, and the in-memory store keeps the persistent mod store untouched.
        /// </summary>
        private static void RegisterMinimalHostServices(IContainerBuilder builder)
        {
            builder.RegisterInstance<IGameLogger>(new SilentGameLogger());
            builder.RegisterInstance<Logging.ILog>(Logging.NullLog.Instance);
            builder.Register<NoopCommandSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
            builder.RegisterInstance<ILuaModStore>(new MemoryStore());
            builder.RegisterInstance<ILuaModSourceStore>(new EmptySourceStore());
        }

        private static void Configure(CoreAiModsLifetimeScope scope, ContainerBuilder builder)
        {
            MethodInfo configure = typeof(CoreAiModsLifetimeScope).GetMethod(
                "Configure", Private, null, new[] { typeof(IContainerBuilder) }, null);
            Assert.IsNotNull(configure, "CoreAiModsLifetimeScope.Configure(IContainerBuilder)");
            configure.Invoke(scope, new object[] { builder });
        }

        private static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.IsNotNull(field, target.GetType().Name + "." + name);
            return field.GetValue(target);
        }

        private sealed class SilentGameLogger : IGameLogger
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

        private sealed class NoopCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
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

                foreach ((string storedModId, string key) in keys)
                {
                    _values.Remove((storedModId, key));
                }
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
    }

    /// <summary>
    /// The half of the Roblox Lua surface proof that needs the shipped composition: every test here
    /// builds the production mods container (<c>RegisterCoreAiMods</c> through VContainer) and drives
    /// the Lua network path, the MP-10 remote-handler budgets or the <c>execute_lua</c> tool entry the
    /// way a scene does. The engine-free rest of that proof is <c>RbxApiLuaBindingsEditModeTests</c>.
    /// </summary>
    /// <remarks>
    /// WHY a separate fixture next to the scope's own: VContainer is not part of the portable Lua-tier
    /// build (tools/portable/LuaTests), so these tests live apart from the engine-free ones and that
    /// file runs whole on Linux while this one runs in the editor only.
    /// </remarks>
    [TestFixture]
    public sealed class RbxApiLuaBindingsProductionContainerEditModeTests
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
            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public event Action<RbxNetworkPeerDisconnected> PeerDisconnected
            {
                add { }
                remove { }
            }

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

        private static string FullText(Exception ex)
        {
            return ex.ToString();
        }

        private static ActorContext Actor(string actorId)
        {
            return new LocalActorIdentityProvider(actorId)
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private static InstanceRecord Record(InstanceRegistry registry, RbxInstance instance)
        {
            Assert.IsTrue(registry.TryGetRecord(instance.Id, out InstanceRecord record));
            return record;
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
                    -- WHY: proves R5.4 deferred dispatch — if OnServerEvent ran synchronously
                    -- inside FireServer, the shared workspace attribute it sets would already be
                    -- true the instant FireServer returns, before any scheduler drain runs.
                    store_set('fired_synchronously',
                        tostring(workspace:GetAttribute('ServerReceivedFire') == true))
                end)", persistToStore: false);

            harness.Runtime.LoadMod(serverActor, "network-delivery-server", @"
                local Players = game:GetService('Players')
                assert(Players.LocalPlayer == nil)
                local remote = Instance.new('RemoteEvent')
                remote.Name = 'DeliveryRemote'
                remote.Parent = workspace
                remote.OnServerEvent:Connect(function(player, payload)
                    store_set('server', tostring(player.UserId) .. ':' .. payload)
                    workspace:SetAttribute('ServerReceivedFire', true)
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

            // WHY: both LoadMod calls only queue task.defer bodies — nothing has drained yet, so
            // the server handler cannot have run regardless of dispatch correctness. This baseline
            // makes the post-pump assertions below meaningful rather than vacuous.
            Assert.AreEqual("", harness.Store.Get("network-delivery-server", "server"));

            harness.PumpFrames(4);

            Assert.IsInstanceOf<NullNetworkBridge>(harness.Bindings.NetworkBridge);
            Assert.AreEqual("1:server-payload",
                harness.Store.Get("network-delivery-server", "server"));
            Assert.AreEqual("1",
                harness.Store.Get("network-delivery-server", "server_player_count"));
            Assert.AreEqual("one,all",
                harness.Store.Get("network-delivery-client", "client"));
            Assert.AreEqual("false",
                harness.Store.Get("network-delivery-client", "fired_synchronously"),
                "OnServerEvent must not run synchronously inside FireServer (R5.4 deferred dispatch).");
        }

        [Test]
        public void Lua_MP_10_RemoteFunctionFlood_IsChargedToTheSenderAndRefusedOverItsBudget()
        {
            using ProductionNetworkHarness harness = new();
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            ActorContext flooder = Actor("mp10-flood-actor");
            ActorContext secondClient = Actor("mp10-second-actor");
            int budget = LuaCsRbxApiBindings.MaxRemoteHandlerThreadsPerSender;
            int calls = budget + 8;

            harness.Runtime.LoadMod(serverActor, "mp10-server", @"
                local remote = Instance.new('RemoteFunction')
                remote.Name = 'FloodRemote'
                remote.Parent = workspace
                local started = 0
                remote.OnServerInvoke = function(player)
                    started = started + 1
                    store_set('started', tostring(started))
                    task.wait(10)
                    return 'done'
                end", persistToStore: false);
            long refusalsBefore = harness.Bindings.RemoteHandlerRefusalCount;
            harness.Runtime.LoadMod(flooder, "mp10-flooder", @"
                local remote = workspace:FindFirstChild('FloodRemote')
                for index = 1, " + calls + @" do
                    task.spawn(function()
                        local ok, err = pcall(function() return remote:InvokeServer(index) end)
                        if not ok then
                            store_set('refused', tostring((tonumber(store_get('refused')) or 0) + 1))
                            store_set('refusal', tostring(err))
                        end
                    end)
                end", persistToStore: false);

            harness.PumpFrames(1);

            // WHY: the handler threads used to be charged to the handler's owner, the host, so one
            // client flooding a yielding OnServerInvoke filled the host's whole thread quota.
            Assert.AreEqual(budget.ToString(CultureInfo.InvariantCulture),
                harness.Store.Get("mp10-server", "started"),
                "at most the sender's budget of handler threads runs at once");
            Assert.AreEqual("8", harness.Store.Get("mp10-flooder", "refused"));
            Assert.AreEqual(8L, harness.Bindings.RemoteHandlerRefusalCount - refusalsBefore,
                "every call over the sender's budget is counted as a refusal of that sender");
            // WHY the caller's own line (B1-05): the budget refusal names the host mod, and the caller is a
            // remote client, so it reads a line of its own; the host log keeps the refusal.
            StringAssert.Contains(LuaCsRbxApiBindings.RemoteFunctionCallRefusedMessage,
                harness.Store.Get("mp10-flooder", "refusal"));
            StringAssert.DoesNotContain("mp10-server", harness.Store.Get("mp10-flooder", "refusal"),
                "a line sent to a remote caller names no host mod");

            harness.Runtime.LoadMod(secondClient, "mp10-second", @"
                local remote = workspace:FindFirstChild('FloodRemote')
                task.spawn(function()
                    local ok, value = pcall(function() return remote:InvokeServer(1) end)
                    store_set('answer', tostring(ok) .. ':' .. tostring(value))
                end)", persistToStore: false);
            harness.PumpFrames(1);

            Assert.AreEqual((budget + 1).ToString(CultureInfo.InvariantCulture),
                harness.Store.Get("mp10-server", "started"), "another sender has its own budget");
            harness.Runtime.LoadMod(serverActor, "mp10-host-spawn",
                "task.spawn(function() store_set('ran', 'yes') end)", persistToStore: false);
            Assert.AreEqual("yes", harness.Store.Get("mp10-host-spawn", "ran"),
                "the host's own thread quota is untouched by the flood");
            Assert.IsEmpty(harness.Runtime.GetRecentHandlerErrors(serverActor, "mp10-server"),
                "a refused remote call is not the handler owner's fault");

            harness.Bindings.Scheduler.Advance(10.1d);
            harness.PumpFrames(1);

            Assert.AreEqual("true:done", harness.Store.Get("mp10-second", "answer"));
            Assert.AreEqual(0, harness.Bindings.Scheduler.CountInducedThreads(flooder.ActorId),
                "finished handlers release the sender's budget");
        }

        [Test]
        public void Lua_MP_10_RemoteEventFlood_DropsAndCountsHandlersOverTheSendersBudget()
        {
            using ProductionNetworkHarness harness = new();
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            ActorContext flooder = Actor("mp10-event-flood-actor");
            ActorContext secondClient = Actor("mp10-event-second-actor");
            int budget = LuaCsRbxApiBindings.MaxRemoteHandlerThreadsPerSender;

            harness.Runtime.LoadMod(serverActor, "mp10-event-server", @"
                local remote = Instance.new('RemoteEvent')
                remote.Name = 'FloodEvent'
                remote.Parent = workspace
                local started = 0
                remote.OnServerEvent:Connect(function(player)
                    started = started + 1
                    store_set('started', tostring(started))
                    task.wait(10)
                end)", persistToStore: false);
            long refusalsBefore = harness.Bindings.RemoteHandlerRefusalCount;
            harness.Runtime.LoadMod(flooder, "mp10-event-flooder", @"
                local remote = workspace:FindFirstChild('FloodEvent')
                for index = 1, " + (budget + 8) + @" do
                    remote:FireServer(index)
                end", persistToStore: false);

            harness.PumpFrames(1);

            Assert.AreEqual(budget.ToString(CultureInfo.InvariantCulture),
                harness.Store.Get("mp10-event-server", "started"));
            Assert.AreEqual(8L, harness.Bindings.RemoteHandlerRefusalCount - refusalsBefore,
                "every dropped invocation is counted");
            Assert.IsEmpty(harness.Runtime.GetRecentHandlerErrors(serverActor, "mp10-event-server"),
                "a dropped remote invocation is not the handler owner's fault");

            harness.Runtime.LoadMod(secondClient, "mp10-event-second",
                "workspace:FindFirstChild('FloodEvent'):FireServer(1)", persistToStore: false);
            harness.PumpFrames(1);

            Assert.AreEqual((budget + 1).ToString(CultureInfo.InvariantCulture),
                harness.Store.Get("mp10-event-server", "started"), "another sender has its own budget");
        }

        [Test]
        public void Lua_MP_10_A4_01_AFloodThroughAHandlerThatSchedulesWork_ChargesTheSender_NeverTheHost()
        {
            // WHY: the sender was charged for the handler thread only; the task.delay inside it was
            // charged to the handler's owner, so one client firing well under any rate limit filled
            // the host's thread quota, broke the host's own task.spawn and got the host's gameplay mod
            // quarantined (A4-01). The Linux-run twin is in RbxTaskSchedulerLuaBindingsEditModeTests.
            using ProductionNetworkHarness harness = new();
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            ActorContext flooder = Actor("a4-01-flood-actor");

            harness.Runtime.LoadMod(serverActor, "a4-01-server", @"
                local remote = Instance.new('RemoteEvent')
                remote.Name = 'Cooldown'
                remote.Parent = workspace
                local handled = 0
                remote.OnServerEvent:Connect(function(player)
                    handled = handled + 1
                    store_set('handled', tostring(handled))
                    task.delay(60, function() end)
                end)", persistToStore: false);
            harness.Runtime.LoadMod(flooder, "a4-01-flooder", @"
                local remote = workspace:FindFirstChild('Cooldown')
                for index = 1, 300 do remote:FireServer(index) end", persistToStore: false);
            harness.PumpFrames(3);

            harness.Runtime.LoadMod(serverActor, "a4-01-host-work",
                "task.spawn(function() store_set('ran', 'yes') end)", persistToStore: false);

            Assert.AreEqual("yes", harness.Store.Get("a4-01-host-work", "ran"),
                "the host's own thread quota is untouched by what the client's calls scheduled");
            Assert.AreEqual("300", harness.Store.Get("a4-01-server", "handled"));
            Assert.AreEqual(harness.Bindings.Scheduler.MaxInducedThreadsPerSender,
                harness.Bindings.Scheduler.CountInducedThreads(flooder.ActorId),
                "the delayed threads are the sender's, held to its induced budget; the finished handlers hold none");
            Assert.IsEmpty(harness.Runtime.GetRecentHandlerErrors(serverActor, "a4-01-server"),
                "a task.delay refused for the sender's budget is not the handler owner's fault");
            foreach (LuaModInfo info in harness.Runtime.ListMods(serverActor))
            {
                if (info.Id == "a4-01-server")
                {
                    Assert.IsFalse(info.Quarantined, "one client's flood must not quarantine the host's mod");
                }
            }
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
        public void Lua_NetworkProductionPath_RemoteEventTablePayload_IsCopiedPerReceiver()
        {
            using ProductionNetworkHarness harness = new();
            ActorContext serverActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            ActorContext firstClient = Actor("payload-copy-actor-a");
            ActorContext secondClient = Actor("payload-copy-actor-b");
            harness.Runtime.LoadMod(serverActor, "payload-copy-server", @"
                local remote = Instance.new('RemoteEvent')
                remote.Name = 'PayloadCopyRemote'
                remote.Parent = workspace", persistToStore: false);

            // WHY two handlers per mod and two client actors: one FireAllClients reaches every
            // actor's OnClientEvent, and every connection of each, with the same decoded argument
            // array. Each handler overwrites a field, a nested field and the metatable of what it
            // received, so any sharing shows up as the other handler's writes.
            const string receiver = @"
                local remote = workspace:FindFirstChild('PayloadCopyRemote')
                local seen = {}
                local function record(tag)
                    return function(payload)
                        local entry = { payload = payload }
                        if type(payload) == 'table' then
                            entry.score = payload.score
                            entry.nested = payload.nested and payload.nested.value
                            entry.metatableAbsent = getmetatable(payload) == nil
                            payload.score = 999
                            payload.nested.value = 'overwritten by ' .. tag
                            setmetatable(payload, { __index = function() return 'leaked from ' .. tag end })
                        end
                        table.insert(seen, entry)
                        if #seen == 2 then
                            local first, second = seen[1], seen[2]
                            store_set('scores', tostring(first.score) .. ',' .. tostring(second.score))
                            store_set('nested', tostring(first.nested) .. ',' .. tostring(second.nested))
                            store_set('metatables', tostring(first.metatableAbsent) .. ','
                                .. tostring(second.metatableAbsent))
                            store_set('distinct', tostring(type(first.payload) == 'table'
                                and not rawequal(first.payload, second.payload)))
                        end
                    end
                end
                remote.OnClientEvent:Connect(record('a'))
                remote.OnClientEvent:Connect(record('b'))";
            harness.Runtime.LoadMod(firstClient, "payload-copy-client-a", receiver, persistToStore: false);
            harness.Runtime.LoadMod(secondClient, "payload-copy-client-b", receiver, persistToStore: false);

            harness.Runtime.LoadMod(serverActor, "payload-copy-sender", @"
                workspace:FindFirstChild('PayloadCopyRemote'):FireAllClients({
                    score = 1,
                    nested = { value = 'original' }
                })", persistToStore: false);
            harness.PumpFrames(2);

            foreach (string modId in new[] { "payload-copy-client-a", "payload-copy-client-b" })
            {
                Assert.AreEqual("1,1", harness.Store.Get(modId, "scores"),
                    modId + ": each handler must read the value the server sent, not another receiver's write");
                Assert.AreEqual("original,original", harness.Store.Get(modId, "nested"),
                    modId + ": nested tables are copied too");
                Assert.AreEqual("true,true", harness.Store.Get(modId, "metatables"),
                    modId + ": a metatable installed by one receiver must not reach another");
                Assert.AreEqual("true", harness.Store.Get(modId, "distinct"),
                    modId + ": two connections must not receive the same table object");
            }
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

            // WHY keep mode: the replacement reuses the remote the loaded run built; a default (clean)
            // reload destroys it, and the client below would find no remote at all.
            harness.Runtime.ReloadMod(serverActor, serverModId, @"
                local remote = workspace:FindFirstChild('ReloadRemote')", ModReloadMode.KeepObjects);

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
    }
}
