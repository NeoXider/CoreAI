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
    }
}
