using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
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

        [SetUp]
        public void SetUpHarnessEnvironment()
        {
            _savedContext = SynchronizationContext.Current;
            _savedLog = Log.Instance;
            _capturingLog = new CapturingLog();
            Log.Instance = _capturingLog;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreHarnessEnvironment()
        {
            Log.Instance = _savedLog;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
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
            }

            public InMemoryInputSource Input { get; }
            public LuaCsRbxApiBindings RbxApi { get; }
            public CapturingGameLogger Logger { get; }
            public LuaCsModStack Stack { get; }
            public List<string> ThreadFaults { get; } = new();
        }

        private sealed class ExecutionOutcome
        {
            public Exception Exception;
            public object Completion;
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

        private static string LoadFixtureSource(TierAFixtureSpec fixture)
        {
            string path = Path.Combine(FixtureDirectory, fixture.FileName);
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
                Assert.IsTrue(File.Exists(Path.Combine(FixtureDirectory, fixture.FileName)),
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
