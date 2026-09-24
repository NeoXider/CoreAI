using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// The composition seam that switches the Mirror transport on: the scene provider hands the
    /// container one working bridge, and the mods scope registers it without building it.
    /// </summary>
    /// <remarks>
    /// WHY the scope's own Configure is driven here instead of a copy of its registration: the
    /// property being proven is WHEN the bridge is built, and only the real registration can show
    /// that — a copy would pass under RegisterInstance too. Nothing here claims bytes cross a
    /// socket; the rules that need a live host stay in the bridge and session host fixtures.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorNetworkBridgeProviderEditModeTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _go;
        private CoreAiMirrorNetworkBridgeProvider _provider;

        [SetUp]
        public void CreateProvider()
        {
            _go = new GameObject("CoreAI_MirrorBridgeProviderTest");
            _provider = _go.AddComponent<CoreAiMirrorNetworkBridgeProvider>();
        }

        [TearDown]
        public void DestroyProvider()
        {
            // WHY explicit: Unity does not run OnDestroy for a plain component in edit mode, and a
            // bridge left alive would keep its Mirror handlers registered for the next fixture.
            _provider.ReleaseTransport();
            UnityEngine.Object.DestroyImmediate(_go);
        }

        [Test]
        public void Bridge_IsAWorkingServerBridgeWithASessionHost()
        {
            INetworkBridge bridge = _provider.Bridge;

            Assert.IsInstanceOf<MirrorNetworkBridge>(bridge);
            Assert.AreEqual(RbxNetworkTopology.Host, bridge.Topology);
            bridge.RegisterActor("actor-a");
            CollectionAssert.Contains(bridge.ActorIds, "actor-a");
            Assert.IsNotNull(_provider.SessionHost, "a server builds its session host with the bridge");
        }

        [Test]
        public void Bridge_IsTheSameInstanceOnEveryAccess()
        {
            INetworkBridge first = _provider.Bridge;

            Assert.AreSame(first, _provider.Bridge);
            Assert.AreSame(_provider.SessionHost, _provider.SessionHost);
        }

        [Test]
        public void ClientRole_BuildsAClientBridgeAndNoSessionHost()
        {
            _provider.Role = CoreAiMirrorRole.Client;

            INetworkBridge bridge = _provider.Bridge;

            Assert.AreEqual(RbxNetworkTopology.Client, bridge.Topology);
            Assert.IsNull(_provider.SessionHost, "admission is a server concern");
        }

        [Test]
        public void Negative_RoleCannotChangeOnceTheBridgeExists()
        {
            _ = _provider.Bridge;

            Assert.Throws<InvalidOperationException>(() => _provider.Role = CoreAiMirrorRole.Client);

            Assert.AreEqual(CoreAiMirrorRole.Server, _provider.Role);
            Assert.AreEqual(RbxNetworkTopology.Host, _provider.Bridge.Topology,
                "a refused role change must leave the existing bridge untouched");
        }

        [Test]
        public void Negative_AReleasedProvider_RefusesToBuildASecondBridge()
        {
            _ = _provider.Bridge;

            _provider.ReleaseTransport();

            Assert.IsFalse(_provider.HasBridge);
            Assert.Throws<ObjectDisposedException>(() => _ = _provider.Bridge,
                "a second bridge would carry traffic the world it was never given cannot see");
        }

        [Test]
        public void LifetimeScope_Configure_RegistersTheBridgeWithoutBuildingIt()
        {
            GameObject scopeGo = new("CoreAiModsLifetimeScope");
            try
            {
                CoreAiModsLifetimeScope scope = scopeGo.AddComponent<CoreAiModsLifetimeScope>();
                SetField(scope, "networkBridgeProvider", _provider);
                ContainerBuilder builder = new();

                Configure(scope, builder);

                Assert.IsFalse(_provider.HasBridge,
                    "Configure runs in Awake, before Mirror can have started; the registration must "
                    + "defer construction, which RegisterInstance(provider.Bridge) would not");
                Assert.IsTrue(builder.Exists(typeof(INetworkBridge), includeInterfaceTypes: true),
                    "the provider's bridge must still be what the installer resolves");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scopeGo);
            }
        }

        [Test]
        public void LifetimeScope_ResolvingTheBridge_BuildsItOnceAndHandsBackTheProviders()
        {
            GameObject scopeGo = new("CoreAiModsLifetimeScope");
            try
            {
                CoreAiModsLifetimeScope scope = scopeGo.AddComponent<CoreAiModsLifetimeScope>();
                SetField(scope, "networkBridgeProvider", _provider);
                ContainerBuilder builder = new();
                Configure(scope, builder);

                using IObjectResolver container = builder.Build();
                Assert.IsFalse(_provider.HasBridge,
                    "nothing in a minimal container resolves the world, so building it must not "
                    + "build the bridge either");

                INetworkBridge resolved = container.Resolve<INetworkBridge>();

                Assert.IsTrue(_provider.HasBridge);
                Assert.AreSame(_provider.Bridge, resolved);
                Assert.AreSame(resolved, container.Resolve<INetworkBridge>(),
                    "one bridge per scope: a second resolve must not build a second transport");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scopeGo);
            }
        }

        [Test]
        public void AttachedWorld_GetsAnActorForEveryAdmittedConnectionAndLosesItOnDisconnect()
        {
            CoreAiMirrorAuthenticator authenticator = _go.AddComponent<CoreAiMirrorAuthenticator>();
            authenticator.Configure(new TokenProvider("open-sesame"), "world-a");
            SetField(_provider, "authenticator", authenticator);
            List<string> connected = new();
            List<string> disconnected = new();
            _provider.AttachWorld(
                context =>
                {
                    connected.Add(context.ActorId);
                    return true;
                },
                context =>
                {
                    disconnected.Add(context.ActorId);
                    return true;
                });
            INetworkBridge bridge = _provider.Bridge;
            ActorAdmissionResult admitted = authenticator.Decide(7, "127.0.0.1:7777",
                Encoding.UTF8.GetBytes("open-sesame"));
            Assert.IsTrue(admitted.Admitted);

            authenticator.OnServerAuthenticated.Invoke(new NetworkConnectionToClient(7));

            CollectionAssert.AreEqual(new[] { admitted.Context.ActorId }, connected);
            Assert.AreEqual(1, _provider.SessionHost.LiveSessionCount);
            CollectionAssert.Contains(bridge.ActorIds, admitted.Context.ActorId);
            Assert.IsTrue(_provider.SessionHost.TryGetIdentity(admitted.Context.ActorId,
                out long userId, out _, out _));
            Assert.AreEqual(admitted.UserId, userId,
                "the UserId the world sees must be the admitted one, not a session counter");

            ((MirrorNetworkBridge)bridge).NotifyDisconnected(7,
                RbxNetworkDisconnectReason.TransportLost);

            CollectionAssert.AreEqual(new[] { admitted.Context.ActorId }, disconnected);
            Assert.AreEqual(0, _provider.SessionHost.LiveSessionCount);
            CollectionAssert.DoesNotContain(bridge.ActorIds, admitted.Context.ActorId);
        }

        [Test]
        public void Negative_NoWorldAttached_AnAdmittedConnectionGetsNoActor()
        {
            CoreAiMirrorAuthenticator authenticator = _go.AddComponent<CoreAiMirrorAuthenticator>();
            authenticator.Configure(new TokenProvider("open-sesame"), "world-a");
            SetField(_provider, "authenticator", authenticator);
            INetworkBridge bridge = _provider.Bridge;
            authenticator.Decide(7, "127.0.0.1:7777", Encoding.UTF8.GetBytes("open-sesame"));

            authenticator.OnServerAuthenticated.Invoke(new NetworkConnectionToClient(7));

            Assert.AreEqual(0, _provider.SessionHost.LiveSessionCount,
                "with no world to create the player in, the session host must leave no binding");
            CollectionAssert.IsEmpty(bridge.ActorIds);
        }

        [Test]
        public void Update_FailsARequestThatReachedTheTimeout()
        {
            double now = 0d;
            _provider.ClockSeconds = () => now;
            INetworkBridge bridge = _provider.Bridge;
            List<RbxNetworkResponse> completed = new();
            bridge.SendRequest(
                new RbxNetworkRequestMessage(new InstanceId(3UL),
                    RbxNetworkDirection.ServerToClient, null, "actor-nobody", Array.Empty<byte>()),
                completed.Add);

            now = MirrorNetworkBridge.RequestTimeoutSeconds - 0.5d;
            InvokeUpdate(_provider);
            Assert.IsEmpty(completed, "the pump must not fail a request before its deadline");

            now = MirrorNetworkBridge.RequestTimeoutSeconds + 0.1d;
            InvokeUpdate(_provider);

            Assert.AreEqual(1, completed.Count);
            Assert.IsFalse(completed[0].Succeeded);
            Assert.AreEqual(1, ((MirrorNetworkBridge)bridge).TimedOutRequests);
        }

        [Test]
        public void AttachWorld_WiresTheSessionHostAsTheWorldsIdentitySource()
        {
            LuaCsRbxApiBindings world = CreateWorld(_provider.Bridge);
            try
            {
                Assert.IsNull(world.Players.IdentitySource, "a fresh world starts with none");

                _provider.AttachWorld(() => world);

                Assert.AreSame(_provider.SessionHost, world.Players.IdentitySource,
                    "attaching a world is all a host does; the admitted identity must not depend on a "
                    + "manual step that, skipped, hands remote players counter UserIds");
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void AnAdmittedPlayer_GetsItsAdmittedUserId_WithoutManualWiring()
        {
            CoreAiMirrorAuthenticator authenticator = _go.AddComponent<CoreAiMirrorAuthenticator>();
            authenticator.Configure(new TokenProvider("open-sesame"), "world-a");
            SetField(_provider, "authenticator", authenticator);
            LuaCsRbxApiBindings world = CreateWorld(_provider.Bridge);
            try
            {
                _provider.AttachWorld(() => world);
                ActorAdmissionResult admitted = authenticator.Decide(7, "127.0.0.1:7777",
                    Encoding.UTF8.GetBytes("open-sesame"));

                authenticator.OnServerAuthenticated.Invoke(new NetworkConnectionToClient(7));

                Assert.IsTrue(world.Players.TryGetByActorId(admitted.Context.ActorId, out RbxPlayer player),
                    "the attached world got the admitted player");
                Assert.AreEqual(admitted.UserId, player.UserId,
                    "the UserId a script saves by is the admitted one, not a per-world counter");
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void AWorldPublishedLater_IsWiredOnTheProvidersNextFrame()
        {
            LuaCsRbxApiBindings first = CreateWorld(_provider.Bridge);
            LuaCsRbxApiBindings second = CreateWorld(_provider.Bridge);
            try
            {
                LuaCsRbxApiBindings live = first;
                _provider.AttachWorld(() => live);
                live = second;

                InvokeUpdate(_provider);

                Assert.AreSame(_provider.SessionHost, second.Players.IdentitySource,
                    "a world loaded at runtime starts with no identity source; the provider follows it");
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        [Test]
        public void Negative_AnIdentitySourceTheHostSet_IsLeftAlone_AndSaidOnce()
        {
            LuaCsRbxApiBindings world = CreateWorld(_provider.Bridge);
            try
            {
                FixedIdentity own = new();
                world.Players.IdentitySource = own;
                LogAssert.Expect(LogType.Warning, new Regex("not this provider's session host"));

                _provider.AttachWorld(() => world);
                InvokeUpdate(_provider);

                Assert.AreSame(own, world.Players.IdentitySource,
                    "a host that chose its own identity source keeps it; the provider does not overrule it");
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void Negative_TheWorldOverload_IsOncePerProvider_LikeTheOther()
        {
            LuaCsRbxApiBindings world = CreateWorld(_provider.Bridge);
            try
            {
                _provider.AttachWorld(() => world);

                Assert.Throws<InvalidOperationException>(() => _provider.AttachWorld(() => world));
                Assert.Throws<InvalidOperationException>(() => _provider.AttachWorld(_ => true, _ => true));
                Assert.Throws<ArgumentNullException>(() => _provider.AttachWorld((Func<LuaCsRbxApiBindings>)null));
            }
            finally
            {
                world.Dispose();
            }
        }

        private static LuaCsRbxApiBindings CreateWorld(INetworkBridge bridge)
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion, worldId: "world-a");
            return new LuaCsRbxApiBindings(registry, DataModelBootstrap.CreateGame(registry),
                networkBridge: bridge, log: _ => { });
        }

        private static void Configure(CoreAiModsLifetimeScope scope, ContainerBuilder builder)
        {
            MethodInfo configure = typeof(CoreAiModsLifetimeScope).GetMethod(
                "Configure", Private, null, new[] { typeof(IContainerBuilder) }, null);
            Assert.IsNotNull(configure, "CoreAiModsLifetimeScope.Configure(IContainerBuilder)");
            configure.Invoke(scope, new object[] { builder });
        }

        private static void InvokeUpdate(CoreAiMirrorNetworkBridgeProvider provider)
        {
            MethodInfo update = typeof(CoreAiMirrorNetworkBridgeProvider).GetMethod(
                "Update", Private, null, Type.EmptyTypes, null);
            Assert.IsNotNull(update, "the provider must pump the bridge from Update()");
            update.Invoke(provider, null);
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.IsNotNull(field, target.GetType().Name + "." + name);
            field.SetValue(target, value);
        }

        private sealed class FixedIdentity : IRbxActorIdentitySource
        {
            public bool TryGetIdentity(string actorId, out long userId, out string username,
                out string displayName)
            {
                userId = 99L;
                username = "fixed";
                displayName = "Fixed";
                return true;
            }
        }

        private sealed class TokenProvider : IActorAdmissionProvider
        {
            private readonly string _expected;
            private int _issued;

            public TokenProvider(string expected)
            {
                _expected = expected;
            }

            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                string offered = Encoding.UTF8.GetString(credential.Opaque);
                if (!string.Equals(offered, _expected, StringComparison.Ordinal))
                {
                    return ActorAdmissionResult.Reject("credential mismatch");
                }

                _issued++;
                ActorContext context = new LocalActorIdentityProvider(
                        "remote-" + _issued,
                        "session-" + _issued,
                        worldId,
                        ActorGrantSet.Create(new[] { "read" }),
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.SmartChat);
                return ActorAdmissionResult.Admit(context, 1000 + _issued, "player" + _issued, "");
            }
        }
    }
}
