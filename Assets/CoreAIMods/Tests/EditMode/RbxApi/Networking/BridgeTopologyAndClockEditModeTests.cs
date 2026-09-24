using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
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
        public void A4_08_ServerTimeNow_OnAClient_HoldsWhileTheServersClockIsHeld_AndRunsOnAfter()
        {
            // WHY: the server holds its GetServerTimeNow while its wall clock catches up after a
            // backward step, and a client's slew kept moving at half speed through the whole hold —
            // half the step apart from the server by its end (A4-08, A3-04).
            FakeClockSource clock = new()
            {
                UnixTimeSecondsFractional = 1700000000d,
                ProcessTimeSeconds = 100d
            };
            FakeBridge bridge = new(RbxNetworkTopology.Client) { ServerClockOffsetSeconds = 0d };
            LuaCsRbxApiBindings bindings = new(networkBridge: bridge, clockSource: clock);
            double held = bindings.GetServerTimeNow();

            bridge.IsServerClockHeld = true;
            for (int second = 1; second <= 10; second++)
            {
                clock.UnixTimeSecondsFractional += 1d;
                clock.ProcessTimeSeconds += 1d;
                bridge.ServerClockOffsetSeconds = held - clock.UnixTimeSecondsFractional;
                Assert.AreEqual(held, bindings.GetServerTimeNow(),
                    "second " + second + " of the hold: the server's clock stands still, so does the client's");
            }

            bridge.IsServerClockHeld = false;
            clock.UnixTimeSecondsFractional += 1d;
            clock.ProcessTimeSeconds += 1d;
            bridge.ServerClockOffsetSeconds = held + 1d - clock.UnixTimeSecondsFractional;

            Assert.AreEqual(held + 1d, bindings.GetServerTimeNow(),
                "when the hold ends the clock runs on with the server; the held time is not made up");
        }

        [Test]
        public void A4_08_ServerTimeNow_WhereThisProcessIsTheServerClock_IsTheClockItsBridgeSends_HoldIncluded()
        {
            FakeClockSource clock = new()
            {
                UnixTimeSecondsFractional = 1700000000d,
                ProcessTimeSeconds = 5d
            };
            FakeBridge bridge = new(RbxNetworkTopology.Host);
            LuaCsRbxApiBindings bindings = new(networkBridge: bridge, clockSource: clock);
            Assert.IsNotNull(bridge.AttachedServerClock, "a server world hands its bridge its clock");

            double running = bridge.AttachedServerClock(out double heldWhileRunning);
            clock.UnixTimeSecondsFractional = 1699999990d;
            double stepped = bridge.AttachedServerClock(out double heldAfterTheStep);
            double scriptsRead = bindings.GetServerTimeNow();

            Assert.AreEqual(1700000000d, running);
            Assert.AreEqual(0d, heldWhileRunning);
            Assert.AreEqual(1700000000d, stepped,
                "after a backward step the bridge sends the held reading the server's scripts read");
            Assert.AreEqual(10d, heldAfterTheStep, "and how far that is held ahead of the wall clock");
            Assert.AreEqual(stepped, scriptsRead);

            bindings.Dispose();

            Assert.IsNull(bridge.AttachedServerClock, "a disposed world takes its clock back");
        }

        [Test]
        public void A4_08_Negative_AClientWorld_HandsItsBridgeNoServerClock()
        {
            FakeBridge bridge = new(RbxNetworkTopology.Client);
            LuaCsRbxApiBindings bindings = new(networkBridge: bridge,
                clockSource: new FakeClockSource { UnixTimeSecondsFractional = 1700000000d });

            Assert.IsNull(bridge.AttachedServerClock, "a client's world is not the server clock");
            bindings.Dispose();
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
        public void B1_01_AServerWorldOnTheStagedBridge_HandsTheTransportItsClock_OnlyOnceItIsLive()
        {
            // WHY: every world loaded from a package keeps the staged bridge for life, and the bridge
            // left AttachServerClock to the interface default, so the transport never got that
            // world's clock: its anchors carried the raw wall clock with no hold (B1-01, A4-08).
            FakeBridge transport = new(RbxNetworkTopology.Host);
            LuaCsRbxApiBindings live = new(networkBridge: transport,
                clockSource: new FakeClockSource { UnixTimeSecondsFractional = 1700000000d });
            INetworkBridge staged = CreateStagedBridge(transport);
            FakeClockSource incomingClock = new() { UnixTimeSecondsFractional = 1800000000d };
            LuaCsRbxApiBindings incoming = null;
            try
            {
                incoming = new LuaCsRbxApiBindings(networkBridge: staged, clockSource: incomingClock);

                Assert.IsNotNull(transport.AttachedServerClock);
                Assert.AreEqual(1700000000d, transport.AttachedServerClock(out _),
                    "a world that is not live yet leaves the live world's clock on the transport");

                ActivateStagedBridge(staged);

                Assert.IsNotNull(transport.AttachedServerClock);
                Assert.AreEqual(1800000000d, transport.AttachedServerClock(out double heldWhileRunning),
                    "once live, the transport sends the incoming world's server time");
                Assert.AreEqual(0d, heldWhileRunning);
                incomingClock.UnixTimeSecondsFractional = 1799999990d;
                Assert.AreEqual(1800000000d, transport.AttachedServerClock(out double heldAfterTheStep),
                    "after a backward step the transport sends the reading the world's scripts hold");
                Assert.AreEqual(10d, heldAfterTheStep);
                Assert.AreEqual(1800000000d, incoming.GetServerTimeNow());

                live.Dispose();

                Assert.IsNotNull(transport.AttachedServerClock,
                    "the outgoing world's dispose does not take the incoming world's clock back");
                Assert.AreEqual(1800000000d, transport.AttachedServerClock(out _));

                incoming.Dispose();

                Assert.IsNull(transport.AttachedServerClock,
                    "a world disposed on the staged bridge takes its own clock back from the transport");
            }
            finally
            {
                incoming?.Dispose();
                live.Dispose();
                ((IDisposable)staged).Dispose();
            }
        }

        [Test]
        public void B1_01_AClientWorldOnTheStagedBridge_HoldsWhileTheServersClockIsHeld()
        {
            // WHY: the staged bridge answered IsServerClockHeld with the interface default (false), so
            // a client world loaded from a package slewed on at half speed through every hold of the
            // server's clock and ended half the step apart from it (B1-01, A4-08).
            FakeClockSource clock = new()
            {
                UnixTimeSecondsFractional = 1700000000d,
                ProcessTimeSeconds = 100d
            };
            FakeBridge transport = new(RbxNetworkTopology.Client) { ServerClockOffsetSeconds = 0d };
            INetworkBridge staged = CreateStagedBridge(transport);
            LuaCsRbxApiBindings world = null;
            try
            {
                world = new LuaCsRbxApiBindings(networkBridge: staged, clockSource: clock);
                ActivateStagedBridge(staged);
                Assert.IsNull(transport.AttachedServerClock,
                    "a client's world hands the transport no server clock, staged or not");
                double held = world.GetServerTimeNow();

                transport.IsServerClockHeld = true;
                Assert.IsTrue(staged.IsServerClockHeld, "the staged bridge answers the transport's hold");
                for (int second = 1; second <= 10; second++)
                {
                    clock.UnixTimeSecondsFractional += 1d;
                    clock.ProcessTimeSeconds += 1d;
                    transport.ServerClockOffsetSeconds = held - clock.UnixTimeSecondsFractional;
                    Assert.AreEqual(held, world.GetServerTimeNow(),
                        "second " + second + " of the hold: the server's clock stands still, so does the client's");
                }

                transport.IsServerClockHeld = false;
                Assert.IsFalse(staged.IsServerClockHeld);
                clock.UnixTimeSecondsFractional += 1d;
                clock.ProcessTimeSeconds += 1d;
                transport.ServerClockOffsetSeconds = held + 1d - clock.UnixTimeSecondsFractional;

                Assert.AreEqual(held + 1d, world.GetServerTimeNow(),
                    "when the hold ends the clock runs on with the server");
            }
            finally
            {
                world?.Dispose();
                ((IDisposable)staged).Dispose();
            }
        }

        [Test]
        public void B1_01_Negative_AStagedWorldThatNeverGoesLive_LeavesTheLiveWorldsClockAttached()
        {
            // WHY the attach is queued and not forwarded: a load that fails after its world was built
            // must leave the transport sending the live world's time. Forwarded at once, the failed
            // world's clock would have replaced it, and that world's dispose would then have left the
            // live world's anchors with no clock at all.
            FakeBridge transport = new(RbxNetworkTopology.Host);
            LuaCsRbxApiBindings live = new(networkBridge: transport,
                clockSource: new FakeClockSource { UnixTimeSecondsFractional = 1700000000d });
            try
            {
                INetworkBridge rolledBack = CreateStagedBridge(transport);
                LuaCsRbxApiBindings failed = new(networkBridge: rolledBack,
                    clockSource: new FakeClockSource { UnixTimeSecondsFractional = 1800000000d });
                failed.Dispose();
                ((IDisposable)rolledBack).Dispose();

                Assert.IsNotNull(transport.AttachedServerClock);
                Assert.AreEqual(1700000000d, transport.AttachedServerClock(out _),
                    "a staged world that was rolled back never reached the transport");

                INetworkBridge disposedFirst = CreateStagedBridge(transport);
                LuaCsRbxApiBindings orphan = new(networkBridge: disposedFirst,
                    clockSource: new FakeClockSource { UnixTimeSecondsFractional = 1900000000d });
                ((IDisposable)disposedFirst).Dispose();

                Assert.DoesNotThrow(() => orphan.Dispose(),
                    "a world disposed after its staged bridge still takes its clock back without a throw");
                Assert.IsNotNull(transport.AttachedServerClock);
                Assert.AreEqual(1700000000d, transport.AttachedServerClock(out _),
                    "and what it takes back is only its own clock");
            }
            finally
            {
                live.Dispose();
            }
        }

        [Test]
        public void B1_01_EveryBridgeWrapper_ImplementsEveryDefaultBodiedMember()
        {
            // WHY a drift guard: a wrapper that leaves a default-bodied member to the interface answers
            // with the loopback's default instead of the transport underneath it, and nothing fails to
            // compile. The staged bridge lost IsServerClockHeld, AttachServerClock and
            // DetachServerClock that way when they were added to the interface (B1-01).
            List<MethodInfo> defaulted = new();
            foreach (MethodInfo member in typeof(INetworkBridge).GetMethods())
            {
                if (!member.IsAbstract)
                {
                    defaulted.Add(member);
                }
            }

            CollectionAssert.IsNotEmpty(defaulted,
                "the interface has default-bodied members; without them this guard checks nothing");

            List<Type> wrappers = FindShippedBridgeWrappers();
            CollectionAssert.Contains(wrappers, StagedBridgeType(),
                "the staged bridge wraps the live transport and must be found as a wrapper");

            List<string> missing = new();
            foreach (Type wrapper in wrappers)
            {
                foreach (MethodInfo member in defaulted)
                {
                    if (!ImplementsInterfaceMember(wrapper, member))
                    {
                        missing.Add(wrapper.FullName + " -> " + member.Name);
                    }
                }
            }

            CollectionAssert.IsEmpty(missing,
                "every INetworkBridge wrapper forwards every default-bodied member to the bridge it "
                + "wraps: " + string.Join(", ", missing));
        }

        [Test]
        public void Negative_ABridgeWithoutAClockOfItsOwn_IsSynchronizedByDefault()
        {
            // WHY: the loopback and every server are the clock, so the default must never make a solo
            // world wait for a synchronization that cannot come.
            INetworkBridge loopback = new NullNetworkBridge();
            Assert.IsTrue(loopback.IsServerClockSynchronized);
        }

        private static Type StagedBridgeType()
        {
            Type staged = typeof(CoreAI.Mods.WorldPackages.RbxWorldRuntimeSessionController)
                .GetNestedType("StagedNetworkBridge", BindingFlags.NonPublic);
            Assert.IsNotNull(staged, "the staged bridge wraps the live transport during a world swap");
            return staged;
        }

        private static INetworkBridge CreateStagedBridge(INetworkBridge inner)
        {
            return (INetworkBridge)Activator.CreateInstance(StagedBridgeType(),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { inner }, null);
        }

        /// <summary>Runs the two steps a world swap runs on the staged bridge once the world is live.</summary>
        private static void ActivateStagedBridge(INetworkBridge staged)
        {
            MethodInfo prepare = staged.GetType().GetMethod("PrepareActivation",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo activate = staged.GetType().GetMethod("ActivateAfterPublication",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(prepare);
            Assert.IsNotNull(activate);
            prepare.Invoke(staged, null);
            Assert.AreEqual("", activate.Invoke(staged, null), "the queued operations replay cleanly");
        }

        /// <summary>
        /// Every class in a shipped CoreAI assembly that implements <see cref="INetworkBridge"/> around
        /// another one: it takes a bridge in a constructor or keeps one in a field.
        /// </summary>
        /// <remarks>
        /// WHY the assembly of the staged bridge is added by hand: an assembly is only in the domain
        /// once something loaded it, and the portable runner loads them lazily.
        /// </remarks>
        private static List<Type> FindShippedBridgeWrappers()
        {
            HashSet<Assembly> assemblies = new()
            {
                typeof(INetworkBridge).Assembly,
                StagedBridgeType().Assembly
            };
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name ?? "";
                if (!assembly.IsDynamic
                    && name.StartsWith("CoreAI", StringComparison.Ordinal)
                    && name.IndexOf("Test", StringComparison.Ordinal) < 0)
                {
                    assemblies.Add(assembly);
                }
            }

            List<Type> wrappers = new();
            foreach (Assembly assembly in assemblies)
            {
                foreach (Type type in LoadableTypes(assembly))
                {
                    if (type.IsClass && !type.IsAbstract
                                     && typeof(INetworkBridge).IsAssignableFrom(type)
                                     && WrapsABridge(type))
                    {
                        wrappers.Add(type);
                    }
                }
            }

            return wrappers;
        }

        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException partial)
            {
                types = partial.Types;
            }

            List<Type> loaded = new();
            foreach (Type type in types)
            {
                if (type != null)
                {
                    loaded.Add(type);
                }
            }

            return loaded;
        }

        private static bool WrapsABridge(Type type)
        {
            const BindingFlags instanceMembers =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (ConstructorInfo constructor in type.GetConstructors(instanceMembers))
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    if (typeof(INetworkBridge).IsAssignableFrom(parameter.ParameterType))
                    {
                        return true;
                    }
                }
            }

            for (Type declaring = type; declaring != null; declaring = declaring.BaseType)
            {
                foreach (FieldInfo field in declaring.GetFields(instanceMembers | BindingFlags.DeclaredOnly))
                {
                    if (typeof(INetworkBridge).IsAssignableFrom(field.FieldType))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="type"/> or a base class declares its own body for
        /// <paramref name="member"/>, implicitly (a public method of that name and signature) or
        /// explicitly (<c>INetworkBridge.Member</c>).
        /// </summary>
        /// <remarks>
        /// WHY by name and signature and not <see cref="Type.GetInterfaceMap"/>: an interface map
        /// over default interface members is not answered the same way by every runtime Unity ships.
        /// </remarks>
        private static bool ImplementsInterfaceMember(Type type, MethodInfo member)
        {
            ParameterInfo[] expected = member.GetParameters();
            string explicitSuffix = nameof(INetworkBridge) + "." + member.Name;
            for (Type declaring = type; declaring != null; declaring = declaring.BaseType)
            {
                foreach (MethodInfo candidate in declaring.GetMethods(BindingFlags.Instance
                             | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    bool named = (candidate.IsPublic && candidate.Name == member.Name)
                                 || candidate.Name.EndsWith(explicitSuffix, StringComparison.Ordinal);
                    if (!named || candidate.ReturnType != member.ReturnType)
                    {
                        continue;
                    }

                    ParameterInfo[] actual = candidate.GetParameters();
                    bool sameParameters = actual.Length == expected.Length;
                    for (int index = 0; sameParameters && index < actual.Length; index++)
                    {
                        sameParameters = actual[index].ParameterType == expected[index].ParameterType;
                    }

                    if (sameParameters)
                    {
                        return true;
                    }
                }
            }

            return false;
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

            public bool IsServerClockHeld { get; set; }

            /// <summary>The clock a server world handed over, until it took it back.</summary>
            public RbxServerClockReader AttachedServerClock { get; private set; }

            public void AttachServerClock(RbxServerClockReader serverClock)
            {
                AttachedServerClock = serverClock;
            }

            public void DetachServerClock(RbxServerClockReader serverClock)
            {
                if (AttachedServerClock == serverClock)
                {
                    AttachedServerClock = null;
                }
            }

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
    /// <summary>
    /// What a server does with a client's event payload it cannot use: an EnumItem it has no such
    /// item for, an Instance the sender cannot see, a payload that does not decode. Each is dropped
    /// or read as nil, counted, and said at a rate the sender cannot drive.
    /// </summary>
    /// <remarks>
    /// WHY through the bridge's receive event: that is where a transport delivers, inside its own
    /// message handler, and what reaches the handler — a throw, or a log line per packet — is what a
    /// hostile client controls.
    /// </remarks>
    [TestFixture]
    public sealed class RbxNetworkReceiveHardeningEditModeTests
    {
        private const string Sender = "remote-1";

        [Test]
        public void A4_02_AFloodOfUnknownEnumItems_IsSaidAtPowersOfTwo_InShortLines()
        {
            // WHY: every packet naming an EnumItem this world lacks wrote one log line carrying the
            // client's own enum name, 60 KB of it, at the client's packet rate (A4-02).
            ReceivingWorld world = new();
            byte[] payload = Encoding.UTF8.GetBytes("[{\"$rbx\":\"EnumItem\",\"enum\":\""
                                                    + new string('E', 60000) + "\",\"name\":\"x\"}]");

            for (int packet = 0; packet < 1000; packet++)
            {
                world.Raise(Sender, payload);
            }

            Assert.LessOrEqual(world.Log.Count, 11, "a flood of 1000 packets is said about log2(1000) times");
            Assert.Greater(world.Log.Count, 0, "the first one is said at once");
            foreach (string line in world.Log)
            {
                Assert.Less(line.Length, 300, "the sender's name is cut, not copied into the log: " + line);
            }

            StringAssert.Contains("'" + Sender + "'", world.Log[0]);
            Assert.AreEqual(1000, world.Handled, "each event still reaches the handler, the item as nil");
        }

        [Test]
        public void A4_02_EveryUnknownEnumItem_IsCounted_WhetherOrNotItIsSaid()
        {
            ReceivingWorld world = new();
            byte[] payload = Encoding.UTF8.GetBytes("[{\"$rbx\":\"EnumItem\",\"enum\":\"Bogus\",\"name\":\"A\"},"
                                                    + "{\"$rbx\":\"EnumItem\",\"enum\":\"Bogus\",\"name\":\"B\"}]");

            for (int packet = 0; packet < 5; packet++)
            {
                world.Raise(Sender, payload);
            }

            Assert.AreEqual(10L, world.Bindings.NetworkCodec.UnresolvedEnumItems);
            Assert.AreEqual(5L, world.Bindings.NetworkCodec.UnresolvedEnumItemPayloads);
            Assert.AreEqual(3, world.Log.Count, "payloads 1, 2 and 4 are said");
            StringAssert.EndsWith("so far: 4.", world.Log[2]);
        }

        [Test]
        public void A4_12_ASecondSendersFirstHiddenReference_IsSaid_WhileTheFirstSenderFloods()
        {
            // WHY: one counter across every sender decided when a line was due, so a flooding client
            // pushed the next line far out and a second client's first report went unsaid (A4-12).
            ReceivingWorld world = new();
            world.Bindings.ConnectActor(Actor("remote-2"));
            byte[] hidden = Encoding.UTF8.GetBytes("[{\"$rbx\":\"Instance\",\"id\":\"987654321\"}]");

            for (int packet = 0; packet < 4; packet++)
            {
                world.Raise(Sender, hidden);
            }

            int floodLines = world.Log.Count;
            world.Raise("remote-2", hidden);

            Assert.AreEqual(3, floodLines, "the flooder's payloads 1, 2 and 4 are said");
            Assert.AreEqual(4, world.Log.Count, "the second sender's first payload is said too");
            StringAssert.Contains("'remote-2'", world.Log[3]);
        }

        [Test]
        public void A4_12_ThePerSenderCounts_StayBounded()
        {
            ReceivingWorld world = new();
            byte[] hidden = Encoding.UTF8.GetBytes("[{\"$rbx\":\"Instance\",\"id\":\"987654321\"}]");
            for (int sender = 0; sender < LuaCsRbxNetworkCodec.MaxThrottledSenders + 50; sender++)
            {
                world.Bindings.NetworkCodec.DecodeClientArguments(hidden, "sender-" + sender);
            }

            Assert.LessOrEqual(world.Bindings.NetworkCodec.ThrottledSenderCount,
                LuaCsRbxNetworkCodec.MaxThrottledSenders + 1,
                "a stream of new sender ids shares one count past the bound instead of growing the table");
            Assert.AreEqual(LuaCsRbxNetworkCodec.MaxThrottledSenders + 50L,
                world.Bindings.NetworkCodec.HiddenClientReferencePayloads, "and every payload is counted");
        }

        [Test]
        public void A4_12_ASenderThatLeftAndCameBack_IsCountedAfresh()
        {
            ReceivingWorld world = new();
            byte[] hidden = Encoding.UTF8.GetBytes("[{\"$rbx\":\"Instance\",\"id\":\"987654321\"}]");
            for (int packet = 0; packet < 4; packet++)
            {
                world.Raise(Sender, hidden);
            }

            int before = world.Log.Count;
            world.Bindings.DisconnectActor(Actor(Sender));
            world.Bindings.ConnectActor(Actor(Sender));
            world.Log.Clear();
            world.Raise(Sender, hidden);

            Assert.AreEqual(3, before, "payloads 1, 2 and 4 are said");
            Assert.AreEqual(1, world.Log.Count,
                "the returning sender's first payload is said again, however many came before it left");
        }

        [Test]
        public void B1_06_AFloodOfServerPayloadsNamingAnUnknownInstance_IsSaidAtPowersOfTwo_AndAllCounted()
        {
            // WHY: a payload from the server naming an Instance the receiving registry does not hold
            // wrote one log line each, while the enum branch beside it was throttled (A4-02). A Mirror
            // client's registry is not a replica yet (MP-11), so every runtime-created Instance a
            // server remote carries is such a reference, and a 20-60 Hz remote logged every frame.
            ReceivingWorld world = new();
            byte[] payload = Encoding.UTF8.GetBytes("[{\"$rbx\":\"Instance\",\"id\":\"987654321\"},"
                                                    + "{\"$rbx\":\"Instance\",\"id\":\"987654322\"}]");

            object[] first = world.Bindings.NetworkCodec.DecodeArguments(payload);
            for (int packet = 1; packet < 1000; packet++)
            {
                world.Bindings.NetworkCodec.DecodeArguments(payload);
            }

            Assert.AreEqual(2, first.Length);
            Assert.IsNull(first[0], "an unknown Instance still decodes as nil");
            Assert.IsNull(first[1]);
            Assert.AreEqual(10, world.Log.Count, "payloads 1, 2, 4 ... 512 of 1000 are said");
            Assert.AreEqual("Remote payload InstanceId 987654321 is not visible in the receiving registry;"
                            + " decoded as nil (2 such Instance references in this payload).",
                world.Log[0], "the first payload is said at once, in the words it always was");
            Assert.AreEqual(2000L, world.Bindings.NetworkCodec.UnresolvedInstanceReferences,
                "every reference is counted, said or not");
            Assert.AreEqual(1000L, world.Bindings.NetworkCodec.UnresolvedInstanceReferencePayloads);
            Assert.AreEqual(0L, world.Bindings.NetworkCodec.HiddenClientReferencePayloads,
                "the server's payloads are not counted as a client's");
        }

        [Test]
        public void B1_06_Negative_AClientsFirstHiddenReference_IsStillSaid_WhileTheServersPayloadsFlood()
        {
            // WHY: the server's payloads are throttled under a key of their own, so their flood must
            // not push out a client sender's first report (A4-12).
            ReceivingWorld world = new();
            byte[] hidden = Encoding.UTF8.GetBytes("[{\"$rbx\":\"Instance\",\"id\":\"987654321\"}]");
            for (int packet = 0; packet < 5; packet++)
            {
                world.Bindings.NetworkCodec.DecodeArguments(hidden);
            }

            int serverLines = world.Log.Count;
            world.Raise(Sender, hidden);

            Assert.AreEqual(serverLines + 1, world.Log.Count, "the client's first hidden reference is said at once");
            StringAssert.Contains("'" + Sender + "'", world.Log[world.Log.Count - 1]);
            Assert.AreEqual(1L, world.Bindings.NetworkCodec.HiddenClientReferencePayloads);
            Assert.AreEqual(1, world.Handled, "the event still reaches the handler, the Instance as nil");
        }

        [Test]
        public void A4_06_AMalformedClientPayload_IsDroppedAndCounted_NeverThrownIntoTheTransport()
        {
            // WHY: the decode error was rethrown out of the bridge's receive event, which on Mirror is
            // the transport's own message handler: an error in the server log and the client dropped
            // for one bad packet, where the request path answered the same payload with a failure (A4-06).
            ReceivingWorld world = new();
            long rejectedBefore = world.Bindings.RejectedNetworkEventCount;

            Assert.DoesNotThrow(() => world.Raise(Sender, Encoding.UTF8.GetBytes(
                "[{\"$rbx\":\"" + new string('T', 60000) + "\"}]")));
            Assert.DoesNotThrow(() => world.Raise(Sender, Encoding.UTF8.GetBytes("not json")));

            Assert.AreEqual(0, world.Handled, "nothing reaches the handler");
            Assert.AreEqual(2L, world.Bindings.RejectedNetworkEventCount - rejectedBefore);
            Assert.AreEqual(1, world.Log.Count, "said once per sender and window, not once per packet");
            StringAssert.Contains("malformed payload", world.Log[0]);
            StringAssert.Contains("'" + Sender + "'", world.Log[0]);
            Assert.Less(world.Log[0].Length, 600, "a line quoting the payload is cut: " + world.Log[0]);
        }

        [Test]
        public void A4_06_Negative_ASenderTheBridgeNeverAdmitted_IsStillRefusedLoudly()
        {
            ReceivingWorld world = new();

            RbxError error = Assert.Throws<RbxError>(() => world.Raise("never-admitted",
                Encoding.UTF8.GetBytes("[]")));

            Assert.AreEqual(RbxErrorCode.NotAuthority, error.Code,
                "an unadmitted in-process sender is a wiring error its caller hears about, as before");
            Assert.AreEqual(0, world.Handled);
        }

        private static ActorContext Actor(string actorId)
        {
            return new LocalActorIdentityProvider(actorId).GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        /// <summary>
        /// A server world with one RemoteEvent whose OnServerEvent counts its calls, fed by a bridge
        /// the test raises client events on.
        /// </summary>
        private sealed class ReceivingWorld
        {
            private readonly RbxRemoteEvent _remote;

            public ReceivingWorld()
            {
                Registry = new InstanceRegistry();
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bridge = new RaisingBridge();
                Bindings = new LuaCsRbxApiBindings(Registry, game, networkBridge: Bridge, log: Log.Add);
                Bindings.ConnectActor(Actor(Sender));
                _remote = (RbxRemoteEvent)Registry.Create("RemoteEvent");
                _remote.Parent = game.GetService("ReplicatedStorage");
                _remote.AttachScheduler(Bindings.Scheduler);
                _remote.OnServerEvent.Connect((Action<object[]>)(_ => Handled++));
                Log.Clear();
            }

            public InstanceRegistry Registry { get; }

            public RaisingBridge Bridge { get; }

            public LuaCsRbxApiBindings Bindings { get; }

            public List<string> Log { get; } = new();

            public int Handled { get; private set; }

            public void Raise(string sender, byte[] payload)
            {
                Bridge.Raise(new RbxNetworkEventMessage(_remote.Id, RbxNetworkDirection.ClientToServer,
                    RbxNetworkReliability.ReliableOrdered, sender, null, payload));
                Bindings.Scheduler.Advance(0d);
            }
        }

        private sealed class RaisingBridge : INetworkBridge
        {
            private readonly List<string> _actors = new();
            private Action<RbxNetworkEventMessage> _events;

            /// <remarks>
            /// WHY Solo: the receive path is the same on every topology, and Solo needs no identity
            /// source to admit the sender, which is not what these tests are about.
            /// </remarks>
            public RbxNetworkTopology Topology => RbxNetworkTopology.Solo;

            public IReadOnlyList<string> ActorIds => _actors;

            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public event Action<RbxNetworkEventMessage> EventReceived
            {
                add => _events += value;
                remove => _events -= value;
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

            public void Raise(RbxNetworkEventMessage message)
            {
                _events?.Invoke(message);
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
            }

            public void SendRequest(RbxNetworkRequestMessage message,
                Action<RbxNetworkResponse> response)
            {
            }
        }
    }
}
