using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Networking
{
    /// <summary>
    /// MVP12 gate: the one place a client's request to change the world is judged.
    /// </summary>
    /// <remarks>
    /// WHY every step gets a negative: each check here is one whose omission is invisible until
    /// someone exploits it. A gateway that applied an ungranted intent, or accepted an unrestricted
    /// sender, or let a replay through twice, would look exactly like a working one.
    /// </remarks>
    [TestFixture]
    public sealed class IntentGatewayEditModeTests
    {
        private InstanceRegistry _registry;
        private WriteGrantLedger _ledger;
        private ReplicationDirtySet _dirty;
        private RbxInstance _part;
        private List<string> _applied;
        private List<string> _audit;
        private IntentGateway _gateway;

        [SetUp]
        public void CreateWorld()
        {
            _registry = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "intent-world");
            DataModelBootstrap.CreateGame(_registry);
            _part = _registry.Create("Part");
            _part.Name = "Door";
            _part.Parent = _registry.WorldRoot;
            _ledger = new WriteGrantLedger(_registry, () => 1_000L);
            _dirty = new ReplicationDirtySet(_registry);
            _applied = new List<string>();
            _audit = new List<string>();
            _gateway = new IntentGateway(_registry, _ledger, ApplyIntent, _dirty,
                audit: _audit.Add);
        }

        [Test]
        public void AGrantedIntent_AppliesAndMarksTheInstanceDirty()
        {
            Grant(WriteGrantActions.WriteProperty);

            IntentOutcome outcome = _gateway.Handle("actor-a", false, "intent-world",
                Intent(MutationIntentAction.WriteProperty, "op-1"));

            Assert.IsTrue(outcome.Applied, outcome.Reason);
            Assert.AreEqual(1, _applied.Count);
            Assert.AreEqual(1, _dirty.PendingCount, "an applied change must reach the clients");
            Assert.AreEqual(1, _audit.Count, "every applied intent is auditable");
        }

        [Test]
        public void Negative_AnUngrantedIntent_ChangesNothing()
        {
            IntentOutcome outcome = _gateway.Handle("actor-a", false, "intent-world",
                Intent(MutationIntentAction.WriteProperty, "op-1"));

            Assert.IsFalse(outcome.Applied);
            Assert.AreEqual("NOT_AUTHORITY", outcome.ReasonCode);
            StringAssert.Contains("actor-a", outcome.Reason,
                "a refusal must name the authenticated actor and what was asked");
            StringAssert.Contains("Door", outcome.Reason);
            Assert.IsEmpty(_applied);
            Assert.AreEqual(0, _dirty.PendingCount);
        }

        [Test]
        public void Negative_AGrantForOneActionDoesNotCoverAnother()
        {
            Grant(WriteGrantActions.WriteProperty);

            IntentOutcome outcome = _gateway.Handle("actor-a", false, "intent-world",
                Intent(MutationIntentAction.Destroy, "op-1"));

            Assert.IsFalse(outcome.Applied);
            Assert.AreEqual("NOT_AUTHORITY", outcome.ReasonCode);
            Assert.IsEmpty(_applied);
        }

        [Test]
        public void Negative_AnUnrestrictedSenderIsRefusedRatherThanElevated()
        {
            // The host's writes originate in the server process and never travel as intents. An
            // unrestricted actor arriving here is a bug or a forgery, and refusing means a spoofed
            // host id can only ever produce a refusal.
            Grant(WriteGrantActions.WriteProperty);

            IntentOutcome outcome = _gateway.Handle("host", true, "intent-world",
                Intent(MutationIntentAction.WriteProperty, "op-1"));

            Assert.IsFalse(outcome.Applied);
            Assert.AreEqual("NOT_AUTHORITY", outcome.ReasonCode);
            StringAssert.Contains("intents are for clients", outcome.Reason);
        }

        [Test]
        public void Negative_AnIntentWithNoStampedSenderIsRefused()
        {
            // An empty sender means the bridge did not resolve the connection, i.e. the packet came
            // from someone nobody admitted.
            IntentOutcome outcome = _gateway.Handle("", false, "intent-world",
                Intent(MutationIntentAction.WriteProperty, "op-1"));

            Assert.IsFalse(outcome.Applied);
            Assert.AreEqual("NOT_AUTHORITY", outcome.ReasonCode);
        }

        [Test]
        public void Negative_AnIntentForAnInstanceTheWorldDoesNotHave()
        {
            Grant(WriteGrantActions.WriteProperty, WriteGrantScope.World);

            IntentOutcome outcome = _gateway.Handle("actor-a", false, "intent-world",
                new MutationIntent("op-1", new InstanceId(987_654UL), 0L,
                    MutationIntentAction.WriteProperty, "Name", Array.Empty<byte>()));

            Assert.IsFalse(outcome.Applied);
            Assert.AreEqual("INSTANCE_DESTROYED", outcome.ReasonCode);
            Assert.IsEmpty(_applied);
        }

        [Test]
        public void Negative_AnOversizePayloadIsRefusedBeforeAnyAuthorization()
        {
            IntentGateway small = new(_registry, _ledger, ApplyIntent, _dirty,
                maxPayloadBytes: 8);
            Grant(WriteGrantActions.WriteProperty);

            IntentOutcome outcome = small.Handle("actor-a", false, "intent-world",
                new MutationIntent("op-1", _part.Id, CurrentRevision(),
                    MutationIntentAction.WriteProperty, "Name", new byte[9]));

            Assert.IsFalse(outcome.Applied);
            Assert.AreEqual("PAYLOAD_TOO_LARGE", outcome.ReasonCode);
            Assert.IsEmpty(_applied);
        }

        [Test]
        public void Negative_PastTheIntentBudget_IsRefusedInItsOwnBucket()
        {
            // An intent costs a world mutation and a broadcast — far more than a RemoteEvent fire —
            // so it is budgeted separately rather than sharing the remote allowance.
            RbxNetworkRateLimiter limiter = new(2, () => 0d);
            IntentGateway budgeted = new(_registry, _ledger, ApplyIntent, _dirty,
                new RbxNetworkRateLimiterAdapter(limiter, RbxNetworkRateGroup.RemoteFunction));
            Grant(WriteGrantActions.WriteProperty, WriteGrantScope.World);

            budgeted.Handle("actor-a", false, "intent-world",
                Intent(MutationIntentAction.WriteProperty, "op-1"));
            budgeted.Handle("actor-a", false, "intent-world",
                Intent(MutationIntentAction.WriteProperty, "op-2"));
            IntentOutcome third = budgeted.Handle("actor-a", false, "intent-world",
                Intent(MutationIntentAction.WriteProperty, "op-3"));

            Assert.IsFalse(third.Applied);
            Assert.AreEqual("BUDGET_EXCEEDED", third.ReasonCode);
            Assert.AreEqual(2, _applied.Count);
        }

        [Test]
        public void AReplayedIntent_IsAppliedOnce()
        {
            // The same operation id twice is one change. Without this a dropped acknowledgement and
            // a client retry would spend a coin, or destroy a part, twice.
            Grant(WriteGrantActions.WriteProperty);
            MutationIntent intent = Intent(MutationIntentAction.WriteProperty, "op-same");

            IntentOutcome first = _gateway.Handle("actor-a", false, "intent-world", intent);
            IntentOutcome second = _gateway.Handle("actor-a", false, "intent-world", intent);

            Assert.IsTrue(first.Applied);
            Assert.IsTrue(second.Applied, "a replay reports the original result, it does not fail");
            Assert.AreEqual(1, _applied.Count, "and it does not apply the change a second time");
        }

        [Test]
        public void Negative_AStaleRevisionIsRefused()
        {
            // A client that acted on a view the server has since moved past must not overwrite the
            // change it never saw.
            Grant(WriteGrantActions.WriteProperty, WriteGrantScope.World);
            _gateway.Handle("actor-a", false, "intent-world",
                Intent(MutationIntentAction.WriteProperty, "op-1"));
            long current = _registry.AdvanceRevision(_part.Id);

            IntentOutcome stale = _gateway.Handle("actor-a", false, "intent-world",
                new MutationIntent("op-2", _part.Id, current - 1L,
                    MutationIntentAction.WriteProperty, "Name", Array.Empty<byte>()));

            Assert.IsFalse(stale.Applied);
            Assert.AreEqual(1, _applied.Count, "the stale intent must not have reached the applier");
        }

        [Test]
        public void Negative_AnEmptyIntentIsRefused()
        {
            IntentOutcome outcome = _gateway.Handle("actor-a", false, "intent-world", null);

            Assert.IsFalse(outcome.Applied);
            Assert.AreEqual("BAD_ARGUMENT", outcome.ReasonCode);
        }

        [Test]
        public void Negative_AGatewayWithoutItsPartsIsRefusedAtConstruction()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new IntentGateway(null, _ledger, ApplyIntent));
            Assert.Throws<ArgumentNullException>(() =>
                new IntentGateway(_registry, null, ApplyIntent));
            Assert.Throws<ArgumentNullException>(() =>
                new IntentGateway(_registry, _ledger, null));
        }

        private long ApplyIntent(string actorId, MutationIntent intent)
        {
            _applied.Add(actorId + ":" + intent.Action + ":" + intent.OperationId);
            return _registry.AdvanceRevision(intent.TargetInstanceId);
        }

        private void Grant(WriteGrantActions actions,
            WriteGrantScope scope = WriteGrantScope.Instance)
        {
            _ledger.Issue("host", true, "actor-a", scope,
                scope == WriteGrantScope.World ? default : _part.Id, actions);
        }

        private MutationIntent Intent(MutationIntentAction action, string operationId)
        {
            // WHY the live revision: a fresh part already carries a revision from its own creation
            // and parenting, so an intent hard-coded to zero would be stale before it was sent —
            // and the test would be asserting the staleness check, not the thing it names.
            return new MutationIntent(operationId, _part.Id, CurrentRevision(), action, "Name",
                Array.Empty<byte>());
        }

        private long CurrentRevision()
        {
            return _registry.TryGetRecord(_part.Id, out InstanceRecord record) ? record.Revision : 0L;
        }
    }
}
