using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// Replication phase 0: the registry knows which side of the boundary it is on, tells the world
    /// which member changed, and does so with its mutation gate released.
    /// </summary>
    /// <remarks>
    /// WHY the gate is probed from another thread rather than asserted on a flag: a subscriber that
    /// ran under the lock would still see every flag the registry could set; only a second thread
    /// trying to take the gate can tell whether it is actually free.
    /// </remarks>
    [TestFixture]
    public sealed class InstanceRegistryAuthorityEditModeTests
    {
        private InstanceRegistry _server;
        private RbxInstance _part;

        [SetUp]
        public void CreateWorld()
        {
            _server = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "authority-world");
            DataModelBootstrap.CreateGame(_server);
            _part = _server.Create("Part");
            _part.Name = "Door";
            _part.Parent = _server.WorldRoot;
        }

        [Test]
        public void ARegistryIsAuthoritativeByDefault()
        {
            Assert.AreEqual(RegistryAuthority.Authoritative, _server.Authority);
            Assert.IsFalse(_server.IsApplyingReplication);
            Assert.IsTrue(_server.Create("Folder").Id.IsServerAssigned);
        }

        [Test]
        public void RevisionAdvanced_NamesTheMemberTheSetterChanged()
        {
            List<string> seen = new();
            _server.RevisionAdvanced += (id, revision, member) =>
                seen.Add(id.Value + ":" + member + "@" + revision);
            RbxInstance folder = _server.Create("Folder");
            folder.Parent = _server.WorldRoot;
            seen.Clear();

            _part.Name = "Gate";
            _part.Archivable = false;
            _part.SetAttribute("Hp", 3d);
            _part.AddTag("Enemy");
            _part.Parent = folder;

            _server.TryGetRecord(_part.Id, out InstanceRecord record);
            CollectionAssert.AreEqual(new[]
            {
                _part.Id.Value + ":Name@" + (record.Revision - 4),
                _part.Id.Value + ":Archivable@" + (record.Revision - 3),
                _part.Id.Value + ":Attribute:Hp@" + (record.Revision - 2),
                _part.Id.Value + ":Tag:Enemy@" + (record.Revision - 1),
                _part.Id.Value + ":Parent@" + record.Revision,
                _server.WorldRoot.Id.Value + ":Children@" + RevisionOf(_server.WorldRoot),
                folder.Id.Value + ":Children@" + RevisionOf(folder)
            }, seen);
        }

        [Test]
        public void RevisionAdvanced_IsRaisedWithTheMutationGateReleased()
        {
            bool? gateWasFree = null;
            _server.RevisionAdvanced += (id, revision, member) => gateWasFree = GateIsFreeFromAnotherThread();

            _part.Name = "Gate";

            Assert.IsTrue(gateWasFree, "the subscriber ran while the raising thread still held the gate");
        }

        [Test]
        public void RevisionAdvanced_UnderApplyMutation_IsRaisedAfterTheOperationAndOutsideTheGate()
        {
            bool operationCompleted = false;
            List<bool> completedWhenRaised = new();
            List<bool> gateFreeWhenRaised = new();
            _server.RevisionAdvanced += (id, revision, member) =>
            {
                completedWhenRaised.Add(operationCompleted);
                gateFreeWhenRaised.Add(GateIsFreeFromAnotherThread());
            };
            _server.TryGetRecord(_part.Id, out InstanceRecord record);
            MutationEnvelope envelope = new("actor-a", _part.Id, "op-1", record.Revision);

            _server.ApplyMutation(envelope, () =>
            {
                _part.Name = "Gate";
                _part.Archivable = false;
                operationCompleted = true;
                return 0;
            });

            Assert.AreEqual(2, completedWhenRaised.Count, "both writes must be published");
            CollectionAssert.AreEqual(new[] { true, true }, completedWhenRaised,
                "events under ApplyMutation are parked until the operation has committed");
            CollectionAssert.AreEqual(new[] { true, true }, gateFreeWhenRaised,
                "the parked events go out only after the gate is released");
        }

        [Test]
        public void Negative_ASubscriberThatThrows_DoesNotBreakTheMutation_AndTheEventsBehindItStillArrive()
        {
            List<string> diagnostics = new();
            _server.Diagnostics = diagnostics.Add;
            List<string> seenByTheThrower = new();
            List<string> seenByTheNext = new();
            _server.RevisionAdvanced += (id, revision, member) =>
            {
                seenByTheThrower.Add(member);
                if (seenByTheThrower.Count == 1)
                {
                    throw new InvalidOperationException("host hook bug");
                }
            };
            _server.RevisionAdvanced += (id, revision, member) => seenByTheNext.Add(member);
            _server.TryGetRecord(_part.Id, out InstanceRecord record);
            MutationEnvelope envelope = new("actor-a", _part.Id, "op-1", record.Revision);

            int result = 0;
            Assert.DoesNotThrow(() => result = _server.ApplyMutation(envelope, () =>
            {
                _part.Name = "Gate";
                _part.Archivable = false;
                _part.SetAttribute("Hp", 3d);
                return 42;
            }));

            Assert.AreEqual(42, result, "the operation's own result survives its subscriber's bug");
            Assert.AreEqual("Gate", _part.Name);
            string[] expected = { ReplicationMembers.Name, ReplicationMembers.Archivable, ReplicationMembers.Attribute("Hp") };
            CollectionAssert.AreEqual(expected, seenByTheThrower, "the events parked behind the throw still arrive");
            CollectionAssert.AreEqual(expected, seenByTheNext,
                "the next subscriber still receives the very event the first one threw on");
            Assert.AreEqual(1, diagnostics.Count, "the fault is reported once, to the registry's diagnostics");
            StringAssert.Contains("host hook bug", diagnostics[0]);
            StringAssert.Contains(nameof(InstanceRegistry.RevisionAdvanced), diagnostics[0]);
        }

        [Test]
        public void Negative_ASubscriberThatThrows_LeavesThePlainSetterWhole()
        {
            List<string> diagnostics = new();
            _server.Diagnostics = diagnostics.Add;
            _server.RevisionAdvanced += (id, revision, member) => throw new InvalidOperationException("host hook bug");

            Assert.DoesNotThrow(() => _part.Name = "Gate");

            Assert.AreEqual("Gate", _part.Name);
            Assert.AreEqual(1, diagnostics.Count);
        }

        [Test]
        public void Negative_WhenTheSubscriberAndTheDiagnosticsSinkBothThrow_TheMutationCompletes_AndTheEventsBehindItStillArrive()
        {
            // WHY both throw: a throwing subscriber is contained by reporting through Diagnostics, and a
            // host that installs a broken hook may well install a broken logger beside it; the report
            // must not become the escape it was added to close.
            int sinkCalls = 0;
            _server.Diagnostics = message =>
            {
                sinkCalls++;
                throw new InvalidOperationException("the host's logger is broken too");
            };
            List<string> seenByTheThrower = new();
            List<string> seenByTheNext = new();
            _server.RevisionAdvanced += (id, revision, member) =>
            {
                seenByTheThrower.Add(member);
                if (seenByTheThrower.Count == 1)
                {
                    throw new InvalidOperationException("host hook bug");
                }
            };
            _server.RevisionAdvanced += (id, revision, member) => seenByTheNext.Add(member);
            _server.TryGetRecord(_part.Id, out InstanceRecord record);
            MutationEnvelope envelope = new("actor-a", _part.Id, "op-1", record.Revision);

            int result = 0;
            Assert.DoesNotThrow(() => result = _server.ApplyMutation(envelope, () =>
            {
                _part.Name = "Gate";
                _part.Archivable = false;
                _part.SetAttribute("Hp", 3d);
                return 42;
            }));

            Assert.AreEqual(42, result, "the operation's own result survives both bugs");
            Assert.AreEqual("Gate", _part.Name);
            string[] expected = { ReplicationMembers.Name, ReplicationMembers.Archivable, ReplicationMembers.Attribute("Hp") };
            CollectionAssert.AreEqual(expected, seenByTheThrower,
                "the events parked behind the throw still arrive although the report of it threw too");
            CollectionAssert.AreEqual(expected, seenByTheNext,
                "the next subscriber still receives the very event the first one threw on");
            Assert.AreEqual(1, sinkCalls, "the sink was asked once, and its own throw went nowhere");
            Assert.AreEqual(1, _server.DiagnosticsFaults, "the sink's failure is counted rather than lost");
        }

        [Test]
        public void Negative_WhenTheSubscriberAndTheDiagnosticsSinkBothThrow_ThePlainSetterIsStillWhole()
        {
            _server.Diagnostics = _ => throw new InvalidOperationException("the host's logger is broken too");
            _server.RevisionAdvanced += (id, revision, member) => throw new InvalidOperationException("host hook bug");
            List<string> seenByTheNext = new();
            _server.RevisionAdvanced += (id, revision, member) => seenByTheNext.Add(member);

            Assert.DoesNotThrow(() => _part.Name = "Gate");
            Assert.DoesNotThrow(() => _part.Archivable = false);

            Assert.AreEqual("Gate", _part.Name);
            Assert.IsFalse(_part.Archivable);
            CollectionAssert.AreEqual(new[] { ReplicationMembers.Name, ReplicationMembers.Archivable }, seenByTheNext,
                "the second setter's event still reaches the subscriber after the first report threw");
            Assert.AreEqual(2, _server.DiagnosticsFaults);
        }

        [Test]
        public void Negative_ASubscriberThatThrows_DoesNotReplaceTheOperationsOwnError()
        {
            _server.Diagnostics = _ => { };
            _server.RevisionAdvanced += (id, revision, member) => throw new InvalidOperationException("host hook bug");
            _server.TryGetRecord(_part.Id, out InstanceRecord record);
            MutationEnvelope envelope = new("actor-a", _part.Id, "op-1", record.Revision);

            RbxError refusal = Assert.Throws<RbxError>(() => _server.ApplyMutation<int>(envelope, () =>
            {
                _part.Name = "Gate";
                throw RbxError.BadArgument("the operation's own refusal", "keep it");
            }));

            StringAssert.Contains("the operation's own refusal", refusal.Message);
        }

        [Test]
        public void Replica_LocalWrite_DoesNotAdvance_MarksDivergence_AndRaisesNothing()
        {
            InstanceRegistry replica = CreateReplica();
            int raised = 0;
            replica.RevisionAdvanced += (id, revision, member) => raised++;
            RbxInstance part = replica.Create("Part");
            replica.TryGetRecord(part.Id, out InstanceRecord record);
            long before = record.Revision;

            part.Name = "Local";

            Assert.AreEqual(before, record.Revision, "a replica never counts revisions of its own");
            Assert.IsTrue(record.IsLocallyDiverged, "a local write must be remembered as divergence");
            Assert.AreEqual(0, raised, "a replica publishes nothing; it is not the source of truth");
        }

        [Test]
        public void Replica_WriteInsideTheApplyScope_IsTheServers_NotDivergence()
        {
            InstanceRegistry replica = CreateReplica();
            RbxInstance part = replica.RestoreInstance("Part", new InstanceId(42UL));

            using (replica.BeginReplicationApply())
            {
                Assert.IsTrue(replica.IsApplyingReplication);
                part.Name = "FromServer";
                replica.SetReplicatedRevision(part.Id, 7L);
            }

            replica.TryGetRecord(part.Id, out InstanceRecord record);
            Assert.IsFalse(replica.IsApplyingReplication);
            Assert.IsFalse(record.IsLocallyDiverged);
            Assert.AreEqual(7L, record.Revision, "the server's revision is stamped, not counted");
            Assert.AreEqual("FromServer", part.Name);
        }

        [Test]
        public void Replica_InstanceNew_AllocatesLocalIds_TheWireContractRefuses()
        {
            InstanceRegistry replica = CreateReplica();

            RbxInstance created = replica.Create("Part");
            RbxInstance scripted = replica.CreateScripted("Folder");
            RbxInstance askedForServerSpace = replica.Create("Part", authority: InstanceIdAuthority.Server);

            Assert.IsTrue(created.Id.IsLocallyAssigned);
            Assert.IsTrue(scripted.Id.IsLocallyAssigned);
            Assert.IsTrue(askedForServerSpace.Id.IsLocallyAssigned,
                "a replica cannot mint server ids whatever the caller asked for");
            RbxError refusal = Assert.Throws<RbxError>(() => InstanceIdWireContract.EnsureWireSafe(scripted.Id));
            Assert.AreEqual(RbxErrorCode.NotAuthority, refusal.Code);
        }

        [Test]
        public void Negative_BeginReplicationApply_OnAnAuthoritativeRegistry_IsRefused()
        {
            Assert.Throws<InvalidOperationException>(() => _server.BeginReplicationApply());
        }

        [Test]
        public void Negative_ReplicationApplyScopes_MustBeDisposedLifo()
        {
            InstanceRegistry replica = CreateReplica();
            ReplicationApplyScope outer = replica.BeginReplicationApply();
            ReplicationApplyScope inner = replica.BeginReplicationApply();

            Assert.Throws<InvalidOperationException>(() => outer.Dispose());

            inner.Dispose();
            outer.Dispose();
            Assert.IsFalse(replica.IsApplyingReplication);
        }

        [Test]
        public void Negative_SetReplicatedRevision_OutsideTheApplyScope_IsRefused()
        {
            InstanceRegistry replica = CreateReplica();
            RbxInstance part = replica.RestoreInstance("Part", new InstanceId(42UL));

            Assert.Throws<InvalidOperationException>(() => replica.SetReplicatedRevision(part.Id, 1L));
        }

        private long RevisionOf(RbxInstance instance)
        {
            _server.TryGetRecord(instance.Id, out InstanceRecord record);
            return record.Revision;
        }

        private bool GateIsFreeFromAnotherThread()
        {
            // WHY a probe thread and a timeout: RetainedMutationOperationCount takes the mutation
            // gate, so a thread that cannot take it within the timeout proves the raising thread
            // still holds it — nothing observable from the raising thread itself could.
            Thread probe = new(() => { _ = _server.RetainedMutationOperationCount; });
            probe.IsBackground = true;
            probe.Start();
            return probe.Join(TimeSpan.FromSeconds(2));
        }

        private static InstanceRegistry CreateReplica()
        {
            return new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "authority-world",
                authority: RegistryAuthority.Replica);
        }
    }
}
