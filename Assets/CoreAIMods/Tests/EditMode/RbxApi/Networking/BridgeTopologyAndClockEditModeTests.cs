using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
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

        [Test]
        public void ServerTimeNow_ReadsTheClientClockThroughTheBridgeOffset()
        {
            // A client's own clock says 1700000000; the bridge says the server is 42.5 s ahead.
            LuaCsModStack stack = StackWith(
                localClockSeconds: 1700000000d,
                topology: RbxNetworkTopology.Client,
                offsetSeconds: 42.5d);
            stack.Runtime.LoadMod("m",
                "assert(workspace:GetServerTimeNow() == 1700000042.5, " +
                "'server time must carry the bridge offset, got ' .. " +
                "tostring(workspace:GetServerTimeNow()))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Negative_AClientWhoseWallClockIsAnHourFast_StillReportsServerTime()
        {
            // The whole point of the offset: two clients disagreeing about the wall clock by an
            // hour must still stamp the same moment. Without the offset the second one would read
            // 1700003600 and every timestamp it sent would be an hour into the future.
            LuaCsModStack onTime = StackWith(1700000000d, RbxNetworkTopology.Client, 0d);
            onTime.Runtime.LoadMod("m", "assert(workspace:GetServerTimeNow() == 1700000000)");
            Assert.IsTrue(onTime.Runtime.IsLoaded("m"));

            LuaCsModStack anHourFast = StackWith(1700003600d, RbxNetworkTopology.Client, -3600d);
            anHourFast.Runtime.LoadMod("m",
                "assert(workspace:GetServerTimeNow() == 1700000000, " +
                "'a skewed client must still read server time, got ' .. " +
                "tostring(workspace:GetServerTimeNow()))");
            Assert.IsTrue(anHourFast.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Negative_AServerAddsNoOffsetToItsOwnClock()
        {
            // The server IS the clock. Solo and host must read exactly the injected source, so the
            // offline behaviour stays byte-identical to the pre-transport one.
            foreach (RbxNetworkTopology topology in
                     new[] { RbxNetworkTopology.Solo, RbxNetworkTopology.Host })
            {
                LuaCsModStack stack = StackWith(1700000000d, topology, 0d);
                stack.Runtime.LoadMod("m",
                    "assert(workspace:GetServerTimeNow() == 1700000000, '" + topology + "')");
                Assert.IsTrue(stack.Runtime.IsLoaded("m"), topology.ToString());
            }
        }

        [Test]
        public void Negative_AnOffsetThatJumpsBackwards_DoesNotRewindServerTime()
        {
            // A resynchronising transport can revise its offset downwards mid-session. Lua must
            // never see time run backwards, or every duration a mod measured turns negative.
            FakeClockSource clock = new() { UnixTimeSecondsFractional = 1700000000d };
            FakeBridge bridge = new(RbxNetworkTopology.Client) { ServerClockOffsetSeconds = 10d };
            LuaCsModStack stack = StackWith(clock, bridge);
            stack.Runtime.LoadMod("m", "assert(workspace:GetServerTimeNow() == 1700000010)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));

            bridge.ServerClockOffsetSeconds = -50d;
            stack.Runtime.LoadMod("m2",
                "assert(workspace:GetServerTimeNow() == 1700000010, " +
                "'a revised offset must clamp, never rewind, got ' .. " +
                "tostring(workspace:GetServerTimeNow()))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m2"));
        }

        private static LuaCsModStack StackWith(double localClockSeconds,
            RbxNetworkTopology topology, double offsetSeconds)
        {
            return StackWith(
                new FakeClockSource { UnixTimeSecondsFractional = localClockSeconds },
                new FakeBridge(topology) { ServerClockOffsetSeconds = offsetSeconds });
        }

        private static LuaCsModStack StackWith(FakeClockSource clock, FakeBridge bridge)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentGameLogger(),
                ModStore = new MemoryModStore(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = new LuaCsRbxApiBindings(networkBridge: bridge, clockSource: clock)
            });
        }

        private sealed class FakeClockSource : IRbxClockSource
        {
            public double GameTimeSeconds { get; set; }

            public long UnixTimeSeconds { get; set; }

            public double ProcessTimeSeconds { get; set; }

            public double UnixTimeSecondsFractional { get; set; }
        }

        private sealed class MemoryModStore : ILuaModStore
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

            public double ServerClockOffsetSeconds { get; set; }

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
