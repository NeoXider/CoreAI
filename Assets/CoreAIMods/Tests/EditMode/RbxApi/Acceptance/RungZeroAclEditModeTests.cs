using System;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>Rung-zero 0.1: ACL promoted into engine-free registry.</summary>
    [TestFixture]
    public sealed class RungZeroAclEditModeTests
    {
        [Test]
        public void WorldAclAuthorizer_LivesInEngineFreeInstancesAssembly()
        {
            Type type = Type.GetType("CoreAI.Mods.Rbx.Instances.WorldAclAuthorizer, CoreAI.RbxApi.Instances");
            Assert.IsNotNull(type, "WorldAclAuthorizer must be promoted into CoreAI.RbxApi.Instances");
            Assert.AreSame(typeof(InstanceRegistry).Assembly, type.Assembly);
            Assert.AreSame(typeof(RbxInstance).Assembly, type.Assembly);
        }

        [Test]
        public void WorldAclDecision_LivesInEngineFreeInstancesAssembly()
        {
            Type type = Type.GetType("CoreAI.Mods.Rbx.Instances.WorldAclDecision, CoreAI.RbxApi.Instances");
            Assert.IsNotNull(type, "WorldAclDecision must be promoted into CoreAI.RbxApi.Instances");
        }

        [Test]
        public void SetAccessControl_WithOnlyRegistryReference_CannotChangeAnotherActorsInstance()
        {
            InstanceRegistry registry = new(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            RbxInstance folderB = registry.Create("Folder", ownerActorId: "actor-b", accessScope: InstanceAccessScope.Owned);
            folderB.Name = "OwnedByB";

            RbxError error = Assert.Throws<RbxError>(() =>
                registry.SetAccessControl(folderB, "actor-a", InstanceAccessScope.Owned, false, "actor-a", false, ""));
            StringAssert.Contains("actor 'actor-a'", error.RawMessage);
            StringAssert.Contains("Owned by actor 'actor-b'", error.RawMessage);
            Assert.AreEqual("actor-b", registry.GetRecord(folderB.Id).OwnerActorId);
        }

        [Test]
        public void DestroyInstance_WithOnlyRegistryReference_CannotDestroyAnotherActorsInstance()
        {
            InstanceRegistry registry = new(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            RbxInstance folderB = registry.Create("Folder", ownerActorId: "actor-b", accessScope: InstanceAccessScope.Owned);

            RbxError error = Assert.Throws<RbxError>(() =>
                registry.DestroyInstance(folderB, "actor-a", false, ""));

            StringAssert.Contains("actor 'actor-a'", error.RawMessage);
            Assert.IsFalse(folderB.IsDestroyed);
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
    }
}
