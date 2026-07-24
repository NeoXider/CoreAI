using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Registry identity invariants per roadmap §3.3: one record per instance, any
    /// key resolves to the same record, ownership sweeps, and creation rules.</summary>
    [TestFixture]
    public sealed class InstanceRegistryEditModeTests
    {
        [Test]
        public void Create_AssignsDefaultsAndRegistersRecord()
        {
            var registry = new InstanceRegistry();
            RbxInstance part = registry.Create("Part");

            Assert.AreEqual("Part", part.ClassName);
            Assert.AreEqual("Part", part.Name);
            Assert.IsTrue(part.Archivable);
            Assert.IsNull(part.Parent);
            Assert.IsTrue(part.Id.IsValid);
            Assert.IsTrue(registry.TryGet(part.Id, out RbxInstance resolved));
            Assert.AreSame(part, resolved);
            Assert.IsTrue(registry.TryGetRecord(part.Id, out InstanceRecord record));
            Assert.AreSame(part, record.Instance);
            Assert.IsNull(record.OwnerModId);
            Assert.IsNull(record.OriginTag);
        }

        [Test]
        public void Lookup_ByNetIdAndWorldName_ResolvesTheSameRecord()
        {
            var registry = new InstanceRegistry();
            RbxInstance part = registry.Create("Part");

            registry.BindNetId(part.Id, 42u);
            registry.BindWorldName(part.Id, "SpawnPad");

            Assert.IsTrue(registry.TryGetByNetId(42u, out RbxInstance byNet));
            Assert.IsTrue(registry.TryGetByWorldName("SpawnPad", out RbxInstance byName));
            Assert.AreSame(part, byNet);
            Assert.AreSame(part, byName);
        }

        [Test]
        public void GetOwnedBy_ReturnsOnlyTheModsInstances()
        {
            var registry = new InstanceRegistry();
            RbxInstance a = registry.Create("Part", "speed_pad", OriginTag.FromMod("speed_pad"));
            registry.Create("Part", "other_mod", OriginTag.FromMod("other_mod"));
            registry.Create("Folder");

            var owned = registry.GetOwnedBy("speed_pad");
            Assert.AreEqual(1, owned.Count);
            Assert.AreSame(a, owned[0]);
        }

        [Test]
        public void RegisteredAndUnregistered_EventsFire()
        {
            var registry = new InstanceRegistry();
            InstanceRecord registered = null;
            InstanceRecord unregistered = null;
            registry.Registered += record => registered = record;
            registry.Unregistered += record => unregistered = record;

            RbxInstance part = registry.Create("Part");
            Assert.IsNotNull(registered);
            Assert.AreEqual(part.Id, registered.Id);

            part.Destroy();
            Assert.IsNotNull(unregistered);
            Assert.AreEqual(part.Id, unregistered.Id);
        }

        [Test]
        public void CreateScripted_RejectsNonCreatableClasses()
        {
            var registry = new InstanceRegistry();

            RbxError unknown = Assert.Throws<RbxError>(() => registry.CreateScripted("Bogus"));
            Assert.AreEqual(RbxErrorCode.BadArgument, unknown.Code);
            StringAssert.Contains("Unable to create an Instance of type 'Bogus'", unknown.RawMessage);

            RbxError service = Assert.Throws<RbxError>(() => registry.CreateScripted("Workspace"));
            Assert.AreEqual(RbxErrorCode.BadArgument, service.Code);

            RbxError abstractClass = Assert.Throws<RbxError>(() => registry.CreateScripted("BasePart"));
            Assert.AreEqual(RbxErrorCode.BadArgument, abstractClass.Code);
        }

        [Test]
        public void Create_RejectsAbstractAndUnknownClasses()
        {
            var registry = new InstanceRegistry();
            Assert.Throws<RbxError>(() => registry.Create("Instance"));
            Assert.Throws<RbxError>(() => registry.Create("NoSuchClass"));
        }

        [Test]
        public void Create_RejectsInvalidOriginTag()
        {
            var registry = new InstanceRegistry();
            RbxError error = Assert.Throws<RbxError>(() => registry.Create("Part", null, "garbage"));
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);

            Assert.DoesNotThrow(() => registry.Create("Part", null, OriginTag.FromConsole("7")));
            Assert.DoesNotThrow(() => registry.Create("Part", "m", OriginTag.FromAi("m")));
        }

        [Test]
        public void RestoreInstance_RejectsDuplicateId()
        {
            var registry = new InstanceRegistry();
            RbxInstance part = registry.Create("Part");

            RbxError error = Assert.Throws<RbxError>(
                () => registry.RestoreInstance("Part", part.Id));
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
        }
    }
}
