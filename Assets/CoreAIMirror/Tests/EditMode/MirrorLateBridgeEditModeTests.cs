using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// Admission first, bridge second: the NetworkManager admits whenever a connection arrives,
    /// the bridge is built on the container's first resolve and a server's world attaches after
    /// that, so a connection let in before either existed must still become a player — on a
    /// server from the authenticator's record, on a client from the actor it recorded.
    /// </summary>
    /// <remarks>
    /// WHY every other fixture builds the bridge first: that is the order a scene gives when the
    /// mods scope auto-runs in Awake, and the only order those fixtures prove. Here the
    /// authenticator admits through its real request handler with one listener standing in for the
    /// NetworkManager's — the one that marks the connection authenticated — and nothing listens for
    /// the bridge, because there is none yet.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorLateBridgeEditModeTests
    {
        private const int Connection = 7;
        private const string Credential = "open-sesame";
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private OfflineMirror _mirror;
        private GameObject _go;
        private CoreAiMirrorAuthenticator _authenticator;
        private CoreAiMirrorNetworkBridgeProvider _provider;
        private List<string> _connected;
        private List<string> _disconnected;

        [SetUp]
        public void CreateProviderWithoutABridge()
        {
            _mirror = new OfflineMirror();
            _go = new GameObject("CoreAI_MirrorLateBridgeTest");
            _authenticator = _go.AddComponent<CoreAiMirrorAuthenticator>();
            _authenticator.Configure(new TokenProvider(Credential), "world-a");
            _authenticator.OnServerAuthenticated.AddListener(conn => conn.isAuthenticated = true);
            _provider = _go.AddComponent<CoreAiMirrorNetworkBridgeProvider>();
            _provider.ClockSeconds = () => 0d;
            SetField(_provider, "authenticator", _authenticator);
            _connected = new List<string>();
            _disconnected = new List<string>();
        }

        [TearDown]
        public void RestoreMirror()
        {
            OfflineMirror.RunAll(
                () => _provider.ReleaseTransport(),
                () => _mirror.Dispose(),
                () => UnityEngine.Object.DestroyImmediate(_go));
        }

        [Test]
        public void Server_AConnectionAdmittedBeforeTheBridge_GetsItsPlayerWhenTheBridgeIsBuilt()
        {
            AttachWorld();
            StartServerAndAdmit(Connection);
            Assert.IsFalse(_provider.HasBridge, "the admission must have completed with no bridge to hear it");
            Assert.IsEmpty(_connected);

            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;

            CollectionAssert.AreEqual(new[] { "remote-1" }, _connected);
            Assert.AreEqual(1, _provider.SessionHost.LiveSessionCount);
            CollectionAssert.Contains(bridge.ActorIds, "remote-1");
            List<RbxNetworkEventMessage> delivered = new();
            bridge.EventReceived += delivered.Add;
            OfflineMirror.DeliverToServer(Connection, OfflineMirror.Event(1UL));
            Assert.AreEqual(1, delivered.Count,
                "the packets of a player admitted before the bridge must not be dropped as unadmitted");
            Assert.AreEqual("remote-1", delivered[0].SenderActorId);
            Assert.AreEqual(0, bridge.UnadmittedPacketsDropped);
        }

        [Test]
        public void Server_APlayerAdmittedBeforeTheBridge_IsTornDownOnDisconnectLikeAnyOther()
        {
            AttachWorld();
            StartServerAndAdmit(Connection);
            _ = _provider.Bridge;
            Frame();

            OfflineMirror.DropServerConnection(Connection);

            CollectionAssert.AreEqual(new[] { "remote-1" }, _disconnected);
            Assert.AreEqual(0, _provider.SessionHost.LiveSessionCount);
            Assert.IsNull(_authenticator.ResultFor(Connection),
                "a reused connection id must not inherit the admission of the player that left");
        }

        [Test]
        public void Server_AConnectionAdmittedBeforeTheWorld_GetsItsPlayerWhenTheWorldAttaches()
        {
            StartServerAndAdmit(Connection);
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            Assert.AreEqual(0, _provider.SessionHost.LiveSessionCount,
                "with no world yet there is nothing to create the player in");
            Assert.IsNotNull(_authenticator.ResultFor(Connection),
                "the admission must survive until a world can take it");

            AttachWorld();

            CollectionAssert.AreEqual(new[] { "remote-1" }, _connected);
            Assert.AreEqual(1, _provider.SessionHost.LiveSessionCount);
            CollectionAssert.Contains(bridge.ActorIds, "remote-1");
        }

        [Test]
        public void Server_AConnectionAdmittedWhileNoWorldWasAttached_IsNotForgotten_AndGetsItsPlayerWhenOneIs()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            StartServerAndAdmit(Connection);
            Assert.AreEqual(0, _provider.SessionHost.LiveSessionCount);
            CollectionAssert.IsEmpty(bridge.ActorIds,
                "no binding may resolve a connection to a player that does not exist");
            Assert.IsNotNull(_authenticator.ResultFor(Connection),
                "having no world to admit into must not forget the admission");

            AttachWorld();

            CollectionAssert.AreEqual(new[] { "remote-1" }, _connected);
            Assert.AreEqual(1, _provider.SessionHost.LiveSessionCount);
        }

        [Test]
        public void Server_AttachingTheWorldAgain_SwapsItWithoutAdmittingTwice()
        {
            AttachWorld();
            StartServerAndAdmit(Connection);
            _ = _provider.Bridge;
            List<string> secondWorld = new();

            _provider.AttachWorld(
                context =>
                {
                    secondWorld.Add(context.ActorId);
                    return true;
                },
                _ => true);

            Assert.IsEmpty(secondWorld, "a session already live is not admitted again into the swapped world");
            CollectionAssert.AreEqual(new[] { "remote-1" }, _connected);
            Assert.AreEqual(1, _provider.SessionHost.LiveSessionCount);
        }

        [Test]
        public void Negative_Server_AStrangerOnAReusedId_IsNotAdmittedOnTheDepartedPlayersRecord()
        {
            StartServerAndAdmit(Connection);
            OfflineMirror.DropServerConnection(Connection);
            Assert.IsNotNull(_authenticator.ResultFor(Connection),
                "with no bridge to hear the drop the record is still there, which is the trap");
            OfflineMirror.OpenServerConnection(Connection);
            AttachWorld();

            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;

            Assert.IsEmpty(_connected, "the stranger on the reused id is nobody until it is admitted itself");
            Assert.AreEqual(0, _provider.SessionHost.LiveSessionCount);
            RequestAdmission(Connection, Credential);
            CollectionAssert.AreEqual(new[] { "remote-2" }, _connected,
                "admitted itself, the stranger is its own actor and never the one that left");
            CollectionAssert.DoesNotContain(bridge.ActorIds, "remote-1");
        }

        [Test]
        public void Client_AnAdmissionBeforeTheBridge_IsBoundWhenTheBridgeIsBuilt()
        {
            _provider.Role = CoreAiMirrorRole.Client;
            StartClientAndAdmit("remote-1");
            Assert.IsFalse(_provider.HasBridge, "the admission must have completed with no bridge to hear it");

            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;

            Assert.AreEqual("remote-1", bridge.AdmittedActorId);
            List<RbxNetworkEventMessage> delivered = new();
            bridge.EventReceived += delivered.Add;
            Frame();
            OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));
            Assert.AreEqual(1, delivered.Count,
                "a server remote to a client admitted before its bridge must not be dropped");
            Assert.AreEqual("remote-1", delivered[0].RecipientActorId);
            Assert.AreEqual(0, bridge.UnadmittedPacketsDropped);
        }

        [Test]
        public void Negative_Client_AnAdmissionThatNamedNobody_IsSaidLoudlyWhenTheBridgeIsBuilt()
        {
            _provider.Role = CoreAiMirrorRole.Client;
            StartClientAndAdmit("");
            LogAssert.Expect(LogType.Error, new Regex("without naming its actor"));

            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;

            Assert.IsNull(bridge.AdmittedActorId);
        }

        [Test]
        public void Negative_Client_AnActorLeftByAClientThatStopped_IsNotBound()
        {
            _provider.Role = CoreAiMirrorRole.Client;
            StartClientAndAdmit("remote-1");
            OfflineMirror.StopClient();
            Assert.AreEqual("remote-1", _authenticator.ClientActorId,
                "a stop the authenticator never heard leaves its record behind, which is the trap");

            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;

            Assert.IsNull(bridge.AdmittedActorId,
                "an id from a connection that is gone is not this connection's admission");
        }

        private void AttachWorld()
        {
            _provider.AttachWorld(
                context =>
                {
                    _connected.Add(context.ActorId);
                    return true;
                },
                context =>
                {
                    _disconnected.Add(context.ActorId);
                    return true;
                });
        }

        /// <summary>
        /// Starts the server if needed, connects one unauthenticated connection and admits it
        /// through the authenticator's real request handler.
        /// </summary>
        private void StartServerAndAdmit(int connectionId)
        {
            if (!NetworkServer.active)
            {
                OfflineMirror.StartServer();
                _authenticator.OnStartServer();
            }

            OfflineMirror.OpenServerConnection(connectionId);
            RequestAdmission(connectionId, Credential);
        }

        private static void RequestAdmission(int connectionId, string credential)
        {
            OfflineMirror.DeliverToServer(connectionId, new CoreAiAdmissionRequestMessage
            {
                Credential = Encoding.UTF8.GetBytes(credential)
            });
        }

        /// <summary>Connects the client and delivers the server's acceptance naming it.</summary>
        private void StartClientAndAdmit(string actorId)
        {
            OfflineMirror.StartClient();
            _authenticator.OnStartClient();
            OfflineMirror.DeliverToClient(new CoreAiAdmissionResponseMessage
            {
                Admitted = true,
                Reason = "",
                ActorId = actorId
            });
        }

        /// <summary>One provider frame, with the reflection wrapper peeled off any exception.</summary>
        private void Frame()
        {
            MethodInfo update = typeof(CoreAiMirrorNetworkBridgeProvider).GetMethod(
                "Update", Private, null, Type.EmptyTypes, null);
            Assert.IsNotNull(update, "the provider must drive the bridge from Update()");
            try
            {
                update.Invoke(_provider, null);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.IsNotNull(field, name);
            field.SetValue(target, value);
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
