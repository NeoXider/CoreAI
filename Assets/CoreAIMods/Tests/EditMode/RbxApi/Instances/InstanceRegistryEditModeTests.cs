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

        /// <summary>
        /// M2-10: every scheduler resume of an actor's mods is a server-generated operation. They
        /// used to share the actor's replay window with its network intents, so a Heartbeat handler
        /// evicted a client intent within a second and the client's retry was refused as stale
        /// instead of answered from the cache. Server operations now keep a window of their own.
        /// </summary>
        [Test]
        public void ApplyServerGeneratedMutation_ManyResumesOfTheSameActor_KeepItsClientIntentReplayable()
        {
            const int capacity = 4;
            InstanceRegistry registry = new(mutationReplayCapacityPerActor: capacity);
            RbxInstance target = registry.Create("Folder");
            Assert.IsTrue(registry.TryGetRecord(target.Id, out InstanceRecord record));
            MutationEnvelope intent = new("player-7", target.Id, "client-op-42", record.Revision);
            int applied = 0;

            string first = registry.ApplyMutation(intent, () =>
            {
                applied++;
                registry.AdvanceRevision(target.Id);
                return "applied";
            });
            for (int resume = 0; resume < capacity * 3; resume++)
            {
                int captured = resume;
                registry.ApplyServerGeneratedMutation("player-7", false, "",
                    "resume Lua scheduler thread", () => captured);
            }

            string retry = registry.ApplyMutation(intent, () =>
            {
                applied++;
                return "applied again";
            });

            Assert.AreEqual("applied", first);
            Assert.AreEqual("applied", retry, "the retry must be answered from the cached result");
            Assert.AreEqual(1, applied, "the intent must run exactly once");
        }

        /// <summary>
        /// Twin of the replay test: server-generated operations are still ledgered, in a bounded
        /// window per actor that neither evicts nor is evicted by the caller window, and a world
        /// teardown clears both.
        /// </summary>
        [Test]
        public void ApplyServerGeneratedMutation_CountsInABoundedPerActorWindowBesideTheCallerWindow()
        {
            const int capacity = 2;
            InstanceRegistry registry = new(mutationReplayCapacityPerActor: capacity);
            RbxInstance target = registry.Create("Folder");

            for (int index = 0; index < 5; index++)
            {
                registry.ApplyServerGeneratedMutation("actor-a", false, "", "server op", () => 0);
            }

            Assert.AreEqual(capacity, registry.RetainedMutationOperationCount,
                "the server window is bounded per actor");

            for (int index = 0; index < capacity; index++)
            {
                Assert.IsTrue(registry.TryGetRecord(target.Id, out InstanceRecord record));
                int captured = index;
                registry.ApplyMutation(
                    new MutationEnvelope("actor-a", target.Id, "caller-op-" + index, record.Revision),
                    () => captured);
            }

            Assert.AreEqual(capacity * 2, registry.RetainedMutationOperationCount,
                "caller results are retained beside the server window, not instead of it");

            registry.ApplyServerGeneratedMutation("actor-a", false, "", "server op", () => 0);
            Assert.AreEqual(capacity * 2, registry.RetainedMutationOperationCount,
                "a full server window replaces its oldest entry and evicts no caller result");

            registry.ApplyServerGeneratedMutation("actor-b", false, "", "server op", () => 0);
            Assert.AreEqual(capacity * 2 + 1, registry.RetainedMutationOperationCount,
                "each actor has its own server window");

            registry.MarkDetached();
            Assert.AreEqual(0, registry.RetainedMutationOperationCount);
        }

        /// <summary>
        /// The operation id every server-generated envelope carries is reserved: a caller cannot
        /// submit it, so a replay of it can never be mistaken for a first-seen caller operation.
        /// </summary>
        [Test]
        public void ApplyMutation_CallerEnvelopeCarryingTheServerGeneratedId_IsRefusedBeforeItRuns()
        {
            InstanceRegistry registry = new();
            RbxInstance target = registry.Create("Folder");
            Assert.IsTrue(registry.TryGetRecord(target.Id, out InstanceRecord record));
            MutationEnvelope forged = new(
                "actor-a", target.Id, InstanceRegistry.ServerGeneratedOperationId, record.Revision);
            bool ran = false;

            RbxError error = Assert.Throws<RbxError>(() => registry.ApplyMutation(forged, () =>
            {
                ran = true;
                return 0;
            }));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("reserved for server-generated envelopes", error.RawMessage);
            Assert.IsFalse(ran);
            Assert.AreEqual(0, registry.RetainedMutationOperationCount);
        }

        /// <summary>
        /// M1-27: binding a world name another record holds moves it; destroying the old holder later
        /// must not unbind the new one, which it used to by removing the key it no longer owned.
        /// </summary>
        [Test]
        public void BindWorldName_Rebind_KeepsTheLiveHolderResolvable()
        {
            InstanceRegistry registry = new();
            RbxInstance first = registry.Create("Part");
            RbxInstance second = registry.Create("Part");

            registry.BindWorldName(first.Id, "Crate");
            registry.BindWorldName(second.Id, "Crate");

            Assert.IsTrue(registry.TryGetRecord(first.Id, out InstanceRecord firstRecord));
            Assert.IsNull(firstRecord.WorldName, "the previous holder must lose the name");
            first.Destroy();

            Assert.IsTrue(registry.TryGetByWorldName("Crate", out RbxInstance resolved));
            Assert.AreSame(second, resolved);
        }

        /// <summary>NetId twin of <see cref="BindWorldName_Rebind_KeepsTheLiveHolderResolvable"/>.</summary>
        [Test]
        public void BindNetId_Rebind_KeepsTheLiveHolderResolvable()
        {
            InstanceRegistry registry = new();
            RbxInstance first = registry.Create("Part");
            RbxInstance second = registry.Create("Part");

            registry.BindNetId(first.Id, 7u);
            registry.BindNetId(second.Id, 7u);

            Assert.IsTrue(registry.TryGetRecord(first.Id, out InstanceRecord firstRecord));
            Assert.AreEqual(0u, firstRecord.NetId, "the previous holder must lose the netId");
            first.Destroy();

            Assert.IsTrue(registry.TryGetByNetId(7u, out RbxInstance resolved));
            Assert.AreSame(second, resolved);
        }

        /// <summary>
        /// M1-27: re-binding the old holder to another name must not remove the entry a newer holder
        /// took over, and an ordinary rename of a sole holder still moves its key.
        /// </summary>
        [Test]
        public void BindWorldName_RebindingTheOldHolder_LeavesTheNewHolderBound()
        {
            InstanceRegistry registry = new();
            RbxInstance first = registry.Create("Part");
            RbxInstance second = registry.Create("Part");
            registry.BindWorldName(first.Id, "Crate");
            registry.BindWorldName(second.Id, "Crate");

            registry.BindWorldName(first.Id, "Barrel");

            Assert.IsTrue(registry.TryGetByWorldName("Crate", out RbxInstance crate));
            Assert.AreSame(second, crate);
            Assert.IsTrue(registry.TryGetByWorldName("Barrel", out RbxInstance barrel));
            Assert.AreSame(first, barrel);

            registry.BindWorldName(second.Id, "Box");
            Assert.IsFalse(registry.TryGetByWorldName("Crate", out _),
                "a renamed sole holder must release its old name");
            Assert.IsTrue(registry.TryGetByWorldName("Box", out RbxInstance box));
            Assert.AreSame(second, box);

            registry.BindWorldName(second.Id, null);
            Assert.IsFalse(registry.TryGetByWorldName("Box", out _));
        }

        /// <summary>
        /// M1-36: the pre-simulation pass visits Models only, so its per-frame cost no longer grows
        /// with the number of parts in the world. It used to walk every record every frame.
        /// </summary>
        [Test]
        public void ProcessPreSimulation_VisitsModelsOnly_NotEveryRecord()
        {
            InstanceRegistry registry = new();
            RbxModel model = (RbxModel)registry.Create("Model");
            for (int index = 0; index < 1000; index++)
            {
                registry.Create("Part");
            }

            long before = registry.PreSimulationVisitCount;
            registry.ProcessPreSimulation();

            Assert.AreEqual(1L, registry.PreSimulationVisitCount - before,
                "one Model in a world of a thousand parts must cost one visit");
            Assert.IsNull(model.PrimaryPart);
        }

        /// <summary>
        /// Functional twin of the cost test: an invalid PrimaryPart is still cleared at the next
        /// pre-simulation step, a Model created after the parts is still visited, and a destroyed
        /// Model leaves the pass.
        /// </summary>
        [Test]
        public void ProcessPreSimulation_StillClearsAnInvalidPrimaryPart_AndDropsDestroyedModels()
        {
            InstanceRegistry registry = new();
            registry.Create("Part");
            RbxModel model = (RbxModel)registry.Create("Model");
            RbxInstance primary = registry.Create("Part");
            primary.Parent = model;
            model.SetPrimaryPart(primary);
            RbxModel doomed = (RbxModel)registry.Create("Model");

            primary.Destroy();
            doomed.Destroy();
            long before = registry.PreSimulationVisitCount;
            registry.ProcessPreSimulation();

            Assert.IsNull(model.PrimaryPart, "a destroyed PrimaryPart is cleared at pre-simulation");
            Assert.AreEqual(1L, registry.PreSimulationVisitCount - before,
                "the destroyed Model must no longer be visited");

            RbxInstance escapee = registry.Create("Part");
            escapee.Parent = model;
            model.SetPrimaryPart(escapee);
            escapee.Parent = null;
            registry.ProcessPreSimulation();
            Assert.IsNull(model.PrimaryPart, "a PrimaryPart that left the Model is cleared too");
        }

        /// <summary>
        /// M1-05 (class half): Instance.new of a real, creatable Roblox class CoreAI does not
        /// implement raises the loud NOT_IMPLEMENTED stub, not "Unable to create an Instance of type"
        /// with advice to pass a Part. Nothing is registered on the way.
        /// </summary>
        [TestCase("WeldConstraint", "no roadmap rung is assigned")]
        [TestCase("SpawnLocation", "no roadmap rung is assigned")]
        [TestCase("WedgePart", "no roadmap rung is assigned")]
        [TestCase("Sound", "is planned for MVP15")]
        [TestCase("ScreenGui", "is planned for MVP14")]
        [TestCase("BodyVelocity", "deliberately unsupported by CoreAI")]
        public void CreateScripted_KnownUnimplementedClass_RaisesTheLoudStub(string className,
            string statusWording)
        {
            InstanceRegistry registry = new();
            int countBefore = registry.Count;

            RbxError error = Assert.Throws<RbxError>(() => registry.CreateScripted(className));

            Assert.AreEqual(RbxErrorCode.NotImplemented, error.Code);
            StringAssert.Contains("Instance.new(\"" + className + "\")", error.RawMessage);
            StringAssert.Contains(statusWording, error.RawMessage);
            StringAssert.DoesNotContain("Unable to create", error.RawMessage);
            Assert.AreEqual(countBefore, registry.Count, "a refused class registers nothing");
        }

        /// <summary>
        /// Negative twin: services, abstract classes and names Roblox does not have keep the Roblox
        /// "Unable to create" BAD_ARGUMENT, and a class that ships for real supersedes its stub.
        /// </summary>
        [Test]
        public void CreateScripted_ServicesAbstractAndUnknownClasses_StayBadArgument()
        {
            InstanceRegistry registry = new();
            foreach (string className in new[] { "Workspace", "StarterGui", "BasePart",
                         "FormFactorPart", "Object", "Bogus" })
            {
                RbxError error = Assert.Throws<RbxError>(() => registry.CreateScripted(className));
                Assert.AreEqual(RbxErrorCode.BadArgument, error.Code, className);
                StringAssert.Contains("Unable to create an Instance of type '" + className + "'",
                    error.RawMessage);
            }

            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            catalog.Register(new ClassDescriptor("WeldConstraint", "Instance", false, true, false));
            InstanceRegistry shipped = new(catalog);
            Assert.IsFalse(catalog.TryGetKnownUnimplementedClass("WeldConstraint", out _),
                "registering the real class retires its stub");
            Assert.AreEqual("WeldConstraint", shipped.CreateScripted("WeldConstraint").ClassName);
            Assert.Throws<System.InvalidOperationException>(() => catalog.RegisterKnownUnimplementedClasses(
                RbxKnownUnimplementedClassDescriptor.Backlog("Part", "a real class is never a stub")));
        }

        /// <summary>
        /// M1-21: the hierarchy is rooted at Object as in the mirror, and Part sits under the
        /// abstract FormFactorPart, so IsA answers both the way Roblox does.
        /// </summary>
        [Test]
        public void ClassHierarchy_IsRootedAtObject_AndPartIsAFormFactorPart()
        {
            InstanceRegistry registry = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxInstance part = registry.Create("Part");
            RbxInstance folder = registry.Create("Folder");

            Assert.IsTrue(part.IsA("Object"));
            Assert.IsTrue(folder.IsA("Object"));
            Assert.IsTrue(game.IsA("Object"));
            Assert.IsTrue(registry.WorldRoot.IsA("Object"));
            Assert.IsTrue(part.IsA("FormFactorPart"));
            Assert.IsTrue(part.IsA("BasePart"));
            Assert.IsTrue(part.IsA("Instance"));
            Assert.IsFalse(folder.IsA("FormFactorPart"));
            Assert.IsFalse(folder.IsA("BasePart"));

            Assert.IsTrue(registry.Catalog.TryGet("Object", out ClassDescriptor objectClass));
            Assert.IsNull(objectClass.BaseClassName, "Object is the root");
            Assert.IsTrue(objectClass.IsAbstract);
            Assert.IsTrue(registry.Catalog.TryGet("FormFactorPart", out ClassDescriptor formFactor));
            Assert.AreEqual("BasePart", formFactor.BaseClassName);
            Assert.IsTrue(formFactor.IsAbstract);
            Assert.Throws<RbxError>(() => registry.Create("Object"));
            Assert.Throws<RbxError>(() => registry.Create("FormFactorPart"));
        }

        /// <summary>
        /// WHY: a quota refusal used to be thrown from inside the Registered multicast, so a
        /// subscriber registered after the refusing one never heard Registered for the record and
        /// still heard its Unregistered from the cleanup. An admission decides before anything is
        /// added or announced.
        /// </summary>
        [Test]
        public void RegistrationAdmission_ARefusal_RegistersAndAnnouncesNothing_AndThrowsTheRefusalText()
        {
            InstanceRegistry registry = new();
            List<string> calls = new();
            RecordingAdmission admission = new(registry, calls, "quota", "Folder");
            registry.AddRegistrationAdmission(admission);
            List<InstanceRecord> registered = new();
            List<InstanceRecord> unregistered = new();
            registry.Registered += record => registered.Add(record);
            registry.Unregistered += record => unregistered.Add(record);

            System.InvalidOperationException refusal =
                Assert.Throws<System.InvalidOperationException>(() => registry.Create("Folder"));

            Assert.AreEqual("quota refuses Folder", refusal.Message,
                "the admission's text is the creation's error, unwrapped");
            Assert.IsEmpty(registered, "a refused record is never announced");
            Assert.IsEmpty(unregistered, "a record never registered is never unregistered either");
            Assert.AreEqual(0, registry.Count, "a refused record is never added");
            Assert.IsEmpty(admission.Revoked, "the refusing check itself is not revoked");

            RbxInstance part = registry.Create("Part");

            Assert.AreEqual(1, registered.Count, "an admitted creation is announced once");
            Assert.AreSame(part, registered[0].Instance);
            Assert.AreEqual(1, admission.Admitted.Count);
            Assert.AreSame(registered[0], admission.Admitted[0],
                "the record the check admitted is the record the registry keeps");
            CollectionAssert.AreEqual(new[] { false, false }, admission.ReachableWhenAsked,
                "a check is asked before the record can be looked up");
            Assert.IsTrue(registry.TryGet(part.Id, out RbxInstance resolved));
            Assert.AreSame(part, resolved);
            Assert.IsEmpty(unregistered);
        }

        [Test]
        public void RegistrationAdmission_ALaterRefusalOrThrow_RevokesEveryEarlierAdmission_NewestFirst()
        {
            InstanceRegistry registry = new();
            List<string> calls = new();
            RecordingAdmission first = new(registry, calls, "first");
            RecordingAdmission second = new(registry, calls, "second");
            RecordingAdmission refusing = new(registry, calls, "refusing", "Folder");
            registry.AddRegistrationAdmission(first);
            registry.AddRegistrationAdmission(second);
            registry.AddRegistrationAdmission(refusing);

            Assert.Throws<System.InvalidOperationException>(() => registry.Create("Folder"));

            CollectionAssert.AreEqual(
                new[]
                {
                    "first admits Folder", "second admits Folder", "refusing refuses Folder",
                    "second revokes Folder", "first revokes Folder"
                },
                calls,
                "every check that already admitted the record is told it will never be registered");
            Assert.AreSame(first.Admitted[0], first.Revoked[0]);
            Assert.AreSame(second.Admitted[0], second.Revoked[0]);
            Assert.AreEqual(0, registry.Count);

            calls.Clear();
            Assert.IsTrue(registry.RemoveRegistrationAdmission(refusing));
            RecordingAdmission throwing = new(registry, calls, "throwing", "Model", throws: true);
            registry.AddRegistrationAdmission(throwing);

            System.InvalidOperationException bug =
                Assert.Throws<System.InvalidOperationException>(() => registry.Create("Model"));

            Assert.AreEqual("throwing threw for Model", bug.Message,
                "a check's own failure propagates as it was thrown");
            CollectionAssert.AreEqual(
                new[]
                {
                    "first admits Model", "second admits Model", "throwing throws for Model",
                    "second revokes Model", "first revokes Model"
                },
                calls);
            Assert.AreEqual(0, registry.Count);
        }

        [Test]
        public void RegistrationAdmission_GuardsRestoreToo_AndARemovedCheckIsNoLongerAsked()
        {
            InstanceRegistry registry = new();
            List<string> calls = new();
            RecordingAdmission refusing = new(registry, calls, "restore", "Folder");
            registry.AddRegistrationAdmission(refusing);
            InstanceId restoredId = new(500UL);

            Assert.Throws<System.InvalidOperationException>(
                () => registry.RestoreInstance("Folder", restoredId));
            Assert.IsFalse(registry.TryGet(restoredId, out RbxInstance _),
                "a refused restore leaves no record under the snapshot's id");

            Assert.AreEqual(1, registry.RegistrationAdmissionCount);
            Assert.IsTrue(registry.RemoveRegistrationAdmission(refusing));
            Assert.AreEqual(0, registry.RegistrationAdmissionCount);
            Assert.IsFalse(registry.RemoveRegistrationAdmission(refusing),
                "removing a check that is not installed reports false");

            calls.Clear();
            RbxInstance restored = registry.RestoreInstance("Folder", restoredId);

            Assert.AreEqual(restoredId, restored.Id);
            Assert.IsEmpty(calls, "a removed check is never asked again");
            Assert.Throws<System.ArgumentNullException>(() => registry.AddRegistrationAdmission(null));
        }

        /// <summary>
        /// An admission check that logs every call into a shared list, refuses one class, and can
        /// throw instead of answering.
        /// </summary>
        private sealed class RecordingAdmission : IInstanceRegistrationAdmission
        {
            private readonly InstanceRegistry _registry;
            private readonly List<string> _calls;
            private readonly string _name;
            private readonly string _refusedClassName;
            private readonly bool _throws;

            public RecordingAdmission(InstanceRegistry registry, List<string> calls, string name,
                string refusedClassName = null, bool throws = false)
            {
                _registry = registry;
                _calls = calls;
                _name = name;
                _refusedClassName = refusedClassName;
                _throws = throws;
            }

            public List<InstanceRecord> Admitted { get; } = new();

            public List<InstanceRecord> Revoked { get; } = new();

            public List<bool> ReachableWhenAsked { get; } = new();

            public string Admit(InstanceRecord record)
            {
                string className = record.Instance.ClassName;
                ReachableWhenAsked.Add(_registry.TryGetRecord(record.Id, out InstanceRecord _));
                if (className == _refusedClassName)
                {
                    if (_throws)
                    {
                        _calls.Add(_name + " throws for " + className);
                        throw new System.InvalidOperationException(_name + " threw for " + className);
                    }

                    _calls.Add(_name + " refuses " + className);
                    return _name + " refuses " + className;
                }

                _calls.Add(_name + " admits " + className);
                Admitted.Add(record);
                return null;
            }

            public void Revoke(InstanceRecord record)
            {
                _calls.Add(_name + " revokes " + record.Instance.ClassName);
                Revoked.Add(record);
            }
        }
    }
}
