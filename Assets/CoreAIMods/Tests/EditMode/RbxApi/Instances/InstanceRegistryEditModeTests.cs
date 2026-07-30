using System.Collections.Generic;
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
            InstanceRegistry registry = new();
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
            InstanceRegistry registry = new();
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
            InstanceRegistry registry = new();
            RbxInstance a = registry.Create("Part", "speed_pad", OriginTag.FromMod("speed_pad"));
            registry.Create("Part", "other_mod", OriginTag.FromMod("other_mod"));
            registry.Create("Folder");

            IReadOnlyList<RbxInstance> owned = registry.GetOwnedBy("speed_pad");
            Assert.AreEqual(1, owned.Count);
            Assert.AreSame(a, owned[0]);
        }

        [Test]
        public void RegisteredAndUnregistered_EventsFire()
        {
            InstanceRegistry registry = new();
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
            InstanceRegistry registry = new();

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
            InstanceRegistry registry = new();
            Assert.Throws<RbxError>(() => registry.Create("Instance"));
            Assert.Throws<RbxError>(() => registry.Create("NoSuchClass"));
        }

        [Test]
        public void Create_RejectsInvalidOriginTag()
        {
            InstanceRegistry registry = new();
            RbxError error = Assert.Throws<RbxError>(() => registry.Create("Part", null, "garbage"));
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);

            Assert.DoesNotThrow(() => registry.Create("Part", null, OriginTag.FromConsole("7")));
            Assert.DoesNotThrow(() => registry.Create("Part", "m", OriginTag.FromAi("m")));
        }

        [Test]
        public void RestoreInstance_RejectsDuplicateId()
        {
            InstanceRegistry registry = new();
            RbxInstance part = registry.Create("Part");

            RbxError error = Assert.Throws<RbxError>(() => registry.RestoreInstance("Part", part.Id));
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
        }

        /// <summary>
        /// A destroyed <c>RbxWorldHost</c> takes the whole DataModel with it, but the mod stack keeps the
        /// registry it captured at install time and goes on calling <c>Instance.new</c>. The spawn must
        /// name that, because what a player reports is "every part vanished and then I got an error about
        /// Workspace" — which reads as a bug in their own script.
        /// </summary>
        [Test]
        public void CreateScripted_AfterTheHostDetachedTheWorld_FailsNamingTheLostHost()
        {
            InstanceRegistry registry = new();
            Assert.IsFalse(registry.IsDetached);

            registry.MarkDetached();

            Assert.IsTrue(registry.IsDetached);
            RbxError error = Assert.Throws<RbxError>(() => registry.CreateScripted("Part"));
            Assert.AreEqual(RbxErrorCode.WorldDetached, error.Code);
            Assert.That(error.Message, Does.Contain("RbxWorldHost"));
            Assert.That(error.Message, Does.Contain("Part"), "the failing call must be identifiable");
            Assert.That(error.Fix, Does.Contain("reload the mods"));
        }

        /// <summary>
        /// Host-level restore paths (snapshot load, service bootstrap) must stay usable while a world is
        /// rebuilt — only the script-facing surface is refused.
        /// </summary>
        [Test]
        public void Create_AfterDetach_StillWorksForHostLevelCallers()
        {
            InstanceRegistry registry = new();
            registry.MarkDetached();

            Assert.DoesNotThrow(() => registry.Create("Part"));
        }
    }
}
