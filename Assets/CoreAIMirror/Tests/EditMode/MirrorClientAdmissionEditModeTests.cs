using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// How a client learns who it is: the admission response names the admitted actor on
    /// acceptance and nothing on refusal, the provider binds that actor on the bridge from the
    /// authenticator's accept event, and forgets it on the frame the client side stops.
    /// </summary>
    [TestFixture]
    public sealed class MirrorClientAdmissionEditModeTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private OfflineMirror _mirror;
        private GameObject _go;
        private CoreAiMirrorAuthenticator _authenticator;
        private CoreAiMirrorNetworkBridgeProvider _provider;

        [SetUp]
        public void CreateClientProvider()
        {
            _mirror = new OfflineMirror();
            _go = new GameObject("CoreAI_ClientAdmissionTest");
            _authenticator = _go.AddComponent<CoreAiMirrorAuthenticator>();
            _provider = _go.AddComponent<CoreAiMirrorNetworkBridgeProvider>();
            _provider.Role = CoreAiMirrorRole.Client;
            _provider.ClockSeconds = () => 0d;
            SetField(_provider, "authenticator", _authenticator);
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
        public void Respond_OnAcceptance_TellsTheClientTheActorItNowIs()
        {
            ActorContext context = new LocalActorIdentityProvider(
                    "remote-7", "session-7", "world-a", ActorGrantSet.Create(new[] { "read" }),
                    AgentMemoryScope.Empty)
                .GetActorContext(BuiltInAgentRoleIds.SmartChat);

            CoreAiAdmissionResponseMessage told = CoreAiMirrorAuthenticator.Respond(
                ActorAdmissionResult.Admit(context, 1007L, "player7", ""));

            Assert.IsTrue(told.Admitted);
            Assert.AreEqual("", told.Reason);
            Assert.AreEqual("remote-7", told.ActorId);
        }

        [Test]
        public void Negative_Respond_OnRefusal_TellsTheClientNothingButTheRefusal()
        {
            CoreAiAdmissionResponseMessage told = CoreAiMirrorAuthenticator.Respond(
                ActorAdmissionResult.Reject("signature mismatch in segment 2"));
            CoreAiAdmissionResponseMessage none = CoreAiMirrorAuthenticator.Respond(null);

            Assert.IsFalse(told.Admitted);
            Assert.AreEqual("not admitted", told.Reason, "the provider's reason stays in the host's log");
            Assert.AreEqual("", told.ActorId, "an identity is the first thing a refusal must not leak");
            Assert.IsFalse(none.Admitted, "no decision is a refusal, not an admission of nobody");
            Assert.AreEqual("", none.ActorId);
        }

        [Test]
        public void Provider_BindsTheAdmittedActor_FromTheAuthenticatorsAcceptEvent()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            List<RbxNetworkEventMessage> delivered = new();
            bridge.EventReceived += delivered.Add;
            StartClientAndFrame();

            OfflineMirror.DeliverToClient(Accepted("remote-1"));

            Assert.AreEqual("remote-1", _authenticator.ClientActorId);
            Assert.AreEqual("remote-1", bridge.AdmittedActorId);
            OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));
            Assert.AreEqual(1, delivered.Count);
            Assert.AreEqual("remote-1", delivered[0].RecipientActorId);
        }

        [Test]
        public void Provider_ForgetsTheAdmittedActor_OnTheFrameTheClientSideStops()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            StartClientAndFrame();
            OfflineMirror.DeliverToClient(Accepted("remote-1"));
            Assert.AreEqual("remote-1", bridge.AdmittedActorId, "admission must have bound before a stop is tested");

            _authenticator.OnStopClient();
            OfflineMirror.StopClient();
            Frame();

            Assert.IsNull(_authenticator.ClientActorId);
            Assert.IsNull(bridge.AdmittedActorId, "a reconnect must start unadmitted");
            StartClientAndFrame();
            LogAssert.Expect(LogType.Warning, new Regex("before this client's admission"));
            OfflineMirror.DeliverToClient(OfflineMirror.Event(2UL));
            Assert.AreEqual(1, bridge.UnadmittedPacketsDropped,
                "until the server says who this client is again, its remotes reach nothing");
        }

        [Test]
        public void Negative_ARefusal_BindsNothingAndLeavesTheClientUnauthenticated()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            StartClientAndFrame();

            OfflineMirror.DeliverToClient(new CoreAiAdmissionResponseMessage
            {
                Admitted = false,
                Reason = "not admitted",
                ActorId = ""
            });

            Assert.IsNull(_authenticator.ClientActorId);
            Assert.IsNull(bridge.AdmittedActorId);
            Assert.IsFalse(NetworkClient.connection.isAuthenticated);
        }

        [Test]
        public void Negative_AnAcceptanceThatNamesNobody_IsSaidLoudlyAndBindsNothing()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            StartClientAndFrame();
            LogAssert.Expect(LogType.Error, new Regex("without naming its actor"));

            OfflineMirror.DeliverToClient(Accepted(""));

            Assert.IsNull(bridge.AdmittedActorId,
                "a server that admits without naming the actor must not leave a half-bound client. NOTE this is NOT the mixed-version case: a genuinely older server sends a two-field message, which a new client cannot even deserialize - it reads past the end and Mirror disconnects it. Adding ActorId to CoreAiAdmissionResponseMessage is a BREAKING wire change; both sides must run the same version.");
            Assert.AreEqual("", _authenticator.ClientActorId,
                "the authenticator records what it was told; the provider is what refuses to bind it");
            LogAssert.Expect(LogType.Warning, new Regex("before this client's admission"));
            OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));
            Assert.AreEqual(1, bridge.UnadmittedPacketsDropped);
        }

        [Test]
        public void Provider_FailsTheClientsOpenRequests_OnTheFrameTheClientSideStops()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            StartClientAndFrame();
            OfflineMirror.DeliverToClient(Accepted("remote-1"));
            Transport.active.OnClientConnected?.Invoke();
            List<RbxNetworkResponse> completed = new();
            bridge.SendRequest(new RbxNetworkRequestMessage(new InstanceId(6UL),
                RbxNetworkDirection.ClientToServer, "remote-1", null, new byte[] { 1 }), completed.Add);
            Assert.IsEmpty(completed, "connected, the InvokeServer is on its way");

            _authenticator.OnStopClient();
            OfflineMirror.StopClient();
            Frame();

            Assert.AreEqual(1, completed.Count,
                "the connection that carried the call is gone; the script hears so on the next frame, "
                + "not after the thirty-second timeout");
            Assert.IsFalse(completed[0].Succeeded);
            StringAssert.Contains("not connected", completed[0].Error);
        }

        private void StartClientAndFrame()
        {
            OfflineMirror.StartClient();
            _authenticator.OnStartClient();
            Frame();
        }

        private static CoreAiAdmissionResponseMessage Accepted(string actorId)
        {
            return new CoreAiAdmissionResponseMessage
            {
                Admitted = true,
                Reason = "",
                ActorId = actorId
            };
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
    }
}
