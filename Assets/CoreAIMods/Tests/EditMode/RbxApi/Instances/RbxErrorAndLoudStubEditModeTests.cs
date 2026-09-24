using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>The §5.2.7 error contract (stable wire names, format, fix hints) and the
    /// §5.1.6 loud-stub inventory that belongs to the registry slice (§5.1.8 item 13): live
    /// signal Connect/Once/Wait, the known-member and known-class catalogs, and the ratchet that
    /// keeps every stub's rung honest.</summary>
    [TestFixture]
    public sealed class RbxErrorAndLoudStubEditModeTests
    {
        private sealed class NoThreadFactory : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("these tests connect plain C# handlers only");
            }
        }

        [Test]
        public void WireNames_AreTheLockedScreamingSnakeSet()
        {
            Assert.AreEqual("NOT_IMPLEMENTED", RbxError.ToWireName(RbxErrorCode.NotImplemented));
            Assert.AreEqual("BAD_ARGUMENT", RbxError.ToWireName(RbxErrorCode.BadArgument));
            Assert.AreEqual("UNKNOWN_SERVICE", RbxError.ToWireName(RbxErrorCode.UnknownService));
            Assert.AreEqual("INSTANCE_DESTROYED", RbxError.ToWireName(RbxErrorCode.InstanceDestroyed));
            Assert.AreEqual("PARENT_LOCKED", RbxError.ToWireName(RbxErrorCode.ParentLocked));
            Assert.AreEqual("BUDGET_EXCEEDED", RbxError.ToWireName(RbxErrorCode.BudgetExceeded));
            Assert.AreEqual("SIGNAL_CASCADE", RbxError.ToWireName(RbxErrorCode.SignalCascade));
            Assert.AreEqual("THREAD_CAP", RbxError.ToWireName(RbxErrorCode.ThreadCap));
            Assert.AreEqual("CYCLIC_REQUIRE", RbxError.ToWireName(RbxErrorCode.CyclicRequire));
            Assert.AreEqual("API_VERSION_MISMATCH", RbxError.ToWireName(RbxErrorCode.ApiVersionMismatch));
            Assert.AreEqual("NOT_AUTHORITY", RbxError.ToWireName(RbxErrorCode.NotAuthority));
            Assert.AreEqual("PAYLOAD_TOO_LARGE", RbxError.ToWireName(RbxErrorCode.PayloadTooLarge));
            Assert.AreEqual("CONTEXT_VIOLATION", RbxError.ToWireName(RbxErrorCode.ContextViolation));
        }

        [Test]
        public void Format_MatchesTheSelfRepairContract()
        {
            RbxError error = new(RbxErrorCode.NotImplemented,
                "TweenService:Create is planned for MVP8.",
                "animate manually with RunService.Heartbeat + lerp until then",
                "speed_pad", "server/main.lua", 12);

            Assert.AreEqual(
                "[mod:speed_pad script:server/main.lua line:12] NOT_IMPLEMENTED: " +
                "TweenService:Create is planned for MVP8. | fix: animate manually with " +
                "RunService.Heartbeat + lerp until then",
                error.Message);
        }

        [Test]
        public void WithContext_AttachesModContextWithoutChangingTheBody()
        {
            RbxError bare = RbxError.NotImplemented("X", "MVP2", "wait for MVP2");
            StringAssert.StartsWith("NOT_IMPLEMENTED:", bare.Message);

            RbxError contextual = bare.WithContext("my_mod", "shared/init.lua", 3);
            Assert.AreEqual(bare.Code, contextual.Code);
            Assert.AreEqual(bare.RawMessage, contextual.RawMessage);
            StringAssert.StartsWith("[mod:my_mod script:shared/init.lua line:3]", contextual.Message);
        }

        /// <summary>
        /// Connect, Once and the property/attribute signals are live: a real handler is connected,
        /// fired by the mutation that should fire it, and delivered by the scheduler. Only the two
        /// genuine refusals stay errors — a non-invokable handler and a C#-side Wait — and neither is
        /// the old MVP2 NOT_IMPLEMENTED stub.
        /// </summary>
        /// <remarks>
        /// WHY real handlers: the previous version passed null handlers only, so every BAD_ARGUMENT it
        /// asserted came from the non-invokable-handler check, and it would still have passed had
        /// Connect itself been a BAD_ARGUMENT stub that never connects anything.
        /// </remarks>
        [Test]
        public void SignalConnect_NoLongerUsesMvp2Stub()
        {
            InstanceRegistry registry = new();
            RbxInstance parent = registry.Create("Folder");
            RbxInstance firstChild = registry.Create("Part");
            RbxInstance secondChild = registry.Create("Part");
            secondChild.Name = "Second";
            ModScheduler scheduler = new(new NoThreadFactory(), new RbxAccumulatingTimeSource());
            parent.ChildAdded.BindScheduler(scheduler);
            parent.AttributeChanged.BindScheduler(scheduler);
            parent.GetPropertyChangedSignal("Name").BindScheduler(scheduler);
            parent.GetAttributeChangedSignal("Health").BindScheduler(scheduler);
            List<string> delivered = new();

            RbxScriptConnection childAdded = parent.ChildAdded.Connect((Action<object[]>)(arguments =>
                delivered.Add("ChildAdded:" + ((RbxInstance)arguments[0]).Name)));
            RbxScriptConnection once = parent.AttributeChanged.Once((Action<object[]>)(arguments =>
                delivered.Add("AttributeChanged:" + arguments[0])));
            RbxScriptConnection nameChanged = parent.GetPropertyChangedSignal("Name").Connect(
                (Action<object[]>)(_ => delivered.Add("NameChanged")));
            RbxScriptConnection healthChanged = parent.GetAttributeChangedSignal("Health").Connect(
                (Action<object[]>)(_ => delivered.Add("HealthChanged")));

            Assert.IsTrue(childAdded.Connected);
            Assert.IsTrue(once.Connected);
            Assert.IsTrue(nameChanged.Connected);
            Assert.IsTrue(healthChanged.Connected);

            firstChild.Parent = parent;
            parent.SetAttribute("Health", 1d);
            parent.SetAttribute("Health", 2d);
            parent.Name = "Renamed";
            scheduler.Advance(0d);

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "ChildAdded:Part", "AttributeChanged:Health", "HealthChanged", "HealthChanged",
                    "NameChanged"
                },
                delivered,
                "every connected handler must be delivered by the scheduler; Once exactly once");
            Assert.IsFalse(once.Connected, "Once disconnects itself after its first delivery");

            childAdded.Disconnect();
            secondChild.Parent = parent;
            scheduler.Advance(0d);
            CollectionAssert.DoesNotContain(delivered, "ChildAdded:Second",
                "a disconnected handler must not be delivered");

            RbxError nonInvokable = Assert.Throws<RbxError>(() => parent.ChildAdded.Connect(null));
            Assert.AreEqual(RbxErrorCode.BadArgument, nonInvokable.Code);
            StringAssert.DoesNotContain("NOT_IMPLEMENTED", nonInvokable.Message);

            RbxError nonInvokableOnce = Assert.Throws<RbxError>(() => parent.Destroying.Once(null));
            Assert.AreEqual(RbxErrorCode.BadArgument, nonInvokableOnce.Code);

            RbxError wait = Assert.Throws<RbxError>(() => parent.AttributeChanged.Wait());
            Assert.AreEqual(RbxErrorCode.BadArgument, wait.Code,
                "a C# caller cannot yield; Wait belongs to a scheduler-owned Lua thread");
            StringAssert.DoesNotContain("NOT_IMPLEMENTED", wait.Message);
        }

        [Test]
        public void SignalProperties_ExistAsCachedLiveHookPoints()
        {
            InstanceRegistry registry = new();
            RbxInstance part = registry.Create("Part");

            Assert.IsNotNull(part.ChildAdded);
            Assert.IsNotNull(part.ChildRemoved);
            Assert.IsNotNull(part.DescendantAdded);
            Assert.IsNotNull(part.DescendantRemoving);
            Assert.IsNotNull(part.Destroying);
            Assert.IsNotNull(part.AncestryChanged);
            Assert.IsNotNull(part.AttributeChanged);
            Assert.AreSame(part.ChildAdded, part.ChildAdded, "signals are cached per instance");
        }

        /// <summary>
        /// M1-05: real Roblox members of Object, Instance, Model, WorldRoot, Camera, DataModel and
        /// BasePart that no binding answers are catalogued, so Lua raises a loud NOT_IMPLEMENTED
        /// naming the declaring class instead of "X is not a valid member". The lookup is the
        /// flattened one the Lua dispatch uses, so each row also proves the inheritance walk.
        /// </summary>
        [TestCase("Model", "MoveTo", "Model", true)]
        [TestCase("Model", "TranslateBy", "Model", true)]
        [TestCase("Model", "GetBoundingBox", "Model", true)]
        [TestCase("Model", "GetExtentsSize", "Model", true)]
        [TestCase("Model", "ScaleTo", "Model", true)]
        [TestCase("Model", "SetPrimaryPartCFrame", "Model", true)]
        [TestCase("Workspace", "GetPartBoundsInBox", "WorldRoot", true)]
        [TestCase("Workspace", "GetPartsInPart", "WorldRoot", true)]
        [TestCase("Workspace", "Spherecast", "WorldRoot", true)]
        [TestCase("Workspace", "BulkMoveTo", "WorldRoot", true)]
        [TestCase("Workspace", "FindPartOnRay", "WorldRoot", true)]
        [TestCase("Camera", "FieldOfView", "Camera", false)]
        [TestCase("Camera", "ViewportSize", "Camera", false)]
        [TestCase("Camera", "Focus", "Camera", false)]
        [TestCase("Camera", "ScreenPointToRay", "Camera", true)]
        [TestCase("Camera", "WorldToViewportPoint", "Camera", true)]
        [TestCase("DataModel", "IsLoaded", "DataModel", true)]
        [TestCase("DataModel", "Loaded", "DataModel", false)]
        [TestCase("DataModel", "PlaceId", "DataModel", false)]
        [TestCase("DataModel", "JobId", "DataModel", false)]
        [TestCase("Part", "BrickColor", "BasePart", false)]
        [TestCase("Part", "Mass", "BasePart", false)]
        [TestCase("Part", "Reflectance", "BasePart", false)]
        [TestCase("Part", "ApplyImpulse", "BasePart", true)]
        [TestCase("Part", "GetTouchingParts", "BasePart", true)]
        [TestCase("Folder", "FindFirstDescendant", "Instance", true)]
        [TestCase("Folder", "QueryDescendants", "Instance", true)]
        [TestCase("Folder", "GetActor", "Instance", true)]
        [TestCase("Part", "Changed", "Object", false)]
        public void KnownMemberCatalog_AnswersAnUnboundRobloxMemberWithTheLoudStub(
            string className, string memberName, string declaringClassName, bool isMethod)
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();

            Assert.IsTrue(catalog.TryGetKnownUnimplementedMember(
                    className, memberName, RbxKnownUnimplementedMemberAccess.Read,
                    out string declaredOn, out RbxKnownUnimplementedMemberDescriptor member),
                className + "." + memberName + " is a real Roblox member and must be catalogued");
            Assert.AreEqual(declaringClassName, declaredOn);
            Assert.AreEqual(isMethod, member.IsMethod);

            RbxError error = member.CreateError(declaredOn);
            Assert.AreEqual(RbxErrorCode.NotImplemented, error.Code);
            StringAssert.StartsWith(
                declaringClassName + (isMethod ? ":" : ".") + memberName + " is ", error.RawMessage);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error.Fix), "every stub carries a workaround");
        }

        /// <summary>
        /// Negative twin: the catalog is consulted before the child lookup and after the bindings,
        /// so it must never list a member a binding answers, a child the standard DataModel
        /// bootstraps (<c>game.Workspace</c> has to keep resolving), a member of another class
        /// (a Folder has no MoveTo or GetService), or a deprecated lowercase alias.
        /// </summary>
        [Test]
        public void KnownMemberCatalog_NeverShadowsABoundMemberAChildOrAnotherClassesMember()
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            (string ClassName, string MemberName)[] absent =
            {
                ("DataModel", "Workspace"), ("DataModel", "RunService"), ("DataModel", "Players"),
                ("DataModel", "GetService"), ("DataModel", "FindService"),
                ("DataModel", "BindToClose"),
                ("Model", "PrimaryPart"), ("Model", "WorldPivot"), ("Model", "GetPivot"),
                ("Model", "PivotTo"),
                ("Workspace", "Raycast"), ("Workspace", "CurrentCamera"), ("Workspace", "Gravity"),
                ("Workspace", "GetServerTimeNow"), ("Workspace", "Camera"),
                ("Camera", "CFrame"), ("Camera", "CameraType"), ("Camera", "CameraSubject"),
                ("Part", "Position"), ("Part", "Size"), ("Part", "CFrame"), ("Part", "Color"),
                ("Part", "Transparency"), ("Part", "Anchored"), ("Part", "CanCollide"),
                ("Part", "Material"), ("Part", "MaterialVariant"), ("Part", "Shape"),
                ("Part", "Orientation"), ("Part", "Rotation"), ("Part", "Touched"),
                ("Part", "TouchEnded"),
                ("Folder", "Name"), ("Folder", "Parent"), ("Folder", "ClassName"),
                ("Folder", "Archivable"), ("Folder", "ChildAdded"), ("Folder", "Destroying"),
                ("Folder", "AttributeChanged"), ("Folder", "FindFirstChild"),
                ("Folder", "GetChildren"), ("Folder", "Clone"), ("Folder", "Destroy"),
                ("Folder", "IsA"), ("Folder", "GetPropertyChangedSignal"),
                ("Folder", "WaitForChild"),
                ("Folder", "GetService"), ("Folder", "PivotTo"), ("Folder", "GetPivot"),
                ("Folder", "MoveTo"), ("Folder", "BrickColor"), ("Folder", "FieldOfView"),
                ("Humanoid", "MoveTo"),
                ("Players", "getPlayers"), ("Players", "localPlayer"), ("Part", "changed"),
            };

            foreach ((string className, string memberName) in absent)
            {
                foreach (RbxKnownUnimplementedMemberAccess access in new[]
                         {
                             RbxKnownUnimplementedMemberAccess.Read,
                             RbxKnownUnimplementedMemberAccess.Write
                         })
                {
                    Assert.IsFalse(catalog.TryGetKnownUnimplementedMember(
                            className, memberName, access, out string declaredOn, out _),
                        className + "." + memberName + " (" + access + ") must not be a loud stub, "
                        + "but the catalog declares it on " + declaredOn);
                }
            }
        }

        /// <summary>
        /// M1-05 (class half): a real, script-creatable Roblox class CoreAI does not implement is a
        /// known-unimplemented class with a status-specific NOT_IMPLEMENTED error, never a class the
        /// catalog can instantiate, and a real class is never listed as unimplemented.
        /// </summary>
        [Test]
        public void KnownClassCatalog_ListsUnimplementedCreatableClassesWithStatusWording()
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            string[] known =
            {
                "WedgePart", "SpawnLocation", "MeshPart", "Weld", "WeldConstraint", "Attachment",
                "BindableEvent", "Sound", "ParticleEmitter", "ScreenGui", "BodyVelocity", "Tool"
            };
            foreach (string className in known)
            {
                Assert.IsTrue(catalog.TryGetKnownUnimplementedClass(
                        className, out RbxKnownUnimplementedClassDescriptor descriptor),
                    className + " is a creatable Roblox class and must be a known stub");
                Assert.AreEqual(className, descriptor.Name);
                Assert.IsFalse(catalog.TryGet(className, out _), className + " must not be instantiable");
            }

            foreach (RbxKnownUnimplementedClassDescriptor descriptor in catalog.KnownUnimplementedClasses)
            {
                Assert.IsFalse(catalog.TryGet(descriptor.Name, out _),
                    descriptor.Name + " is registered for real and listed as unimplemented");
            }

            foreach (string real in new[] { "Part", "Folder", "Model", "Workspace", "Camera", "Script",
                         "StarterGui", "Bogus" })
            {
                Assert.IsFalse(catalog.TryGetKnownUnimplementedClass(real, out _),
                    real + " must not be a known-unimplemented class");
            }

            Assert.IsFalse(catalog.TryGetKnownUnimplementedClass(null, out _));

            Assert.IsTrue(catalog.TryGetKnownUnimplementedClass(
                "Sound", out RbxKnownUnimplementedClassDescriptor sound));
            RbxError planned = sound.CreateInstanceNewError();
            Assert.AreEqual(RbxErrorCode.NotImplemented, planned.Code);
            Assert.AreEqual("Instance.new(\"Sound\") is planned for MVP15.", planned.RawMessage);

            Assert.IsTrue(catalog.TryGetKnownUnimplementedClass(
                "WeldConstraint", out RbxKnownUnimplementedClassDescriptor weld));
            RbxError backlog = weld.CreateInstanceNewError();
            Assert.AreEqual(RbxErrorCode.NotImplemented, backlog.Code);
            Assert.AreEqual(
                "Instance.new(\"WeldConstraint\"): WeldConstraint is a known Rbx class, but no "
                + "roadmap rung is assigned.",
                backlog.RawMessage);

            Assert.IsTrue(catalog.TryGetKnownUnimplementedClass(
                "BodyVelocity", out RbxKnownUnimplementedClassDescriptor mover));
            RbxError unsupported = mover.CreateInstanceNewError();
            Assert.AreEqual(RbxErrorCode.NotImplemented, unsupported.Code);
            Assert.AreEqual(
                "Instance.new(\"BodyVelocity\"): BodyVelocity is a known Rbx class deliberately "
                + "unsupported by CoreAI.",
                unsupported.RawMessage);
            foreach (RbxError error in new[] { planned, backlog, unsupported })
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(error.Fix));
            }
        }

        /// <summary>
        /// The member error helper reproduces the Lua dispatch's wording exactly, so a stub raised
        /// from C# and one raised from Lua read the same to the self-repair loop.
        /// </summary>
        [Test]
        public void KnownMemberDescriptor_CreateError_MatchesTheLuaDispatchWording()
        {
            Assert.AreEqual(
                "Lighting.ClockTime is a known Rbx member, but no roadmap rung is assigned.",
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("ClockTime", "fix")
                    .CreateError("Lighting").RawMessage);
            Assert.AreEqual(
                "Workspace.Terrain is a known Rbx member deliberately unsupported by CoreAI.",
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty("Terrain", "fix")
                    .CreateError("Workspace").RawMessage);
            Assert.AreEqual(
                "RunService:BindToRenderStep is planned for MVP2.",
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod("BindToRenderStep", "MVP2", "fix")
                    .CreateError("RunService").RawMessage);
        }

        /// <summary>
        /// Ratchet over every loud stub the catalogs ship — members, creatable classes and services
        /// (M8-24, M1-25): no stub may promise a rung that is already closed, credit a shipped slice
        /// with a member it shipped without, or carry a "planned" rung that says it is not planned.
        /// </summary>
        /// <remarks>
        /// WHY MVP0 and MVP1 are the closed rungs: TODO.md records MVP1 as closed and MVP0 landed
        /// before it; MVP2 stays open while its G10 gate is FAILED. The character pipeline is the
        /// MVP8 slice that shipped without RespawnLocation and the appearance fields, which the
        /// Player stubs used to promise to it.
        /// </remarks>
        [Test]
        public void LoudStubInventory_NamesNoClosedRungAndNoContradictoryPlannedPhase()
        {
            Regex closedRung = new(@"\bMVP[01]\b");
            string[] shippedSliceClaims = { "character pipeline in MVP8" };
            string[] notPlannedMarkers = { "no planned", "not planned", "backlog" };
            List<string> texts = new();
            List<string> plannedPhases = new();

            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            foreach (ClassDescriptor descriptor in catalog.All)
            {
                foreach (RbxKnownUnimplementedMemberDescriptor member in
                         catalog.GetDeclaredKnownUnimplementedMembers(descriptor.Name))
                {
                    string owner = descriptor.Name + "." + member.Name;
                    texts.Add(owner + " workaround: " + member.Workaround);
                    texts.Add(owner + " error: " + member.CreateError(descriptor.Name).RawMessage);
                    if (member.Status == RbxKnownUnimplementedMemberStatus.Planned)
                    {
                        plannedPhases.Add(owner + " phase: " + member.Phase);
                    }
                }
            }

            foreach (RbxKnownUnimplementedClassDescriptor knownClass in catalog.KnownUnimplementedClasses)
            {
                texts.Add(knownClass.Name + " workaround: " + knownClass.Workaround);
                texts.Add(knownClass.Name + " error: " + knownClass.CreateInstanceNewError().RawMessage);
                if (knownClass.Status == RbxKnownUnimplementedMemberStatus.Planned)
                {
                    plannedPhases.Add(knownClass.Name + " phase: " + knownClass.Phase);
                }
            }

            ServiceCatalog services = ServiceCatalog.CreateMvp2();
            int stubCount = 0;
            foreach (string serviceName in new List<string>(services.ServiceNames))
            {
                RbxStubService stub;
                try
                {
                    stub = services.GetService(serviceName) as RbxStubService;
                }
                catch (RbxError)
                {
                    // WHY: a tree-backed registration with no DataModel behind it is a shipped
                    // service, not a stub; GetService says so by raising.
                    continue;
                }

                if (stub == null || stub.IsImplementedFallback)
                {
                    continue;
                }

                stubCount++;
                texts.Add(serviceName + " workaround: " + stub.WorkaroundHint);
                texts.Add(serviceName + " error: " + stub.MemberAccessError("Probe").RawMessage);
                if (stub.Status == RbxKnownUnimplementedMemberStatus.Planned)
                {
                    plannedPhases.Add(serviceName + " phase: " + stub.PlannedMvp);
                }
            }

            Assert.Greater(stubCount, 0, "precondition: the service catalog ships stubs");
            Assert.Greater(texts.Count, 100, "precondition: the member and class catalogs are populated");
            foreach (string text in texts)
            {
                Assert.IsFalse(closedRung.IsMatch(text), "a stub names a closed rung: " + text);
                foreach (string claim in shippedSliceClaims)
                {
                    StringAssert.DoesNotContain(claim, text, "a stub credits a shipped slice: " + text);
                }

                StringAssert.DoesNotContain("is planned for no", text, "self-contradictory stub: " + text);
            }

            foreach (string phase in plannedPhases)
            {
                foreach (string marker in notPlannedMarkers)
                {
                    StringAssert.DoesNotContain(marker, phase.ToLowerInvariant(),
                        "a planned stub must name a rung, not say it has none: " + phase);
                }
            }
        }
    }
}
