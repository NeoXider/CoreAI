using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.CharacterRig;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi.Terrain
{
    /// <summary>
    /// A character on block terrain, as in a block game: the real character root (the 2x2x1 part the binder
    /// builds, with its Rigidbody) driven by CoreAI's motor over real chunk colliders built on demand by the
    /// physics port. It jumps onto a one-block step while holding forward, jumps in place beside a wall and lands
    /// where it started, slides along a wall it is pushed into, walks off a ledge, walks through a one-block-wide,
    /// two-block-high tunnel, and cannot climb a two-block step with a 1.25-block jump; at ScaleTo(1.44) it
    /// stands on the floor, passes a two-block gap and not a one-block one. A twin replays the step with the old
    /// root box and its friction, and the matrix of box or capsule with or without friction shows the cause.
    /// Root positions and the numbers go to <c>artifacts/charactervoxel/</c>.
    /// </summary>
    public sealed class CharacterOnVoxelTerrainPlayModeTests
    {
        private const float FixedStep = 0.02f;
        private const float Cell = 4f;
        private const float FloorTop = 16f;
        private const double WalkSpeed = 16d;
        private const double DefaultJumpHeight = 7.2d;
        private const double BlockGameJumpHeight = 5d;

        private static readonly StringBuilder Report = new();

        private SimulationMode _savedSimulationMode;
        private GameObject _host;
        private RbxTerrain _terrain;
        private RbxTerrainPresenter _presenter;
        private InstanceGameObjectBinder _binder;
        private InstanceRegistry _registry;
        private UnityRbxPhysicsPort _port;

        [SetUp]
        public void CreateWorld()
        {
            _savedSimulationMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            if (!catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                catalog.Register(RbxTerrain.CreateDescriptor());
            }

            InstanceRegistry terrainRegistry = new(catalog);
            RbxDataModel game = DataModelBootstrap.CreateGame(terrainRegistry);
            _terrain = RbxTerrainBootstrap.EnsureTerrain(terrainRegistry, game.FindFirstChildOfClass("Workspace"),
                TerrainLimits.Desktop);
            _host = new GameObject("Character on voxels host");
            _presenter = RbxTerrainPresenter.Attach(_host, game);
            _binder = new InstanceGameObjectBinder(_host.transform, null);
            _registry = new InstanceRegistry(null, _binder, worldInstanceAdapter: new WorldInstanceAdapter(_binder));
            DataModelBootstrap.CreateGame(_registry);
            _port = new UnityRbxPhysicsPort(_binder) { TerrainHits = _presenter };
        }

        [TearDown]
        public void DestroyWorld()
        {
            try
            {
                _port?.Dispose();
                if (_host != null)
                {
                    UnityEngine.Object.Destroy(_host);
                }

                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
                string folder = Path.Combine(projectRoot, "artifacts", "charactervoxel");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "numbers.txt"), Report.ToString());
            }
            finally
            {
                Physics.simulationMode = _savedSimulationMode;
            }
        }

        // ---- the scenarios ---------------------------------------------------------------------------------------

        [UnityTest]
        public IEnumerator JumpingOntoAOneBlockStep_WhileHoldingForward_LandsOnTopWithinASecond_AndWalksOn(
            [Values(DefaultJumpHeight, BlockGameJumpHeight)] double jumpHeight)
        {
            yield return Layout(stepBlocks: 1, stepFrom: 10);
            Walker walker = Spawn(new RbxVector3(24f, 0f, 32f), 1f);
            float onTop = walker.JumpOntoStep(jumpHeight, 40f, FloorTop + Cell, out float walkedOn, "step jump " + jumpHeight);

            Assert.LessOrEqual(onTop, 1f, "on top of the one-block step within a second of the jump");
            Assert.GreaterOrEqual(walkedOn, 0.8f * WalkSpeed * 0.5f, "and walking on at least 80 % of WalkSpeed");
        }

        [UnityTest]
        public IEnumerator AtScale1_44_JumpingOntoAOneBlockStep_WithABlockGameJump_LandsOnTopWithinASecond()
        {
            yield return Layout(stepBlocks: 1, stepFrom: 10);
            Walker walker = Spawn(new RbxVector3(24f, 0f, 32f), 1.44f);
            float onTop = walker.JumpOntoStep(BlockGameJumpHeight, 40f, FloorTop + Cell, out float walkedOn,
                "scale 1.44 step jump 5");

            Assert.LessOrEqual(onTop, 1f, "a block-game sized character gets on a one-block step with a 1.25-block jump");
            Assert.GreaterOrEqual(walkedOn, 0.8f * WalkSpeed * 0.5f, "and walks on");
        }

        [UnityTest]
        public IEnumerator StandingStill_OnABlocksRim_AndOnASlope_DoesNotDrift()
        {
            yield return Layout(stepBlocks: 1, stepFrom: 10);
            Walker rim = Spawn(new RbxVector3(40.3f, 0f, 32f), 1f, FloorTop + Cell);
            rim.Settle(0.5f);
            RbxVector3 rimStart = rim.Root;
            rim.Run(2f, RbxVector3.Zero, false, "standing on the rim");
            Record("rim: drift " + F((rim.Root - rimStart).Magnitude) + " studs in 2 s, gripping " + rim.Gripping);
            Assert.AreEqual(0f, (rim.Root - rimStart).Magnitude, 0.05f, "no drift on a block's rim");
            Assert.AreEqual(FloorTop + Cell, rim.Feet, 0.05f, "still on top");
            rim.Despawn();
            yield return null;

            RbxInstance wedge = _registry.Create("Part");
            wedge.Parent = _registry.WorldRoot;
            _binder.SetShape(wedge.Id, RbxPartShape.Wedge);
            _binder.SetSize(wedge.Id, new RbxVector3(8f, 4f, 8f));
            _binder.SetPosition(wedge.Id, new RbxVector3(20f, FloorTop + 2f, 80f));
            _binder.SetAnchored(wedge.Id, true);
            Physics.SyncTransforms();
            Walker slope = Spawn(new RbxVector3(20f, 0f, 80f), 1f, FloorTop + 2.6f);
            slope.Settle(0.6f);
            RbxVector3 slopeStart = slope.Root;
            slope.Run(2f, RbxVector3.Zero, false, "standing on a 27-degree wedge");
            Record("slope: drift " + F((slope.Root - slopeStart).Magnitude) + " studs in 2 s, gripping " + slope.Gripping);
            Assert.AreEqual(0f, (slope.Root - slopeStart).Magnitude, 0.1f, "no drift on a slope");
        }

        [UnityTest]
        public IEnumerator AScriptMovingAStandingCharacterByVelocity_StillMovesIt()
        {
            yield return Layout(stepBlocks: 0, stepFrom: 10);
            Walker walker = Spawn(new RbxVector3(24f, 0f, 32f), 1f);
            walker.Settle(0.4f);
            Assert.IsTrue(walker.Gripping, "a character standing on the floor with nothing to do grips");
            float from = walker.Root.X;
            walker.PushEveryStep(8f, 0.5f);
            float moved = walker.Root.X - from;
            Record("pushed by velocity 8 studs/s for 0.5 s while standing: moved " + F(moved) + " studs (" + F(moved / 4f * 100f)
                   + " %)");

            Assert.GreaterOrEqual(moved, 0.5f * 4f, "the grip only adds friction: a script's velocity still moves it");
        }

        [UnityTest]
        public IEnumerator MoveTo_WalksToTheTarget_AndStopsThere()
        {
            yield return Layout(stepBlocks: 0, stepFrom: 10);
            Walker walker = Spawn(new RbxVector3(24f, 0f, 32f), 1f);
            walker.Settle(0.3f);
            float arrived = walker.WalkTo(new RbxVector3(36f, FloorTop, 44f), 3f);
            RbxVector3 stopped = walker.Root;
            walker.Run(1f, RbxVector3.Zero, false, "after MoveTo");
            Record("MoveTo 17 studs away: arrived in " + F(arrived) + " s, drift afterwards "
                   + F((walker.Root - stopped).Magnitude) + " studs");

            Assert.LessOrEqual(arrived, 17f / (float)WalkSpeed + 0.4f, "it walks there at WalkSpeed");
            Assert.AreEqual(0f, (walker.Root - stopped).Magnitude, 0.05f, "and stands still once there");
        }

        [UnityTest]
        public IEnumerator TheOldRootBoxWithFriction_GetsStuckOnTheStep_NegativeTwin()
        {
            yield return Layout(stepBlocks: 1, stepFrom: 10);
            Walker walker = Spawn(new RbxVector3(24f, 0f, 32f), 1f);
            walker.UseOldRootBox(0.6f);
            float onTop = walker.JumpOntoStep(DefaultJumpHeight, 40f, FloorTop + Cell, out float _, "old box, friction 0.6");

            Assert.Greater(onTop, 1f, "the old box pressed into the step with friction does not get on top in time");
        }

        [UnityTest]
        public IEnumerator TheCause_BoxOrCapsule_WithOrWithoutFriction()
        {
            yield return Layout(stepBlocks: 1, stepFrom: 10);
            foreach ((string name, bool box, float friction) in new[]
                     {
                         ("box, friction 0.6", true, 0.6f), ("box, no friction", true, 0f),
                         ("capsule, friction 0.6", false, 0.6f), ("capsule, no friction", false, 0f)
                     })
            {
                Walker walker = Spawn(new RbxVector3(24f, 0f, 32f), 1f);
                if (box)
                {
                    walker.UseOldRootBox(friction);
                }
                else if (friction > 0f)
                {
                    walker.UseCapsuleFriction(friction);
                }

                walker.JumpOntoStep(DefaultJumpHeight, 40f, FloorTop + Cell, out float _, "cause: " + name);
                walker.Despawn();
                yield return null;
            }

            Record("cause matrix recorded above");
        }

        [UnityTest]
        public IEnumerator JumpingInPlace_BesideAWall_LandsWhereItStarted()
        {
            yield return Layout(stepBlocks: 2, stepFrom: 10);
            RbxCharacterBodyShape shape = RbxCharacterBodyShape.ForR6Root(new RbxVector3(2f, 2f, 1f));
            Walker walker = Spawn(new RbxVector3(40f - shape.Radius - 0.05f, 0f, 32f), 1f);
            walker.Settle(0.3f);
            RbxVector3 start = walker.Root;
            walker.Jump(DefaultJumpHeight);
            walker.Run(1.2f, RbxVector3.Zero, false, "jump in place");
            RbxVector3 end = walker.Root;

            Assert.AreEqual(start.X, end.X, 0.05f, "lands where it started (x)");
            Assert.AreEqual(start.Z, end.Z, 0.05f, "lands where it started (z)");
            Assert.AreEqual(FloorTop, walker.Feet, 0.05f, "back on the floor");
        }

        [UnityTest]
        public IEnumerator WalkingAlongAWall_WhilePushingIntoIt_SlidesAtFourFifthsOfWalkSpeed()
        {
            yield return Layout(stepBlocks: 2, stepFrom: 10);
            RbxCharacterBodyShape shape = RbxCharacterBodyShape.ForR6Root(new RbxVector3(2f, 2f, 1f));
            Walker walker = Spawn(new RbxVector3(40f - shape.Radius - 0.05f, 0f, 6f), 1f);
            walker.Settle(0.3f);
            float angle = 20f * Mathf.Deg2Rad;
            RbxVector3 into = new(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
            walker.Run(0.4f, into, false, "along the wall, settling");
            float from = walker.Root.Z;
            walker.Run(1f, into, false, "along the wall");
            float speed = walker.Root.Z - from;
            Record("along the wall pushed 20 degrees into it: " + F(speed) + " studs/s along it (WalkSpeed " + WalkSpeed + ")");

            Assert.GreaterOrEqual(speed, 0.8f * (float)WalkSpeed, "no sticking: at least 80 % of WalkSpeed along the wall");
        }

        [UnityTest]
        public IEnumerator WalkingOffAOneBlockLedge_DropsToTheFloorBelow_AndWalksOn()
        {
            yield return Layout(stepBlocks: 0, stepFrom: 10, ledge: true);
            Walker walker = Spawn(new RbxVector3(30f, 0f, 32f), 1f, FloorTop + Cell);
            walker.Settle(0.3f);
            walker.Run(1.5f, new RbxVector3(1f, 0f, 0f), false, "off the ledge");

            Assert.AreEqual(FloorTop, walker.Feet, 0.05f, "down on the lower floor");
            Assert.Greater(walker.Root.X, 30f + 0.8f * (float)WalkSpeed * 1.5f - 2f, "it never hung on the edge");
        }

        [UnityTest]
        public IEnumerator AOneBlockWideTwoBlockHighTunnel_IsWalkedThrough_AndStoodIn()
        {
            yield return Layout(stepBlocks: 0, stepFrom: 10, tunnelHeightBlocks: 2);
            Walker walker = Spawn(new RbxVector3(30f, 0f, 30f), 1f);
            walker.Settle(0.3f);
            walker.Run(1.2f, new RbxVector3(1f, 0f, 0f), false, "into the tunnel");
            RbxVector3 inside = walker.Root;
            walker.Run(1f, RbxVector3.Zero, false, "standing in the tunnel");
            RbxVector3 still = walker.Root;
            walker.Run(2f, new RbxVector3(1f, 0f, 0f), false, "through the tunnel");

            Assert.Greater(inside.X, 42f, "it went in");
            Assert.AreEqual(inside.X, still.X, 0.05f, "standing still inside, nothing pushes it out");
            Assert.AreEqual(FloorTop, walker.Feet, 0.1f, "on the tunnel floor");
            Assert.Greater(walker.Root.X, 70f, "and out through the far end");
        }

        [UnityTest]
        public IEnumerator ATwoBlockStep_IsNotClimbed_WithABlockGameJump_NegativeTwin()
        {
            yield return Layout(stepBlocks: 2, stepFrom: 10);
            Walker walker = Spawn(new RbxVector3(24f, 0f, 32f), 1f);
            float onTop = walker.JumpOntoStep(BlockGameJumpHeight, 40f, FloorTop + 2f * Cell, out float _,
                "two-block step, jump 5");

            Assert.IsTrue(float.IsPositiveInfinity(onTop), "a 1.25-block jump never gets on a two-block step");
        }

        [UnityTest]
        public IEnumerator AtScale1_44_TheCharacterStandsOnTheFloor_PassesTwoBlocks_AndNotOne()
        {
            yield return Layout(stepBlocks: 0, stepFrom: 10, tunnelHeightBlocks: 2);
            Walker tall = Spawn(new RbxVector3(30f, 0f, 30f), 1.44f);
            tall.Settle(0.5f);
            Assert.AreEqual(FloorTop, tall.Feet, 0.05f, "a scaled character stands on the floor");
            Record("scale 1.44: capsule " + F(tall.Shape.Height) + " studs tall, " + F(tall.Shape.Radius * 2f)
                   + " wide, eye " + F(tall.Shape.BottomBelowRoot + tall.Shape.EyeAboveRoot) + " above the feet");
            tall.Run(3f, new RbxVector3(1f, 0f, 0f), false, "scale 1.44 through a two-block tunnel");
            Assert.Greater(tall.Root.X, 70f, "it walks through a two-block-high gap");
            tall.Despawn();
            yield return null;

            yield return Layout(stepBlocks: 0, stepFrom: 10, tunnelHeightBlocks: 1);
            Walker blocked = Spawn(new RbxVector3(30f, 0f, 30f), 1.44f);
            blocked.Settle(0.5f);
            blocked.Run(2f, new RbxVector3(1f, 0f, 0f), false, "scale 1.44 into a one-block tunnel");
            Assert.Less(blocked.Root.X, 40f, "a one-block-high gap stops it");
        }

        // ---- character against character ----------------------------------------------------------------------------

        [UnityTest]
        public IEnumerator AWalker_StopsAgainstAStandingCharacter_AndNeverPushesIt_FromAnySide(
            [Values(0f, 30f, -45f)] float approachDegrees)
        {
            yield return Layout(stepBlocks: 0, stepFrom: 30);
            Walker standing = Spawn(new RbxVector3(60f, 0f, 60f), 1f);
            float angle = approachDegrees * Mathf.Deg2Rad;
            RbxVector3 toward = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Walker walker = Spawn(new RbxVector3(60f - toward.X * 10f, 0f, 60f - toward.Z * 10f), 1f);
            Settle(0.3f, walker, standing);
            RbxVector3 before = standing.Root;
            RunBoth(1.5f, walker, toward, standing, RbxVector3.Zero);
            float pushed = (standing.Root - before).Magnitude;
            float gap = (walker.Root - standing.Root).Magnitude;
            Record("walking into a standing character at " + approachDegrees + " degrees: it moved " + F(pushed)
                   + " studs, centres " + F(gap) + " studs apart");

            Assert.Less(pushed, 0.1f, "the standing character is not pushed");
            Assert.GreaterOrEqual(gap, 1.5f, "the walker stops against it (two capsule radii, 1.6 studs)");
        }

        [UnityTest]
        public IEnumerator AStandingCharacter_IsNotPushedOffALedge_NorIntoAWall()
        {
            yield return Layout(stepBlocks: 0, stepFrom: 10, ledge: true);
            Walker atEdge = Spawn(new RbxVector3(39.1f, 0f, 32f), 1f, FloorTop + Cell);
            Walker walker = Spawn(new RbxVector3(30f, 0f, 32f), 1f, FloorTop + Cell);
            Settle(0.3f, walker, atEdge);
            RunBoth(1.5f, walker, new RbxVector3(1f, 0f, 0f), atEdge, RbxVector3.Zero);
            Record("at a ledge: the standing character's feet " + F(atEdge.Feet) + ", x " + F(atEdge.Root.X));
            Assert.AreEqual(FloorTop + Cell, atEdge.Feet, 0.05f, "not pushed off the ledge");
            atEdge.Despawn();
            walker.Despawn();
            yield return null;

            yield return Layout(stepBlocks: 2, stepFrom: 10);
            Walker atWall = Spawn(new RbxVector3(39.15f, 0f, 32f), 1f);
            Walker pusher = Spawn(new RbxVector3(30f, 0f, 32f), 1f);
            Settle(0.3f, pusher, atWall);
            float before = atWall.Root.X;
            RunBoth(1.5f, pusher, new RbxVector3(1f, 0f, 0f), atWall, RbxVector3.Zero);
            Record("against a wall: the standing character moved " + F(atWall.Root.X - before) + " studs toward it");
            Assert.LessOrEqual(atWall.Root.X, 40f - 0.75f, "not pushed into the block");
        }

        [UnityTest]
        public IEnumerator AMoveToWalker_StopsAndWaits_ThenGoesOnWhenTheWayClears()
        {
            yield return Layout(stepBlocks: 0, stepFrom: 30);
            Walker blocker = Spawn(new RbxVector3(40f, 0f, 32f), 1f);
            Walker walker = Spawn(new RbxVector3(30f, 0f, 32f), 1f);
            Settle(0.3f, walker, blocker);
            RbxVector3 target = new(52f, FloorTop, 32f);
            walker.Motor.MoveTo(target);
            RbxVector3 blockerBefore = blocker.Root;
            RunBoth(1.5f, walker, RbxVector3.Zero, blocker, RbxVector3.Zero);
            float waitedAt = walker.Root.X;
            Assert.Less(waitedAt, 39f, "it waits in front of the character in its way");
            Assert.Less((blocker.Root - blockerBefore).Magnitude, 0.1f, "and does not push it");

            blocker.Despawn();
            yield return null;
            RunBoth(1.5f, walker, RbxVector3.Zero, null, RbxVector3.Zero);
            Record("MoveTo past a character: waited at x " + F(waitedAt) + ", then reached x " + F(walker.Root.X));
            Assert.Greater(walker.Root.X, 50f, "the way cleared: it walks on to the target");
        }

        [UnityTest]
        public IEnumerator AVillagersMoveTo_BlockedByThePlayer_EndsNotReachedAfterASecond()
        {
            yield return Layout(stepBlocks: 0, stepFrom: 30);
            Walker player = Spawn(new RbxVector3(40f, 0f, 32f), 1f);
            Walker villager = Spawn(new RbxVector3(30f, 0f, 32f), 1f);
            Settle(0.3f, villager, player);
            RbxHumanoid humanoid = (RbxHumanoid)_registry.Create("Humanoid");
            humanoid.AttachHost(null, villager.Motor, null);
            humanoid.MoveTo(new RbxVector3(52f, FloorTop, 32f));
            float ended = -1f;
            for (int step = 0; step < 200 && ended < 0f; step++)
            {
                humanoid.Advance(FixedStep);
                RunBoth(FixedStep, villager, RbxVector3.Zero, player, RbxVector3.Zero);
                if (!villager.Motor.HasMoveTarget)
                {
                    ended = (step + 1) * FixedStep;
                }
            }

            Record("a villager's MoveTo blocked by the player ends (MoveToFinished false) after " + F(ended)
                   + " s, waiting at x " + F(villager.Root.X));
            Assert.Greater(ended, 0f, "the blocked walk ends");
            Assert.Less(ended, 2.5f, "after about a second of waiting, not eight");
        }

        [UnityTest]
        public IEnumerator InAOneBlockCorridor_ThePlayerWalksIntoAVillager_AndBacksOut()
        {
            yield return Layout(stepBlocks: 0, stepFrom: 10, tunnelHeightBlocks: 2);
            Walker villager = Spawn(new RbxVector3(52f, 0f, 30f), 1f);
            Walker player = Spawn(new RbxVector3(44f, 0f, 30f), 1f);
            Settle(0.3f, player, villager);
            RunBoth(1f, player, new RbxVector3(1f, 0f, 0f), villager, RbxVector3.Zero);
            float stopped = player.Root.X;
            RunBoth(0.5f, player, new RbxVector3(-1f, 0f, 0f), villager, RbxVector3.Zero);
            Record("in a one-block corridor: stopped at x " + F(stopped) + " behind the villager, backed out to x "
                   + F(player.Root.X));

            Assert.Less(stopped, 52f - 1.5f, "it stops behind the villager");
            Assert.Less(player.Root.X, stopped - 5f, "and walks back out");
        }

        [UnityTest]
        public IEnumerator WithNoOneInTheWay_AWalkerWalksFreely_NegativeTwin()
        {
            yield return Layout(stepBlocks: 0, stepFrom: 30);
            Walker walker = Spawn(new RbxVector3(30f, 0f, 32f), 1f);
            Settle(0.3f, walker, null);
            RunBoth(1f, walker, new RbxVector3(1f, 0f, 0f), null, RbxVector3.Zero);

            Assert.Greater(walker.Root.X, 30f + 0.8f * (float)WalkSpeed, "a free walk is not slowed");
        }

        private void Settle(float seconds, Walker first, Walker second)
        {
            RunBoth(seconds, first, RbxVector3.Zero, second, RbxVector3.Zero);
        }

        /// <summary>Steps both characters together, as the world host steps every character in one fixed step.</summary>
        private void RunBoth(float seconds, Walker first, RbxVector3 firstDirection, Walker second,
            RbxVector3 secondDirection)
        {
            int steps = Mathf.CeilToInt(seconds / FixedStep);
            for (int step = 0; step < steps; step++)
            {
                _port.ApplyGravity();
                first.Motor.Move(firstDirection);
                first.Motor.Step(FixedStep);
                if (second != null)
                {
                    second.Motor.Move(secondDirection);
                    second.Motor.Step(FixedStep);
                }

                Physics.Simulate(FixedStep);
            }
        }

        // ---- the world --------------------------------------------------------------------------------------------

        /// <summary>
        /// A Grass floor with its top at 16 studs over x and z in 0..160, and, from cell <paramref name="stepFrom"/>
        /// on along x, either a step <paramref name="stepBlocks"/> blocks higher, a floor one block higher before x 40
        /// (<paramref name="ledge"/>), or a rock mass with a one-cell-wide tunnel at z 28..32.
        /// </summary>
        private IEnumerator Layout(int stepBlocks, int stepFrom, bool ledge = false, int tunnelHeightBlocks = 0)
        {
            byte grass = Slot("Grass");
            byte rock = Slot("Rock");
            Fill(0, -2, 0, 40, 4, 40, grass);
            Fill(0, 4, 0, 40, 10, 40, TerrainMaterials.Air);
            if (ledge)
            {
                Fill(0, 4, 0, 10, 5, 40, grass);
            }

            if (stepBlocks > 0)
            {
                Fill(stepFrom, 4, 0, 40, 4 + stepBlocks, 40, grass);
            }

            if (tunnelHeightBlocks > 0)
            {
                Fill(stepFrom, 4, 0, 20, 8, 40, rock);
                Fill(stepFrom, 4, 7, 20, 4 + tunnelHeightBlocks, 8, TerrainMaterials.Air);
            }

            yield return null;
            yield return null;
        }

        private void Fill(int x0, int y0, int z0, int x1, int y1, int z1, byte material)
        {
            RbxVector3 centre = new((x0 + x1) * 0.5f * Cell, (y0 + y1) * 0.5f * Cell, (z0 + z1) * 0.5f * Cell);
            RbxVector3 size = new((x1 - x0) * Cell, (y1 - y0) * Cell, (z1 - z0) * Cell);
            _terrain.FillBlock(RbxCFrame.FromPosition(centre), size, material);
        }

        private static byte Slot(string name)
        {
            TerrainMaterials.TryGetSlot(name, out byte slot);
            return slot;
        }

        private Walker Spawn(RbxVector3 at, float scale, float floor = FloorTop)
        {
            RbxVector3 size = new RbxVector3(2f, 2f, 1f) * scale;
            RbxInstance part = _registry.Create("Part");
            part.Parent = _registry.WorldRoot;
            _binder.SetSize(part.Id, size);
            _binder.SetPosition(part.Id, new RbxVector3(at.X, floor + size.Y * 0.5f + 0.05f, at.Z));
            _binder.SetAnchored(part.Id, false);
            Physics.SyncTransforms();
            Assert.IsTrue(_binder.TryGetBoundObject(part.Id, out GameObject body), "the root part is bound");
            Rigidbody rigidbody = body.GetComponent<Rigidbody>();
            UnityRbxCharacterMotor motor = new(rigidbody,
                worldGravityMetresPerSecondSquared: () => _port.GravityMetresPerSecondSquared.magnitude);
            motor.SetWalkSpeed(WalkSpeed);
            return new Walker(this, part, rigidbody, motor, RbxCharacterBodyShape.ForR6Root(size));
        }

        private static void Record(string line)
        {
            Report.AppendLine(line);
            TestContext.Out.WriteLine(line);
        }

        private static string F(float value)
        {
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>One character root, its motor, and the fixed-step loop the world host runs.</summary>
        private sealed class Walker
        {
            private readonly CharacterOnVoxelTerrainPlayModeTests _world;
            private readonly RbxInstance _part;
            private readonly Rigidbody _body;
            private readonly UnityRbxCharacterMotor _motor;
            private float _time;
            private double _jumpHeight = DefaultJumpHeight;

            public Walker(CharacterOnVoxelTerrainPlayModeTests world, RbxInstance part, Rigidbody body,
                UnityRbxCharacterMotor motor, RbxCharacterBodyShape shape)
            {
                _world = world;
                _part = part;
                _body = body;
                _motor = motor;
                Shape = shape;
            }

            public RbxCharacterBodyShape Shape { get; }

            public UnityRbxCharacterMotor Motor => _motor;

            public RbxVector3 Root => RbxSpace.FromUnity(_body.position);

            public float Feet => Root.Y - Shape.BottomBelowRoot;

            /// <summary>The root as it was before the fix: its box collides, with <paramref name="friction"/>.</summary>
            public void UseOldRootBox(float friction)
            {
                Assert.IsNotNull(_motor.BodyShape, "the motor gave the root its capsule");
                _motor.BodyShape.enabled = false;
                BoxCollider box = _body.GetComponent<BoxCollider>();
                box.enabled = true;
                box.sharedMaterial = Material(friction);
            }

            /// <summary>The capsule with <paramref name="friction"/> instead of none.</summary>
            public void UseCapsuleFriction(float friction)
            {
                _motor.BodyShape.sharedMaterial = Material(friction);
            }

            public void Settle(float seconds)
            {
                Run(seconds, RbxVector3.Zero, false, null);
            }

            public void Jump(double jumpHeight)
            {
                Assert.IsTrue(_motor.TryJump(50d, jumpHeight, false), "a grounded character jumps");
            }

            /// <summary>
            /// Walks +X into the step until blocked, then holds forward and jump; returns the seconds from the first
            /// jump until the feet are on the step's top past its face, or infinity after three seconds.
            /// </summary>
            public float JumpOntoStep(double jumpHeight, float faceX, float top, out float walkedOnPerSecond,
                string name)
            {
                walkedOnPerSecond = 0f;
                _jumpHeight = jumpHeight;
                RbxVector3 forward = new(1f, 0f, 0f);
                float lastX = Root.X;
                int still = 0;
                for (int step = 0; step < 150 && still < 10; step++)
                {
                    Tick(forward, false);
                    still = Root.X - lastX < 0.01f ? still + 1 : 0;
                    lastX = Root.X;
                }

                Record(name + ": blocked at x " + F(Root.X) + ", feet " + F(Feet));
                float jumpedAt = _time;
                float onTop = float.PositiveInfinity;
                float maxFeet = Feet;
                StringBuilder trace = new();
                for (int step = 0; step < 150; step++)
                {
                    Tick(forward, true);
                    maxFeet = Mathf.Max(maxFeet, Feet);
                    if (step % 3 == 0)
                    {
                        trace.Append(F(_time - jumpedAt)).Append(':').Append(F(Root.X)).Append('/').Append(F(Feet)).Append(' ');
                    }

                    if (float.IsPositiveInfinity(onTop) && Feet >= top - 0.05f && Root.X - Shape.Radius > faceX)
                    {
                        onTop = _time - jumpedAt;
                    }
                }

                if (!float.IsPositiveInfinity(onTop))
                {
                    float from = Root.X;
                    Run(0.5f, forward, false, null);
                    walkedOnPerSecond = Root.X - from;
                }

                Record(name + ": on top after " + (float.IsPositiveInfinity(onTop) ? "never (3 s)" : F(onTop) + " s")
                       + ", highest feet " + F(maxFeet) + " (top " + F(top) + "), final x " + F(Root.X)
                       + ", walked on " + F(walkedOnPerSecond * 2f) + " studs/s");
                Record("  t:x/feet " + trace);
                return onTop;
            }

            public void Run(float seconds, RbxVector3 direction, bool jump, string name)
            {
                int steps = Mathf.CeilToInt(seconds / FixedStep);
                StringBuilder trace = name != null ? new StringBuilder() : null;
                for (int step = 0; step < steps; step++)
                {
                    Tick(direction, jump);
                    if (trace != null && step % 10 == 0)
                    {
                        trace.Append(F(Root.X)).Append(',').Append(F(Feet)).Append(',').Append(F(Root.Z)).Append(' ');
                    }
                }

                if (trace != null)
                {
                    Record(name + ": " + trace);
                }
            }

            public void Despawn()
            {
                _part.Destroy();
            }

            public bool Gripping => _motor.IsGripping;

            /// <summary>Writes an x velocity on the standing body every step, as a script moving it would.</summary>
            public void PushEveryStep(float studsPerSecond, float seconds)
            {
                int steps = Mathf.CeilToInt(seconds / FixedStep);
                for (int step = 0; step < steps; step++)
                {
                    _world._port.ApplyGravity();
                    _motor.Move(RbxVector3.Zero);
                    _motor.Step(FixedStep);
                    Vector3 velocity = _body.linearVelocity;
                    velocity.x = RbxSpace.LengthToUnity(studsPerSecond);
                    _body.linearVelocity = velocity;
                    Physics.Simulate(FixedStep);
                    _time += FixedStep;
                }
            }

            /// <summary>
            /// The motor's MoveTo, ended within one stud of the target as the Humanoid ends it; returns the seconds
            /// it took, or infinity.
            /// </summary>
            public float WalkTo(RbxVector3 target, float limit)
            {
                _motor.MoveTo(target);
                float started = _time;
                int steps = Mathf.CeilToInt(limit / FixedStep);
                for (int step = 0; step < steps; step++)
                {
                    _world._port.ApplyGravity();
                    _motor.Step(FixedStep);
                    Physics.Simulate(FixedStep);
                    _time += FixedStep;
                    RbxVector3 flat = new(Root.X - target.X, 0f, Root.Z - target.Z);
                    if (flat.Magnitude < 1f)
                    {
                        _motor.MoveTo(null);
                        return _time - started;
                    }
                }

                _motor.MoveTo(null);
                return float.PositiveInfinity;
            }

            private void Tick(RbxVector3 direction, bool jump)
            {
                _world._port.ApplyGravity();
                _motor.Move(direction);
                if (jump && _motor.IsGrounded)
                {
                    _motor.TryJump(50d, _jumpHeight, false);
                }

                _motor.Step(FixedStep);
                Physics.Simulate(FixedStep);
                _time += FixedStep;
            }

            private static PhysicsMaterial Material(float friction)
            {
                return new PhysicsMaterial("test friction " + friction)
                {
                    dynamicFriction = friction,
                    staticFriction = friction,
                    frictionCombine = PhysicsMaterialCombine.Average
                };
            }
        }
    }
}
