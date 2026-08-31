using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

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
        public void ApplyMutation_PerActorReplayCapacity_EvictsOldestAndRejectsItsReplayAsStale()
        {
            InstanceRegistry registry = new(mutationReplayCapacityPerActor: 2);
            RbxInstance target = registry.Create("Folder");
            Assert.IsTrue(registry.TryGetRecord(target.Id, out InstanceRecord record));
            MutationEnvelope firstEnvelope = default;

            for (int operationIndex = 0; operationIndex < 3; operationIndex++)
            {
                MutationEnvelope envelope = new(
                    "bounded-actor", target.Id, "operation-" + operationIndex, record.Revision);
                if (operationIndex == 0)
                {
                    firstEnvelope = envelope;
                }

                string result = registry.ApplyMutation(envelope, () =>
                {
                    target.Name = "Applied-" + operationIndex;
                    return target.Name;
                });
                Assert.AreEqual("Applied-" + operationIndex, result);
            }

            Assert.AreEqual(2, registry.RetainedMutationOperationCount,
                "A per-actor replay cap of two must not retain three completed results.");
            string nameAfterNewestOperation = target.Name;

            RbxError replayError = Assert.Throws<RbxError>(() =>
                registry.ApplyMutation(firstEnvelope, () =>
                {
                    target.Name = "EvictedReplayApplied";
                    return target.Name;
                }));

            StringAssert.Contains("stale expected revision", replayError.Message);
            Assert.AreEqual(nameAfterNewestOperation, target.Name,
                "An evicted replay must be rejected before its mutation runs.");
            Assert.AreEqual(2, registry.RetainedMutationOperationCount);
        }

        [Test]
        public void Lookup_ByWorldName_LazilyWrapsHostObject_AndEveryKeyResolvesTheSameRecord()
        {
            RbxSpace.ResetForTests(0.28f);
            GameObject hostObject = new("LazyWorldIdentityHost");
            GameObject worldObject = new("LazyWorldSpawnPad");
            worldObject.transform.position = new Vector3(0f, 1.8f, 0f);
            worldObject.SetActive(false);
            RbxWorldHost host = hostObject.AddComponent<RbxWorldHost>();
            try
            {
                host.Initialize();
                int countBeforeLookup = host.Registry.Count;

                Assert.IsTrue(host.Registry.TryGetByWorldName(worldObject.name, out RbxInstance byName),
                    "a scene object must be wrapped on its first world-name lookup without pre-binding");
                Assert.AreEqual(countBeforeLookup + 1, host.Registry.Count,
                    "the first lookup creates exactly one host-owned registry record");
                Assert.AreEqual("Part", byName.ClassName);
                Assert.AreSame(host.Registry.WorldRoot, byName.Parent);
                Assert.IsTrue(host.Registry.TryGetRecord(byName.Id, out InstanceRecord record));
                Assert.AreEqual(worldObject.name, record.WorldName);
                Assert.IsNull(record.OwnerModId);
                Assert.IsTrue(host.Binder.TryGetBoundObject(byName.Id, out GameObject backingObject));
                Assert.AreSame(worldObject, backingObject, "the wrapper must adopt, not duplicate, the host object");
                Assert.IsFalse(backingObject.activeSelf, "adoption must preserve host-owned activation state");

                PartProperties properties = host.Binder.GetPartPropertiesOrDefault(byName.Id);
                Assert.AreEqual(1.8f / 0.28f, properties.Position.Y, 1e-3f);

                Assert.IsTrue(host.Registry.TryGet(byName.Id, out RbxInstance byId));
                Assert.AreSame(byName, byId);
                host.Registry.BindNetId(byName.Id, 42u);
                Assert.IsTrue(host.Registry.TryGetByNetId(42u, out RbxInstance byNet));
                Assert.AreSame(byName, byNet);

                Assert.IsTrue(host.Registry.TryGetByWorldName(worldObject.name, out RbxInstance secondLookup));
                Assert.AreSame(byName, secondLookup);
                Assert.AreEqual(countBeforeLookup + 1, host.Registry.Count,
                    "later lookups must reuse the same lazy record");
            }
            finally
            {
                Object.DestroyImmediate(hostObject);
                Object.DestroyImmediate(worldObject);
                RbxSpace.ResetForTests();
            }
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
        public void AuthoredCount_ExcludesHostAndRuntimeInfrastructureRecords()
        {
            InstanceRegistry registry = new();
            registry.Create("Player");
            registry.Create("Folder", "runtime-mod", OriginTag.FromMod("runtime-mod"),
                isRuntimeInfrastructure: true);
            registry.Create("Script", "runtime-mod", OriginTag.FromMod("runtime-mod"),
                isRuntimeInfrastructure: true);
            RbxInstance modPart = registry.CreateScripted(
                "Part", "authored-mod", OriginTag.FromMod("authored-mod"));
            RbxInstance consoleFolder = registry.CreateScripted(
                "Folder", originTag: OriginTag.FromConsole("authored-console"));

            Assert.AreEqual(5, registry.Count);
            Assert.AreEqual(2, registry.AuthoredCount);
            Assert.IsTrue(registry.TryGetRecord(modPart.Id, out InstanceRecord modRecord));
            Assert.IsTrue(modRecord.IsAuthoredContent);
            Assert.IsTrue(registry.TryGetRecord(
                consoleFolder.Id, out InstanceRecord consoleRecord));
            Assert.IsTrue(consoleRecord.IsAuthoredContent);
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
