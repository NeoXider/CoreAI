using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Architecture guards for the general deferred RBXScriptSignal surface.</summary>
    [TestFixture]
    public sealed class RbxScriptSignalEditModeTests
    {
        private sealed class NoThreadFactory : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("these tests connect plain C# handlers only");
            }
        }

        [Test]
        public void GeneralSignal_HasNoSupportsDispatchSplit()
        {
            Assert.IsNull(typeof(RbxScriptSignal).GetProperty("SupportsDispatch"));
            Assert.IsNull(typeof(RbxScriptSignal).GetConstructor(
                new System.Type[] { typeof(string), typeof(bool) }));
        }

        [Test]
        public void DirectConnectWithoutScheduler_FailsLoudlyWithoutMvpStub()
        {
            RbxScriptSignal signal = new("Test.Signal");
            RbxError error = Assert.Throws<RbxError>(() =>
                signal.Connect((System.Action<object[]>)(_ => { })));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("no scheduler", error.RawMessage);
        }

        [Test]
        public void Track_StampsTheOwningModOnTheConnection_OneOffConnectionsStayOwnerless()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxScriptSignal signal = BoundSignal(scheduler, "Test.Owned");
            ModConnectionRegistry registry = new();
            RbxScriptConnection owned = signal.Connect((Action<object[]>)(_ => { }));
            RbxScriptConnection oneOff = signal.Connect((Action<object[]>)(_ => { }));

            registry.Track("mod-a", registry.BeginGeneration("mod-a"), owned);
            registry.Track(null, 0, oneOff);

            Assert.AreEqual("mod-a", owned.OwnerModId,
                "the scheduler attributes handler faults and signal overload to this owner");
            Assert.IsNull(oneOff.OwnerModId, "the ownerless one-off surface records no owner");
        }

        [Test]
        public void M1_36_Track_ManyLiveConnections_PruneWorkIsAmortizedLinear()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxScriptSignal signal = BoundSignal(scheduler, "Test.Many");
            ModConnectionRegistry registry = new();
            const int count = 10000;
            for (int index = 0; index < count; index++)
            {
                registry.Track("mod-a", 1, signal.Connect((Action<object[]>)(_ => { })));
            }

            // WHY a work counter: the old Track pruned the whole ledger on every connect, so a mod
            // holding n live connections paid n(n-1)/2 entry visits (about 50 million here).
            Assert.LessOrEqual(registry.PruneVisitCount, 4L * count);
            Assert.AreEqual(count, registry.GetOwnedBy("mod-a").Count);
        }

        [Test]
        public void M1_36_Track_ConnectDisconnectChurn_KeepsTheLedgerBounded()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxScriptSignal signal = BoundSignal(scheduler, "Test.Churn");
            ModConnectionRegistry registry = new();
            for (int index = 0; index < 10000; index++)
            {
                RbxScriptConnection connection = signal.Connect((Action<object[]>)(_ => { }));
                registry.Track("mod-a", 1, connection);
                connection.Disconnect();
            }

            Assert.LessOrEqual(registry.TrackedEntryCount("mod-a"), 32,
                "dead entries are still pruned, just not on every single connect");
            Assert.IsFalse(signal.HasConnections);
            Assert.AreEqual(0, registry.DisconnectOwnedBy("mod-a"));
        }

        [Test]
        public void M2_26_DisconnectInConnectOrder_DoesAmortizedLinearWorkAndKeepsDispatchOrder()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxScriptSignal signal = BoundSignal(scheduler, "Test.Ordered");
            const int count = 10000;
            List<int> delivered = new();
            List<RbxScriptConnection> connections = new();
            for (int index = 0; index < count; index++)
            {
                int captured = index;
                connections.Add(signal.Connect((Action<object[]>)(_ => delivered.Add(captured))));
            }

            for (int index = 0; index < count; index += 2)
            {
                connections[index].Disconnect();
            }

            // WHY a work counter: List.Remove from the front shifts every later slot, so disconnecting
            // in connect order cost O(n) per call and O(n^2) in total on the old signal.
            Assert.LessOrEqual(signal.CompactionMoveCount, 2L * count);
            Assert.LessOrEqual(signal.ConnectionSlotCount, count);
            signal.Fire();
            scheduler.Advance(0d);

            Assert.AreEqual(count / 2, delivered.Count);
            for (int index = 0; index < delivered.Count; index++)
            {
                Assert.AreEqual((index * 2) + 1, delivered[index],
                    "surviving connections still fire in connect order after compaction");
            }
        }

        [Test]
        public void M2_26_DisconnectAll_IsLinearAndLeavesNoSlots()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxScriptSignal signal = BoundSignal(scheduler, "Test.Destroyed");
            List<RbxScriptConnection> connections = new();
            for (int index = 0; index < 10000; index++)
            {
                connections.Add(signal.Connect((Action<object[]>)(_ => { })));
            }

            signal.DisconnectAll();

            Assert.IsFalse(signal.HasConnections);
            Assert.AreEqual(0, signal.ConnectionSlotCount);
            Assert.AreEqual(0, signal.CompactionMoveCount, "a destroy-time teardown moves nothing");
            Assert.IsTrue(connections.TrueForAll(connection => !connection.Connected));
        }

        [Test]
        public void M2_26_ConnectAfterDisconnects_DoesNotReceiveAFireQueuedBeforeIt()
        {
            ModScheduler scheduler = CreateScheduler();
            RbxScriptSignal signal = BoundSignal(scheduler, "Test.Late");
            List<string> delivered = new();
            RbxScriptConnection early = signal.Connect((Action<object[]>)(_ => delivered.Add("early")));
            signal.Connect((Action<object[]>)(_ => delivered.Add("kept")));
            early.Disconnect();

            signal.Fire();
            signal.Connect((Action<object[]>)(_ => delivered.Add("late")));
            scheduler.Advance(0d);

            CollectionAssert.AreEqual(new[] { "kept" }, delivered,
                "R5.5: a connection made after the fire never receives it");
        }

        private static ModScheduler CreateScheduler()
        {
            return new ModScheduler(new NoThreadFactory(), new RbxAccumulatingTimeSource());
        }

        private static RbxScriptSignal BoundSignal(ModScheduler scheduler, string name)
        {
            RbxScriptSignal signal = new(name);
            signal.BindScheduler(scheduler);
            return signal;
        }
    }
}
