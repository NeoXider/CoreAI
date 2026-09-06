using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// MVP2.5 slice 8.5 gate, engine-free half: the Roblox rules for <c>workspace:Raycast</c>,
    /// <c>Workspace.Gravity</c> and <c>BasePart.Touched</c>.
    /// </summary>
    /// <remarks>
    /// WHY a fake port instead of a scene: every rule asserted here is a Roblox semantic, not a
    /// physics-engine behaviour — the 15,000-stud refusal, the filter meaning descendants, contacts
    /// firing on both parts, a teleport not counting as a touch. Proving them against a real
    /// simulation would make them slow, flaky, and dependent on collider tuning; the PlayMode gates
    /// next door prove the other half, that the real engine actually feeds this.
    /// </remarks>
    [TestFixture]
    public sealed class Mvp8PhysicsEditModeTests
    {
        private const LuaCapabilities Capabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit;

        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        // ---- Raycast argument rules ----------------------------------------------------------

        [Test]
        public void Raycast_ReturnsTheHitTheEngineReported()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxInstance part = harness.Part("target");
            harness.Port.NextHit = new RbxPhysicsRaycastHit(
                part.Id, new RbxVector3(1f, 2f, 3f), new RbxVector3(0f, 1f, 0f),
                RbxMaterialId.Plastic, 12.5d);

            RbxRaycastResult result = harness.Physics.Raycast(
                RbxVector3.Zero, new RbxVector3(0f, -50f, 0f), null);

            Assert.IsNotNull(result);
            Assert.AreSame(part, result.Instance);
            Assert.AreEqual(12.5d, result.Distance, 1e-6d);
            Assert.AreEqual(2f, result.Position.Y, 1e-5f);
            Assert.AreEqual(1f, result.Normal.Y, 1e-5f);
        }

        [Test]
        public void Negative_Raycast_WithNoHit_IsNilNotAnEmptyResult()
        {
            using ProductionHarness harness = new ProductionHarness();
            harness.Port.NextHit = null;

            Assert.IsNull(harness.Physics.Raycast(
                RbxVector3.Zero, new RbxVector3(0f, -50f, 0f), null));
        }

        [Test]
        public void Negative_Raycast_DirectionLongerThanTheMirrorsCap_IsRefused()
        {
            // The mirror states the maximum length of the direction vector is 15,000 studs. Refusing
            // is the mirror-faithful answer AND the useful one: a clamp would silently test a
            // shorter ray than the script asked for and report a miss it cannot explain.
            using ProductionHarness harness = new ProductionHarness();

            RbxError error = Assert.Throws<RbxError>(() => harness.Physics.Raycast(
                RbxVector3.Zero, new RbxVector3(0f, -15000.5f, 0f), null));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("15000", error.Message);
            Assert.IsFalse(harness.Port.WasQueried, "a refused ray must not reach the engine");
        }

        [Test]
        public void Raycast_ExactlyAtTheCap_IsAllowed()
        {
            // The negative twin above would also pass on an off-by-one that refused the legal
            // maximum, so the boundary itself is pinned.
            using ProductionHarness harness = new ProductionHarness();
            harness.Port.NextHit = null;

            Assert.DoesNotThrow(() => harness.Physics.Raycast(
                RbxVector3.Zero, new RbxVector3(0f, -15000f, 0f), null));
            Assert.IsTrue(harness.Port.WasQueried);
        }

        [Test]
        public void Negative_Raycast_ZeroOrNonFiniteDirection_IsRefused()
        {
            using ProductionHarness harness = new ProductionHarness();

            Assert.Throws<RbxError>(() => harness.Physics.Raycast(
                RbxVector3.Zero, RbxVector3.Zero, null), "a zero direction tests nothing");
            Assert.Throws<RbxError>(() => harness.Physics.Raycast(
                RbxVector3.Zero, new RbxVector3(float.NaN, 1f, 0f), null));
            Assert.Throws<RbxError>(() => harness.Physics.Raycast(
                new RbxVector3(float.PositiveInfinity, 0f, 0f), new RbxVector3(0f, 1f, 0f), null));
            Assert.IsFalse(harness.Port.WasQueried);
        }

        [Test]
        public void Negative_Raycast_HitOnAnInstanceTheTreeLostReadsAsAMiss()
        {
            // RaycastResult.Instance is a BasePart in the mirror, so a hit the registry can no
            // longer resolve has no honest result to build — and a script cannot tell a nil
            // Instance apart from a bug in its own code.
            using ProductionHarness harness = new ProductionHarness();
            RbxInstance part = harness.Part("doomed");
            harness.Port.NextHit = new RbxPhysicsRaycastHit(
                part.Id, RbxVector3.Zero, new RbxVector3(0f, 1f, 0f), RbxMaterialId.Plastic, 1d);
            part.Destroy();

            Assert.IsNull(harness.Physics.Raycast(
                RbxVector3.Zero, new RbxVector3(0f, -10f, 0f), null));
        }

        // ---- Filter semantics ----------------------------------------------------------------

        [Test]
        public void Filter_ExcludeSkipsTheListedInstanceAndItsDescendants()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxInstance model = harness.Registry.Create("Model");
            model.Parent = harness.Registry.WorldRoot;
            RbxInstance limb = harness.Part("limb");
            limb.Parent = model;
            RbxInstance other = harness.Part("scenery");

            RbxRaycastParams filter = new();
            filter.SetFilterDescendantsInstances(new[] { model });

            Assert.IsFalse(filter.Accepts(limb), "a descendant of an excluded model is excluded");
            Assert.IsFalse(filter.Accepts(model));
            Assert.IsTrue(filter.Accepts(other));
        }

        [Test]
        public void Filter_IncludeIsTheExactInverse()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxInstance wanted = harness.Part("wanted");
            RbxInstance ignored = harness.Part("ignored");

            RbxRaycastParams filter = new() { FilterType = RbxRaycastFilterType.Include };
            filter.SetFilterDescendantsInstances(new[] { wanted });

            Assert.IsTrue(filter.Accepts(wanted));
            Assert.IsFalse(filter.Accepts(ignored),
                "Include means only the listed subtree is eligible");
        }

        [Test]
        public void Filter_EmptyListMeansEverythingIsEligible()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxInstance part = harness.Part("anything");

            Assert.IsTrue(new RbxRaycastParams().Accepts(part),
                "the mirror's default RaycastParams considers all parts");
        }

        [Test]
        public void Filter_ReachesTheEngineAsTheEligibilityPredicate()
        {
            // The filter has to be applied where the engine sweeps, not after: a nearer excluded
            // part must not swallow the hit on the part behind it.
            using ProductionHarness harness = new ProductionHarness();
            RbxInstance blocker = harness.Part("blocker");
            RbxInstance behind = harness.Part("behind");
            RbxRaycastParams filter = new();
            filter.SetFilterDescendantsInstances(new[] { blocker });
            harness.Port.NextHit = new RbxPhysicsRaycastHit(
                behind.Id, RbxVector3.Zero, new RbxVector3(0f, 1f, 0f), RbxMaterialId.Plastic, 9d);

            harness.Physics.Raycast(RbxVector3.Zero, new RbxVector3(0f, 0f, 20f), filter);

            Assert.IsNotNull(harness.Port.LastEligibility);
            Assert.IsFalse(harness.Port.LastEligibility(blocker.Id));
            Assert.IsTrue(harness.Port.LastEligibility(behind.Id));
        }

        [Test]
        public void Negative_CollisionGroupOtherThanDefault_IsRefused()
        {
            // Accepting a group CoreAI cannot honour would return a confidently wrong hit — the very
            // parts the script asked to filter out. IgnoreWater and BruteForceAllSlow are accepted
            // precisely because neither can change the answer.
            RbxRaycastParams filter = new();

            RbxError error = Assert.Throws<RbxError>(() => filter.CollisionGroup = "Players");
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            Assert.AreEqual("Default", filter.CollisionGroup, "the refused value must not stick");
            Assert.DoesNotThrow(() => filter.CollisionGroup = "Default");
            Assert.DoesNotThrow(() => filter.IgnoreWater = true);
            Assert.DoesNotThrow(() => filter.BruteForceAllSlow = true);
        }

        // ---- Gravity -------------------------------------------------------------------------

        [Test]
        public void Gravity_DefaultsToTheMirrorValueAndReachesTheEngine()
        {
            using ProductionHarness harness = new ProductionHarness();

            Assert.AreEqual(196.2d, harness.Physics.Gravity, 1e-9d);
            Assert.AreEqual(196.2d, harness.Port.Gravity, 1e-9d,
                "attaching a port must push the current gravity, not wait for the next write");

            harness.Physics.Gravity = 50d;

            Assert.AreEqual(50d, harness.Port.Gravity, 1e-9d);
        }

        [Test]
        public void Gravity_SetBeforeAPortExists_IsPushedWhenOneAttaches()
        {
            // A world is scripted before the scene finishes binding; a Gravity written in that
            // window must not be a value only Lua remembers.
            RbxWorldPhysics physics = new(new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "physics-world"));
            physics.Gravity = 12.5d;

            FakePhysicsPort port = new();
            physics.AttachPort(port);

            Assert.AreEqual(12.5d, port.Gravity, 1e-9d);
        }

        [Test]
        public void Negative_Gravity_NonFinite_IsRefused()
        {
            using ProductionHarness harness = new ProductionHarness();

            Assert.Throws<RbxError>(() => harness.Physics.Gravity = double.NaN);
            Assert.Throws<RbxError>(() => harness.Physics.Gravity = double.PositiveInfinity);
            Assert.AreEqual(196.2d, harness.Physics.Gravity, 1e-9d);
            Assert.AreEqual(196.2d, harness.Port.Gravity, 1e-9d,
                "a refused write must not reach the engine either");
        }

        // ---- Touched / TouchEnded ------------------------------------------------------------

        [Test]
        public void Contact_FiresTouchedOnBothParts()
        {
            // WHY connected from Lua and not from C#: a part's contact signals are created on first
            // read and get their scheduler from the mod context that reads them, so Lua is both the
            // production path and the only one where the signal is live.
            using ProductionHarness harness = new ProductionHarness();
            harness.LoadTouchCounter("both");

            harness.RaiseContact(began: true);

            Assert.AreEqual("1", harness.Store.Get("both", "pad_touched"),
                "log: " + string.Join(" || ", harness.LogLines));
            Assert.AreEqual("1", harness.Store.Get("both", "ball_touched"),
                "the mirror is explicit that PartA.Touched fires with PartB and PartB.Touched with A");
            Assert.AreEqual("Ball", harness.Store.Get("both", "pad_other"));
            Assert.AreEqual("Pad", harness.Store.Get("both", "ball_other"));
        }

        [Test]
        public void Contact_EndFiresTouchEndedOnBothParts()
        {
            using ProductionHarness harness = new ProductionHarness();
            harness.LoadTouchCounter("ended");

            harness.RaiseContact(began: true);
            harness.RaiseContact(began: false);

            Assert.AreEqual("1", harness.Store.Get("ended", "pad_ended"));
            Assert.AreEqual("1", harness.Store.Get("ended", "ball_ended"));
        }

        [Test]
        public void Contact_RepeatedEngineReportsForOnePair_FireOnce()
        {
            // One collision between two bodies produces several engine contact points; Roblox fires
            // Touched once for the pair.
            using ProductionHarness harness = new ProductionHarness();
            harness.LoadTouchCounter("dedupe");

            harness.RaiseContact(began: true);
            harness.RaiseContact(began: true);
            harness.RaiseContactReversed(began: true);

            Assert.AreEqual("1", harness.Store.Get("dedupe", "pad_touched"));
            Assert.AreEqual("1", harness.Store.Get("dedupe", "ball_touched"));
        }

        [Test]
        public void Negative_Contact_AfterATeleport_FiresNothing()
        {
            // The mirror: Touched "will not fire if the CFrame property was changed such that the
            // part overlaps another part". A teleporting pad must not read as a hit.
            //
            // WHY this order and not BeginPhysicsStep -> NoteTeleport -> contact: production never
            // produces that order. A CFrame/Position write reaches NoteTeleport from Lua, which
            // ticks in Update(); BeginPhysicsStep runs in the FixedUpdate that follows, right before
            // the engine simulates and reports this step's contacts. So the real order is note
            // (previous Update), then BeginPhysicsStep (this FixedUpdate), then the contact (this
            // step's simulate) — exactly what is driven here.
            using ProductionHarness harness = new ProductionHarness();
            harness.LoadTouchCounter("teleport");

            harness.Physics.NoteTeleport(harness.Pad.Id);
            harness.Physics.BeginPhysicsStep();
            harness.RaiseContact(began: true);
            harness.RaiseContact(began: false);

            Assert.AreEqual("", harness.Store.Get("teleport", "pad_touched"),
                "neither Touched nor TouchEnded: a contact that never began cannot end");
            Assert.AreEqual("", harness.Store.Get("teleport", "ball_touched"));
            Assert.AreEqual("", harness.Store.Get("teleport", "pad_ended"));
        }

        [Test]
        public void Contact_AfterTheTeleportStepEnds_FiresAgain()
        {
            // The twin of the suppression: it lasts one step, not forever, or a part that ever
            // teleported would go permanently deaf to real collisions. Same real ordering as above:
            // note during the "previous Update", then the two physics steps that follow it.
            using ProductionHarness harness = new ProductionHarness();
            harness.LoadTouchCounter("nextstep");

            harness.Physics.NoteTeleport(harness.Pad.Id);
            harness.Physics.BeginPhysicsStep();
            harness.RaiseContact(began: true);
            harness.Physics.BeginPhysicsStep();
            harness.RaiseContact(began: true);

            Assert.AreEqual("1", harness.Store.Get("nextstep", "pad_touched"));
            Assert.AreEqual("1", harness.Store.Get("nextstep", "ball_touched"));
        }

        [Test]
        public void Contact_DestroyedMidContact_LeavesNoResidueAndTheSameIdsFireAgain()
        {
            // WHY reflection: the leaked pair lives in a private dictionary with no other
            // observable surface, and production InstanceIds are never reused by design (see
            // InstanceRecord.Id), so the exact reuse scenario the defect describes cannot be
            // constructed through the public registry API. Replaying the identical (pad, ball) ids
            // straight at the port is the closest honest reproduction: it proves the stale pair no
            // longer blocks a fresh contact report carrying those same values, which is exactly the
            // mechanism a reused id would trigger.
            using ProductionHarness harness = new ProductionHarness();
            harness.LoadTouchCounter("residue");
            InstanceId padId = harness.Pad.Id;
            InstanceId ballId = harness.Ball.Id;

            harness.RaiseContact(began: true);
            Assert.AreEqual("1", harness.Store.Get("residue", "pad_touched"));
            Assert.AreEqual(1, OpenContactCount(harness.Physics));

            harness.Ball.Destroy();
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(0, OpenContactCount(harness.Physics),
                "a part destroyed mid-contact must not leave its pair resident forever");

            harness.Port.RaiseBegan(padId, ballId);

            Assert.AreEqual(1, OpenContactCount(harness.Physics),
                "the pair must be recorded as freshly open, not silently dropped by a dedupe check "
                + "against the stale entry — that stale check is exactly what would deduplicate "
                + "away the next genuine Touched on a reused id");
        }

        private static int OpenContactCount(RbxWorldPhysics physics)
        {
            FieldInfo field = typeof(RbxWorldPhysics).GetField(
                "_openContacts", BindingFlags.NonPublic | BindingFlags.Instance);
            return ((System.Collections.IDictionary)field.GetValue(physics)).Count;
        }

        [Test]
        public void Negative_Contact_OnAPartNobodyListensTo_CostsNothing()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxBasePart first = harness.Part("a");
            RbxBasePart second = harness.Part("b");

            Assert.DoesNotThrow(() => harness.Port.RaiseBegan(first.Id, second.Id));
            Assert.DoesNotThrow(() => harness.Bindings.Scheduler.Advance(0d));
        }

        // ---- Null port -----------------------------------------------------------------------

        [Test]
        public void NullPort_MissesEveryRayAndAcceptsGravity()
        {
            // A headless world runs the same mod code as a live one: a mod casting a ray to look
            // around should find nothing there, not crash.
            RbxWorldPhysics physics = new(new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "headless-world"));

            Assert.IsNull(physics.Raycast(RbxVector3.Zero, new RbxVector3(0f, -10f, 0f), null));
            Assert.DoesNotThrow(() => physics.Gravity = 10d);
            Assert.AreEqual(10d, physics.Gravity, 1e-9d);
        }

        // ---- Lua surface ---------------------------------------------------------------------

        [Test]
        public void Lua_RaycastReturnsAResultWhoseMembersRead()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("ray-a");
            RbxInstance part = harness.Part("target");
            harness.Port.NextHit = new RbxPhysicsRaycastHit(
                part.Id, new RbxVector3(0f, 4f, 0f), new RbxVector3(0f, 1f, 0f),
                RbxMaterialId.Plastic, 6d);

            harness.Stack.Runtime.LoadMod(actor, "ray-mod", @"
                local hit = workspace:Raycast(Vector3.new(0, 10, 0), Vector3.new(0, -20, 0))
                store_set('name', hit.Instance.Name)
                store_set('distance', tostring(hit.Distance))
                store_set('y', tostring(hit.Position.Y))
                store_set('material', hit.Material.Name)
                store_set('miss', tostring(workspace:Raycast(
                    Vector3.new(0, 10, 0), Vector3.new(0, -20, 0)) == nil))",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("target", harness.Store.Get("ray-mod", "name"));
            StringAssert.StartsWith("6", harness.Store.Get("ray-mod", "distance"));
            StringAssert.StartsWith("4", harness.Store.Get("ray-mod", "y"));
            Assert.AreEqual("Plastic", harness.Store.Get("ray-mod", "material"));
            Assert.AreEqual("true", harness.Store.Get("ray-mod", "miss"),
                "the port reports one hit then nothing; a second cast must read as nil in Lua");
        }

        [Test]
        public void Lua_RaycastParamsCarriesTheFilterAndTheEnum()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("params-a");
            harness.Port.NextHit = null;

            harness.Stack.Runtime.LoadMod(actor, "params-mod", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                local params = RaycastParams.new()
                params.FilterType = Enum.RaycastFilterType.Include
                params.FilterDescendantsInstances = {part}
                params.IgnoreWater = true
                store_set('type', params.FilterType.Name)
                store_set('count', tostring(#params.FilterDescendantsInstances))
                store_set('first', params.FilterDescendantsInstances[1].Name)
                store_set('water', tostring(params.IgnoreWater))
                workspace:Raycast(Vector3.new(0, 0, 0), Vector3.new(0, 0, 10), params)",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("Include", harness.Store.Get("params-mod", "type"));
            Assert.AreEqual("1", harness.Store.Get("params-mod", "count"));
            Assert.AreEqual("Part", harness.Store.Get("params-mod", "first"));
            Assert.AreEqual("true", harness.Store.Get("params-mod", "water"));
            Assert.IsTrue(harness.Port.WasQueried);
        }

        [Test]
        public void Lua_GravityReadsAndWrites()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("grav-a");

            harness.Stack.Runtime.LoadMod(actor, "grav-mod", @"
                store_set('default', tostring(workspace.Gravity))
                workspace.Gravity = 40
                store_set('after', tostring(workspace.Gravity))",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            StringAssert.StartsWith("196", harness.Store.Get("grav-mod", "default"));
            StringAssert.StartsWith("40", harness.Store.Get("grav-mod", "after"));
            Assert.AreEqual(40d, harness.Port.Gravity, 1e-9d);
        }

        [Test]
        public void Lua_TouchedDeliversTheOtherPart()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("touch-a");

            harness.Stack.Runtime.LoadMod(actor, "touch-mod", @"
                local pad = Instance.new('Part')
                pad.Name = 'Pad'
                pad.Parent = workspace
                local ball = Instance.new('Part')
                ball.Name = 'Ball'
                ball.Parent = workspace
                pad.Touched:Connect(function(other) store_set('hit', other.Name) end)
                pad.TouchEnded:Connect(function(other) store_set('left', other.Name) end)",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            RbxInstance pad = harness.Registry.WorldRoot.FindFirstChild("Pad");
            RbxInstance ball = harness.Registry.WorldRoot.FindFirstChild("Ball");
            harness.Port.RaiseBegan(pad.Id, ball.Id);
            harness.Port.RaiseEnded(pad.Id, ball.Id);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("Ball", harness.Store.Get("touch-mod", "hit"),
                "log: " + string.Join(" || ", harness.LogLines));
            Assert.AreEqual("Ball", harness.Store.Get("touch-mod", "left"));
        }

        [Test]
        public void Negative_Lua_RaycastOverTheCap_RaisesBadArgument()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("cap-a");

            harness.Stack.Runtime.LoadMod(actor, "cap-mod", @"
                local ok, err = pcall(function()
                    return workspace:Raycast(Vector3.new(0, 0, 0), Vector3.new(0, 0, 20000))
                end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))",
                persistToStore: false);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("false", harness.Store.Get("cap-mod", "ok"));
            StringAssert.Contains("BAD_ARGUMENT", harness.Store.Get("cap-mod", "err"));
        }

        // ---- Harness -------------------------------------------------------------------------

        /// <summary>A physics engine that reports exactly what a test tells it to.</summary>
        private sealed class FakePhysicsPort : IRbxPhysicsPort
        {
            public event Action<InstanceId, InstanceId> ContactBegan;

            public event Action<InstanceId, InstanceId> ContactEnded;

            public RbxPhysicsRaycastHit? NextHit { get; set; }

            public bool WasQueried { get; private set; }

            public double Gravity { get; private set; } = double.NaN;

            public Func<InstanceId, bool> LastEligibility { get; private set; }

            public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds,
                bool respectCanCollide, Func<InstanceId, bool> isEligible,
                out RbxPhysicsRaycastHit hit)
            {
                WasQueried = true;
                LastEligibility = isEligible;
                if (NextHit.HasValue)
                {
                    hit = NextHit.Value;
                    NextHit = null;
                    return true;
                }

                hit = default;
                return false;
            }

            public void SetGravity(double studsPerSecondSquared)
            {
                Gravity = studsPerSecondSquared;
            }

            public void RaiseBegan(InstanceId first, InstanceId second)
            {
                ContactBegan?.Invoke(first, second);
            }

            public void RaiseEnded(InstanceId first, InstanceId second)
            {
                ContactEnded?.Invoke(first, second);
            }
        }

        private sealed class ProductionHarness : IDisposable
        {
            public ProductionHarness()
            {
                LogLines = new List<string>();
                Binder = new InMemoryInstanceBackingBinder();
                Registry = new InstanceRegistry(
                    binder: Binder,
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                    worldId: "physics-world");
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bindings = new LuaCsRbxApiBindings(Registry, game, log: LogLines.Add);
                Port = new FakePhysicsPort();
                Bindings.WorldPhysics.AttachPort(Port);
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    Capabilities = Capabilities,
                    OneOffCapabilities = Capabilities,
                    RbxApi = Bindings
                });
            }

            public List<string> LogLines { get; }

            public InMemoryInstanceBackingBinder Binder { get; }

            public InstanceRegistry Registry { get; }

            public LuaCsRbxApiBindings Bindings { get; }

            public FakePhysicsPort Port { get; }

            public RbxWorldPhysics Physics => Bindings.WorldPhysics;

            public MemoryStore Store { get; }

            public LuaCsModStack Stack { get; }

            public RbxBasePart Part(string name)
            {
                RbxBasePart part = (RbxBasePart)Registry.Create("Part");
                part.Name = name;
                part.Parent = Registry.WorldRoot;
                return part;
            }

            /// <summary>The two parts the Lua contact counter mod creates.</summary>
            public RbxBasePart Pad { get; private set; }

            /// <summary>The other part in the contact pair.</summary>
            public RbxBasePart Ball { get; private set; }

            /// <summary>
            /// Loads a mod that counts Touched/TouchEnded on both parts of one pair, and captures the
            /// two parts it created.
            /// </summary>
            public void LoadTouchCounter(string modId)
            {
                Stack.Runtime.LoadMod(Actor(modId + "-actor"), modId, @"
                    local pad = Instance.new('Part')
                    pad.Name = 'Pad'
                    pad.Parent = workspace
                    local ball = Instance.new('Part')
                    ball.Name = 'Ball'
                    ball.Parent = workspace
                    local function bump(key)
                        store_set(key, tostring((tonumber(store_get(key)) or 0) + 1))
                    end
                    pad.Touched:Connect(function(other)
                        bump('pad_touched')
                        store_set('pad_other', other.Name)
                    end)
                    ball.Touched:Connect(function(other)
                        bump('ball_touched')
                        store_set('ball_other', other.Name)
                    end)
                    pad.TouchEnded:Connect(function() bump('pad_ended') end)
                    ball.TouchEnded:Connect(function() bump('ball_ended') end)",
                    persistToStore: false);
                Bindings.Scheduler.Advance(0d);
                Pad = (RbxBasePart)Registry.WorldRoot.FindFirstChild("Pad");
                Ball = (RbxBasePart)Registry.WorldRoot.FindFirstChild("Ball");
            }

            /// <summary>Reports one engine contact between the pair and drains the signal queue.</summary>
            public void RaiseContact(bool began)
            {
                if (began)
                {
                    Port.RaiseBegan(Pad.Id, Ball.Id);
                }
                else
                {
                    Port.RaiseEnded(Pad.Id, Ball.Id);
                }

                Bindings.Scheduler.Advance(0d);
            }

            /// <summary>The same contact reported with the two ids swapped, as both relays do.</summary>
            public void RaiseContactReversed(bool began)
            {
                if (began)
                {
                    Port.RaiseBegan(Ball.Id, Pad.Id);
                }
                else
                {
                    Port.RaiseEnded(Ball.Id, Pad.Id);
                }

                Bindings.Scheduler.Advance(0d);
            }

            public ActorContext Actor(string actorId)
            {
                return new LocalActorIdentityProvider(
                        actorId,
                        "session-" + actorId,
                        Registry.WorldId,
                        ActorGrantSet.None,
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
            }

            public void Dispose()
            {
                Bindings.Dispose();
            }
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> removed = new();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (string.Equals(key.ModId, modId, StringComparison.Ordinal))
                    {
                        removed.Add(key);
                    }
                }

                for (int index = 0; index < removed.Count; index++)
                {
                    _values.Remove(removed[index]);
                }
            }
        }

        private sealed class SilentGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }
        }
    }
}
