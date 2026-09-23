using NUnit.Framework;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Engine-free ACL proof: registry-only caller cannot mutate another actor's instance.</summary>
    [TestFixture]
    public sealed class RungZeroAclEngineFreeTests
    {
        [Test]
        public void SetAccessControl_RegistryOnly_CannotReattributeAnotherActorsOwnedInstance()
        {
            InstanceRegistry registry = new(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            RbxInstance ownedByB = registry.Create("Folder", ownerActorId: "actor-b", accessScope: InstanceAccessScope.Owned);
            ownedByB.Name = "OwnedByB";
            RbxError error = Assert.Throws<RbxError>(() =>
                registry.SetAccessControl(ownedByB, "actor-a", InstanceAccessScope.Owned, false, "actor-a", false, ""));
            StringAssert.Contains("actor 'actor-a'", error.RawMessage);
            StringAssert.Contains("Owned by actor 'actor-b'", error.RawMessage);
            Assert.AreEqual("actor-b", registry.GetRecord(ownedByB.Id).OwnerActorId);
        }

        [Test]
        public void DestroyInstance_RegistryOnly_CannotDestroyAnotherActorsInstance()
        {
            InstanceRegistry registry = new(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            RbxInstance ownedByB = registry.Create("Folder", ownerActorId: "actor-b", accessScope: InstanceAccessScope.Owned);
            RbxError error = Assert.Throws<RbxError>(() =>
                registry.DestroyInstance(ownedByB, "actor-a", false, ""));
            StringAssert.Contains("actor 'actor-a'", error.RawMessage);
            Assert.IsFalse(ownedByB.IsDestroyed);
        }

        [Test]
        public void AuthorizeMutation_EnvelopedOwnedWrite_ByOwner_Succeeds()
        {
            InstanceRegistry registry = new(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            RbxInstance owned = registry.Create("Folder", ownerActorId: "actor-a", accessScope: InstanceAccessScope.Owned);
            Assert.DoesNotThrow(() => registry.ApplyServerGeneratedMutation(
                "actor-a", false, "", "write property", () =>
                {
                    registry.AuthorizeMutation(
                        "actor-a", false, "", owned,
                        WorldAclDecision.WriteProperty, "write property");
                    return true;
                }));
        }

        [Test]
        public void WorldAclAuthorizer_LivesInEngineFreeAssembly()
        {
            System.Type type = System.Type.GetType("CoreAI.Mods.Rbx.Instances.WorldAclAuthorizer, CoreAI.RbxApi.Instances");
            Assert.IsNotNull(type);
            Assert.AreSame(typeof(InstanceRegistry).Assembly, type.Assembly);
        }

        [Test]
        public void HostRestore_AclSnapshot_RetainsExactlyOneOperationForTheHostActor()
        {
            InstanceTreeSnapshot snapshot = CaptureAclWorld(out _);
            InstanceRegistry target = new(mutationReplayCapacityPerActor: 1);

            InstanceTreeSerializer.Restore(snapshot, target, HostActorId);

            Assert.AreEqual(InstanceRegistry.CurrentWorldAclVersion, target.WorldAclVersion);
            Assert.AreEqual(1, target.RetainedMutationOperationCount,
                "the whole restore must be one server-generated operation");
            Assert.IsFalse(target.HasActiveMutationEnvelope(HostActorId),
                "the host scope must close when the restore returns");
            RbxError leaked = Assert.Throws<RbxError>(() =>
                target.DemandMutationEnvelope(HostActorId, "write property"));
            StringAssert.Contains("no server-generated mutation envelope is active", leaked.RawMessage);

            // WHY a replay window of one proves whose operation it is: another operation by the SAME
            // actor evicts the restore from that actor's window, so the count stays at one, while an
            // operation by any other actor is retained beside it.
            target.ApplyServerGeneratedMutation(HostActorId, true, "", "probe host window", () => 0);
            Assert.AreEqual(1, target.RetainedMutationOperationCount,
                "the restore operation must have been ledgered under the host actor");
            target.ApplyServerGeneratedMutation("other-actor", true, "", "probe other window", () => 0);
            Assert.AreEqual(2, target.RetainedMutationOperationCount);
        }

        [Test]
        public void HostRestore_KeepsEveryCapturedRevision()
        {
            InstanceTreeSnapshot snapshot = CaptureAclWorld(out _);
            InstanceRegistry target = new();

            RbxInstance root = InstanceTreeSerializer.Restore(snapshot, target, HostActorId);

            bool sawAdvancedRevision = false;
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                Assert.IsTrue(target.TryGetRecord(new InstanceId(node.Id), out InstanceRecord record));
                Assert.AreEqual(node.Revision, record.Revision,
                    "restored revision drifted for instance " + node.Id + " (" + node.ClassName + ")");
                sawAdvancedRevision |= node.Revision > 0L;
            }

            Assert.IsTrue(sawAdvancedRevision, "precondition: the captured world carries non-zero revisions");
            InstanceTreeSnapshot recaptured = InstanceTreeSerializer.Capture(root);
            Assert.AreEqual(snapshot.Instances.Count, recaptured.Instances.Count);
            for (int index = 0; index < snapshot.Instances.Count; index++)
            {
                Assert.AreEqual(snapshot.Instances[index].Id, recaptured.Instances[index].Id);
                Assert.AreEqual(snapshot.Instances[index].Revision, recaptured.Instances[index].Revision);
            }
        }

        [Test]
        public void HostRestore_LinksHostProtectedServicesWithoutAclRefusal()
        {
            InstanceTreeSnapshot snapshot = CaptureAclWorld(out RbxDataModel source);
            InstanceRegistry target = new();
            RbxDataModel restoredGame = null;

            Assert.DoesNotThrow(() => restoredGame =
                (RbxDataModel)InstanceTreeSerializer.Restore(snapshot, target, HostActorId));

            foreach (string serviceName in new[] { "Workspace", "Players", "ReplicatedStorage" })
            {
                RbxInstance service = restoredGame.FindFirstChild(serviceName);
                Assert.IsNotNull(service, serviceName + " must be linked under the restored DataModel");
                Assert.IsTrue(target.TryGetRecord(service.Id, out InstanceRecord record));
                Assert.AreEqual(InstanceAccessScope.HostProtected, record.AccessScope,
                    "precondition: " + serviceName + " is a HostProtected world singleton");
                Assert.AreEqual(source.FindFirstChild(serviceName).Id, service.Id);
            }

            RbxInstance owned = restoredGame.FindFirstChild("Workspace").FindFirstChild("OwnedByA");
            Assert.IsNotNull(owned);
            Assert.IsTrue(target.TryGetRecord(owned.Id, out InstanceRecord ownedRecord));
            Assert.AreEqual("actor-a", ownedRecord.OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.Owned, ownedRecord.AccessScope);
        }

        [Test]
        public void HostRestore_BlankHostActor_IsRefusedBeforeAnyRegistration()
        {
            InstanceTreeSnapshot snapshot = CaptureAclWorld(out _);
            foreach (string blank in new[] { null, "", "   " })
            {
                InstanceRegistry target = new();

                RbxError error = Assert.Throws<RbxError>(() =>
                    InstanceTreeSerializer.Restore(snapshot, target, blank));

                StringAssert.Contains("host actor id", error.RawMessage);
                Assert.AreEqual(0, target.Count, "nothing may be registered before the refusal");
                Assert.IsNull(target.WorldAclVersion, "the ACL version may not be configured either");
                Assert.AreEqual(0, target.RetainedMutationOperationCount);
            }
        }

        [Test]
        public void LegacyTwoArgumentRestore_RetainsNoOperation()
        {
            // WHY this pins the un-enveloped path: the two-argument overload stays for callers that
            // restore outside a world session; production restore goes through RestoreFresh, which
            // passes the host actor.
            InstanceTreeSnapshot snapshot = CaptureAclWorld(out _);
            InstanceRegistry target = new();

            InstanceTreeSerializer.Restore(snapshot, target);

            Assert.AreEqual(InstanceRegistry.CurrentWorldAclVersion, target.WorldAclVersion);
            Assert.AreEqual(0, target.RetainedMutationOperationCount);
        }

        private const string HostActorId = "restore-host";

        private static InstanceTreeSnapshot CaptureAclWorld(out RbxDataModel game)
        {
            InstanceRegistry source = new(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            game = DataModelBootstrap.CreateGame(source);
            RbxInstance owned = source.Create(
                "Folder", ownerActorId: "actor-a", accessScope: InstanceAccessScope.Owned);
            owned.Name = "OwnedByA";
            owned.Parent = source.WorldRoot;
            owned.SetAttribute("Level", 3d);
            owned.AddTag("Restorable");
            return InstanceTreeSerializer.Capture(game);
        }
    }
}
