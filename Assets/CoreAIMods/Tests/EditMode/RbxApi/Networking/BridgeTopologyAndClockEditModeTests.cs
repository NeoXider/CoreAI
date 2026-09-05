using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Networking
{
    /// <summary>
    /// MVP11 gate N11.6: which side a process is, and whose clock it tells the time by.
    /// </summary>
    /// <remarks>
    /// WHY both live on the bridge: CoreAI already carried a second "am I the host" answer in the AI
    /// authority layer, and two independently configured answers eventually disagree — at which point
    /// a script gets one story and the command pipeline another. The bridge is the thing actually
    /// connected to the other side, so it is the one that knows.
    /// </remarks>
    [TestFixture]
    public sealed class BridgeTopologyAndClockEditModeTests
    {
        [Test]
        public void Topology_FollowsTheBridge()
        {
            Assert.IsTrue(TopologyFor(RbxNetworkTopology.Solo).IsServer);
            Assert.IsFalse(TopologyFor(RbxNetworkTopology.Solo).IsClient);

            Assert.IsTrue(TopologyFor(RbxNetworkTopology.Host).IsServer);
            Assert.IsTrue(TopologyFor(RbxNetworkTopology.DedicatedServer).IsServer);

            Assert.IsFalse(TopologyFor(RbxNetworkTopology.Client).IsServer);
            Assert.IsTrue(TopologyFor(RbxNetworkTopology.Client).IsClient);
        }

        [Test]
        public void Negative_AHostDoesNotClaimToBeAClient()
        {
            // In Roblox IsClient is true inside a CLIENT execution context, and CoreAI does not yet
            // let a mod declare its context. Answering true on a host would tell server-side Lua it
            // is a client — a wrong answer a script would branch on, which is worse than a
            // conservative one.
            Assert.IsFalse(TopologyFor(RbxNetworkTopology.Host).IsClient);
        }

        [Test]
        public void Negative_StudioIsNeverClaimed()
        {
            // Mods must never branch on Studio: built players are the only target CoreAI ships to.
            foreach (RbxNetworkTopology topology in Enum.GetValues(typeof(RbxNetworkTopology)))
            {
                Assert.IsFalse(TopologyFor(topology).IsStudio, topology.ToString());
                Assert.IsTrue(TopologyFor(topology).IsRunning, topology.ToString());
            }
        }

        [Test]
        public void Negative_ATopologyWithoutABridge_IsRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new RbxBridgeRuntimeTopology(null));
        }

        [Test]
        public void SoloTopology_IsUnchanged()
        {
            // The bridge-derived topology is additive; the solo answer a world gets with no network
            // wiring must be exactly what it was.
            Assert.IsTrue(RbxSoloRuntimeTopology.Shared.IsServer);
            Assert.IsFalse(RbxSoloRuntimeTopology.Shared.IsClient);
            Assert.IsFalse(RbxSoloRuntimeTopology.Shared.IsStudio);
            Assert.IsTrue(RbxSoloRuntimeTopology.Shared.IsRunning);
        }

        private static IRbxRuntimeTopology TopologyFor(RbxNetworkTopology topology)
        {
            return new RbxBridgeRuntimeTopology(new FakeBridge(topology));
        }

        private sealed class FakeBridge : INetworkBridge
        {
            public FakeBridge(RbxNetworkTopology topology)
            {
                Topology = topology;
            }

            public RbxNetworkTopology Topology { get; }

            public IReadOnlyList<string> ActorIds => Array.Empty<string>();

            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public event Action<RbxNetworkEventMessage> EventReceived
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkPeerDisconnected> PeerDisconnected
            {
                add { }
                remove { }
            }

            public void RegisterActor(string actorId)
            {
            }

            public void UnregisterActor(string actorId)
            {
            }

            public void SendEvent(RbxNetworkEventMessage message)
            {
            }

            public void SendRequest(RbxNetworkRequestMessage message,
                Action<RbxNetworkResponse> response)
            {
            }
        }
    }
}
