#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Benchmarking;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// Tests of the G6 geometry grade: the read-back parser and pager, synthetic builds (a coherent castle,
    /// a heap, floating parts, a bare tower ring, construction faults) and the part lists of the five
    /// 2026-09-29 benchmark builds, whose ranking the signals must reproduce.
    /// </summary>
    public sealed class G6BuildAnalysisPlayModeTests
    {
        [Test]
        public void TryParsePage_ReadsHeaderAndRecordsAndIgnoresTrailingLines()
        {
            const string page =
                "G6P|3|2\n" +
                "CastleWallNorth,Block,Cobblestone,64,11,3,0,5.5,32,1,0,0,0,1,0,0,0,1,146,141,132,0\n" +
                "CastleTowerNE,Cylinder,Granite,25,11,11,36,13,33,0,-1,0,1,0,0,0,0,1,226,224,214,0.25\n" +
                "a note the executor appended";
            List<G6Part> parts = new();

            Assert.IsTrue(G6BuildAnalysis.TryParsePage(page, 1, parts, out int total, out int last, out string error),
                error);

            Assert.AreEqual(3, total);
            Assert.AreEqual(2, last);
            Assert.AreEqual(2, parts.Count);
            G6Part tower = parts[1];
            Assert.AreEqual("CastleTowerNE", tower.Name);
            Assert.AreEqual("Cylinder", tower.Shape);
            Assert.AreEqual("Granite", tower.Material);
            Assert.AreEqual(25, tower.SizeX);
            Assert.AreEqual(13, tower.Y);
            CollectionAssert.AreEqual(new double[] { 0, -1, 0, 1, 0, 0, 0, 0, 1 }, tower.R);
            Assert.AreEqual(226, tower.ColorR);
            Assert.AreEqual(0.25, tower.Transparency);
        }

        [Test]
        public void TryParsePage_RejectsMalformedPages()
        {
            const string record = "CastleA,Block,Brick,1,1,1,0,0.5,0,1,0,0,0,1,0,0,0,1,1,1,1,0";
            List<G6Part> parts = new();

            Assert.IsFalse(G6BuildAnalysis.TryParsePage("", 1, parts, out _, out _, out _));
            Assert.IsFalse(G6BuildAnalysis.TryParsePage("nil", 1, parts, out _, out _, out _));
            Assert.IsFalse(G6BuildAnalysis.TryParsePage("G6P|2|2\n" + record, 1, parts, out _, out _, out string shortPage));
            StringAssert.Contains("carries 1 records", shortPage);
            Assert.IsFalse(G6BuildAnalysis.TryParsePage("G6P|1|1\n" + record.Replace(",0.5,", ",nan,"), 1, parts,
                out _, out _, out _));
            Assert.IsFalse(G6BuildAnalysis.TryParsePage("G6P|1|1\nCastleA,Block,Brick,1,1", 1, parts,
                out _, out _, out _));
            Assert.IsFalse(G6BuildAnalysis.TryParseRecord(record + ",7", out _));
            Assert.IsTrue(G6BuildAnalysis.TryParseRecord(record, out G6Part part));
            Assert.AreEqual(0.5, part.Y);
        }

        [Test]
        public void ReadParts_PagesThroughTheWorldUntilEveryPartIsRead()
        {
            List<string> records = new();
            for (int i = 1; i <= 7; i++)
            {
                records.Add($"CastlePart{i},Block,Brick,1,1,1,{i * 3},0.5,0,1,0,0,0,1,0,0,0,1,200,200,200,0");
            }

            int calls = 0;
            G6BuildAnalysis.LuaEvaluator evaluator = (string lua, out string output) =>
            {
                calls++;
                output = ServePage(lua, records, 3);
                return true;
            };

            List<G6Part> parts = G6BuildAnalysis.ReadParts(evaluator, "Castle", out int total, out string error);

            Assert.IsNotNull(parts, error);
            Assert.AreEqual(7, total);
            Assert.AreEqual(7, parts.Count);
            Assert.AreEqual(3, calls, "three pages of at most three records");
            Assert.AreEqual("CastlePart7", parts[6].Name);
            Assert.AreEqual(21, parts[6].X);
        }

        [Test]
        public void ReadParts_ReportsAFailedChunkOrAStalledPage()
        {
            G6BuildAnalysis.LuaEvaluator failing = (string lua, out string output) =>
            {
                output = "";
                return false;
            };
            Assert.IsNull(G6BuildAnalysis.ReadParts(failing, "Castle", out _, out string failed));
            StringAssert.Contains("failed at part 1", failed);

            G6BuildAnalysis.LuaEvaluator stalled = (string lua, out string output) =>
            {
                output = "G6P|5|0";
                return true;
            };
            Assert.IsNull(G6BuildAnalysis.ReadParts(stalled, "Castle", out _, out string stuck));
            StringAssert.Contains("no progress", stuck);
        }

        [Test]
        public void DumpLua_PagesFromTheRequestedPartUnderTheResultCap()
        {
            string lua = G6BuildAnalysis.DumpLua(41, "Castle");

            StringAssert.Contains("local START, BUDGET, PREFIX = 41, " + G6BuildAnalysis.PageBudget + ", 'Castle'", lua);
            StringAssert.Contains("p.CFrame:GetComponents()", lua);
            Assert.Less(G6BuildAnalysis.PageBudget + 64, 4000,
                "a page plus its header must stay under the executor's 4000-character result cap");
        }

        [Test]
        public void CoherentCastle_PassesEveryGeometryCheck()
        {
            G6BuildMetrics m = G6BuildAnalysis.Analyze(Parts(CoherentCastle()));

            Assert.That(FailedChecks(m, true), Is.Empty, Describe(m));
            Assert.AreEqual(5, m.Towers, "four corner towers and the gatehouse");
            Assert.AreEqual(4, m.ClosingRadius, "an 8-stud gate needs a closing radius of 4");
            Assert.Greater(m.KeepCells, 200);
            Assert.AreEqual(m.Parts, m.Grounded);
        }

        [Test]
        public void HeapAtTheOrigin_FailsCompositionAndScoresFarBelowTheCastle()
        {
            G6BuildMetrics heap = G6BuildAnalysis.Analyze(Parts(Heap()));
            G6BuildMetrics castle = G6BuildAnalysis.Analyze(Parts(CoherentCastle()));

            CollectionAssert.IsSubsetOf(new[]
            {
                "spread_out", "castle_enclosure", "castle_gate", "castle_towers", "castle_four_towers",
                "castle_keep", "castle_roofs", "courtyard_life", "surroundings"
            }, FailedChecks(heap, true));
            Assert.AreEqual(heap.Parts, heap.Grounded, "a heap of touching parts is grounded, so grounding alone cannot save it");
            Assert.Less(GeometryScore(heap, true), 0.3);
            Assert.Greater(GeometryScore(castle, true) - GeometryScore(heap, true), 0.6);
        }

        [Test]
        public void FloatingParts_FailGroundingAndAreNamed()
        {
            List<PartSpec> specs = CoherentCastle();
            for (int i = 0; i < 6; i++)
            {
                specs.Add(new PartSpec($"CastleCloud{i}", "Ball", "SmoothPlastic", 6, 6, 6, -40 + i * 16, 70, 0));
            }

            G6BuildMetrics m = G6BuildAnalysis.Analyze(Parts(specs));

            Assert.AreEqual(6, m.Floating);
            CollectionAssert.Contains(FailedChecks(m, true), "grounded");
            CollectionAssert.Contains(m.FloatingNames, "CastleCloud0");
        }

        [Test]
        public void BareTowerRingWithoutGate_IsEnclosedButMissesGateKeepRoofsAndLife()
        {
            G6BuildMetrics ring = G6BuildAnalysis.Analyze(Parts(BareRing()));
            G6BuildMetrics castle = G6BuildAnalysis.Analyze(Parts(CoherentCastle()));
            List<string> failed = FailedChecks(ring, true);

            Assert.IsTrue(ring.Enclosed, Describe(ring));
            Assert.AreEqual(0, ring.ClosingRadius, "a sealed ring needs no closing");
            Assert.AreEqual(4, ring.Towers);
            CollectionAssert.IsSubsetOf(new[]
                { "castle_gate", "castle_keep", "castle_roofs", "courtyard_life", "surroundings" }, failed);
            CollectionAssert.DoesNotContain(failed, "castle_enclosure");
            CollectionAssert.DoesNotContain(failed, "castle_four_towers");
            Assert.Greater(GeometryScore(castle, true) - GeometryScore(ring, true), 0.35);
        }

        [Test]
        public void ConstructionFaults_FailTheirOwnChecks()
        {
            List<PartSpec> specs = CoherentCastle();
            specs.Add(specs[1].Renamed("CastleWallNorthAgain"));
            specs.Add(specs[2].Renamed("CastleWallEastAgain"));
            for (int i = 0; i < 6; i++)
            {
                specs.Add(new PartSpec($"CastleStubA{i}", "Block", "Brick", 6, 3, 2, -50 + i * 18, 1.5, 54));
                specs.Add(new PartSpec($"CastleStubB{i}", "Block", "Brick", 2, 3, 6, -48 + i * 18, 1.5, 54));
            }

            specs.Add(new PartSpec("CastleHiddenCore", "Block", "Brick", 2, 2, 2, 0, 12, 6));
            specs.Add(new PartSpec("CastleOvalTower", "Cylinder", "Granite", 12, 29, 12, 50, 14.5, -20));
            G6BuildMetrics m = G6BuildAnalysis.Analyze(Parts(specs));
            List<string> failed = FailedChecks(m, true);

            Assert.AreEqual(2, m.Duplicates);
            Assert.AreEqual(1, m.Buried, "the core inside the keep; duplicates are not counted again");
            Assert.AreEqual(1, m.Distorted);
            Assert.GreaterOrEqual(m.ZFightingPairs, 6);
            CollectionAssert.IsSubsetOf(new[] { "no_duplicates", "no_z_fighting" }, failed);
            CollectionAssert.Contains(m.DistortedNames, "CastleOvalTower");
            CollectionAssert.Contains(m.BuriedNames, "CastleHiddenCore");
        }

        [Test]
        public void RoundShapes_AreDistortedOnlyWhenTheirSizeIsNotRound()
        {
            Assert.IsFalse(G6BuildAnalysis.IsDistorted(G6Part.Create("a", "Cylinder", "Granite", 25, 11, 11, 0, 0, 0, 0, 0, 90)));
            Assert.IsTrue(G6BuildAnalysis.IsDistorted(G6Part.Create("b", "Cylinder", "Granite", 12, 29, 12, 0, 0, 0)));
            Assert.IsTrue(G6BuildAnalysis.IsDistorted(G6Part.Create("c", "Cylinder", "Wood", 1, 8, 1, 0, 0, 0)));
            Assert.IsFalse(G6BuildAnalysis.IsDistorted(G6Part.Create("d", "Ball", "Neon", 3, 3, 3, 0, 0, 0)));
            Assert.IsTrue(G6BuildAnalysis.IsDistorted(G6Part.Create("e", "Ball", "Neon", 3, 5, 3, 0, 0, 0)));
            Assert.IsFalse(G6BuildAnalysis.IsDistorted(G6Part.Create("f", "Block", "Brick", 1, 9, 30, 0, 0, 0)));
        }

        [Test]
        public void SubjectBuild_IsJudgedOnlyBySubjectNeutralSignals()
        {
            G6BuildMetrics castle = G6BuildAnalysis.Analyze(Parts(CoherentCastle()));
            G6BuildMetrics heap = G6BuildAnalysis.Analyze(Parts(Heap()));
            List<string> ids = new();
            foreach (G6Check check in G6BuildAnalysis.Checks(castle, false))
            {
                ids.Add(check.Id);
                StringAssert.DoesNotStartWith("castle_", check.Id);
            }

            CollectionAssert.IsSubsetOf(new[] { "spread_out", "vertical_relief", "separate_objects", "grounded" }, ids);
            CollectionAssert.DoesNotContain(ids, "courtyard_life");
            Assert.That(FailedChecks(castle, false), Is.Empty);
            CollectionAssert.IsSubsetOf(new[] { "spread_out", "separate_objects" }, FailedChecks(heap, false));
        }

        [Test]
        public void EmptyBuild_PassesNoGeometryCheck()
        {
            G6BuildMetrics m = G6BuildAnalysis.Analyze(new List<G6Part>());

            foreach (G6Check check in G6BuildAnalysis.Checks(m, true))
            {
                Assert.IsFalse(check.Passed, check.Id);
            }
        }

        [Test]
        public void RealBuilds_FlagTheFlawsTheirHeroImagesShow()
        {
            AssertFailed(SolCastle, "Sol: three roof quarters re-created in place, flush wall and moat overlaps",
                "no_duplicates", "no_z_fighting");
            AssertFailed(BunnyCastle, "Bunny 20:50: a coherent castle with nothing beyond the moat",
                "surroundings");
            AssertFailed(BunnySparseCastle,
                "Bunny 20:04: corner towers barely above the walls, no roofs, portcullis inside the wall",
                "castle_towers", "castle_four_towers", "castle_roofs", "surroundings", "no_hidden_parts");
            AssertFailed(LunaCastle,
                "Luna 16:39: solid gatehouse, gatehouse towers hovering above it, unrotated Cylinders " +
                "rendered as oval discs, portcullis and windows buried in walls",
                "castle_gate", "grounded", "no_z_fighting", "no_hidden_parts", "round_shapes");
            AssertFailed(LunaUnfinishedCastle, "Luna 15:41: the 38 parts built before the bridge failed",
                "castle_gate", "courtyard_life", "surroundings", "grounded", "no_z_fighting", "no_hidden_parts",
                "round_shapes");
        }

        [Test]
        public void RealBuilds_RankCoherentCastlesAboveFlawedOnes()
        {
            double sol = GeometryScore(G6BuildAnalysis.Analyze(ParseFixture(SolCastle)), true);
            double bunny = GeometryScore(G6BuildAnalysis.Analyze(ParseFixture(BunnyCastle)), true);
            double sparse = GeometryScore(G6BuildAnalysis.Analyze(ParseFixture(BunnySparseCastle)), true);
            double luna = GeometryScore(G6BuildAnalysis.Analyze(ParseFixture(LunaCastle)), true);
            double unfinished = GeometryScore(G6BuildAnalysis.Analyze(ParseFixture(LunaUnfinishedCastle)), true);
            string scores = $"sol {sol:0.00}, bunny {bunny:0.00}, sparse {sparse:0.00}, luna {luna:0.00}, " +
                            $"unfinished {unfinished:0.00}";

            Assert.GreaterOrEqual(sol, 0.9, scores);
            Assert.GreaterOrEqual(bunny, 0.9, scores);
            Assert.Less(sol, 1.0, "the best build still has measurable faults: " + scores);
            Assert.Greater(Math.Min(sol, bunny) - sparse, 0.15, scores);
            Assert.Greater(sparse, luna, scores);
            Assert.Greater(luna, unfinished, scores);
        }

        [Test]
        public void RealBuilds_MeasureTheirCastleStructure()
        {
            G6BuildMetrics sol = G6BuildAnalysis.Analyze(ParseFixture(SolCastle));
            G6BuildMetrics luna = G6BuildAnalysis.Analyze(ParseFixture(LunaCastle));

            Assert.AreEqual(154, sol.Parts);
            Assert.AreEqual(6, sol.ClosingRadius, "Sol's gate between the gatehouse piers is 12 studs wide");
            Assert.AreEqual(15.75, sol.WallTop, 0.01, "16-stud walls on the 1.25-stud ground surface");
            Assert.AreEqual(5, sol.Towers);
            Assert.AreEqual(2, sol.Floating, "the well canopy hovers 0.65 studs off its posts");
            Assert.AreEqual(188, luna.Parts);
            Assert.AreEqual(40, luna.Distorted, "Size (1, 8, 1) poles and (12, 29, 12) towers left unrotated");
            Assert.AreEqual(1, luna.ClosingRadius, "a solid gatehouse block leaves no walkable gate");
        }

        [Test]
        public void LiveWorld_ReadBackReturnsWhatTheLuaBuilt()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            BenchmarkEnvironment env = new(settings);
            try
            {
                List<PartSpec> specs = CoherentCastle();
                BuildInWorld(env, specs);
                int pages = 0;
                G6BuildAnalysis.LuaEvaluator counting = (string lua, out string output) =>
                {
                    pages++;
                    return env.RbxWorld.TryEvaluateLua(lua, out output);
                };

                List<G6Part> parts = G6BuildAnalysis.ReadParts(counting, RbxBenchmarkWorld.BuildPrefix,
                    out int total, out string error);

                Assert.IsNotNull(parts, error);
                Assert.AreEqual(specs.Count, total);
                Assert.AreEqual(specs.Count, parts.Count);
                Assert.Greater(pages, 1, "the castle needs more than one page under the result cap");
                Dictionary<string, G6Part> byName = new();
                foreach (G6Part part in parts)
                {
                    byName[part.Name] = part;
                }

                foreach (PartSpec spec in specs)
                {
                    Assert.IsTrue(byName.TryGetValue(spec.Name, out G6Part read), spec.Name);
                    G6Part expected = spec.ToPart();
                    Assert.AreEqual(expected.Shape, read.Shape, spec.Name);
                    Assert.AreEqual(expected.Material, read.Material, spec.Name);
                    double[] want =
                        { expected.SizeX, expected.SizeY, expected.SizeZ, expected.X, expected.Y, expected.Z };
                    double[] got = { read.SizeX, read.SizeY, read.SizeZ, read.X, read.Y, read.Z };
                    for (int i = 0; i < want.Length; i++)
                    {
                        Assert.AreEqual(want[i], got[i], 1e-3, spec.Name);
                    }

                    for (int i = 0; i < 9; i++)
                    {
                        Assert.AreEqual(expected.R[i], read.R[i], 2e-3, spec.Name);
                    }
                }

                CollectionAssert.AreEquivalent(FailedChecks(G6BuildAnalysis.Analyze(Parts(specs)), true),
                    FailedChecks(G6BuildAnalysis.Analyze(parts), true));
            }
            finally
            {
                env.DisposeRbxWorld();
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void LiveWorld_GradePassesTheCoherentCastleAndFailsTheHeap()
        {
            using (new EnvScope(("COREAI_BENCHMARK_FREEBUILD_SUBJECT", null),
                       ("COREAI_BENCHMARK_FREEBUILD_PROMPT", null), ("COREAI_BENCHMARK_VISION_MODE", null)))
            {
                GameBenchmarkScenario scenario = Array.Find(GameFreeBuildScenariosG6.All(),
                    candidate => candidate.Id == "g6_free_build");
                Assert.IsNotNull(scenario);

                List<PartSpec> compliantCastle = CoherentCastle();
                Assert.AreEqual(60, compliantCastle.Count, "The golden castle retains all original parts.");
                // WHY: lift the existing foundation into bounds without losing a part or changing support contacts.
                for (int index = 0; index < compliantCastle.Count; index++)
                {
                    PartSpec part = compliantCastle[index];
                    compliantCastle[index] = new PartSpec(part.Name, part.Shape, part.Material,
                        part.Sx, part.Sy, part.Sz, part.X, part.Y + 1, part.Z,
                        part.Rx, part.Ry, part.Rz);
                }
                ScenarioGrading castle = GradeBuild(scenario, compliantCastle);
                double totalWeight = 0;
                foreach (BenchmarkCheckpoint checkpoint in castle.Checkpoints)
                {
                    totalWeight += checkpoint.Weight;
                    Assert.IsTrue(checkpoint.Passed, $"{checkpoint.Id}: {checkpoint.Detail}");
                    Assert.IsNotEmpty(checkpoint.Detail, checkpoint.Id + " must explain itself in the report");
                }

                Assert.AreEqual(100, totalWeight, 1e-9, "the G6 castle weights are laid out to sum to 100");
                Assert.IsNotNull(castle.Checkpoints.Find(c => c.Id == "castle_gate"));

                ScenarioGrading heap = GradeBuild(scenario, Heap());
                Assert.Less(PassedShare(heap), 0.5, "a heap of varied parts must not reach a partial pass");
                Assert.IsTrue(heap.Checkpoints.Find(c => c.Id == "material_variety").Passed);
                Assert.IsFalse(heap.Checkpoints.Find(c => c.Id == "castle_enclosure").Passed);
            }
        }

        [Test]
        public void LiveWorld_NothingBuiltFailsTheGeometryWithoutThrowing()
        {
            using (new EnvScope(("COREAI_BENCHMARK_FREEBUILD_SUBJECT", null),
                       ("COREAI_BENCHMARK_FREEBUILD_PROMPT", null), ("COREAI_BENCHMARK_VISION_MODE", null)))
            {
                CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                try
                {
                    GameBenchmarkScenario scenario = Array.Find(GameFreeBuildScenariosG6.All(),
                        candidate => candidate.Id == "g6_free_build");
                    ScenarioGrading grade = scenario.Grade(new BenchmarkEnvironment(settings), new RunObservation());

                    Assert.IsFalse(grade.Checkpoints.Find(c => c.Id == "grounded").Passed);
                    Assert.IsFalse(grade.Checkpoints.Find(c => c.Id == "castle_keep").Passed);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }
        }

        [Test]
        public void Goal_StatesEveryThresholdTheGeometryGradeUses()
        {
            using (new EnvScope(("COREAI_BENCHMARK_FREEBUILD_SUBJECT", null),
                       ("COREAI_BENCHMARK_FREEBUILD_PROMPT", null), ("COREAI_BENCHMARK_VISION_MODE", null)))
            {
                string castle = GameFreeBuildScenariosG6.All()[0].Goal;
                Assert.AreEqual(0.5, 2 * G6BuildAnalysis.ContactTolerance, "the goal says 'within half a stud'");
                Assert.AreEqual(36, G6BuildAnalysis.MinTowerCells, "the goal says '6 by 6 studs'");
                string[] construction =
                {
                    $"at least {Pct(G6BuildAnalysis.MinGroundedShare)} of parts must rest on the ground",
                    "touch, within half a stud, a part that does",
                    "repeat the same shape, size and placement",
                    $"at most {Pct(G6BuildAnalysis.MaxZFightingShare)} of parts may have a face lying flush",
                    $"at most {Pct(G6BuildAnalysis.MaxHiddenShare)} may sit entirely inside an opaque Block or " +
                    $"be thinner than {N(G6BuildAnalysis.MinDimension)} studs",
                    $"at most {Pct(G6BuildAnalysis.MaxDistortedShare)} may be squashed round shapes",
                    "keep Size.Y equal to Size.Z on a Cylinder and all three sides equal on a Ball"
                };
                string[] structure =
                {
                    $"at least {N(G6BuildAnalysis.WallCrestRise)} studs tall and " +
                    $"{N(G6BuildAnalysis.MinWallThickness)} studs thick, their base within " +
                    $"{N(G6BuildAnalysis.WallFootRise)} studs of the ground",
                    $"enclose at least {G6BuildAnalysis.MinCourtyardCells} square studs of open courtyard, ground " +
                    $"nothing taller than {N(G6BuildAnalysis.OpenYardRise)} studs stands on",
                    $"at ground level {2 * G6BuildAnalysis.MinGateClosingRadius - 1} to " +
                    $"{2 * G6BuildAnalysis.MaxClosingRadius} studs wide",
                    $"thinner than {N(G6BuildAnalysis.MinWallThickness)} studs do not close it",
                    $"at least {G6BuildAnalysis.MinTowers} of them, each at least 6 by 6 studs and rising at least " +
                    $"{N(G6BuildAnalysis.TowerRise)} studs above the wall crest",
                    $"at least {N(G6BuildAnalysis.RingDepth + 2)} studs in from the outer face of the walls, with a " +
                    $"footprint of at least {G6BuildAnalysis.MinKeepCells} square studs and at least as tall as " +
                    "the walls",
                    $"at least {G6BuildAnalysis.MinRoofedStructures} towers or buildings capped by Wedge, " +
                    "CornerWedge or Ball roofs",
                    $"at least {G6BuildAnalysis.MinCourtyardObjects} separate objects inside the walls (the keep " +
                    $"counts as one) and at least {G6BuildAnalysis.MinSurroundingObjects} beyond them",
                    $"the parts standing {N(G6BuildAnalysis.StructureRise)} or more studs above the ground cover " +
                    $"at least {G6BuildAnalysis.MinFootprintArea} square studs and span at least " +
                    $"{N(G6BuildAnalysis.MinSpan)} studs in both x and z",
                    "at least 12 different Enum.Material values and all five Enum.PartType shapes"
                };
                foreach (string phrase in construction)
                {
                    StringAssert.Contains(phrase, castle);
                }

                foreach (string phrase in structure)
                {
                    StringAssert.Contains(phrase, castle);
                }

                using (new EnvScope(("COREAI_BENCHMARK_FREEBUILD_SUBJECT", "a harbour town")))
                {
                    string generic = GameFreeBuildScenariosG6.All()[0].Goal;
                    foreach (string phrase in construction)
                    {
                        StringAssert.Contains(phrase, generic);
                    }

                    StringAssert.Contains($"the parts standing {N(G6BuildAnalysis.StructureRise)} or more studs " +
                                          $"above the ground must cover at least {G6BuildAnalysis.MinFootprintArea} " +
                                          $"square studs and span at least {N(G6BuildAnalysis.MinSpan)} studs in " +
                                          "both x and z", generic);
                    StringAssert.Contains($"rise at least {N(G6BuildAnalysis.MinRelief)} studs above the ground",
                        generic);
                    StringAssert.Contains($"at least {G6BuildAnalysis.MinSceneObjects} separate objects rather " +
                                          "than one heap", generic);
                    StringAssert.DoesNotContain("curtain", generic);
                }
            }
        }

        [Test]
        public void VisionTwin_SharesTheGoalAndSystemPrompt()
        {
            using (new EnvScope(("COREAI_BENCHMARK_FREEBUILD_SUBJECT", null),
                       ("COREAI_BENCHMARK_FREEBUILD_PROMPT", null), ("COREAI_BENCHMARK_VISION_MODE", "both")))
            {
                GameBenchmarkScenario[] all = GameFreeBuildScenariosG6.All();
                GameBenchmarkScenario plain = Array.Find(all, candidate => candidate.Id == "g6_free_build");
                GameBenchmarkScenario vision = Array.Find(all, candidate => candidate.Id == "g6_free_build_vision");

                Assert.IsNotNull(plain);
                Assert.IsNotNull(vision);
                Assert.AreEqual(plain.Goal, vision.Goal);
                Assert.AreEqual(plain.SystemPrompt, vision.SystemPrompt);
            }
        }

        [TestCase("Vector3.new(20,2,2)", "CFrame.new(63,2,0)", false)]
        [TestCase("Vector3.new(2,2,20)", "CFrame.new(0,2,-63)", false)]
        [TestCase("Vector3.new(2,4,2)", "CFrame.new(0,95,0)", false)]
        [TestCase("Vector3.new(2,2,2)", "CFrame.new(0,-1,0)", false)]
        [TestCase("Vector3.new(2,2,2)", "CFrame.new(0,0.5,0)", false)]
        [TestCase("Vector3.new(2,2,8)", "CFrame.new(62,2,0)*CFrame.Angles(0,math.pi/4,0)", false)]
        [TestCase("Vector3.new(8,2,2)", "CFrame.new(0,2,62)*CFrame.Angles(0,math.pi/4,0)", false)]
        [TestCase("Vector3.new(2,8,2)", "CFrame.new(0,2,0)*CFrame.Angles(0,0,math.pi/4)", false)]
        [TestCase("Vector3.new(128,96,128)", "CFrame.new(0,48,0)", true)]
        [TestCase("Vector3.new(2,2,2)", "CFrame.new(63.0005,1,0)", true)]
        [TestCase("Vector3.new(2,2,2)", "CFrame.new(63.01,1,0)", false)]
        [TestCase("Vector3.new(2,2,8)", "CFrame.new(62,2,0)*CFrame.Angles(0,math.pi/2,0)", false)]
        [TestCase("Vector3.new(8,2,2)", "CFrame.new(62,2,0)*CFrame.Angles(0,math.pi/2,0)", true)]
        public void LiveWorld_BuildVolumeUsesRotatedFullExtentsAndPublishedGround(string size, string cframe, bool inside)
        {
            using (new EnvScope(("COREAI_BENCHMARK_FREEBUILD_SUBJECT", null),
                       ("COREAI_BENCHMARK_FREEBUILD_PROMPT", null), ("COREAI_BENCHMARK_VISION_MODE", null)))
            {
                CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                BenchmarkEnvironment env = new(settings);
                try
                {
                    string chunk = "local p=Instance.new('Part'); p.Name='CastleBoundary'; p.Anchored=true; " +
                                   $"p.Size={size}; p.CFrame={cframe}; p.Parent=workspace";
                    Assert.IsTrue(env.RbxWorld.TryEvaluateLua(chunk, out string output), output);
                    RbxBenchmarkWorld.Snapshot snapshot = env.RbxWorld.Measure();
                    Assert.AreEqual(1, snapshot.Parts, "The actual Rbx world must contain the test part.");
                    Assert.AreEqual(inside ? 0 : 1, snapshot.OutOfBounds);
                    GameBenchmarkScenario scenario = Array.Find(GameFreeBuildScenariosG6.All(), candidate => candidate.Id == "g6_free_build");
                    BenchmarkCheckpoint gate = scenario.Grade(env, new RunObservation()).Checkpoints.Find(cp => cp.Id == "within_build_volume");
                    Assert.IsTrue(gate.Mandatory);
                    Assert.AreEqual(inside, gate.Passed, gate.Detail);
                }
                finally
                {
                    env.DisposeRbxWorld();
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }
        }

        private static ScenarioGrading GradeBuild(GameBenchmarkScenario scenario, List<PartSpec> specs)
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            BenchmarkEnvironment env = new(settings);
            try
            {
                BuildInWorld(env, specs);
                return scenario.Grade(env, new RunObservation { ToolCalls = 3 });
            }
            finally
            {
                env.DisposeRbxWorld();
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        private static double PassedShare(ScenarioGrading grading)
        {
            double total = 0, passed = 0;
            foreach (BenchmarkCheckpoint checkpoint in grading.Checkpoints)
            {
                total += checkpoint.Weight;
                passed += checkpoint.Passed ? checkpoint.Weight : 0;
            }

            return passed / total;
        }

        /// <summary>Builds the parts through the world's Lua surface, in sections like a model would.</summary>
        private static void BuildInWorld(BenchmarkEnvironment env, List<PartSpec> specs)
        {
            const string helper =
                "local function add(name, size, cframe, material, shape)\n" +
                "  local p = Instance.new('Part')\n" +
                "  p.Name = name\n" +
                "  p.Size = size\n" +
                "  p.CFrame = cframe\n" +
                "  p.Material = material\n" +
                "  p.Shape = shape\n" +
                "  p.Anchored = true\n" +
                "  p.Parent = workspace\n" +
                "end\n";
            for (int start = 0; start < specs.Count; start += 20)
            {
                StringBuilder chunk = new(helper);
                for (int i = start; i < Math.Min(specs.Count, start + 20); i++)
                {
                    chunk.Append(specs[i].ToLua());
                }

                Assert.IsTrue(env.RbxWorld.TryEvaluateLua(chunk.ToString(), out string output),
                    "building section " + start + " failed: " + output);
            }
        }

        private static string Pct(double share)
        {
            return (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        private static string N(double value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Sets environment variables for one test and restores the previous values afterwards.</summary>
        private sealed class EnvScope : IDisposable
        {
            private readonly Dictionary<string, string> _previous = new();

            public EnvScope(params (string key, string value)[] values)
            {
                foreach ((string key, string value) in values)
                {
                    _previous[key] = Environment.GetEnvironmentVariable(key);
                    Environment.SetEnvironmentVariable(key, value);
                }
            }

            public void Dispose()
            {
                foreach (KeyValuePair<string, string> entry in _previous)
                {
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
                }
            }
        }

        private static void AssertFailed(string fixture, string flaws, params string[] expected)
        {
            G6BuildMetrics m = G6BuildAnalysis.Analyze(ParseFixture(fixture));
            List<string> failed = FailedChecks(m, true);
            CollectionAssert.AreEquivalent(expected, failed, flaws + "\n" + Describe(m));
        }

        private static List<string> FailedChecks(G6BuildMetrics m, bool castle)
        {
            List<string> failed = new();
            foreach (G6Check check in G6BuildAnalysis.Checks(m, castle))
            {
                if (!check.Passed)
                {
                    failed.Add(check.Id);
                }
            }

            return failed;
        }

        /// <summary>Share of the geometry checkpoint weight a build earns (0..1).</summary>
        private static double GeometryScore(G6BuildMetrics m, bool castle)
        {
            double total = 0, earned = 0;
            foreach (G6Check check in G6BuildAnalysis.Checks(m, castle))
            {
                total += check.Weight;
                earned += check.Passed ? check.Weight : 0;
            }

            return earned / total;
        }

        private static string Describe(G6BuildMetrics m)
        {
            StringBuilder sb = new();
            foreach (G6Check check in G6BuildAnalysis.Checks(m, true))
            {
                sb.Append(check.Passed ? "[x] " : "[ ] ").Append(check.Id).Append(": ").Append(check.Detail)
                    .Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>Answers one dump page the way the Lua chunk does, at most <paramref name="perPage"/> records.</summary>
        private static string ServePage(string lua, List<string> records, int perPage)
        {
            Match match = Regex.Match(lua, @"local START, BUDGET, PREFIX = (\d+),");
            int start = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int last = Math.Min(records.Count, start + perPage - 1);
            StringBuilder sb = new();
            sb.Append("G6P|").Append(records.Count).Append('|').Append(last);
            for (int i = start; i <= last; i++)
            {
                sb.Append('\n').Append(records[i - 1]);
            }

            return sb.ToString();
        }

        /// <summary>A part as it would be written in Lua: CFrame.new(x, y, z) * CFrame.Angles(rx, ry, rz) in degrees.</summary>
        private readonly struct PartSpec
        {
            public PartSpec(string name, string shape, string material, double sx, double sy, double sz,
                double x, double y, double z, double rx = 0, double ry = 0, double rz = 0)
            {
                Name = name;
                Shape = shape;
                Material = material;
                Sx = sx;
                Sy = sy;
                Sz = sz;
                X = x;
                Y = y;
                Z = z;
                Rx = rx;
                Ry = ry;
                Rz = rz;
            }

            public string Name { get; }
            public string Shape { get; }
            public string Material { get; }
            public double Sx { get; }
            public double Sy { get; }
            public double Sz { get; }
            public double X { get; }
            public double Y { get; }
            public double Z { get; }
            public double Rx { get; }
            public double Ry { get; }
            public double Rz { get; }

            public PartSpec Renamed(string name)
            {
                return new PartSpec(name, Shape, Material, Sx, Sy, Sz, X, Y, Z, Rx, Ry, Rz);
            }

            public G6Part ToPart()
            {
                return G6Part.Create(Name, Shape, Material, Sx, Sy, Sz, X, Y, Z, Rx, Ry, Rz);
            }

            public string ToLua()
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "add('{0}', Vector3.new({1}, {2}, {3}), CFrame.new({4}, {5}, {6}) * " +
                    "CFrame.Angles(math.rad({7}), math.rad({8}), math.rad({9})), Enum.Material.{10}, " +
                    "Enum.PartType.{11})\n",
                    Name, Sx, Sy, Sz, X, Y, Z, Rx, Ry, Rz, Material, Shape);
            }
        }

        private static List<G6Part> Parts(List<PartSpec> specs)
        {
            List<G6Part> parts = new();
            foreach (PartSpec spec in specs)
            {
                parts.Add(spec.ToPart());
            }

            return parts;
        }

        /// <summary>
        /// A small but complete castle: curtain walls with merlons, four roofed corner towers, a gatehouse
        /// over an 8-stud gate, a roofed keep, seven courtyard props and a world beyond the walls. Walls end
        /// inside the towers and gatehouse piers, so no faces lie flush.
        /// </summary>
        private static List<PartSpec> CoherentCastle()
        {
            List<PartSpec> s = new()
            {
                new PartSpec("CastleGround", "Block", "Grass", 124, 1, 124, 0, -0.5, 0),
                new PartSpec("CastleWallNorth", "Block", "Cobblestone", 56, 12, 3, 0, 6, 32),
                new PartSpec("CastleWallEast", "Block", "Cobblestone", 3, 12, 56, 32, 6, 0),
                new PartSpec("CastleWallWest", "Block", "Cobblestone", 3, 12, 56, -32, 6, 0),
                new PartSpec("CastleWallSouthWest", "Block", "Cobblestone", 22, 12, 3, -17, 6, -32),
                new PartSpec("CastleWallSouthEast", "Block", "Cobblestone", 22, 12, 3, 17, 6, -32),
                new PartSpec("CastleGatePierWest", "Block", "Limestone", 6, 18, 7, -7, 9, -32),
                new PartSpec("CastleGatePierEast", "Block", "Limestone", 6, 18, 7, 7, 9, -32),
                new PartSpec("CastleGateLintel", "Block", "Granite", 8, 4, 7, 0, 16, -32),
                new PartSpec("CastleKeep", "Block", "Brick", 18, 24, 18, 0, 12, 6),
                new PartSpec("CastleKeepRoof", "Wedge", "ClayRoofTiles", 18, 6, 18, 0, 27, 6),
                new PartSpec("CastleWell", "Cylinder", "Rock", 3, 4, 4, -18, 1.5, -14, 0, 0, 90),
                new PartSpec("CastleStall", "Block", "Wood", 6, 3, 4, 18, 1.5, -16),
                new PartSpec("CastleStallCanopy", "Wedge", "Fabric", 6, 2, 4, 18, 4, -16),
                new PartSpec("CastleBarrel", "Cylinder", "WoodPlanks", 3, 2, 2, -20, 1.5, 18, 0, 0, 90),
                new PartSpec("CastleBarrelSpare", "Cylinder", "WoodPlanks", 3, 2, 2, -24, 1.5, 10, 0, 0, 90),
                new PartSpec("CastleCrate", "Block", "WoodPlanks", 2, 2, 2, -15, 1, 21),
                new PartSpec("CastleBench", "Block", "Wood", 5, 1, 1.5, 18, 0.5, 20),
                new PartSpec("CastleLampPost", "Cylinder", "Metal", 6, 0.6, 0.6, 14, 3, -4, 0, 0, 90),
                new PartSpec("CastleLampGlow", "Ball", "Neon", 1.2, 1.2, 1.2, 14, 6.6, -4),
                new PartSpec("CastleRoad", "Block", "Pavement", 8, 0.4, 24, 0, 0.2, -50),
                new PartSpec("CastleRock", "Ball", "Rock", 4, 4, 4, 50, 2, -46),
                new PartSpec("CastleHut", "Block", "Plaster", 6, 5, 6, -50, 2.5, -46),
                new PartSpec("CastleHutRoof", "Wedge", "RoofShingles", 6, 3, 6, -50, 6.5, -46)
            };

            int[] corners = { -32, 32 };
            foreach (int x in corners)
            {
                foreach (int z in corners)
                {
                    string tag = (z > 0 ? "N" : "S") + (x > 0 ? "E" : "W");
                    s.Add(new PartSpec("CastleTower" + tag, "Cylinder", "Sandstone", 22, 10, 10, x, 11, z, 0, 0, 90));
                    s.Add(new PartSpec("CastleTowerRoof" + tag, "CornerWedge", "RoofShingles", 12, 6, 12, x, 25, z));
                    s.Add(new PartSpec("CastleTowerFinial" + tag, "Ball", "Metal", 1.5, 1.5, 1.5, x, 28.75, z));
                }
            }

            for (int i = 0; i < 4; i++)
            {
                double offset = -18 + i * 12;
                s.Add(new PartSpec($"CastleMerlonNorth{i}", "Block", "Cobblestone", 2, 2, 2, offset, 13, 32));
                s.Add(new PartSpec($"CastleMerlonEast{i}", "Block", "Cobblestone", 2, 2, 2, 32, 13, offset));
                s.Add(new PartSpec($"CastleMerlonWest{i}", "Block", "Cobblestone", 2, 2, 2, -32, 13, offset));
            }

            s.Add(new PartSpec("CastleMerlonSouth0", "Block", "Cobblestone", 2, 2, 2, -20, 13, -32));
            s.Add(new PartSpec("CastleMerlonSouth1", "Block", "Cobblestone", 2, 2, 2, 20, 13, -32));
            for (int i = 0; i < 3; i++)
            {
                double x = i % 2 == 0 ? -48 : 48;
                double z = 44 - i * 8;
                s.Add(new PartSpec($"CastleTreeTrunk{i}", "Cylinder", "Wood", 8, 1.5, 1.5, x, 4, z, 0, 0, 90));
                s.Add(new PartSpec($"CastleTreeCrown{i}", "Ball", "LeafyGrass", 7, 7, 7, x, 11.5, z));
            }

            for (int i = 0; i < 4; i++)
            {
                s.Add(new PartSpec($"CastleShrub{i}", "Ball", "LeafyGrass", 2, 2, 2, -12 + i * 8, 1, 46));
            }

            return s;
        }

        /// <summary>The castle's walls and four towers only: a sealed ring with nothing inside or around it.</summary>
        private static List<PartSpec> BareRing()
        {
            List<PartSpec> s = new()
            {
                new PartSpec("CastleGround", "Block", "Grass", 124, 1, 124, 0, -0.5, 0),
                new PartSpec("CastleWallNorth", "Block", "Cobblestone", 56, 12, 3, 0, 6, 32),
                new PartSpec("CastleWallSouth", "Block", "Cobblestone", 56, 12, 3, 0, 6, -32),
                new PartSpec("CastleWallEast", "Block", "Cobblestone", 3, 12, 56, 32, 6, 0),
                new PartSpec("CastleWallWest", "Block", "Cobblestone", 3, 12, 56, -32, 6, 0)
            };
            int[] corners = { -32, 32 };
            foreach (int x in corners)
            {
                foreach (int z in corners)
                {
                    s.Add(new PartSpec($"CastleTower{x}_{z}", "Cylinder", "Sandstone", 22, 10, 10, x, 11, z, 0, 0, 90));
                }
            }

            return s;
        }

        /// <summary>Sixty varied parts piled in a 12 x 9 stud block at the origin, every one touching its neighbours.</summary>
        private static List<PartSpec> Heap()
        {
            string[] shapes = { "Block", "Ball", "Cylinder", "Wedge", "CornerWedge" };
            string[] materials =
            {
                "Brick", "Granite", "Wood", "Metal", "Glass", "Neon", "Sand", "Slate", "Marble", "Fabric",
                "Grass", "Ice", "Concrete", "Basalt", "Foil"
            };
            List<PartSpec> s = new();
            for (int i = 0; i < 60; i++)
            {
                int layer = i / 12, gx = i % 4, gz = i / 4 % 3;
                string shape = shapes[i % shapes.Length];
                double rz = shape == "Cylinder" ? 90 : 0;
                s.Add(new PartSpec($"CastleHeap{i}", shape, materials[i % materials.Length], 3, 3, 3,
                    gx * 3 - 4.5, layer * 3 + 1.5, gz * 3 - 3, 0, 0, rz));
            }

            return s;
        }

        /// <summary>
        /// Parses a compact fixture: one part per line, <c>Name Shape Material sx sy sz x y z [rx ry rz]</c>
        /// with the shape abbreviated (B Block, C Cylinder, W Wedge, CW CornerWedge, S Ball), the name
        /// without its Castle prefix and the CFrame rotation as CFrame.Angles degrees. The lists were read
        /// back with <see cref="G6BuildAnalysis.DumpLua"/> after replaying each run's <c>_g6_replay.lua</c>.
        /// </summary>
        private static List<G6Part> ParseFixture(string fixture)
        {
            List<G6Part> parts = new();
            foreach (string raw in fixture.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string[] t = line.Split(' ');
                double[] n = new double[9];
                for (int i = 3; i < t.Length; i++)
                {
                    n[i - 3] = double.Parse(t[i], CultureInfo.InvariantCulture);
                }

                parts.Add(G6Part.Create("Castle" + t[0], FixtureShape(t[1]), t[2], n[0], n[1], n[2], n[3], n[4], n[5],
                    n[6], n[7], n[8]));
            }

            return parts;
        }

        private static string FixtureShape(string code)
        {
            switch (code)
            {
                case "B":
                    return "Block";
                case "C":
                    return "Cylinder";
                case "W":
                    return "Wedge";
                case "CW":
                    return "CornerWedge";
                case "S":
                    return "Ball";
                default:
                    throw new ArgumentException("unknown fixture shape " + code);
            }
        }

        /// <summary>Read back from BENCHMARK_20260929_165050_codex-GPT-6-Sol_g6_replay.lua (154 parts).</summary>
        private const string SolCastle = @"
Ground B Grass 128 1 128 0 0.5 0
CourtyardBase B Sandstone 76 1 70 0 1 0
NorthWall B Limestone 72 16 4 0 9 33
EastWall B Limestone 4 16 66 36 9 0
WestWall B Limestone 4 16 66 -36 9 0
SouthWallLeft B Limestone 29 16 4 -21.5 9 -33
SouthWallRight B Limestone 29 16 4 21.5 9 -33
TowerNW C Cobblestone 25 11 11 -36 13 33 0 0 90
TowerNE C Cobblestone 25 11 11 36 13 33 0 0 90
TowerSW C Cobblestone 25 11 11 -36 13 -33 0 0 90
TowerSE C Cobblestone 25 11 11 36 13 -33 0 0 90
GatehouseLeft B Granite 6 23 11 -9 12.5 -33
GatehouseRight B Granite 6 23 11 9 12.5 -33
GatehouseLintel B Granite 24 5 11 0 22.5 -33
KeepNorth B Brick 28 27 3 0 15 21
KeepSouth B Brick 28 27 3 0 15 1
KeepEast B Brick 3 27 20 14 15 11
KeepWest B Brick 3 27 20 -14 15 11
KeepRoofDeck B Slate 30 2 22 0 29.5 11
ApproachRoad B Pavement 13 0.25 27 0 1.15 -49
KeepRoofWest W ClayRoofTiles 16 9 24 -8 34.5 11 0 90 0
KeepRoofEast W ClayRoofTiles 16 9 24 8 34.5 11 0 -90 0
TowerRoofNW CW RoofShingles 13 7 13 -36 29 33
TowerRoofNE CW RoofShingles 13 7 13 36 29 33 0 90 0
TowerRoofSE CW RoofShingles 13 7 13 36 29 -33 -180 0 -180
TowerRoofSW CW RoofShingles 13 7 13 -36 29 -33 0 -90 0
GatePortcullisTop B Metal 13 1 1 0 16 -38.5
GatePortcullisBar1 B Metal 0.6 14 0.6 -5 8.5 -38.5
GatePortcullisBar2 B Metal 0.6 14 0.6 -2.5 8.5 -38.5
GatePortcullisBar3 B Metal 0.6 14 0.6 0 8.5 -38.5
GatePortcullisBar4 B Metal 0.6 14 0.6 2.5 8.5 -38.5
GatePortcullisBar5 B Metal 0.6 14 0.6 5 8.5 -38.5
GatePortcullisCrossbar B Metal 12 0.6 0.6 0 7 -38.5
GateDrawbridge B WoodPlanks 13 0.8 12 0 1.8 -40
GateRamp W WoodPlanks 13 1.5 6 0 1.2 -47
KeepGreatDoor B Wood 6 11 0.5 0 7 -0.7
NorthCrenel1 B Limestone 5 3 4 -27 18.5 33
NorthCrenel2 B Limestone 5 3 4 -18 18.5 33
NorthCrenel3 B Limestone 5 3 4 -9 18.5 33
NorthCrenel4 B Limestone 5 3 4 0 18.5 33
NorthCrenel5 B Limestone 5 3 4 9 18.5 33
NorthCrenel6 B Limestone 5 3 4 18 18.5 33
NorthCrenel7 B Limestone 5 3 4 27 18.5 33
EastCrenel1 B Limestone 4 3 6 36 18.5 -21
EastCrenel2 B Limestone 4 3 6 36 18.5 -7
EastCrenel3 B Limestone 4 3 6 36 18.5 7
EastCrenel4 B Limestone 4 3 6 36 18.5 21
WestCrenel1 B Limestone 4 3 6 -36 18.5 -21
WestCrenel2 B Limestone 4 3 6 -36 18.5 -7
WestCrenel3 B Limestone 4 3 6 -36 18.5 7
WestCrenel4 B Limestone 4 3 6 -36 18.5 21
KeepWindow1 B Glass 3 5 0.4 -8 18 -0.5
KeepWindow2 B Glass 3 5 0.4 0 18 -0.5
KeepWindow3 B Glass 3 5 0.4 8 18 -0.5
WellStoneDrum C Cobblestone 3 7 7 -20 3 -8 0 0 90
WellWater C Glass 0.2 5.4 5.4 -20 4.6 -8 0 0 90
WellStoneRim C Marble 0.6 7.5 7.5 -20 5 -8 0 0 90
WellCanopyPost1 B Wood 0.7 7 0.7 -23 8.5 -11
WellCanopyPost2 B Wood 0.7 7 0.7 -17 8.5 -11
WellCanopyPost3 B Wood 0.7 7 0.7 -23 8.5 -5
WellCanopyPost4 B Wood 0.7 7 0.7 -17 8.5 -5
WellCanopyWest W RoofShingles 4 3 8 -22 13 -8 0 90 0
WellCanopyEast W RoofShingles 4 3 8 -18 13 -8 0 -90 0
ForgeBody B Plaster 12 9 13 23 5.8 -9
ForgeRoofLeft W ClayRoofTiles 7 4 15 19.5 12 -9 0 90 0
ForgeRoofRight W ClayRoofTiles 7 4 15 26.5 12 -9 0 -90 0
ForgeChimney C Brick 8 2 2 27 17 -12 0 0 90
MarketTable B WoodPlanks 9 0.6 4 -20 4 -23
MarketPostLeft B Wood 0.5 8 0.5 -24 6 -23
MarketPostRight B Wood 0.5 8 0.5 -16 6 -23
MarketAwning B Fabric 10 0.4 6 -20 10 -23
MarketBarrel C Wood 3 2 2 -14 3 -26 0 0 90
MoatEast B Glass 9 0.3 88 44 1.1 0
MoatWest B Glass 9 0.3 88 -44 1.1 0
MoatNorth B Glass 80 0.3 9 0 1.1 43
MoatSouthLeft B Glass 34 0.3 9 -24 1.1 -43
MoatSouthRight B Glass 34 0.3 9 24 1.1 -43
BridgeRailLeft B Wood 0.6 2 12 -6.5 3 -40
BridgeRailRight B Wood 0.6 2 12 6.5 3 -40
TreeTrunkWest C Wood 11 2 2 -55 6 -53 0 0 90
TreeCrownWest S LeafyGrass 13 13 13 -55 16 -53
TreeTrunkEast C Wood 10 2 2 54 6 51 0 0 90
TreeCrownEast S LeafyGrass 12 12 12 54 15 51
GateBannerLeft B Fabric 2.5 7 0.2 -9 22 -39
GateBannerRight B Fabric 2.5 7 0.2 9 22 -39
GateLanternLeft S Neon 1.7 1.7 1.7 -13 12 -39
GateLanternRight S Neon 1.7 1.7 1.7 13 12 -39
OuterCottageBody B Plaster 11 8 11 55 5 9
OuterCottageRoofLeft W RoofShingles 6 4 13 52 11 9 0 90 0
OuterCottageRoofRight W RoofShingles 6 4 13 58 11 9 0 -90 0
TowerRoofNWQuarter1 CW RoofShingles 13 7 13 -36 29 33 0 90 0
TowerRoofNWQuarter2 CW RoofShingles 13 7 13 -36 29 33 -180 0 -180
TowerRoofNWQuarter3 CW RoofShingles 13 7 13 -36 29 33 0 -90 0
TowerRoofNEQuarter1 CW RoofShingles 13 7 13 36 29 33 0 90 0
TowerRoofNEQuarter2 CW RoofShingles 13 7 13 36 29 33 -180 0 -180
TowerRoofNEQuarter3 CW RoofShingles 13 7 13 36 29 33 0 -90 0
TowerRoofSEQuarter1 CW RoofShingles 13 7 13 36 29 -33 0 90 0
TowerRoofSEQuarter2 CW RoofShingles 13 7 13 36 29 -33 -180 0 -180
TowerRoofSEQuarter3 CW RoofShingles 13 7 13 36 29 -33 0 -90 0
TowerRoofSWQuarter1 CW RoofShingles 13 7 13 -36 29 -33 0 90 0
TowerRoofSWQuarter2 CW RoofShingles 13 7 13 -36 29 -33 -180 0 -180
TowerRoofSWQuarter3 CW RoofShingles 13 7 13 -36 29 -33 0 -90 0
TowerFinialNW S Metal 2 2 2 -36 34 33
TowerFinialNE S Metal 2 2 2 36 34 33
TowerFinialSE S Metal 2 2 2 36 34 -33
TowerFinialSW S Metal 2 2 2 -36 34 -33
StableBody B WoodPlanks 13 8 14 25 5.5 18
StableRoofWest W RoofShingles 7 4 16 21.5 11 18 0 90 0
StableRoofEast W RoofShingles 7 4 16 28.5 11 18 0 -90 0
StableDoor B Wood 4 6 0.4 25 4.5 10.8
StableHay B Fabric 4 2 3 31 2.8 8
GardenBedLeft B Ground 5 0.4 12 -27 1.8 13
GardenBedRight B Ground 5 0.4 12 -20 1.8 13
GardenShrub1 S LeafyGrass 2.2 2.2 2.2 -27 3 8
GardenShrub2 S LeafyGrass 2.2 2.2 2.2 -20 3 8
GardenShrub3 S LeafyGrass 2.2 2.2 2.2 -27 3 12
GardenShrub4 S LeafyGrass 2.2 2.2 2.2 -20 3 12
GardenShrub5 S LeafyGrass 2.2 2.2 2.2 -27 3 16
GardenShrub6 S LeafyGrass 2.2 2.2 2.2 -20 3 16
CourtyardBenchSeat B WoodPlanks 7 0.5 1.5 -10 3 -21
CourtyardBenchLegLeft B Wood 0.5 1.5 1.5 -13 2 -21
CourtyardBenchLegRight B Wood 0.5 1.5 1.5 -7 2 -21
KeepFlagPole C Metal 13 0.4 0.4 0 44 11 0 0 90
KeepFlag B Fabric 7 4 0.2 3.5 48 11
KeepFlagEmblem S SmoothPlastic 1.4 1.4 0.25 3 48 10.8
KeepWindowFrameLeft B Sandstone 0.4 5.4 0.5 -9.7 18 -0.3
KeepWindowFrameRight B Sandstone 0.4 5.4 0.5 9.7 18 -0.3
KeepDoorStep B Granite 8 0.6 3 0 1.9 -2
ForgeFire S Neon 2 2 2 17 3 -9
ForgeAnvil B Metal 3 1.2 1.5 17 3 -13
CourtyardCrateOne B WoodPlanks 3 3 3 19 3 -23
CourtyardCrateTwo B WoodPlanks 2.5 2.5 2.5 22 2.8 -25
OuterRockWest B Rock 7 4 6 -55 2.5 22 0 23 0
OuterRockEast B Rock 6 3 5 54 2 35 0 -31 0
ApproachMarkerLeft C Basalt 7 2 2 -9 4 -54 0 0 90
ApproachMarkerRight C Basalt 7 2 2 9 4 -54 0 0 90
ApproachLampLeft S Neon 1.5 1.5 1.5 -9 8 -54
ApproachLampRight S Neon 1.5 1.5 1.5 9 8 -54
SouthCrenelLeft1 B Limestone 5 3 4 -31 18.5 -33
SouthCrenelLeft2 B Limestone 5 3 4 -22 18.5 -33
SouthCrenelLeft3 B Limestone 5 3 4 -13 18.5 -33
SouthCrenelRight1 B Limestone 5 3 4 13 18.5 -33
SouthCrenelRight2 B Limestone 5 3 4 22 18.5 -33
SouthCrenelRight3 B Limestone 5 3 4 31 18.5 -33
GatehouseCrenelLeft B Granite 4 3 11 -10 26.5 -33
GatehouseCrenelCenter B Granite 4 3 11 0 26.5 -33
GatehouseCrenelRight B Granite 4 3 11 10 26.5 -33
KeepParapetNorth B Slate 30 3 1 0 31 22
KeepParapetSouth B Slate 30 3 1 0 31 0
ForgeChimneyCap B Basalt 3 1 3 27 21 -12
ApproachMilestone C Limestone 5 2 2 -12 3 -59 0 0 90
ApproachMilestoneFinial S Marble 2 2 2 -12 6 -59
MarketCrate B WoodPlanks 3 2 3 -25 2.5 -27
StableWaterTrough B Wood 6 1.5 2 17 2.6 23";

        /// <summary>Read back from BENCHMARK_20260929_205051_opencode-space-bunny-free_g6_replay.lua (110 parts).</summary>
        private const string BunnyCastle = @"
Ground B Grass 128 2 128 0 -1 0
MoatNorth B Glass 104 0.3 8 0 0.1 -47
MoatSouthWest B Glass 44 0.3 8 -30 0.1 47
MoatSouthEast B Glass 44 0.3 8 30 0.1 47
MoatWest B Glass 8 0.3 64 -47 0.1 0
MoatEast B Glass 8 0.3 64 47 0.1 0
WallNorth B Cobblestone 64 11 3 0 5.5 -32
WallWest B Cobblestone 3 11 64 -38 5.5 1
WallEast B Cobblestone 3 11 64 38 5.5 1
WallSouthWest B Cobblestone 26 11 3 -23 5.5 34
WallSouthEast B Cobblestone 26 11 3 23 5.5 34
TowerNW C Limestone 20 11 11 -38 10 -30 0 0 90
TowerNE C Limestone 20 11 11 38 10 -30 0 0 90
TowerSW C Limestone 20 11 11 -38 10 30 0 0 90
TowerSE C Limestone 20 11 11 38 10 30 0 0 90
GateTowerWest C Brick 17 8 8 -10 8.5 34 0 0 90
GateTowerEast C Brick 17 8 8 10 8.5 34 0 0 90
Keep B Plaster 30 22 24 0 11 -10
KeepUpper B Limestone 24 8 18 0 25 -10
KeepRoofWest W ClayRoofTiles 15 7 22 -7.5 32.5 -10
KeepRoofEast W ClayRoofTiles 15 7 22 7.5 32.5 -10 -180 0 -180
TowerNWRoofQ1 CW ClayRoofTiles 7 7 7 -41.5 21.5 -33.5
TowerNWRoofQ2 CW ClayRoofTiles 7 7 7 -34.5 21.5 -33.5 0 90 0
TowerNWRoofQ3 CW ClayRoofTiles 7 7 7 -34.5 21.5 -26.5 -180 0 -180
TowerNWRoofQ4 CW ClayRoofTiles 7 7 7 -41.5 21.5 -26.5 0 -90 0
TowerNERoofQ1 CW Slate 7 7 7 34.5 21.5 -33.5
TowerNERoofQ2 CW Slate 7 7 7 41.5 21.5 -33.5 0 90 0
TowerNERoofQ3 CW Slate 7 7 7 41.5 21.5 -26.5 -180 0 -180
TowerNERoofQ4 CW Slate 7 7 7 34.5 21.5 -26.5 0 -90 0
TowerSWRoofQ1 CW ClayRoofTiles 7 7 7 -41.5 21.5 26.5
TowerSWRoofQ2 CW ClayRoofTiles 7 7 7 -34.5 21.5 26.5 0 90 0
TowerSWRoofQ3 CW ClayRoofTiles 7 7 7 -34.5 21.5 33.5 -180 0 -180
TowerSWRoofQ4 CW ClayRoofTiles 7 7 7 -41.5 21.5 33.5 0 -90 0
TowerSERoofQ1 CW Slate 7 7 7 34.5 21.5 26.5
TowerSERoofQ2 CW Slate 7 7 7 41.5 21.5 26.5 0 90 0
TowerSERoofQ3 CW Slate 7 7 7 41.5 21.5 33.5 -180 0 -180
TowerSERoofQ4 CW Slate 7 7 7 34.5 21.5 33.5 0 -90 0
NorthBattlement01 B Limestone 3 3 4 -30 12.5 -32
NorthBattlement02 B Limestone 3 3 4 -21 12.5 -32
NorthBattlement03 B Limestone 3 3 4 -12 12.5 -32
NorthBattlement04 B Limestone 3 3 4 -3 12.5 -32
NorthBattlement05 B Limestone 3 3 4 6 12.5 -32
NorthBattlement06 B Limestone 3 3 4 15 12.5 -32
NorthBattlement07 B Limestone 3 3 4 24 12.5 -32
SouthWestBattlement01 B Sandstone 3 3 4 -34 12.5 34
SouthWestBattlement02 B Sandstone 3 3 4 -25 12.5 34
SouthWestBattlement03 B Sandstone 3 3 4 -16 12.5 34
SouthEastBattlement01 B Sandstone 3 3 4 16 12.5 34
SouthEastBattlement02 B Sandstone 3 3 4 25 12.5 34
SouthEastBattlement03 B Sandstone 3 3 4 34 12.5 34
WallWalkNorth B Pavement 64 0.6 3.5 0 11.1 -32
WallWalkWest B Pavement 3.5 0.6 64 -38 11.1 0
WallWalkEast B Pavement 3.5 0.6 64 38 11.1 0
DrawbridgeDeck B WoodPlanks 12 0.7 17 0 0.45 43.5
DrawbridgeRailWest B Wood 0.6 2.2 17 -6.1 1.8 43.5
DrawbridgeRailEast B Wood 0.6 2.2 17 6.1 1.8 43.5
GateJambWest B Sandstone 2.2 8.5 4 -5.2 4.25 34
GateJambEast B Sandstone 2.2 8.5 4 5.2 4.25 34
GateLintelWest B Sandstone 3.6 2 4 -4 9.25 34
GateLintelCenter B Sandstone 3.6 2 4 0 9.25 34
GateLintelEast B Sandstone 3.6 2 4 4 9.25 34
GateParapet B Sandstone 18 2.5 4 0 11.75 34
PortcullisTopRail C Metal 8.4 0.4 0.4 0 8.1 35.2
PortcullisBottomRail C Metal 8.4 0.4 0.4 0 1.5 35.2
PortcullisBar01 C CorrodedMetal 8.4 0.35 0.35 -3.6 4.5 35.2 0 0 90
PortcullisBar02 C CorrodedMetal 8.4 0.35 0.35 -1.8 4.5 35.2 0 0 90
PortcullisBar03 C CorrodedMetal 8.4 0.35 0.35 0 4.5 35.2 0 0 90
PortcullisBar04 C CorrodedMetal 8.4 0.35 0.35 1.8 4.5 35.2 0 0 90
PortcullisBar05 C CorrodedMetal 8.4 0.35 0.35 3.6 4.5 35.2 0 0 90
PortcullisCrossbarLow C CorrodedMetal 8.4 0.4 0.4 0 2.3 35.2
PortcullisCrossbarMid C CorrodedMetal 8.4 0.4 0.4 0 4.5 35.2
PortcullisCrossbarHigh C CorrodedMetal 8.4 0.4 0.4 0 6.7 35.2
DrawbridgeCrossBeam B Metal 12.8 0.5 0.7 0 0.25 51.7
KeepWindowSouthWestFrame B Limestone 3 3.8 0.4 -10 8 2.15
KeepWindowSouthWestGlass B Glass 2.2 3 0.2 -10 8 2.42
KeepWindowSouthWestSill B Marble 3.6 0.4 1 -10 5.9 2.45
KeepWindowSouthEastFrame B Limestone 3 3.8 0.4 10 8 2.15
KeepWindowSouthEastGlass B Glass 2.2 3 0.2 10 8 2.42
KeepWindowSouthEastSill B Marble 3.6 0.4 1 10 5.9 2.45
KeepWindowUpperFrame B Brick 3.2 4 0.4 0 24.5 0.15
KeepWindowUpperGlass B Glass 2.3 3.1 0.2 0 24.5 0.42
KeepWindowUpperSill B Limestone 3.8 0.4 1 0 22.2 0.5
KeepGreatDoor B Wood 3.2 5.5 0.55 0 2.8 2.3
KeepThreshold B Marble 4.5 0.35 2.5 0 0.2 2.6
KeepWindowWestFrame B Limestone 3 3.8 0.4 -15.2 10 -7 0 90 0
KeepWindowWestGlass B Glass 2.2 3 0.2 -15.48 10 -7 0 90 0
KeepWindowEastFrame B Limestone 3 3.8 0.4 15.2 15 -16 0 90 0
KeepWindowEastGlass B Glass 2.2 3 0.2 15.48 15 -16 0 90 0
KeepFlagpole C Metal 13 0.35 0.35 0 43 -10 0 0 90
KeepBanner W Fabric 5 2.8 0.18 2.8 46.3 -10
KeepBannerFinial S Metal 1.3 1.3 1.3 0 50 -10
CourtyardFloor B Pavement 28 0.4 27 0 0.2 0
CourtyardPath B Cobblestone 8 0.3 30 0 0.5 16
WellRimNorth B Sandstone 6 1.4 1 -8 1.4 5
WellRimSouth B Sandstone 6 1.4 1 -8 1.4 -1
WellRimWest B Sandstone 1 1.4 6 -10.5 1.4 2
WellRimEast B Sandstone 1 1.4 6 -5.5 1.4 2
WellWater C Glass 0.3 4.5 4.5 -8 1.4 2 0 0 90
WellPostWest C Wood 0.6 4.5 0.6 -10 2.5 2 0 0 90
WellPostEast C Wood 0.6 4.5 0.6 -6 2.5 2 0 0 90
WellRoofWest W ClayRoofTiles 7 3 7 -8 5.4 2
WellRoofEast W ClayRoofTiles 7 3 7 -8 5.4 2 -180 0 -180
MarketCanopyWest W Fabric 9 3 6 -18 4 8
MarketCanopyEast W Fabric 9 3 6 15 4 9 -180 0 -180
MarketCounterWest B WoodPlanks 8 1 3 -18 1.1 8
MarketCounterEast B WoodPlanks 8 1 3 15 1.1 9
CampfireStone B Rock 2 1 2 -1 0.6 12
CampfireLogWest C Wood 4 0.7 0.7 -1 1 12
CampfireLogEast C Wood 4 0.7 0.7 -1 1 12 0 90 0
CampfireFlame S Neon 2.5 3.5 2.5 -1 2 12";

        /// <summary>Read back from BENCHMARK_20260929_200407_opencode-space-bunny-free_g6_replay.lua (76 parts).</summary>
        private const string BunnySparseCastle = @"
Ground B Grass 132 1 132 0 -0.5 0
MoatNorth B Glass 108 0.25 8 0 0.08 43
MoatSouth B Glass 108 0.25 8 0 0.08 -43
MoatWest B Glass 8 0.25 78 -51 0.08 0
MoatEast B Glass 8 0.25 78 51 0.08 0
WallNorth B Cobblestone 88 10 3 0 5 34
WallSouthWest B Cobblestone 32 10 3 -26 5 -34
WallSouthEast B Cobblestone 32 10 3 26 5 -34
WallWest B Cobblestone 3 10 68 -42 5 0
WallEast B Cobblestone 3 10 68 42 5 0
TowerNW C Granite 12 10 10 -42 7 34 0 0 90
TowerNE C Granite 12 10 10 42 7 34 0 0 90
TowerSW C Granite 12 10 10 -42 7 -34 0 0 90
TowerSE C Granite 12 10 10 42 7 -34 0 0 90
GateTowerWest C Sandstone 14 12 11 -10 8 34 0 0 90
GateTowerEast C Sandstone 14 12 11 10 8 34 0 0 90
GateLintel B Basalt 20 7 5 0 10.5 34
KeepBase B Limestone 32 24 24 0 12 -10
KeepUpper B Marble 24 10 18 0 29 -10
KeepRoof B Slate 28 2 22 0 35 -10
NorthMerlon_40 B Granite 4 2 4 -40 11 34
NorthMerlon_30 B Granite 4 2 4 -30 11 34
NorthMerlon_20 B Granite 4 2 4 -20 11 34
NorthMerlon_10 B Granite 4 2 4 -10 11 34
NorthMerlon10 B Granite 4 2 4 10 11 34
NorthMerlon20 B Granite 4 2 4 20 11 34
NorthMerlon30 B Granite 4 2 4 30 11 34
NorthMerlon40 B Granite 4 2 4 40 11 34
SouthMerlon_36 B Granite 4 2 4 -36 11 -34
SouthMerlon_24 B Granite 4 2 4 -24 11 -34
SouthMerlon_12 B Granite 4 2 4 -12 11 -34
SouthMerlon12 B Granite 4 2 4 12 11 -34
SouthMerlon24 B Granite 4 2 4 24 11 -34
SouthMerlon36 B Granite 4 2 4 36 11 -34
WestMerlonS B Granite 4 2 4 -42 11 -20
WestMerlonN B Granite 4 2 4 -42 11 20
EastMerlonS B Granite 4 2 4 42 11 -20
EastMerlonN B Granite 4 2 4 42 11 20
KeepGableWest W Slate 15 1.5 24 -7 38.2 -10 0 0 -30
KeepGableEast W Slate 15 1.5 24 7 38.2 -10 0 0 30
KeepRidge C Metal 1 1 25 0 44 -10
KeepNorthMerlon_11 B Granite 2.2 2.5 2.2 -11 36.1 1.5
KeepNorthMerlon_8 B Granite 2.2 2.5 2.2 -8 36.1 1.5
KeepNorthMerlon_5 B Granite 2.2 2.5 2.2 -5 36.1 1.5
KeepNorthMerlon_2 B Granite 2.2 2.5 2.2 -2 36.1 1.5
KeepNorthMerlon1 B Granite 2.2 2.5 2.2 1 36.1 1.5
KeepNorthMerlon4 B Granite 2.2 2.5 2.2 4 36.1 1.5
KeepNorthMerlon7 B Granite 2.2 2.5 2.2 7 36.1 1.5
KeepNorthMerlon10 B Granite 2.2 2.5 2.2 10 36.1 1.5
KeepSouthMerlon_10 B Granite 2.2 2.5 2.2 -10 36.1 -21.5
KeepSouthMerlon_6 B Granite 2.2 2.5 2.2 -6 36.1 -21.5
KeepSouthMerlon_2 B Granite 2.2 2.5 2.2 -2 36.1 -21.5
KeepSouthMerlon2 B Granite 2.2 2.5 2.2 2 36.1 -21.5
KeepSouthMerlon6 B Granite 2.2 2.5 2.2 6 36.1 -21.5
KeepSouthMerlon10 B Granite 2.2 2.5 2.2 10 36.1 -21.5
KeepWindowWest B Glass 3.2 6 0.3 -5 28.5 0.9
KeepWindowEast B Glass 3.2 6 0.3 5 28.5 0.9
GateArchLeftPost B Sandstone 1.4 7 1.4 -4.2 3.5 36.9
GateArchRightPost B Sandstone 1.4 7 1.4 4.2 3.5 36.9
GateArchLeftWedge W Sandstone 4.2 4.2 1.4 -3.25 6.1 36.9 0 0 45
GateArchRightWedge W Sandstone 4.2 4.2 1.4 3.25 6.1 36.9 0 0 -45
GateArchKeystone B Limestone 2.6 2 1.6 0 7.35 36.9
PortcullisBar1 C CorrodedMetal 7 0.38 0.38 -3.1 4 35.2 0 0 90
PortcullisSpike1 W Metal 0.65 1.4 0.65 -3.1 0.35 35.2 -180 0 0
PortcullisBar2 C CorrodedMetal 7 0.38 0.38 -1.55 4 35.2 0 0 90
PortcullisSpike2 W Metal 0.65 1.4 0.65 -1.55 0.35 35.2 -180 0 0
PortcullisBar3 C CorrodedMetal 7 0.38 0.38 0 4 35.2 0 0 90
PortcullisSpike3 W Metal 0.65 1.4 0.65 0 0.35 35.2 -180 0 0
PortcullisBar4 C CorrodedMetal 7 0.38 0.38 1.55 4 35.2 0 0 90
PortcullisSpike4 W Metal 0.65 1.4 0.65 1.55 0.35 35.2 -180 0 0
PortcullisBar5 C CorrodedMetal 7 0.38 0.38 3.1 4 35.2 0 0 90
PortcullisSpike5 W Metal 0.65 1.4 0.65 3.1 0.35 35.2 -180 0 0
PortcullisRailLow C CorrodedMetal 8.2 0.45 0.45 0 2.4 35.2
PortcullisRailHigh C CorrodedMetal 8.2 0.45 0.45 0 5.6 35.2
DrawbridgeDeck B WoodPlanks 10 1 20 0 0.65 46.5 1.489 0 0
DrawbridgeHinge C CorrodedMetal 10.8 0.9 0.9 0 0.75 36.8";

        /// <summary>Read back from BENCHMARK_20260929_163934_codex-GPT-6-Luna_g6_replay.lua (188 parts).</summary>
        private const string LunaCastle = @"
Ground B Grass 120 1 120 0 -0.5 0
MoatNorth B Water 100 1 9 0 0.05 -44
MoatSouth B Water 100 1 9 0 0.05 44
MoatWest B Water 9 1 78 -45 0.05 0
MoatEast B Water 9 1 78 45 0.05 0
WallNorth B Cobblestone 76 13 4 0 7 -34
WallSouthL B Cobblestone 27 13 4 -24 7 34
WallSouthR B Cobblestone 27 13 4 24 7 34
WallWest B Cobblestone 4 13 64 -38 7 0
WallEast B Cobblestone 4 13 64 38 7 0
WallWalkNorth B Slate 76 1 5 0 14 -34
WallWalkSouth B Slate 76 1 5 0 14 34
WallWalkWest B Slate 5 1 64 -38 14 0
WallWalkEast B Slate 5 1 64 38 14 0
Gatehouse B Brick 18 21 10 0 10.5 32
GatePassage B Basalt 8 12 11 0 6 32
Drawbridge W WoodPlanks 12 1 18 0 1 -48 8 0 0
GatePortcullisBar1 B Metal 1 12 1 -3.5 6 29
GatePortcullisBar2 B Metal 1 12 1 -1.2 6 29
GatePortcullisBar3 B Metal 1 12 1 1.2 6 29
GatePortcullisBar4 B Metal 1 12 1 3.5 6 29
GateCrossbarA B Metal 8 1 1 0 2 29
GateCrossbarB B Metal 8 1 1 0 7 29
KeepBase B Limestone 29 19 24 0 10.5 -3
KeepUpper B Brick 23 17 19 0 28.5 -4
KeepTowerWest B Granite 7 30 7 -11 34 -6
KeepTowerEast B Granite 7 30 7 11 34 -6
KeepRoofWest W ClayRoofTiles 10 8 11 -5.5 41 -4
KeepRoofEast W ClayRoofTiles 10 8 11 5.5 41 -4 -180 0 -180
KeepFinial S Metal 3 3 3 0 46 -4
TowerNW C Cobblestone 12 29 12 -35 17 -31
TowerNE C Cobblestone 12 29 12 35 17 -31
TowerSW C Cobblestone 12 29 12 -35 17 31
TowerSE C Cobblestone 12 29 12 35 17 31
TowerCapNW CW RoofShingles 11 8 11 -35 35 -31
TowerCapNE CW RoofShingles 11 8 11 35 35 -31 0 90 0
TowerCapSW CW RoofShingles 11 8 11 -35 35 31 -180 0 -180
TowerCapSE CW RoofShingles 11 8 11 35 35 31 0 -90 0
TowerFlagNW C Wood 1 8 1 -35 43 -31
TowerFlagNE C Wood 1 8 1 35 43 -31
BannerNorth C Wood 1 5 1 -35 40 -30
BannerClothNorth B Fabric 5 4 0.4 -35 39 -29
BannerSouth C Wood 1 5 1 35 40 30
BannerClothSouth B Fabric 5 4 0.4 35 39 29
WellRim C Pebble 8 2 8 -12 1 13
WellWater C Glass 5 0.4 5 -12 2.1 13
WellPost1 B Wood 0.7 8 0.7 -15.5 5 13
WellPost2 B Wood 0.7 8 0.7 -8.5 5 13
WellBeam C Wood 1 1 8 -12 9 13 0 0 90
CourtyardHall B Plaster 18 10 13 13 5 -21
HallRoofWest W ClayRoofTiles 10 6 15 8 13 -21
HallRoofEast W ClayRoofTiles 10 6 15 18 13 -21 -180 0 -180
HallChimney B Brick 2 8 2 18 17 -21
Stable B WoodPlanks 16 8 10 -19 4 -20
StableRoof W RoofShingles 17 5 12 -19 10 -20
StableDoor B Wood 4 6 0.5 -19 3 -14.7
Storehouse B Sandstone 11 8 10 23 4 14
StoreRoof W RoofShingles 12 5 11 23 10 14
CourtyardRoad B Pavement 9 0.3 60 0 0.2 0
GateRoad B Pavement 12 0.4 19 0 0.4 -53
Crate1 B Wood 3 3 3 -21 1.5 7
Crate2 B WoodPlanks 2.5 2.5 2.5 -17 1.25 7
Barrel1 C Wood 3 4 3 -23 2 11
BarrelBand1 C Metal 3.1 0.4 3.1 -23 1.3 11
BarrelBand2 C Metal 3.1 0.4 3.1 -23 2.7 11
MarketAwningPole1 B Wood 0.6 6 0.6 21 3 0
MarketAwningPole2 B Wood 0.6 6 0.6 29 3 0
MarketAwning B Fabric 10 1 6 25 6.3 0
BenchSeat B WoodPlanks 7 0.7 2 -2 2 16
BenchLeg1 B Wood 0.7 2 0.7 -4.5 1 16
BenchLeg2 B Wood 0.7 2 0.7 0.5 1 16
LanternPost B Metal 0.7 7 0.7 -24 3.5 -5
LanternGlow S Neon 2 2 2 -24 7.5 -5
GardenBed B Mud 13 0.6 7 24 0.4 -2
GardenHedgeA B LeafyGrass 1 3 7 18 1.8 -2
GardenHedgeB B LeafyGrass 1 3 7 30 1.8 -2
GardenHedgeC B LeafyGrass 13 3 1 24 1.8 -5
RockOutcrop1 S Rock 8 5 7 -53 2 -15
RockOutcrop2 S Granite 6 4 5 52 2 19
TreeTrunk1 C Wood 2 9 2 -53 4.5 14
TreeCrown1 S LeafyGrass 9 8 9 -53 12 14
TreeTrunk2 C Wood 2 8 2 52 4 14
TreeCrown2 S Grass 8 7 8 52 11 14
RoadMarker S Marble 2 2 2 7 1 -54
MerlonNorthA B Limestone 3 3 4 -31 15 -34
MerlonNorthB B Limestone 3 3 4 -20 15 -34
MerlonNorthC B Limestone 3 3 4 -9 15 -34
MerlonNorthD B Limestone 3 3 4 2 15 -34
MerlonNorthE B Limestone 3 3 4 13 15 -34
MerlonNorthF B Limestone 3 3 4 24 15 -34
MerlonWestA B Limestone 4 3 3 -38 15 -22
MerlonWestB B Limestone 4 3 3 -38 15 -10
MerlonWestC B Limestone 4 3 3 -38 15 2
KeepWindowWest B Glass 2 4 0.5 -7 29 -13
KeepWindowEast B Glass 2 4 0.5 7 29 -13
KeepWindowFront B Glass 2 4 0.5 -7 29 5.7
KeepWindowFrontEast B Glass 2 4 0.5 7 29 5.7
CourtyardFountainBowl C Marble 7 2 7 12 1 8
FountainWater C Glass 4 0.4 4 12 2.2 8
FountainSpout C Limestone 0.8 4 0.8 12 4 8
FountainOrb S Glass 2 2 2 12 6.5 8
OuterRoadStone B Pebble 14 1 12 0 0.6 -57
GardenFlowerBed B Ground 7 0.5 5 24 0.8 -2
GardenFlowerRed S Neon 1.2 1.2 1.2 22 1.7 -2
MerlonEastA B Limestone 4 3 3 38 15 -22
MerlonEastB B Limestone 4 3 3 38 15 -10
MerlonEastC B Limestone 4 3 3 38 15 2
MerlonSouthA B Limestone 3 3 4 -30 15 34
MerlonSouthB B Limestone 3 3 4 -18 15 34
MerlonSouthC B Limestone 3 3 4 18 15 34
MerlonSouthD B Limestone 3 3 4 30 15 34
GatehouseRoof W Slate 20 7 12 0 22 -32
GatehouseTowerLeft B Brick 6 12 7 -7 28 31
GatehouseTowerRight B Brick 6 12 7 7 28 31
GatehouseCapLeft CW ClayRoofTiles 6 5 7 -7 36 31
GatehouseCapRight CW ClayRoofTiles 6 5 7 7 36 31 -180 0 -180
CourtyardFlagPole C Metal 0.8 11 0.8 0 31 -32
CourtyardFlag B Fabric 7 4 0.5 3 34 -32
OuterFencePost B Wood 1 5 1 -55 2.5 28
OuterFenceRail B WoodPlanks 12 0.8 0.8 -49 3 28
OuterFenceRailLower B WoodPlanks 12 0.8 0.8 -49 1.5 28
PathLantern S Neon 2 2 2 0 2 -55
PathLanternBase C Granite 2 1 2 0 0.5 -55
CourtyardOven B Brick 4 5 4 25 2.5 20
OvenRoof W ClayRoofTiles 5 3 5 25 6.5 20
OvenMouth B Basalt 1.5 2 0.4 25 2 17.8
Chapel B Limestone 13 12 16 -2 6 -19
ChapelRoofWest W Slate 8 6 18 -6 15 -19
ChapelRoofEast W Slate 8 6 18 2 15 -19 -180 0 -180
ChapelBellTower B Brick 5 11 5 -2 23 -19
ChapelBell C Metal 1.5 2 1.5 -2 22 -19
ChapelSpire CW RoofShingles 5 5 5 -2 31 -19
ChapelCrossStem B Metal 0.5 4 0.5 -2 36 -19
ChapelCrossArm B Metal 3 0.5 0.5 -2 36 -19
ChapelWindow B Glass 2 4 0.5 -2 6 -10.7
EastWatchPlatform B WoodPlanks 12 1 12 52 8 -25
EastWatchPost1 C Wood 1 8 1 47 12 -30
EastWatchPost2 C Wood 1 8 1 57 12 -30
EastWatchRoof W RoofShingles 12 5 12 52 17 -25
OuterRock3 S Sandstone 7 4 6 -54 2 -28
OuterRock4 S Rock 5 3 5 54 1.5 -7
CourtyardFlagOrb S Metal 1.8 1.8 1.8 0 37 -32
GateLantern S Neon 2 2 2 -10 20 31
GateLanternRight S Neon 2 2 2 10 20 31
KeepFrontDoor B Wood 4 7 0.6 0 3.5 9.3
KeepDoorLintel B Sandstone 6 1 1 0 7.5 9.3
KeepWindowFrameWest B Wood 3 5 0.7 -7 29 5.9
KeepWindowFrameEast B Wood 3 5 0.7 7 29 5.9
KeepUpperCornice B Limestone 24 1 20 0 37 -4
TowerDrumNW C Brick 13 3 13 -35 32 -31
TowerDrumNE C Brick 13 3 13 35 32 -31
TowerDrumSW C Brick 13 3 13 -35 32 31
TowerDrumSE C Brick 13 3 13 35 32 31
TowerCrestNW C Metal 1 4 1 -35 42 -31
TowerCrestNE C Metal 1 4 1 35 42 -31
TowerCrestSW C Metal 1 4 1 -35 42 31
TowerCrestSE C Metal 1 4 1 35 42 31
TowerOrbNW S Metal 2 2 2 -35 45 -31
TowerOrbNE S Metal 2 2 2 35 45 -31
TowerOrbSW S Metal 2 2 2 -35 45 31
TowerOrbSE S Metal 2 2 2 35 45 31
GateKeystone B Marble 2 3 1 0 15 26.8
GateCrest S Marble 3 3 3 0 27 31
NorthArrowSlit1 B Basalt 0.6 4 0.4 -24 9 -31.8
NorthArrowSlit2 B Basalt 0.6 4 0.4 0 9 -31.8
NorthArrowSlit3 B Basalt 0.6 4 0.4 24 9 -31.8
WestArrowSlit1 B Basalt 0.4 4 0.6 -35.8 9 -18
WestArrowSlit2 B Basalt 0.4 4 0.6 -35.8 9 5
EastArrowSlit1 B Basalt 0.4 4 0.6 35.8 9 -18
EastArrowSlit2 B Basalt 0.4 4 0.6 35.8 9 5
CourtyardPavingA B CeramicTiles 12 0.4 8 -20 0.25 -2
CourtyardPavingB B Pavement 10 0.4 7 18 0.25 22
CourtyardFireBowl C Basalt 4 1.5 4 -3 1 5
CourtyardFire S Neon 1.8 2.4 1.8 -3 2.8 5
StableWindow B Glass 2 2 0.5 -19 5 -14.7
StoreDoor B Wood 3 5 0.5 23 2.5 8.7
StoreWindow B Glass 2 3 0.5 27 5 8.7
MarketCounter B WoodPlanks 9 2 2 25 2 3
MarketFruitA S Neon 1.4 1.4 1.4 23 3.7 3
MarketFruitB S Neon 1.4 1.4 1.4 26 3.7 3
CourtyardBarrel2 C Wood 2.5 3.5 2.5 -28 1.75 4
CourtyardRockGarden S Pebble 5 2 4 30 1 -12
CrenelSouthE B Limestone 3 3 4 -6 15 34
CrenelSouthF B Limestone 3 3 4 6 15 34
PillarBase B Sandstone 4 1 4 31 0.5 -16
GardenHedgeD B LeafyGrass 13 2 1 24 1.3 1
GardenRoseA S Neon 1.3 1.3 1.3 20 2.3 -2
GardenRoseB S Neon 1.3 1.3 1.3 27 2.3 -2";

        /// <summary>Read back from BENCHMARK_20260929_154152_codex-GPT-6-Luna_g6_replay.lua (38 parts).</summary>
        private const string LunaUnfinishedCastle = @"
Ground B Grass 128 1 128 0 -0.5 0
Base B Pavement 72 2 62 0 1 0
NorthWall B Cobblestone 64 12 3 0 8 -29
SouthWallL B Cobblestone 23 12 3 -20 8 29
SouthWallR B Cobblestone 23 12 3 20 8 29
WestWall B Limestone 3 12 58 -31 8 0
EastWall B Limestone 3 12 58 31 8 0
Gatehouse B Brick 16 18 10 0 11 29
GateDoor B WoodPlanks 9 12 1 0 7 34
PortcullisBar1 C Metal 0.5 12 0.5 -3 7 35
PortcullisBar2 C Metal 0.5 12 0.5 0 7 35
PortcullisBar3 C Metal 0.5 12 0.5 3 7 35
TowerNW C Granite 12 23 12 -30 12.5 -29
TowerNE C Granite 12 23 12 30 12.5 -29
TowerSW C Granite 12 23 12 -30 12.5 29
TowerSE C Granite 12 23 12 30 12.5 29
Keep B Slate 25 27 21 -3 15.5 -5
KeepRoof W ClayRoofTiles 27 13 24 -3 35.5 -5 0 0 90
GatehouseRoof W RoofShingles 18 10 12 0 25 29 0 0 90
TowerRoofNW CW CeramicTiles 7 7 7 -30 27 -29
TowerRoofNE CW CeramicTiles 7 7 7 30 27 -29
TowerRoofSW CW CeramicTiles 7 7 7 -30 27 29
TowerRoofSE CW CeramicTiles 7 7 7 30 27 29
KeepFinial S Metal 3 3 3 -3 43 -5
CourtyardWell C Rock 7 3 7 12 3 5
WellWater C Glass 5 0.4 5 12 4.7 5
WellRoofPostL C Wood 0.7 5 0.7 9 6 5
WellRoofPostR C Wood 0.7 5 0.7 15 6 5
WellCanopy W RoofShingles 8 4 6 12 9 5 0 0 90
KeepWindow1 B Glass 2 4 0.5 -3 22 -15
KeepWindow2 B Glass 2 4 0.5 3 22 -15
BannerPole C Metal 0.5 10 0.5 0 33 29
Banner B Fabric 5 7 0.4 3 32 29
Road B Pebble 14 0.4 30 0 0.2 48
Drawbridge B WoodPlanks 12 1 9 0 1 37 -8 0 0
Moat B Water 78 0.5 4 0 0 37
MarketStall B WoodPlanks 9 1 6 17 5 14
StallCanopy B Fabric 10 1 7 17 9 14";
    }
}
#endif
#endif
