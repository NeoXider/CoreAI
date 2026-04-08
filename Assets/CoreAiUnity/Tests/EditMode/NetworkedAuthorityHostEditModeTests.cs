using System;
using CoreAI.Authority;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для NetworkedAuthorityHost — проверка всех политик выполнения.
    /// </summary>
    [TestFixture]
    public sealed class NetworkedAuthorityHostEditModeTests
    {
        #region AllPeers Policy

        [Test]
        public void AllPeers_HostPeer_CanRunAiTasks()
        {
            TestPeer peer = new(true, false);
            NetworkedAuthorityHost host = new(peer, AiNetworkExecutionPolicy.AllPeers);

            Assert.IsTrue(host.CanRunAiTasks);
        }

        [Test]
        public void AllPeers_ClientPeer_CanRunAiTasks()
        {
            TestPeer peer = new(false, true);
            NetworkedAuthorityHost host = new(peer, AiNetworkExecutionPolicy.AllPeers);

            Assert.IsTrue(host.CanRunAiTasks);
        }

        #endregion

        #region HostOnly Policy

        [Test]
        public void HostOnly_HostPeer_CanRunAiTasks()
        {
            TestPeer peer = new(true, false);
            NetworkedAuthorityHost host = new(peer, AiNetworkExecutionPolicy.HostOnly);

            Assert.IsTrue(host.CanRunAiTasks);
        }

        [Test]
        public void HostOnly_ClientPeer_CannotRunAiTasks()
        {
            TestPeer peer = new(false, true);
            NetworkedAuthorityHost host = new(peer, AiNetworkExecutionPolicy.HostOnly);

            Assert.IsFalse(host.CanRunAiTasks);
        }

        #endregion

        #region ClientPeersOnly Policy

        [Test]
        public void ClientPeersOnly_ClientPeer_CanRunAiTasks()
        {
            TestPeer peer = new(false, true);
            NetworkedAuthorityHost host = new(peer, AiNetworkExecutionPolicy.ClientPeersOnly);

            Assert.IsTrue(host.CanRunAiTasks);
        }

        [Test]
        public void ClientPeersOnly_HostPeer_CannotRunAiTasks()
        {
            TestPeer peer = new(true, false);
            NetworkedAuthorityHost host = new(peer, AiNetworkExecutionPolicy.ClientPeersOnly);

            Assert.IsFalse(host.CanRunAiTasks);
        }

        #endregion

        #region Constructor Validation

        [Test]
        public void Constructor_NullPeer_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new NetworkedAuthorityHost(null, AiNetworkExecutionPolicy.AllPeers));
        }

        #endregion

        #region Test Helpers

        private sealed class TestPeer : IAiNetworkPeer
        {
            public bool IsHostAuthority { get; }
            public bool IsPureClient { get; }

            public TestPeer(bool isHost, bool isPureClient)
            {
                IsHostAuthority = isHost;
                IsPureClient = isPureClient;
            }
        }

        #endregion
    }
}