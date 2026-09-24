using System;
using System.Collections.Generic;
using System.Reflection;
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

        [Test]
        public void ServerTimeNow_BeforeTheFirstSynchronization_IsTheLocalClockUnclamped()
        {
            FakeClockSource clock = new() { UnixTimeSecondsFractional = 1700000000d };
            FakeBridge bridge = new(RbxNetworkTopology.Client) { IsServerClockSynchronized = false };
            LuaCsRbxApiBindings bindings = new(networkBridge: bridge, clockSource: clock);

            Assert.AreEqual(1700000000d, bindings.GetServerTimeNow());
            clock.UnixTimeSecondsFractional = 1699999990d;
            clock.ProcessTimeSeconds = 1d;

            // WHY: before an anchor nothing is known about the server, so no floor is kept either; a
            // floor recorded here is what used to freeze the synchronized clock afterwards.
            Assert.AreEqual(1699999990d, bindings.GetServerTimeNow(),
                "an unsynchronized client reads its own clock, not a clamped copy of it");
        }

        [TestCase(-3600d)]
        [TestCase(3600d)]
        public void ServerTimeNow_TheFirstSynchronization_ReBasesOnce_InEitherDirection(double skewSeconds)
        {
            FakeClockSource clock = new() { UnixTimeSecondsFractional = 1700000000d };
            FakeBridge bridge = new(RbxNetworkTopology.Client) { IsServerClockSynchronized = false };
            LuaCsRbxApiBindings bindings = new(networkBridge: bridge, clockSource: clock);
            bindings.GetServerTimeNow();

            bridge.IsServerClockSynchronized = true;
            bridge.ServerClockOffsetSeconds = skewSeconds;
            clock.UnixTimeSecondsFractional = 1700000001d;
            clock.ProcessTimeSeconds = 1d;
            double synchronized = bindings.GetServerTimeNow();
            clock.UnixTimeSecondsFractional = 1700000002d;
            clock.ProcessTimeSeconds = 2d;
            double oneSecondLater = bindings.GetServerTimeNow();

            Assert.AreEqual(1700000001d + skewSeconds, synchronized,
                "the first synchronized reading is the server's time, even an hour behind the local one");
            Assert.AreEqual(synchronized + 1d, oneSecondLater,
                "after the re-base the clock runs at real time instead of freezing for the skew");
        }

        [Test]
        public void ServerTimeNow_AfterSynchronization_ASmallBackwardCorrectionSlewsMonotonically()
        {
            FakeClockSource clock = new() { UnixTimeSecondsFractional = 1700000000d };
            FakeBridge bridge = new(RbxNetworkTopology.Client) { ServerClockOffsetSeconds = 10d };
            LuaCsRbxApiBindings bindings = new(networkBridge: bridge, clockSource: clock);
            double previous = bindings.GetServerTimeNow();

            bridge.ServerClockOffsetSeconds = 9.5d;
            for (int second = 1; second <= 3; second++)
            {
                clock.UnixTimeSecondsFractional += 1d;
                clock.ProcessTimeSeconds += 1d;
                double next = bindings.GetServerTimeNow();
                Assert.Greater(next, previous, "a correction never runs the clock backwards");
                previous = next;
            }

            Assert.AreEqual(clock.UnixTimeSecondsFractional + 9.5d, previous,
                "a half-second correction is absorbed within a second of real time");
        }

        [Test]
        public void Negative_ServerTimeNow_ALargeBackwardCorrectionAfterSync_DoesNotFreezeForItsSize()
        {
            FakeClockSource clock = new() { UnixTimeSecondsFractional = 1700000000d };
            FakeBridge bridge = new(RbxNetworkTopology.Client) { ServerClockOffsetSeconds = 0d };
            LuaCsRbxApiBindings bindings = new(networkBridge: bridge, clockSource: clock);
            double before = bindings.GetServerTimeNow();

            bridge.ServerClockOffsetSeconds = -3600d;
            clock.UnixTimeSecondsFractional += 10d;
            clock.ProcessTimeSeconds += 10d;
            double tenSecondsLater = bindings.GetServerTimeNow();

            Assert.AreEqual(before + 10d * (1d - LuaCsRbxApiBindings.ServerTimeSlewRate), tenSecondsLater,
                "an hour-sized correction slows the clock down; it no longer stops it for the hour");
        }

        [TestCase(RbxNetworkTopology.Solo)]
        [TestCase(RbxNetworkTopology.Host)]
        [TestCase(RbxNetworkTopology.DedicatedServer)]
        public void ServerTimeNow_WhereThisProcessIsTheServerClock_IsTheLocalClockHeldAtItsLastReading(
            RbxNetworkTopology topology)
        {
            // WHY: the slew's free-running clock advanced by process time on the server too, so a
            // world whose injected Unix clock stood still read a later time on every call.
            FakeClockSource clock = new()
            {
                UnixTimeSecondsFractional = 1700000000.25d,
                ProcessTimeSeconds = 5d
            };
            LuaCsRbxApiBindings bindings = new(networkBridge: new FakeBridge(topology),
                clockSource: clock);

            double first = bindings.GetServerTimeNow();
            clock.ProcessTimeSeconds = 6d;
            double aSecondLater = bindings.GetServerTimeNow();
            clock.UnixTimeSecondsFractional = 1699999990d;
            clock.ProcessTimeSeconds = 7d;
            double afterABackwardStep = bindings.GetServerTimeNow();
            clock.UnixTimeSecondsFractional = 1700000003d;
            clock.ProcessTimeSeconds = 8d;
            double afterAForwardStep = bindings.GetServerTimeNow();

            Assert.AreEqual(1700000000.25d, first, topology.ToString());
            Assert.AreEqual(first, aSecondLater,
                "a clock that stood still reads the same, whatever real time passed");
            Assert.AreEqual(first, afterABackwardStep,
                "a backward step holds the last reading instead of running the clock back");
            Assert.AreEqual(1700000003d, afterAForwardStep, "a forward step is taken at once");
        }

        [Test]
        public void StagedNetworkBridge_QueuesAKickWithItsMessage_AndHandsTheTransportThatMessage()
        {
            // WHY: without its own message overload the staged bridge took the interface default,
            // which drops the text, so every kick in a world loaded from a package showed the client
            // the transport's default notice.
            Type staged = typeof(CoreAI.Mods.WorldPackages.RbxWorldRuntimeSessionController)
                .GetNestedType("StagedNetworkBridge", BindingFlags.NonPublic);
            Assert.IsNotNull(staged, "the staged bridge wraps the live transport during a world swap");
            FakeBridge inner = new(RbxNetworkTopology.Host);
            INetworkBridge wrapper = (INetworkBridge)Activator.CreateInstance(staged,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { inner }, null);
            try
            {
                wrapper.DisconnectActor("actor-a", "banned for griefing");
                CollectionAssert.IsEmpty(inner.KicksWithMessage,
                    "a world that is not live yet must not close a connection the live world serves");
                CollectionAssert.IsEmpty(inner.KicksWithoutMessage);

                MethodInfo activate = staged.GetMethod("ActivateAfterPublication",
                    BindingFlags.Instance | BindingFlags.Public);
                Assert.IsNotNull(activate);
                Assert.AreEqual("", activate.Invoke(wrapper, null));
                wrapper.DisconnectActor("actor-b", null);
                wrapper.DisconnectActor("actor-c");

                CollectionAssert.AreEqual(
                    new[]
                    {
                        new KeyValuePair<string, string>("actor-a", "banned for griefing"),
                        new KeyValuePair<string, string>("actor-b", null)
                    },
                    inner.KicksWithMessage, "each kick reaches the transport with its own message");
                CollectionAssert.AreEqual(new[] { "actor-c" }, inner.KicksWithoutMessage);
            }
            finally
            {
                ((IDisposable)wrapper).Dispose();
            }
        }

        [Test]
        public void StagedNetworkBridge_ForwardsTheInnerBridgesClockSynchronization()
        {
            Type staged = typeof(CoreAI.Mods.WorldPackages.RbxWorldRuntimeSessionController)
                .GetNestedType("StagedNetworkBridge", BindingFlags.NonPublic);
            Assert.IsNotNull(staged, "the staged bridge wraps the live transport during a world swap");
            FakeBridge inner = new(RbxNetworkTopology.Client) { IsServerClockSynchronized = false };
            INetworkBridge wrapper = (INetworkBridge)Activator.CreateInstance(staged,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { inner }, null);
            try
            {
                Assert.IsFalse(wrapper.IsServerClockSynchronized,
                    "a staged world must not read an unsynchronized client as synchronized");
                inner.IsServerClockSynchronized = true;
                Assert.IsTrue(wrapper.IsServerClockSynchronized);
            }
            finally
            {
                ((IDisposable)wrapper).Dispose();
            }
        }

        [Test]
        public void Negative_ABridgeWithoutAClockOfItsOwn_IsSynchronizedByDefault()
        {
            // WHY: the loopback and every server are the clock, so the default must never make a solo
            // world wait for a synchronization that cannot come.
            INetworkBridge loopback = new NullNetworkBridge();
            Assert.IsTrue(loopback.IsServerClockSynchronized);
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

            public bool IsServerClockSynchronized { get; set; } = true;

            /// <summary>Every kick asked for without a message, in order.</summary>
            public List<string> KicksWithoutMessage { get; } = new();

            /// <summary>Every kick asked for through the message overload, with its message, in order.</summary>
            public List<KeyValuePair<string, string>> KicksWithMessage { get; } = new();

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

            public void DisconnectActor(string actorId)
            {
                KicksWithoutMessage.Add(actorId);
            }

            public void DisconnectActor(string actorId, string message)
            {
                KicksWithMessage.Add(new KeyValuePair<string, string>(actorId, message));
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
