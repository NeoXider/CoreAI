#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// G7 — comprehensive integration. G1-G6 each isolate one skill (spatial build, Lua logic, reasoning,
    /// simulated playthrough, strict instructions, free-form visual); a model can specialize on one and
    /// still clear the bar, which is why even a 4B reference model scores 90+ across the suite. G7 forces
    /// ALL of those skills to work together in a single coherent task AND cross-checks that the model's own
    /// spatial choices (recorded from its <c>world_command</c> spawns) stay CONSISTENT with the logic it
    /// wrote for <c>execute_lua</c> — a model can get spawning right and logic right in isolation while
    /// still being inconsistent between the two (e.g. writing a distance check that assumes a different
    /// key position than the one it actually spawned). That cross-consistency check is the differentiator:
    /// it cannot be gamed by memorized canned I/O the way an isolated unit check can.
    /// Runs once regardless of suite reps (<see cref="GameBenchmarkScenario.RepsOverride"/>) — a heavy,
    /// one-off comprehensive scenario, same convention as the G6 visual hero.
    /// </summary>
    internal static class GameIntegrationScenariosG7
    {
        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new KeyPuzzle(),
                new ExtremeDungeon()
            };
        }

        private abstract class G7Scenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G7";

            public override int Difficulty => 9;

            // No scene shot for G7: the Player/Gate/Key puzzle photographs as a cluttered composite of
            // overlapping primitives and floating world-space labels that is unreadable and adds nothing
            // to the score (user-reported). Grading reads world state + Lua consistency, not the image.
            // G6 stays the only hero image; scoring is unaffected.
            public override bool CaptureScene => false;
            public override int? RepsOverride => 1;
            public override int TokenBudget => 2500;
            public override double TimeBudgetMs => 40000;
            public override float TimeoutSeconds => 300f;

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                return new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.WorldTool())
                    .WithTool(env.LuaTool())
                    .WithMaxOutputTokens(MaxOutputTokens)
                    .WithMode(AgentMode.ToolsOnly)
                    .BuildDetached();
            }

            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run)
            {
                g.AddToolErrorCheckpoint("clean_tools", "no failed tool calls or invalid world commands", 5,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0 && env.Lua.FailedExecutions == 0,
                    scoresFailedToolCalls: true, scoresInvalidCommands: true);
            }

            protected static List<RecordedWorldCommand> SpawnCommands(BenchmarkEnvironment env)
            {
                List<RecordedWorldCommand> result = new();
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (string.Equals(c.Action, "spawn", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(c);
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// Spawn Player/Gate/Key in specific zones and exact order, then a single Lua distance-threshold
        /// slot — graded not just on each piece in isolation, but on whether the slot behaves correctly
        /// when fed the model's OWN recorded Key position.
        /// </summary>
        private sealed class KeyPuzzle : G7Scenario
        {
            public override string Id => "g7_key_puzzle";
            public override string Name => "Integration puzzle";

            public override string WhatItChecks =>
                "Comprehensive: exact spawn order/zones + a Lua logic slot that must stay consistent with " +
                "the model's own spawned Key position — not just correct in isolation.";

            // logic_define throws "slot not declared by the game" for any name the game didn't pre-declare
            // (LuaLogicSlots.Define) — without this, the model's logic_define('key_found', ...) call would
            // always fail, exactly like G4's playthrough scenarios declare their slots up front.
            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("key_found");
            }

            public override string Goal =>
                "This is a comprehensive test combining spatial building and game logic. Follow every step " +
                "exactly.\n\n" +
                "Step 1 — using world_command action='spawn', create EXACTLY these 3 objects, IN THIS ORDER, " +
                "with these exact targetNames:\n" +
                "1. 'Player' — prefabKey='capsule', y=1, x between -6 and 6, z between -8 and -4.\n" +
                "2. 'Gate' — prefabKey='cube', y=1, x between -6 and 6, z between 4 and 8.\n" +
                "3. 'Key' — prefabKey='sphere', y=1, x between -6 and 6, z between -3 and 3.\n" +
                "Do not spawn any other objects.\n\n" +
                "Step 2 — using execute_lua, define EXACTLY one logic slot named 'key_found' via " +
                "logic_define('key_found', function(player_x, player_z, key_x, key_z) ... end). It must " +
                "return true when the straight-line distance between (player_x, player_z) and " +
                "(key_x, key_z) in the x/z plane is 2.0 units or less, and false otherwise — use " +
                "math.sqrt((player_x-key_x)^2 + (player_z-key_z)^2) <= 2.0. Do not define any other logic " +
                "slots. " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                // WHY: an object moved after its spawn ends up where the final scene shows it, and that is
                // where the integration samples below must probe.
                RecordedScene scene = RecordedScene.From(env.World.Commands);
                List<RecordedWorldCommand> spawns = SpawnCommands(env);
                bool hasPlayer = scene.TryGet("Player", out RecordedSceneObject player);
                bool hasGate = scene.TryGet("Gate", out RecordedSceneObject gate);
                bool hasKey = scene.TryGet("Key", out RecordedSceneObject key);
                bool spawnedAllThree = hasPlayer && hasGate && hasKey;
                g.Add("spawned_all_three", "spawned Player, Gate, and Key", 15, spawnedAllThree, true,
                    dimension: BenchmarkDimension.TaskCompletion);

                // Exactly 3 (not >=3) and in order: a 4th spawn after Key must fail this, matching the
                // "do not spawn any other objects" instruction.
                bool exactOrder = spawns.Count == 3
                                  && string.Equals(spawns[0].TargetName?.Trim(), "Player",
                                      StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(spawns[1].TargetName?.Trim(), "Gate",
                                      StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(spawns[2].TargetName?.Trim(), "Key",
                                      StringComparison.OrdinalIgnoreCase);
                g.Constraint("exact_count_and_order", "spawned exactly 3 objects, in order Player, Gate, Key",
                    15, !exactOrder, 5,
                    Math.Max(0, spawns.Count - 3));

                // WHY: the brief fixes y=1 for all three objects and only x/z used to be graded.
                bool zonesOk = spawnedAllThree
                               && InRange(player.X, -6f, 6f) && InRange(player.Z, -8f, -4f)
                               && InRange(gate.X, -6f, 6f) && InRange(gate.Z, 4f, 8f)
                               && InRange(key.X, -6f, 6f) && InRange(key.Z, -3f, 3f)
                               && AtHeight(player) && AtHeight(gate) && AtHeight(key);
                g.Add("zone_compliance", "each object placed within its required x/z zone at y=1", 15, zonesOk,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: spawnedAllThree ? null : "missing object(s), zones not checked");

                bool shapesOk = spawnedAllThree
                                && IsPrefab(player, "capsule") && IsPrefab(gate, "cube") && IsPrefab(key, "sphere");
                g.Add("shape_compliance", "used the specified prefabKey for each object (capsule/cube/sphere)",
                    10, shapesOk, dimension: BenchmarkDimension.ToolCorrectness,
                    detail: spawnedAllThree ? null : "missing object(s), shapes not checked");

                bool logicInstalled = env.Lua.LogicSlots.IsOverridden("key_found");
                g.Add("logic_installed", "key_found logic slot installed", 10, logicInstalled, true,
                    dimension: BenchmarkDimension.IntentSequence);

                // WHY: with x-only offsets a slot that compared |dx| alone, or a Manhattan/Chebyshev
                // distance, passed every check; z-axis and diagonal samples tell them apart.
                bool unitOk = logicInstalled
                              && Bool(env, "key_found", true, 0.0, 0.0, 0.0, 0.0)
                              && Bool(env, "key_found", true, 2.0, 0.0, 0.0, 0.0)
                              && Bool(env, "key_found", false, 3.0, 0.0, 0.0, 0.0)
                              && Bool(env, "key_found", true, 0.0, 0.0, 0.0, 2.0)
                              && Bool(env, "key_found", true, 0.0, 0.0, 1.4, 1.4)
                              && Bool(env, "key_found", false, 0.0, 0.0, 1.5, 1.5)
                              && Bool(env, "key_found", false, -3.0, -4.0, -1.5, -5.5);
                g.Add("unit_correctness",
                    "key_found is true within 2.0 in x/z (axis, boundary and diagonal samples), false beyond", 15,
                    unitOk, dimension: BenchmarkDimension.TaskCompletion);

                // The differentiator: feed the model's OWN recorded Key position back into its OWN logic
                // slot. A model that hardcoded/assumed a different key position, or got the distance
                // formula subtly wrong, fails here even if the isolated unit checks above happen to pass.
                bool integrationOk = false;
                string integrationDetail = "Key not spawned";
                if (hasKey && logicInstalled)
                {
                    double kx = key.X, kz = key.Z;
                    bool atKey = Bool(env, "key_found", true, kx, kz, kx, kz);
                    bool nearKey = Bool(env, "key_found", true, kx + 1.5, kz, kx, kz);
                    bool farFromKey = Bool(env, "key_found", false, kx + 2.5, kz, kx, kz);
                    bool edgeOnZ = Bool(env, "key_found", true, kx, kz - 2.0, kx, kz);
                    bool farOnZ = Bool(env, "key_found", false, kx, kz + 2.5, kx, kz);
                    bool nearDiagonal = Bool(env, "key_found", true, kx + 1.4, kz + 1.4, kx, kz);
                    bool farDiagonal = Bool(env, "key_found", false, kx - 1.5, kz + 1.5, kx, kz);
                    integrationOk = atKey && nearKey && farFromKey && edgeOnZ && farOnZ && nearDiagonal &&
                                    farDiagonal;
                    integrationDetail = integrationOk
                        ? null
                        : string.Format(CultureInfo.InvariantCulture,
                            "sample ok: atKey={0}, near(+1.5x)={1}, far(+2.5x)={2}, edge(-2z)={3}, far(+2.5z)={4}, " +
                            "diag(1.4)={5}, diag(1.5)={6}, key=({7:0.##},{8:0.##})", atKey, nearKey, farFromKey,
                            edgeOnZ, farOnZ, nearDiagonal, farDiagonal, kx, kz);
                }

                g.Add("integration_consistency",
                    "key_found stays correct when fed the model's own spawned Key position", 30, integrationOk,
                    dimension: BenchmarkDimension.Reasoning, detail: integrationDetail);

                if (integrationOk && exactOrder && zonesOk && shapesOk)
                {
                    g.Bonus = 8;
                }

                return g;
            }

            private static bool InRange(double value, double min, double max)
            {
                return value >= min && value <= max;
            }

            private static bool AtHeight(RecordedSceneObject o)
            {
                return Math.Abs(o.Y - 1.0) <= ExpectedSceneObject.PositionTolerance;
            }

            private static bool IsPrefab(RecordedSceneObject o, string expectedPrefabKey)
            {
                return string.Equals(o.Prefab, expectedPrefabKey, StringComparison.OrdinalIgnoreCase);
            }

            private static bool Bool(BenchmarkEnvironment env, string slot, bool expected, params object[] a)
            {
                return env.Lua.TryBool(slot, out bool v, a) && v == expected;
            }
        }

        /// <summary>
        /// Extreme tier: a 3x3 locked-door dungeon whose key positions the model must derive from reachability
        /// rules, plus five logic slots (room lookup, lock rule, static reachability, shortest-path next step,
        /// and a key-collecting shortest escape) that must read the live world. The logic is graded on the
        /// model's own dungeon, on a reference copy of the specification, and again after the grader moves
        /// keys, the Exit and the Player and removes a door in that copy.
        /// </summary>
        private sealed class ExtremeDungeon : G7Scenario
        {
            private const string RoomOfSlot = "room_of";
            private const string CanOpenSlot = "can_open";
            private const string ReachableSlot = "reachable";
            private const string NextStepSlot = "next_step";
            private const string MinDoorsSlot = "min_doors_to_exit";
            private const string Letters = "ABCDEFGHI";

            /// <summary>The specified doors: two room letters and the lock colour ("" for an open door).</summary>
            private static readonly string[,] DoorSpec =
            {
                { "A", "B", "" }, { "B", "C", "Red" }, { "A", "D", "Blue" }, { "B", "E", "" }, { "D", "E", "" },
                { "E", "F", "Red" }, { "C", "F", "" }, { "D", "G", "Blue" }, { "F", "I", "" },
                { "G", "H", "Gold" }, { "H", "I", "Gold" }
            };

            private static readonly List<ExpectedSceneObject> Canonical = BuildCanonical();

            private static readonly string[] SlotNames =
                { RoomOfSlot, CanOpenSlot, ReachableSlot, NextStepSlot, MinDoorsSlot };

            public override string Id => "g7_extreme_dungeon";
            public override string Name => "Extreme: Keyed dungeon";
            public override bool IsExtreme => true;
            public override int Difficulty => 5;
            public override float TimeoutSeconds => 480f;
            public override int TokenBudget => 16000;
            public override double TimeBudgetMs => 240000;

            // WHY: 25 objects and five interlocking slots need more than the default 40 round trips when a
            // model builds and verifies step by step.
            public override int? MaxToolCallRoundtripsOverride => 80;

            public override string WhatItChecks =>
                "Extreme: a 25-object keyed dungeon with rule-derived key rooms and five world-reading slots " +
                "(room lookup, locks, reachability, next step, key-collecting escape) re-checked after keys, " +
                "Exit and Player move and a door disappears.";

            public override IReadOnlyList<string> ExpectedSceneObjectNames
            {
                get
                {
                    List<string> names = new();
                    foreach (ExpectedSceneObject e in Canonical)
                    {
                        names.Add(e.Name);
                    }

                    return names;
                }
            }

            public override void Prepare(BenchmarkEnvironment env)
            {
                foreach (string slot in SlotNames)
                {
                    env.Lua.DeclareSlot(slot);
                }
            }

            public override string Goal =>
                "EXTREME TASK: build a locked-door dungeon with world_command and install Lua logic that agrees " +
                "with the world. Every rule is exact; follow it literally.\n\n" +
                "WORLD RULES. Units are metres; +x is east, +z is north, +y is up. Create every object with " +
                "world_command spawn or spawn_batch at scene root (no parent or stringValue, no helper/root " +
                "objects), without rotation, each name exactly once, nothing else. In a spawn_batch give every " +
                "item its own prefabKey, x/y/z and scale (scale or scaleX/scaleY/scaleZ). Tolerances: position " +
                "0.05 m, scale 0.02.\n" +
                "ROOMS: a 3 x 3 grid, column c = 0, 1, 2 from west to east and row r = 0, 1, 2 from south to " +
                "north. Letters: row 0 is A, B, C (c = 0, 1, 2), row 1 is D, E, F, row 2 is G, H, I. Room L is " +
                "the cube 'Room_L' (e.g. 'Room_A') centred at x = 10*c - 10, y = -0.25, z = 10*r + 5 with " +
                "scale 8/0.5/8.\n" +
                "DOORS: rooms connect only through these doors: A-B open, B-C Red, A-D Blue, B-E open, D-E " +
                "open, E-F Red, C-F open, D-G Blue, F-I open, G-H Gold, H-I Gold (no door between E and H). A " +
                "door between rooms P and Q (letters in alphabetical order) is the cube 'Door_P_Q' when open " +
                "and 'Door_P_Q_Colour' when locked (e.g. 'Door_A_B', 'Door_B_C_Red'), centred at the midpoint " +
                "(x, z) of its two room centres, y = 1.5, scale 2/3/3 between east-west neighbours and 3/3/2 " +
                "between north-south neighbours.\n" +
                "LOCKS: an open door can always be passed. A Red door needs the Red key, a Blue door the Blue " +
                "key; the Gold key opens EVERY locked door (Red, Blue and Gold). Passing one door is one step.\n" +
                "PLAYER AND EXIT: 'Player' is a capsule at x/z of the Room_A centre, y = 1, scale 1. 'Exit' is a " +
                "cylinder at x/z of the Room_H centre, y = 0.5, scale 2/0.5/2.\n" +
                "KEYS: 'Key_Red', 'Key_Blue', 'Key_Gold' are spheres of scale 0.5 at (room centre x + 2, " +
                "y 0.5, room centre z + 2) of the room picked by these rules, where distance means the fewest " +
                "doors from Room_A:\n" +
                "- Key_Red: of the rooms reachable from Room_A holding no key, the one farthest from Room_A.\n" +
                "- Key_Blue: of the rooms reachable holding only the Red key but not reachable holding no key, " +
                "the one farthest from Room_A while holding the Red key.\n" +
                "- Key_Gold: of the rooms reachable holding the Red and Blue keys but not reachable holding only " +
                "the Red key, the one farthest from Room_A while holding Red and Blue.\n" +
                "A tie goes to the alphabetically first room. Work these rooms out yourself.\n" +
                "That is 25 objects.\n\n" +
                "LOGIC. With execute_lua define the pre-declared slots below via logic_define(name, function(...) " +
                "... end). This Lua has the standard string/table/math libraries in Lua 5.2 style " +
                "(math.atan2(y, x) is available) plus read-only world queries: coreai_world_pos(name) " +
                "returns {x=..., y=..., " +
                "z=...} or nil; coreai_world_exists(name) returns a boolean; coreai_world_find(pattern) returns " +
                "a sorted list of the names of existing objects whose name contains pattern (case-insensitive). " +
                "coreai_world_pos answers in Roblox studs (x_studs = x_m / 0.28, z_studs = -z_m / 0.28), so " +
                "compare world positions only with each other. Lua cannot create objects here, and " +
                "execute_lua accepts at most 20 calls a minute. One slot call may run at most 200,000 Lua VM " +
                "instructions; a call that exceeds it or raises uninstalls the slot. Every failed tool call (a " +
                "Lua chunk that raises, a rejected or rate-limited call) costs points: check values by " +
                "returning a string, never by raising. You have about 8 minutes for this task.\n" +
                "Every slot must read the CURRENT world at call time: the grader calls the slots on your " +
                "dungeon, on a reference copy of this specification, and on that copy after moving keys, the " +
                "Exit and the Player and after removing doors, so hard-coded rooms, doors, key places or " +
                "answers fail. A door exists only while its object exists, and its two rooms and its colour " +
                "come from its name. A door name is exactly 'Door_<P>_<Q>' or 'Door_<P>_<Q>_Red' / '_Blue' / " +
                "'_Gold' (case-sensitive), where P and Q are two different letters from A..I in either order; " +
                "any other name is not a door. The rooms are the " +
                "existing objects among Room_A..Room_I. An inventory is a string of key colours separated by " +
                "commas without spaces ('' = no keys, 'Red', 'Blue,Gold', in any order).\n" +
                "1. room_of(name) -> string: the room whose centre is nearest (x and z only) to the named " +
                "object, taking the alphabetically first room whose distance is at most 1.01 times the smallest " +
                "distance; 'none' if the object does not exist or no room exists.\n" +
                "2. can_open(door, inventory) -> boolean: false if no object has that name or it is not a door " +
                "name; true for an open door; for a locked door, true when the inventory holds its colour or " +
                "Gold.\n" +
                "3. reachable(fromRoom, toRoom, inventory) -> boolean: rooms are given by name, e.g. 'Room_C'. " +
                "True if toRoom can be reached from fromRoom through existing doors the inventory opens (keys " +
                "are NOT picked up on the way); true when fromRoom equals toRoom; false if either room does not " +
                "exist.\n" +
                "4. next_step(fromRoom, toRoom, inventory) -> string: with the rules of reachable, the room to " +
                "enter first on a path with the fewest doors; if several first rooms start such a path, the " +
                "alphabetically first. fromRoom when it equals toRoom; 'none' if toRoom is unreachable or " +
                "either room does not exist.\n" +
                "5. min_doors_to_exit(inventory) -> number: the player starts in room_of('Player') holding the " +
                "inventory. Whenever the player is in a room, the start room included, it picks up every key " +
                "object 'Key_Red' / 'Key_Blue' / 'Key_Gold' whose room_of is that room. Return the fewest doors " +
                "passed to reach room_of('Exit'); 0 if already there; -1 if it cannot be reached or the Player " +
                "or Exit does not exist.\n" +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                RecordedScene scene = RecordedScene.From(env.World.Commands);
                int invalid = env.World.InvalidCommandCount;
                int luaFailures = env.Lua.FailedExecutions;
                g.AddToolErrorCheckpoint("clean_tools", "no failed tool calls, invalid world commands or failed Lua",
                    4, run.FailedToolCalls == 0 && invalid == 0 && luaFailures == 0,
                    scoresFailedToolCalls: true, scoresInvalidCommands: true,
                    detail: $"{run.FailedToolCalls} failed, {invalid} invalid, {luaFailures} Lua failures");

                AddFamily(g, scene, "rooms", "nine 8 m floor cubes on the 10 m grid", 6, RoomNames(),
                    SceneAspect.All, BenchmarkDimension.TaskCompletion);

                List<string> doorNames = DoorNames();
                bool doorSet = DoorSetExact(scene, doorNames, out string doorSetDetail);
                g.Add("doors_set", "exactly the eleven specified door cubes, named by rooms and colour", 6, doorSet,
                    true, doorSetDetail, BenchmarkDimension.TaskCompletion);
                AddFamily(g, scene, "doors_geometry", "doors at room midpoints, sized by their orientation", 7,
                    doorNames, SceneAspect.All, BenchmarkDimension.Reasoning);
                AddFamily(g, scene, "key_red", "Key_Red in the farthest room reachable without keys", 5,
                    new[] { "Key_Red" }, SceneAspect.All, BenchmarkDimension.Reasoning);
                AddFamily(g, scene, "key_blue", "Key_Blue in the farthest room the Red key newly opens", 5,
                    new[] { "Key_Blue" }, SceneAspect.All, BenchmarkDimension.Reasoning);
                AddFamily(g, scene, "key_gold", "Key_Gold in the farthest room Red and Blue newly open", 5,
                    new[] { "Key_Gold" }, SceneAspect.All, BenchmarkDimension.Reasoning);
                AddFamily(g, scene, "player_exit", "Player in Room_A and Exit in Room_H as specified", 3,
                    new[] { "Player", "Exit" }, SceneAspect.All, BenchmarkDimension.TaskCompletion);

                InventoryReport inventory = InventoryReport.Compare(scene, Canonical);
                g.Add("exact_inventory", "exactly the 25 specified objects, each once, at scene root", 4,
                    inventory.Exact, dimension: BenchmarkDimension.InstructionAdherence,
                    detail: inventory.Describe());

                bool installed = true;
                foreach (string slot in SlotNames)
                {
                    installed &= env.Lua.LogicSlots.IsOverridden(slot);
                }

                g.Add("slots_installed", "all five dungeon slots installed", 3, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);

                int baseline = env.World.Commands.Count;
                Dictionary<string, PlanPosition> world = ExpectedSceneObject.PlanMap(Canonical);
                SlotSamples roomOf = new(env);
                SlotSamples canOpen = new(env);
                SlotSamples reachable = new(env);
                SlotSamples nextStep = new(env);
                SlotSamples minDoors = new(env);
                SlotSamples liveKeysExit = new(env);
                SlotSamples liveDoors = new(env);
                try
                {
                    VirtualScene.Load(env, Canonical);
                    DungeonReference reference = new(world);
                    StaticSamples(reference, roomOf, canOpen, reachable, nextStep, minDoors);
                    ProbeMovedObjects(env, world, liveKeysExit);
                    ProbeRemovedDoor(env, world, liveDoors);

                    // WHY: a slot that throws is uninstalled, so inputs naming missing objects run last and
                    // cannot hide the outcome of the other samples.
                    reference = new DungeonReference(world);
                    roomOf.String(RoomOfSlot, reference.RoomOf("Nope"), "Nope");
                    canOpen.Bool(CanOpenSlot, reference.CanOpen("Door_E_H_Red", "Red"), "Door_E_H_Red", "Red");
                    canOpen.Bool(CanOpenSlot, reference.CanOpen("Room_A", "Gold"), "Room_A", "Gold");
                    reachable.Bool(ReachableSlot, reference.Reachable("Room_Z", "Room_A", "Gold"), "Room_Z",
                        "Room_A", "Gold");
                    nextStep.String(NextStepSlot, reference.NextStep("Room_A", "Room_Z", "Gold"), "Room_A",
                        "Room_Z", "Gold");
                }
                finally
                {
                    VirtualScene.Restore(env, scene, baseline);
                }

                // WHY: after the reference probes, because a slot that raises on the model's own world is
                // uninstalled and must not also forfeit the reference checkpoints.
                SlotSamples own = OwnWorldSamples(env, scene);

                g.Add("room_of", "room_of on the reference dungeon, ties included", 5, roomOf.AllPassed,
                    dimension: BenchmarkDimension.TaskCompletion, detail: roomOf.Detail);
                g.Add("can_open", "can_open: colours, Gold master key, missing doors", 6, canOpen.AllPassed,
                    dimension: BenchmarkDimension.TaskCompletion, detail: canOpen.Detail);
                g.Add("reachable", "reachable over existing openable doors without pickups", 6,
                    reachable.AllPassed, dimension: BenchmarkDimension.Reasoning, detail: reachable.Detail);
                g.Add("next_step", "next_step on fewest-door paths with alphabetical ties", 7, nextStep.AllPassed,
                    dimension: BenchmarkDimension.Reasoning, detail: nextStep.Detail);
                g.Add("min_doors", "min_doors_to_exit with key pickups on hidden inventories", 9,
                    minDoors.AllPassed, true, minDoors.Detail, BenchmarkDimension.Reasoning);
                g.Add("live_keys_exit", "slots follow a moved key, a moved Exit and a moved Player", 7,
                    liveKeysExit.AllPassed, true, liveKeysExit.Detail, BenchmarkDimension.Reasoning);
                g.Add("live_doors", "slots follow a removed door", 6, liveDoors.AllPassed,
                    dimension: BenchmarkDimension.Reasoning, detail: liveDoors.Detail);
                g.Add("own_world_consistency", "room_of and min_doors_to_exit agree with the dungeon the model built",
                    6, own.AllPassed, dimension: BenchmarkDimension.Reasoning, detail: own.Detail);

                if (inventory.Extra.Count > 0)
                {
                    g.Penalty($"spawned {inventory.Extra.Count} object(s) outside the specification",
                        3 * inventory.Extra.Count);
                }

                if (scene.DuplicateSpawns > 0)
                {
                    g.Penalty($"spawned {scene.DuplicateSpawns} duplicate object(s)", 3 * scene.DuplicateSpawns);
                }

                bool allPassed = true;
                foreach (BenchmarkCheckpoint checkpoint in g.Checkpoints)
                {
                    allPassed &= checkpoint.Passed;
                }

                if (allPassed && g.Penalties.Count == 0)
                {
                    g.Bonus = 8;
                }

                if (run.ToolCalls == 0)
                {
                    g.HardCap = 40;
                }

                return g;
            }

            private static void AddFamily(ScenarioGrading g, RecordedScene scene, string id, string description,
                double weight, IReadOnlyList<string> names, SceneAspect aspects, BenchmarkDimension dimension)
            {
                bool passed = ExpectedSceneObject.CheckFamily(scene, Canonical, names, aspects, out string detail);
                g.Add(id, description, weight, passed, detail: detail, dimension: dimension);
            }

            /// <summary>
            /// The model's slots against the model's own dungeon, with expectations from the reference rules
            /// applied to the positions and door names it built. Needs the rooms, Player, Exit and keys.
            /// </summary>
            private static SlotSamples OwnWorldSamples(BenchmarkEnvironment env, RecordedScene scene)
            {
                SlotSamples own = new(env);
                List<string> required = RoomNames();
                required.AddRange(new[] { "Player", "Exit", "Key_Red", "Key_Blue", "Key_Gold" });
                List<string> missing = new();
                foreach (string name in required)
                {
                    if (!scene.Contains(name))
                    {
                        missing.Add(name);
                    }
                }

                if (missing.Count > 0)
                {
                    own.Fail("built dungeon lacks " +
                             string.Join(", ", missing.GetRange(0, Math.Min(5, missing.Count))));
                    return own;
                }

                DungeonReference reference = new(scene.PlanMap());
                foreach (string name in new[] { "Key_Red", "Key_Blue", "Key_Gold", "Player", "Exit" })
                {
                    own.String(RoomOfSlot, reference.RoomOf(name), name);
                }

                own.Number(MinDoorsSlot, reference.MinDoorsToExit(""), "");
                own.Number(MinDoorsSlot, reference.MinDoorsToExit("Gold"), "Gold");
                own.Bool(ReachableSlot, reference.Reachable("Room_A", "Room_H", ""), "Room_A", "Room_H", "");
                return own;
            }

            private static void StaticSamples(DungeonReference reference, SlotSamples roomOf, SlotSamples canOpen,
                SlotSamples reachable, SlotSamples nextStep, SlotSamples minDoors)
            {
                foreach (string name in new[]
                         {
                             "Key_Red", "Key_Blue", "Key_Gold", "Player", "Exit", "Door_A_B", "Door_D_G_Blue",
                             "Door_H_I_Gold"
                         })
                {
                    roomOf.String(RoomOfSlot, reference.RoomOf(name), name);
                }

                string[,] doors =
                {
                    { "Door_A_B", "" }, { "Door_B_C_Red", "" }, { "Door_B_C_Red", "Red" },
                    { "Door_B_C_Red", "Blue" }, { "Door_D_G_Blue", "Red,Blue" }, { "Door_G_H_Gold", "Red,Blue" },
                    { "Door_G_H_Gold", "Gold" }, { "Door_E_F_Red", "Gold" }, { "Door_H_I_Gold", "Blue,Gold,Red" },
                    { "Door_A_D_Blue", "Blue" }
                };
                for (int i = 0; i < doors.GetLength(0); i++)
                {
                    canOpen.Bool(CanOpenSlot, reference.CanOpen(doors[i, 0], doors[i, 1]), doors[i, 0], doors[i, 1]);
                }

                string[,] routes =
                {
                    { "Room_A", "Room_H", "" }, { "Room_A", "Room_D", "" }, { "Room_A", "Room_H", "Gold" },
                    { "Room_A", "Room_I", "Red" }, { "Room_A", "Room_G", "Red" }, { "Room_G", "Room_A", "Blue" },
                    { "Room_C", "Room_A", "" }, { "Room_E", "Room_E", "" }
                };
                for (int i = 0; i < routes.GetLength(0); i++)
                {
                    reachable.Bool(ReachableSlot, reference.Reachable(routes[i, 0], routes[i, 1], routes[i, 2]),
                        routes[i, 0], routes[i, 1], routes[i, 2]);
                }

                string[,] steps =
                {
                    { "Room_A", "Room_D", "" }, { "Room_A", "Room_D", "Blue" }, { "Room_A", "Room_I", "Red" },
                    { "Room_E", "Room_C", "Red" }, { "Room_D", "Room_D", "" }, { "Room_A", "Room_H", "" },
                    { "Room_H", "Room_A", "Gold" }, { "Room_I", "Room_A", "Red" }, { "Room_G", "Room_C", "Gold" }
                };
                for (int i = 0; i < steps.GetLength(0); i++)
                {
                    nextStep.String(NextStepSlot, reference.NextStep(steps[i, 0], steps[i, 1], steps[i, 2]),
                        steps[i, 0], steps[i, 1], steps[i, 2]);
                }

                foreach (string inventory in new[] { "", "Red", "Blue", "Gold", "Red,Blue", "Blue,Red" })
                {
                    minDoors.Number(MinDoorsSlot, reference.MinDoorsToExit(inventory), inventory);
                }
            }

            private static void ProbeMovedObjects(BenchmarkEnvironment env, Dictionary<string, PlanPosition> world,
                SlotSamples live)
            {
                ExpectedSceneObject blue = Find("Key_Blue");
                ExpectedSceneObject roomB = Find("Room_B");
                MoveProbe(env, world, blue, roomB.X + 2.0, blue.Y, roomB.Z + 2.0, reference =>
                {
                    live.String(RoomOfSlot, reference.RoomOf("Key_Blue"), "Key_Blue");
                    live.Number(MinDoorsSlot, reference.MinDoorsToExit(""), "");
                });

                ExpectedSceneObject exit = Find("Exit");
                ExpectedSceneObject roomC = Find("Room_C");
                MoveProbe(env, world, exit, roomC.X, exit.Y, roomC.Z, reference =>
                {
                    live.String(RoomOfSlot, reference.RoomOf("Exit"), "Exit");
                    live.Number(MinDoorsSlot, reference.MinDoorsToExit(""), "");
                });

                ExpectedSceneObject player = Find("Player");
                ExpectedSceneObject roomI = Find("Room_I");
                MoveProbe(env, world, player, roomI.X, player.Y, roomI.Z, reference =>
                {
                    live.Number(MinDoorsSlot, reference.MinDoorsToExit(""), "");
                    live.Number(MinDoorsSlot, reference.MinDoorsToExit("Gold"), "Gold");
                });
            }

            private static void MoveProbe(BenchmarkEnvironment env, Dictionary<string, PlanPosition> world,
                ExpectedSceneObject subject, double x, double y, double z, Action<DungeonReference> checks)
            {
                VirtualScene.Move(env, subject.Name, x, y, z);
                world[subject.Name] = PlanPosition.Of(x, z);
                checks(new DungeonReference(world));
                VirtualScene.Move(env, subject.Name, subject.X, subject.Y, subject.Z);
                world[subject.Name] = PlanPosition.Of(subject.X, subject.Z);
            }

            private static void ProbeRemovedDoor(BenchmarkEnvironment env, Dictionary<string, PlanPosition> world,
                SlotSamples live)
            {
                ExpectedSceneObject door = Find("Door_D_G_Blue");
                VirtualScene.Destroy(env, door.Name);
                world.Remove(door.Name);
                DungeonReference reference = new(world);
                live.Number(MinDoorsSlot, reference.MinDoorsToExit(""), "");
                live.Number(MinDoorsSlot, reference.MinDoorsToExit("Gold"), "Gold");
                live.Bool(CanOpenSlot, reference.CanOpen(door.Name, "Blue"), door.Name, "Blue");
                live.Bool(ReachableSlot, reference.Reachable("Room_A", "Room_G", "Blue"), "Room_A", "Room_G",
                    "Blue");
                live.String(NextStepSlot, reference.NextStep("Room_A", "Room_G", "Gold"), "Room_A", "Room_G",
                    "Gold");
                VirtualScene.Spawn(env, door);
                world[door.Name] = PlanPosition.Of(door.X, door.Z);
            }

            private static bool DoorSetExact(RecordedScene scene, List<string> doorNames, out string detail)
            {
                List<string> problems = new();
                HashSet<string> expected = new(doorNames, StringComparer.Ordinal);
                foreach (string name in doorNames)
                {
                    if (!scene.TryGet(name, out RecordedSceneObject door) ||
                        !string.Equals(door.Name, name, StringComparison.Ordinal))
                    {
                        problems.Add($"{name} missing");
                    }
                    else if (!string.Equals(door.Prefab, "cube", StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{name} is '{door.Prefab}'");
                    }
                }

                foreach (RecordedSceneObject o in scene.Objects)
                {
                    if (o.Name.StartsWith("Door", StringComparison.OrdinalIgnoreCase) && !expected.Contains(o.Name))
                    {
                        problems.Add($"unexpected door '{o.Name}'");
                    }
                }

                detail = problems.Count == 0
                    ? null
                    : string.Join("; ", problems.GetRange(0, Math.Min(4, problems.Count)));
                return problems.Count == 0;
            }

            private static ExpectedSceneObject Find(string name)
            {
                foreach (ExpectedSceneObject e in Canonical)
                {
                    if (string.Equals(e.Name, name, StringComparison.Ordinal))
                    {
                        return e;
                    }
                }

                throw new InvalidOperationException($"'{name}' is not part of the dungeon specification.");
            }

            private static List<string> RoomNames()
            {
                List<string> names = new();
                foreach (char letter in Letters)
                {
                    names.Add("Room_" + letter);
                }

                return names;
            }

            private static List<string> DoorNames()
            {
                List<string> names = new();
                for (int i = 0; i < DoorSpec.GetLength(0); i++)
                {
                    string colour = DoorSpec[i, 2];
                    names.Add($"Door_{DoorSpec[i, 0]}_{DoorSpec[i, 1]}" + (colour.Length > 0 ? "_" + colour : ""));
                }

                return names;
            }

            private static List<ExpectedSceneObject> BuildCanonical()
            {
                List<ExpectedSceneObject> list = new();
                Dictionary<char, (double X, double Z)> centres = new();
                for (int i = 0; i < Letters.Length; i++)
                {
                    int column = i % 3;
                    int row = i / 3;
                    double x = 10.0 * column - 10.0;
                    double z = 10.0 * row + 5.0;
                    centres[Letters[i]] = (x, z);
                    list.Add(ExpectedSceneObject.Box("Room_" + Letters[i], "cube", x, -0.25, z, 8.0, 0.5, 8.0, 0.0,
                        90.0));
                }

                List<string> doorNames = DoorNames();
                for (int i = 0; i < DoorSpec.GetLength(0); i++)
                {
                    (double X, double Z) a = centres[DoorSpec[i, 0][0]];
                    (double X, double Z) b = centres[DoorSpec[i, 1][0]];
                    bool eastWest = Math.Abs(a.Z - b.Z) < 1e-9;
                    list.Add(ExpectedSceneObject.Box(doorNames[i], "cube", (a.X + b.X) / 2.0, 1.5, (a.Z + b.Z) / 2.0,
                        eastWest ? 2.0 : 3.0, 3.0, eastWest ? 3.0 : 2.0, 0.0, 180.0));
                }

                string[] keyColours = { "Red", "Blue", "Gold" };
                char[] keyRooms = DeriveKeyRooms();
                for (int i = 0; i < keyColours.Length; i++)
                {
                    (double X, double Z) room = centres[keyRooms[i]];
                    list.Add(ExpectedSceneObject.Ball("Key_" + keyColours[i], room.X + 2.0, 0.5, room.Z + 2.0, 0.5));
                }

                list.Add(ExpectedSceneObject.Upright("Player", "capsule", centres['A'].X, 1.0, centres['A'].Z, 1.0,
                    1.0, 1.0));
                list.Add(ExpectedSceneObject.Upright("Exit", "cylinder", centres['H'].X, 0.5, centres['H'].Z, 2.0,
                    0.5, 2.0));
                return list;
            }

            /// <summary>The brief's key-placement rules on the specified doors: Red, Blue, Gold rooms.</summary>
            private static char[] DeriveKeyRooms()
            {
                Dictionary<char, int> none = SpecDistances(DungeonReference.NoKeys);
                Dictionary<char, int> red = SpecDistances(DungeonReference.RedBit);
                Dictionary<char, int> redBlue = SpecDistances(DungeonReference.RedBit | DungeonReference.BlueBit);
                return new[]
                {
                    Farthest(none, null),
                    Farthest(red, none),
                    Farthest(redBlue, red)
                };
            }

            private static char Farthest(Dictionary<char, int> reachable, Dictionary<char, int> excluded)
            {
                char best = '?';
                int bestDistance = -1;
                foreach (char letter in Letters)
                {
                    if (!reachable.TryGetValue(letter, out int distance) ||
                        excluded != null && excluded.ContainsKey(letter))
                    {
                        continue;
                    }

                    if (distance > bestDistance)
                    {
                        bestDistance = distance;
                        best = letter;
                    }
                }

                return best;
            }

            private static Dictionary<char, int> SpecDistances(int keys)
            {
                Dictionary<char, int> distance = new() { { 'A', 0 } };
                Queue<char> queue = new();
                queue.Enqueue('A');
                while (queue.Count > 0)
                {
                    char room = queue.Dequeue();
                    for (int i = 0; i < DoorSpec.GetLength(0); i++)
                    {
                        if (!DungeonReference.Opens(DoorSpec[i, 2], keys))
                        {
                            continue;
                        }

                        char a = DoorSpec[i, 0][0];
                        char b = DoorSpec[i, 1][0];
                        char other = room == a ? b : room == b ? a : '\0';
                        if (other != '\0' && !distance.ContainsKey(other))
                        {
                            distance[other] = distance[room] + 1;
                            queue.Enqueue(other);
                        }
                    }
                }

                return distance;
            }
        }

        /// <summary>
        /// Independent C# statement of the dungeon rules in the brief, evaluated on a name-to-position map of a
        /// world: the grader's source of expected slot answers.
        /// </summary>
        private sealed class DungeonReference
        {
            public const int NoKeys = 0;
            public const int RedBit = 1;
            public const int BlueBit = 2;
            public const int GoldBit = 4;

            private const string RoomLetters = "ABCDEFGHI";
            private const double TieRatio = 1.01;

            private static readonly Regex DoorPattern =
                new("^Door_([A-I])_([A-I])(?:_(Red|Blue|Gold))?$", RegexOptions.CultureInvariant);

            private readonly Dictionary<string, PlanPosition> _world;
            private readonly List<string> _rooms = new();
            private readonly List<(string A, string B, string Colour)> _doors = new();

            public DungeonReference(Dictionary<string, PlanPosition> world)
            {
                _world = world;
                foreach (char letter in RoomLetters)
                {
                    if (world.ContainsKey("Room_" + letter))
                    {
                        _rooms.Add("Room_" + letter);
                    }
                }

                foreach (string name in world.Keys)
                {
                    Match match = DoorPattern.Match(name);
                    if (!match.Success || match.Groups[1].Value == match.Groups[2].Value)
                    {
                        continue;
                    }

                    string a = "Room_" + match.Groups[1].Value;
                    string b = "Room_" + match.Groups[2].Value;
                    if (_rooms.Contains(a) && _rooms.Contains(b))
                    {
                        _doors.Add((a, b, match.Groups[3].Success ? match.Groups[3].Value : ""));
                    }
                }
            }

            public static bool Opens(string colour, int keys)
            {
                if (string.IsNullOrEmpty(colour) || (keys & GoldBit) != 0)
                {
                    return true;
                }

                return colour == "Red" ? (keys & RedBit) != 0 : colour == "Blue" && (keys & BlueBit) != 0;
            }

            public string RoomOf(string name)
            {
                return PlanPosition.NearestCandidate(_world, name, _rooms, TieRatio);
            }

            public bool CanOpen(string door, string inventory)
            {
                if (string.IsNullOrWhiteSpace(door) || !_world.ContainsKey(door.Trim()))
                {
                    return false;
                }

                Match match = DoorPattern.Match(door);
                return match.Success && match.Groups[1].Value != match.Groups[2].Value
                                     && Opens(match.Groups[3].Success ? match.Groups[3].Value : "", Keys(inventory));
            }

            public bool Reachable(string from, string to, string inventory)
            {
                if (!_rooms.Contains(from) || !_rooms.Contains(to))
                {
                    return false;
                }

                return Distances(from, Keys(inventory)).ContainsKey(to);
            }

            public string NextStep(string from, string to, string inventory)
            {
                if (!_rooms.Contains(from) || !_rooms.Contains(to))
                {
                    return "none";
                }

                if (from == to)
                {
                    return from;
                }

                int keys = Keys(inventory);
                Dictionary<string, int> toTarget = Distances(to, keys);
                if (!toTarget.TryGetValue(from, out int remaining))
                {
                    return "none";
                }

                string best = null;
                foreach (string neighbour in Neighbours(from, keys))
                {
                    if (toTarget.TryGetValue(neighbour, out int d) && d == remaining - 1 &&
                        (best == null || string.CompareOrdinal(neighbour, best) < 0))
                    {
                        best = neighbour;
                    }
                }

                return best ?? "none";
            }

            public int MinDoorsToExit(string inventory)
            {
                string start = RoomOf("Player");
                string goal = RoomOf("Exit");
                if (start == "none" || goal == "none")
                {
                    return -1;
                }

                string[] keyRooms = { RoomOf("Key_Red"), RoomOf("Key_Blue"), RoomOf("Key_Gold") };
                int[] bits = { RedBit, BlueBit, GoldBit };
                int Pick(string room, int held)
                {
                    for (int i = 0; i < keyRooms.Length; i++)
                    {
                        if (keyRooms[i] == room)
                        {
                            held |= bits[i];
                        }
                    }

                    return held;
                }

                (string Room, int Keys) first = (start, Pick(start, Keys(inventory)));
                Dictionary<(string, int), int> seen = new() { { first, 0 } };
                Queue<(string Room, int Keys)> queue = new();
                queue.Enqueue(first);
                while (queue.Count > 0)
                {
                    (string Room, int Keys) state = queue.Dequeue();
                    int distance = seen[state];
                    if (state.Room == goal)
                    {
                        return distance;
                    }

                    foreach (string neighbour in Neighbours(state.Room, state.Keys))
                    {
                        (string Room, int Keys) next = (neighbour, Pick(neighbour, state.Keys));
                        if (!seen.ContainsKey(next))
                        {
                            seen[next] = distance + 1;
                            queue.Enqueue(next);
                        }
                    }
                }

                return -1;
            }

            private static int Keys(string inventory)
            {
                int keys = NoKeys;
                foreach (string colour in (inventory ?? "").Split(','))
                {
                    keys |= colour == "Red" ? RedBit : colour == "Blue" ? BlueBit : colour == "Gold" ? GoldBit : 0;
                }

                return keys;
            }

            private IEnumerable<string> Neighbours(string room, int keys)
            {
                foreach ((string A, string B, string Colour) door in _doors)
                {
                    if (!Opens(door.Colour, keys))
                    {
                        continue;
                    }

                    if (door.A == room)
                    {
                        yield return door.B;
                    }
                    else if (door.B == room)
                    {
                        yield return door.A;
                    }
                }
            }

            private Dictionary<string, int> Distances(string from, int keys)
            {
                Dictionary<string, int> distance = new() { { from, 0 } };
                Queue<string> queue = new();
                queue.Enqueue(from);
                while (queue.Count > 0)
                {
                    string room = queue.Dequeue();
                    foreach (string neighbour in Neighbours(room, keys))
                    {
                        if (!distance.ContainsKey(neighbour))
                        {
                            distance[neighbour] = distance[room] + 1;
                            queue.Enqueue(neighbour);
                        }
                    }
                }

                return distance;
            }
        }
    }
}
#endif
#endif
