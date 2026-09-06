using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.CompatibilityCorpus
{
    /// <summary>
    /// Executes every frozen Tier-A fixture through the production Lua-CSharp composition and locks
    /// its recorded compatibility classification to the observed runtime result.
    /// </summary>
    [TestFixture]
    public sealed class TierACorpusEditModeTests
    {
        private const int KeyE = 101;
        private const string ResultAttribute = "TierACorpusResult";
        private const int FrozenFixtureCount = 20;
        private const int MinimumUnmodifiedPercent = 30;
        private const int FrozenTierBFixtureCount = 10;

        /// <summary>
        /// The MVP8 acceptance threshold over Tier-A and Tier-B together.
        /// </summary>
        /// <remarks>
        /// WHY the combined figure is higher than Tier-A's own 30%: Tier-A deliberately includes
        /// probes for surfaces CoreAI has not built yet, so its own bar is a floor, not a target.
        /// Tier-B measures whole gameplay idioms, which is what "can you build a game on this"
        /// actually means, and MVP8's gate is 60% of the two tiers together.
        /// </remarks>
        private const int MinimumCombinedUnmodifiedPercent = 60;

        private static readonly string[] TierBFixtureIds =
        {
            "TBC-001-kill-brick",
            "TBC-002-touch-pickup-with-leaderstats",
            "TBC-003-door-tween",
            "TBC-004-raycast-ground-check",
            "TBC-005-humanoid-damage-loop",
            "TBC-006-collection-service-respawner",
            "TBC-007-player-leave-save",
            "TBC-008-tween-cancel-restart",
            "TBC-009-attribute-driven-config",
            "TBC-010-gravity-low-jump"
        };
        private static readonly string[] FrozenFixtureIds =
        {
            "TAC-001-instance-parent-last",
            "TAC-002-part-properties",
            "TAC-003-attributes-change-signal",
            "TAC-004-signal-connect-disconnect",
            "TAC-005-signal-once",
            "TAC-006-signal-wait",
            "TAC-007-task-scheduling",
            "TAC-008-runservice-heartbeat-loop",
            "TAC-009-userinput-began",
            "TAC-010-vector3-math",
            "TAC-011-cframe-math",
            "TAC-012-color3-math",
            "TAC-013-getservice-identity",
            "TAC-014-destroy-pcall-cleanup",
            "TAC-015-script-parent-property-signal",
            "TAC-016-generic-for-descendants",
            "TAC-017-waitforchild-yield",
            "TAC-018-contextaction-bind",
            "TAC-019-tween-create",
            "TAC-020-players-localplayer"
        };

        private SynchronizationContext _savedContext;
        private ILog _savedLog;
        private CapturingLog _capturingLog;
        private StubHitCounter _stubHitCounter;

        [SetUp]
        public void SetUpHarnessEnvironment()
        {
            _savedContext = SynchronizationContext.Current;
            _savedLog = Log.Instance;
            _capturingLog = new CapturingLog();
            Log.Instance = _capturingLog;
            SynchronizationContext.SetSynchronizationContext(null);
            _stubHitCounter = new StubHitCounter();
        }

        [TearDown]
        public void RestoreHarnessEnvironment()
        {
            _stubHitCounter.Dispose();
            Log.Instance = _savedLog;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        /// <summary>
        /// Counts every NOT_IMPLEMENTED stub raise at the point the error is BUILT, through
        /// <see cref="RbxStubRaiseObserver"/>.
        /// </summary>
        /// <remarks>
        /// WHY the raise site and not the log: nothing in the runtime logs when a loud stub is
        /// raised — a stub is a plain <c>throw</c> — so a fixture that wraps the call in
        /// <c>pcall</c> swallows the error, emits nothing, and reads as a clean pass. The corpus's
        /// central claim ("this fixture ran unmodified against real API, not around a stub") was
        /// therefore unproven.
        /// <para>
        /// WHY not <c>AppDomain.FirstChanceException</c>: that is the framework answer, and it was
        /// tried first. Unity's Mono exposes the event but never raises it, so the counter reported
        /// zero for a raise that definitely happened — measured on a full EditMode run, not assumed.
        /// </para>
        /// </remarks>
        private sealed class StubHitCounter : IDisposable
        {
            public int Count { get; private set; }

            public StubHitCounter()
            {
                RbxStubRaiseObserver.Raised += OnRaised;
            }

            private void OnRaised(string code)
            {
                if (code == "NOT_IMPLEMENTED")
                {
                    Count++;
                }
            }

            public void Dispose()
            {
                RbxStubRaiseObserver.Raised -= OnRaised;
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
                List<(string ModId, string Key)> keys = new();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (key.ModId == modId)
                    {
                        keys.Add(key);
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class CapturingGameLogger : IGameLogger
        {
            public readonly List<string> Messages = new();
            public readonly List<string> Errors = new();

            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Messages.Add(message ?? "");
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Messages.Add(message ?? "");
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Messages.Add(message ?? "");
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                string text = message ?? "";
                Messages.Add(text);
                Errors.Add(text);
            }
        }

        private sealed class CapturingLog : ILog
        {
            public readonly List<string> Errors = new();

            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
            }

            public void Error(string message, string tag = null)
            {
                Errors.Add(message ?? "");
            }
        }

        private sealed class RuntimeHarness
        {
            public RuntimeHarness(CapturingLog runtimeLog)
            {
                Input = new InMemoryInputSource();
                InstanceRegistry registry = new(
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
                RbxApi = new LuaCsRbxApiBindings(registry: registry, inputSource: Input);
                Logger = new CapturingGameLogger();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = Logger,
                    ModStore = new MemoryStore(),
                    Log = runtimeLog,
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    RbxApi = RbxApi,
                    RegisterWorldEditBuildBindings = false
                });
                RbxApi.Scheduler.ThreadFaulted += (string modId, RbxError error) =>
                    ThreadFaults.Add(modId + ": " + error);
                Physics = new ScriptedContactPort();
                RbxApi.WorldPhysics.AttachPort(Physics);

                // WHY the corpus has one connected player: a Roblox SERVER always does, and the
                // idioms this corpus measures (save on leave, leaderstats under a Player) are server
                // code. Running them against an empty Players service would measure the harness, not
                // the API. Players.LocalPlayer stays nil, which is what the mirror says a server
                // sees — TAC-020 still fails for its own, correct reason.
                RbxApi.ConnectActor(new LocalActorIdentityProvider(
                        "corpus-player",
                        "corpus-session",
                        registry.WorldId,
                        ActorGrantSet.None,
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.Programmer));
            }

            public InMemoryInputSource Input { get; }
            public LuaCsRbxApiBindings RbxApi { get; }
            public CapturingGameLogger Logger { get; }
            public LuaCsModStack Stack { get; }
            public ScriptedContactPort Physics { get; }
            public List<string> ThreadFaults { get; } = new();

            /// <summary>Reports a contact between the first two parts the fixture put in the world.</summary>
            /// <remarks>
            /// WHY the harness supplies the collision: a headless corpus has no physics engine, so a
            /// kill brick could never be exercised — and dropping it would leave the most common
            /// gameplay idiom in Roblox untested. The fixture source stays exactly what a developer
            /// writes; only the physical event a real engine would produce is injected here.
            /// </remarks>
            public void TouchFirstTwoParts()
            {
                RbxInstance world = RbxApi.Game.FindFirstChildOfClass("Workspace");
                if (world == null)
                {
                    return;
                }

                List<RbxInstance> parts = new();
                foreach (RbxInstance child in world.GetChildren())
                {
                    if (child.IsA("BasePart"))
                    {
                        parts.Add(child);
                    }
                }

                if (parts.Count < 2)
                {
                    return;
                }

                Physics.RaiseBegan(parts[0].Id, parts[1].Id);
            }
        }

        /// <summary>A physics engine the corpus drives by hand: no rays hit, contacts are injected.</summary>
        private sealed class ScriptedContactPort : IRbxPhysicsPort
        {
            public event Action<InstanceId, InstanceId> ContactBegan;

            public event Action<InstanceId, InstanceId> ContactEnded;

            public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds,
                bool respectCanCollide, Func<InstanceId, bool> isEligible,
                out RbxPhysicsRaycastHit hit)
            {
                hit = default;
                return false;
            }

            public void SetGravity(double studsPerSecondSquared)
            {
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

        private sealed class ExecutionOutcome
        {
            public Exception Exception;
            public object Completion;
            public int StubHitCount;
            public readonly List<string> Failures = new();

            public bool Failed => Exception != null || Failures.Count > 0;

            public bool QuietlyIncomplete => !Failed && Completion == null;

            public string FailureText()
            {
                List<string> diagnostics = new(Failures);
                if (Exception != null)
                {
                    diagnostics.Insert(0, Exception.ToString());
                }

                return diagnostics.Count == 0 ? "<none>" : string.Join("\n", diagnostics);
            }
        }

        private static IEnumerable FixtureCases()
        {
            foreach (TierAFixtureSpec fixture in TierACorpusCatalog.Fixtures)
            {
                yield return new TestCaseData(fixture).SetName("TierA_" + fixture.Id);
            }
        }

        private static string FixtureDirectory => Path.Combine(
            Application.dataPath, "CoreAIMods", "Tests", "EditMode", "RbxApi",
            "CompatibilityCorpus", "Fixtures");

        private static string TierBFixtureDirectory => Path.Combine(
            Application.dataPath, "CoreAIMods", "Tests", "EditMode", "RbxApi",
            "CompatibilityCorpus", "FixturesB");

        /// <summary>The directory a fixture lives in, chosen by its id prefix.</summary>
        private static string DirectoryFor(TierAFixtureSpec fixture)
        {
            return fixture.Id.StartsWith("TBC-", StringComparison.Ordinal)
                ? TierBFixtureDirectory
                : FixtureDirectory;
        }

        private static string LoadFixtureSource(TierAFixtureSpec fixture)
        {
            string path = Path.Combine(DirectoryFor(fixture), fixture.FileName);
            Assert.IsTrue(File.Exists(path), fixture.Id + " fixture is missing: " + path);
            return File.ReadAllText(path);
        }

        private static void Drive(RuntimeHarness harness, TierAFixtureDriver driver)
        {
            switch (driver)
            {
                case TierAFixtureDriver.None:
                    return;
                case TierAFixtureDriver.AdvanceImmediate:
                    harness.RbxApi.Scheduler.Advance(0d);
                    return;
                case TierAFixtureDriver.AdvanceQuarterSecond:
                    harness.RbxApi.Scheduler.Advance(0.25d);
                    return;
                case TierAFixtureDriver.AdvanceHalfSecond:
                    harness.RbxApi.Scheduler.Advance(0.5d);
                    return;
                case TierAFixtureDriver.PumpThreeFrames:
                    for (int frame = 0; frame < 3; frame++)
                    {
                        harness.RbxApi.PumpFrame(1f / 60f);
                        harness.RbxApi.Scheduler.Advance(0d);
                    }

                    return;
                case TierAFixtureDriver.PumpSixSeconds:
                    for (int frame = 0; frame < 360; frame++)
                    {
                        harness.RbxApi.PumpFrame(1f / 60f);
                        harness.RbxApi.Scheduler.Advance(0d);
                    }

                    return;
                case TierAFixtureDriver.TouchFirstTwoParts:
                    harness.RbxApi.Scheduler.Advance(0d);
                    harness.TouchFirstTwoParts();
                    harness.RbxApi.Scheduler.Advance(0d);
                    return;
                case TierAFixtureDriver.PressE:
                    harness.Input.PressKey(KeyE);
                    harness.RbxApi.PumpInput();
                    harness.RbxApi.Scheduler.Advance(0d);
                    harness.Input.ReleaseKey(KeyE);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(driver), driver, null);
            }
        }

        private ExecutionOutcome Execute(TierAFixtureSpec fixture)
        {
            RuntimeHarness harness = new(_capturingLog);
            ExecutionOutcome outcome = new();
            int stubHitsBefore = _stubHitCounter.Count;
            try
            {
                string source = LoadFixtureSource(fixture);
                harness.Stack.Runtime.LoadMod(
                    fixture.Id, source, LuaCapabilities.All, persistToStore: false);
                Drive(harness, fixture.Driver);
                RbxInstance workspace = harness.RbxApi.Game.FindFirstChildOfClass("Workspace");
                outcome.Completion = workspace?.GetAttribute(ResultAttribute);
            }
            catch (Exception exception)
            {
                outcome.Exception = exception;
            }

            outcome.StubHitCount = _stubHitCounter.Count - stubHitsBefore;
            outcome.Failures.AddRange(harness.ThreadFaults);
            outcome.Failures.AddRange(_capturingLog.Errors);
            outcome.Failures.AddRange(harness.Logger.Errors);
            foreach (string message in harness.Logger.Messages)
            {
                if (message.IndexOf("NOT_IMPLEMENTED", StringComparison.Ordinal) >= 0)
                {
                    outcome.Failures.Add(message);
                }
            }

            if (outcome.StubHitCount > 0)
            {
                outcome.Failures.Add(fixture.Id + " raised " + outcome.StubHitCount
                    + " NOT_IMPLEMENTED stub hit(s), observed via FirstChanceException "
                    + "(a pcall may have swallowed it from Lua's perspective)");
            }

            return outcome;
        }

        [Test]
        public void FrozenCatalog_HasTwentyUniqueFixturesAndCompleteClassificationMetadata()
        {
            Assert.AreEqual(FrozenFixtureCount, TierACorpusCatalog.Fixtures.Length);
            HashSet<string> ids = new(StringComparer.Ordinal);
            HashSet<string> fileNames = new(StringComparer.Ordinal);
            int unmodifiedCount = 0;

            foreach (TierAFixtureSpec fixture in TierACorpusCatalog.Fixtures)
            {
                Assert.IsTrue(ids.Add(fixture.Id), "Duplicate fixture id: " + fixture.Id);
                Assert.IsTrue(fileNames.Add(fixture.FileName), "Duplicate fixture file: " + fixture.FileName);
                Assert.IsNotEmpty(fixture.Why, fixture.Id + " must record its classification reason.");
                Assert.IsTrue(File.Exists(Path.Combine(DirectoryFor(fixture), fixture.FileName)),
                    fixture.Id + " fixture file is missing.");

                if (fixture.Classification == TierAFixtureClassification.Unmodified)
                {
                    unmodifiedCount++;
                    Assert.AreEqual("None.", fixture.Accommodation);
                    Assert.IsEmpty(fixture.ApiGap);
                    Assert.IsEmpty(fixture.ExpectedFailureText);
                    Assert.AreEqual(TierAFixtureExpectedOutcome.Completion,
                        fixture.ExpectedOutcome);
                }
                else if (fixture.Classification == TierAFixtureClassification.Modified)
                {
                    StringAssert.StartsWith("Replaced `", fixture.Accommodation,
                        fixture.Id + " must state its exact source edit.");
                    Assert.IsNotEmpty(fixture.ApiGap);
                    Assert.IsEmpty(fixture.ExpectedFailureText);
                    Assert.AreEqual(TierAFixtureExpectedOutcome.Completion,
                        fixture.ExpectedOutcome);
                }
                else
                {
                    Assert.IsNotEmpty(fixture.ApiGap);
                    Assert.AreNotEqual(TierAFixtureExpectedOutcome.Completion,
                        fixture.ExpectedOutcome);
                    if (fixture.ExpectedOutcome ==
                        TierAFixtureExpectedOutcome.DiagnosticFailure)
                    {
                        Assert.IsNotEmpty(fixture.ExpectedFailureText);
                    }
                    else
                    {
                        Assert.AreEqual(TierAFixtureExpectedOutcome.IndefiniteYield,
                            fixture.ExpectedOutcome);
                        Assert.IsEmpty(fixture.ExpectedFailureText);
                    }
                }
            }

            CollectionAssert.AreEquivalent(FrozenFixtureIds, ids,
                "The Tier-A catalog ids must match the frozen acceptance set exactly.");
            Assert.GreaterOrEqual(
                unmodifiedCount * 100,
                FrozenFixtureCount * MinimumUnmodifiedPercent,
                $"At least {MinimumUnmodifiedPercent}% of the frozen Tier-A catalog must run unmodified.");

            string[] fixtureFiles = Directory.GetFiles(FixtureDirectory, "*.lua", SearchOption.TopDirectoryOnly);
            Assert.AreEqual(FrozenFixtureCount, fixtureFiles.Length,
                "The fixture directory and frozen catalog must contain the same 20 entries.");
        }

        private static IEnumerable TierBFixtureCases()
        {
            foreach (TierAFixtureSpec fixture in TierBCorpusCatalog.Fixtures)
            {
                yield return new TestCaseData(fixture).SetName("TierB_" + fixture.Id);
            }
        }

        [Test]
        public void FrozenTierBCatalog_MatchesItsFilesAndIds()
        {
            // The negative twin of the gate below: it would pass on a shrinking corpus, so the
            // membership is asserted against the frozen list AND the directory on disk.
            Assert.AreEqual(FrozenTierBFixtureCount, TierBCorpusCatalog.Fixtures.Length);

            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (TierAFixtureSpec fixture in TierBCorpusCatalog.Fixtures)
            {
                Assert.IsTrue(ids.Add(fixture.Id), "Duplicate Tier-B fixture id: " + fixture.Id);
                Assert.IsNotEmpty(fixture.Why, fixture.Id + " must record why it is in the corpus.");
                Assert.IsTrue(
                    File.Exists(Path.Combine(TierBFixtureDirectory, fixture.FileName)),
                    fixture.Id + " fixture file is missing.");
            }

            CollectionAssert.AreEquivalent(TierBFixtureIds, ids,
                "The Tier-B catalog ids must match the frozen acceptance set exactly.");
            Assert.AreEqual(FrozenTierBFixtureCount,
                Directory.GetFiles(TierBFixtureDirectory, "*.lua", SearchOption.TopDirectoryOnly).Length,
                "The Tier-B fixture directory and the frozen catalog must agree.");
        }

        [Test]
        public void CombinedCorpus_MeetsTheMvp8UnmodifiedThreshold()
        {
            int total = TierACorpusCatalog.Fixtures.Length + TierBCorpusCatalog.Fixtures.Length;
            int unmodified = 0;
            List<string> modifiedOrFailing = new();

            foreach (TierAFixtureSpec fixture in TierACorpusCatalog.Fixtures)
            {
                Count(fixture, ref unmodified, modifiedOrFailing);
            }

            foreach (TierAFixtureSpec fixture in TierBCorpusCatalog.Fixtures)
            {
                Count(fixture, ref unmodified, modifiedOrFailing);
            }

            Assert.GreaterOrEqual(unmodified * 100, total * MinimumCombinedUnmodifiedPercent,
                $"MVP8 requires {MinimumCombinedUnmodifiedPercent}% of Tier-A + Tier-B to run "
                + $"unmodified; {unmodified} of {total} do. Still short: "
                + string.Join(", ", modifiedOrFailing));
        }

        [TestCaseSource(nameof(TierBFixtureCases))]
        public void TierBFixture_RunsUnmodifiedWithNoStubHits(object fixtureValue)
        {
            // WHY the stub check and not only the completion attribute: a fixture wrapped in pcall
            // can "complete" while every interesting call inside it raised NOT_IMPLEMENTED. The
            // harness records those as failures, so a fixture only counts when nothing was stubbed.
            TierAFixtureSpec fixture = (TierAFixtureSpec)fixtureValue;
            ExecutionOutcome outcome = Execute(fixture);

            Assert.IsNull(outcome.Exception,
                fixture.Id + " raised: " + outcome.Exception);
            Assert.IsEmpty(outcome.Failures,
                fixture.Id + " hit a stub or logged an error: "
                + string.Join(" | ", outcome.Failures));
            Assert.AreEqual(fixture.Id, outcome.Completion,
                fixture.Id + " did not reach its completion marker.");
        }

        [Test]
        public void Negative_CorruptedTierBFixtures_Fail()
        {
            // The zero-work counter for the gate above: if the runner reported success for anything,
            // these three deliberately broken twins of the named MVP8 fixtures would pass too.
            (string Id, string Source, string Expected)[] corrupted =
            {
                ("TBC-001-kill-brick",
                    "local h = Instance.new('Humanoid')\nh.Parent = workspace\nh:Vaporize()",
                    "Vaporize"),
                ("TBC-002-touch-pickup-with-leaderstats",
                    "local v = Instance.new('IntValue')\nv.Parent = workspace\nv.Value = 'gold'",
                    "IntValue"),
                ("TBC-003-door-tween",
                    "local t = game:GetService('TweenService')\n"
                    + "local p = Instance.new('Part')\np.Parent = workspace\n"
                    + "t:Create(p, TweenInfo.new(1), { CanCollide = false }):Play()",
                    "CanCollide")
            };

            foreach ((string id, string source, string expected) in corrupted)
            {
                RuntimeHarness harness = new(_capturingLog);
                bool failed = false;
                string detail = "";
                try
                {
                    harness.Stack.Runtime.LoadMod(
                        id + "-corrupt", source, LuaCapabilities.All, persistToStore: false);
                    harness.RbxApi.Scheduler.Advance(0d);
                }
                catch (Exception exception)
                {
                    failed = true;
                    detail = exception.ToString();
                }

                if (!failed)
                {
                    detail = string.Join(" | ", harness.Logger.Messages);
                    failed = detail.IndexOf("NOT_IMPLEMENTED", StringComparison.Ordinal) >= 0
                             || detail.IndexOf("BAD_ARGUMENT", StringComparison.Ordinal) >= 0;
                }

                Assert.IsTrue(failed, "corrupted " + id + " was accepted: " + detail);
                StringAssert.Contains(expected, detail,
                    "corrupted " + id + " must fail for its own reason, not an unrelated one");
            }
        }

        [Test]
        public void Negative_PcallWrappedStubHit_CountsAsFailing()
        {
            // The proof for the gate above (P8.5's negative twin): a fixture that wraps a stubbed
            // call in pcall produces no logger line and completes cleanly from Lua's perspective,
            // so the old text-scrape check would have reported a clean pass. The raise-site
            // counter must still classify this as failing.
            RuntimeHarness harness = new(_capturingLog);
            int stubHitsBefore = _stubHitCounter.Count;

            harness.Stack.Runtime.LoadMod("hostile-pcall-stub", @"
                local ok, err = pcall(function() game:BindToClose(function() end) end)
                workspace:SetAttribute('" + ResultAttribute + @"', 'hostile-pcall-stub')",
                LuaCapabilities.All, persistToStore: false);
            harness.RbxApi.Scheduler.Advance(0d);

            int stubHits = _stubHitCounter.Count - stubHitsBefore;
            Assert.Greater(stubHits, 0,
                "a pcall-wrapped NOT_IMPLEMENTED raise must still be counted at the throw site");

            RbxInstance workspace = harness.RbxApi.Game.FindFirstChildOfClass("Workspace");
            object completion = workspace?.GetAttribute(ResultAttribute);
            Assert.AreEqual("hostile-pcall-stub", completion,
                "the fixture reaches its completion marker cleanly, which is exactly why a "
                + "log-scrape or completion-only check would have wrongly passed it");
        }

        private static void Count(TierAFixtureSpec fixture, ref int unmodified,
            List<string> shortfall)
        {
            if (fixture.Classification == TierAFixtureClassification.Unmodified)
            {
                unmodified++;
                return;
            }

            shortfall.Add(fixture.Id);
        }

        [TestCaseSource(nameof(FixtureCases))]
        public void Fixture_ExecutesWithRecordedClassification(object fixtureValue)
        {
            TierAFixtureSpec fixture = (TierAFixtureSpec)fixtureValue;
            ExecutionOutcome outcome = Execute(fixture);
            string failureText = outcome.FailureText();

            if (fixture.ExpectedOutcome == TierAFixtureExpectedOutcome.DiagnosticFailure)
            {
                Assert.IsTrue(outcome.Failed,
                    fixture.Id + " no longer produces its recorded failure. Completion: " +
                    (outcome.Completion ?? "<none>"));
                StringAssert.Contains(fixture.ExpectedFailureText, failureText,
                    fixture.Id + " failed for a reason other than its recorded API gap.\n" + failureText);
                return;
            }

            if (fixture.ExpectedOutcome == TierAFixtureExpectedOutcome.IndefiniteYield)
            {
                Assert.IsTrue(outcome.QuietlyIncomplete,
                    fixture.Id + " no longer produces its recorded indefinite yield. Completion: " +
                    (outcome.Completion ?? "<none>") + "\n" + failureText);
                return;
            }

            Assert.IsFalse(outcome.Failed,
                fixture.Id + " changed from " + fixture.Classification + " to failing.\n" + failureText);
            Assert.AreEqual(fixture.Id, outcome.Completion,
                fixture.Id + " loaded but did not reach its exact completion marker.");
        }
    }
}
