using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// The bridge's own re-registration contract: Mirror's Shutdown takes its handlers away,
    /// <see cref="MirrorNetworkBridge.AttachHandlers"/> puts them back, doing so on a live Mirror
    /// never doubles them, and a disposed bridge refuses.
    /// </summary>
    [TestFixture]
    public sealed class MirrorBridgeAttachEditModeTests
    {
        private const int Connection = 11;

        private OfflineMirror _mirror;
        private MirrorNetworkBridge _bridge;

        [SetUp]
        public void CreateBridge()
        {
            _mirror = new OfflineMirror();
            _bridge = new MirrorNetworkBridge(isServer: true, clockSeconds: () => 0d);
        }

        [TearDown]
        public void RestoreMirror()
        {
            OfflineMirror.RunAll(
                () => _bridge.Dispose(),
                () => _mirror.Dispose());
        }

        [Test]
        public void Negative_MirrorShutdown_ClearsTheHandlers_AndAttachHandlersRestoresThem()
        {
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;
            StartServerAndBind();
            OfflineMirror.DeliverToServer(Connection, OfflineMirror.Event(1UL));
            Assert.AreEqual(1, delivered.Count);

            OfflineMirror.StopServer();
            StartServerAndBind();
            OfflineMirror.ExpectNoServerHandler();
            OfflineMirror.DeliverToServer(Connection, OfflineMirror.Event(2UL));
            Assert.AreEqual(1, delivered.Count, "this is the defect: Shutdown cleared the handlers");

            _bridge.AttachHandlers();
            OfflineMirror.DeliverToServer(Connection, OfflineMirror.Event(3UL));

            Assert.AreEqual(2, delivered.Count);
            Assert.AreEqual(3UL, delivered[1].RemoteId.Value);
        }

        [Test]
        public void AttachHandlers_OnALiveServer_ReplacesRatherThanDoubles()
        {
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;
            StartServerAndBind();

            _bridge.AttachHandlers();
            _bridge.AttachHandlers();
            OfflineMirror.DeliverToServer(Connection, OfflineMirror.Event(1UL));

            Assert.AreEqual(1, delivered.Count, "one packet, one delivery, however often attached");
            Assert.AreEqual(1, _bridge.PacketsDelivered);
        }

        [Test]
        public void AttachHandlers_OnALiveClient_ReplacesRatherThanDoubles()
        {
            MirrorNetworkBridge client = new(isServer: false, clockSeconds: () => 0d);
            try
            {
                client.BindAdmittedActor("actor-a");
                List<RbxNetworkEventMessage> delivered = new();
                client.EventReceived += delivered.Add;
                OfflineMirror.StartClient();

                client.AttachHandlers();
                client.AttachHandlers();
                OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));

                Assert.AreEqual(1, delivered.Count);
                Assert.AreEqual(1, client.PacketsDelivered);
            }
            finally
            {
                client.Dispose();
            }
        }

        [Test]
        public void Negative_ADisposedBridge_RefusesToAttachAndStaysOffTheTable()
        {
            StartServerAndBind();

            _bridge.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _bridge.AttachHandlers());
            OfflineMirror.ExpectNoServerHandler();
            OfflineMirror.DeliverToServer(Connection, OfflineMirror.Event(1UL));
            Assert.AreEqual(0, _bridge.PacketsDelivered);
        }

        private void StartServerAndBind()
        {
            OfflineMirror.StartServer();
            OfflineMirror.AdmitServerConnection(Connection);
            _bridge.BindConnection(Connection, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
        }
    }
}
