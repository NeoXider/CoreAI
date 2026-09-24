using System;
using System.Collections;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Builders;
using NUnit.Framework.Internal.Execution;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace CoreAI.Tests.PortableRunner
{
    /// <summary>
    /// Self-tests of the portable runner: each one runs witness fixtures in a nested NUnit run, under the
    /// same assembly-level <see cref="PortableUnityTestScopeAttribute"/> every linked fixture gets, and
    /// asserts on the outcomes the scope gave them. A witness that must not pass is only ever an
    /// outcome to assert on here, so this suite stays green while the rule holds and a normal test
    /// fails when it breaks.
    /// </summary>
    public sealed class PortableUnityTestScopeSelfTests
    {
        private const string Refused = "Time.frameCount";

        [Test]
        public void RefusalInOneTimeSetUp_MakesEveryTestOfThatFixtureInconclusive()
        {
            PortableNestedRun run = PortableNestedRun.Execute(
                typeof(OneTimeSetUpRefusalWitness<NestedRunOnly>), typeof(CleanWitness<NestedRunOnly>));

            AssertInconclusive(run.Result(typeof(OneTimeSetUpRefusalWitness<NestedRunOnly>), "FirstTest"),
                "before the test started");
            AssertInconclusive(run.Result(typeof(OneTimeSetUpRefusalWitness<NestedRunOnly>), "SecondTest"),
                "before the test started");
            AssertPassed(run.Result(typeof(CleanWitness<NestedRunOnly>), "OnlyTest"));
        }

        [Test]
        public void RefusalInFixtureConstructor_MakesTheFixtureTestInconclusive()
        {
            PortableNestedRun run = PortableNestedRun.Execute(
                typeof(ConstructorRefusalWitness<NestedRunOnly>), typeof(CleanWitness<NestedRunOnly>));

            AssertInconclusive(run.Result(typeof(ConstructorRefusalWitness<NestedRunOnly>), "OnlyTest"),
                "before the test started");
            AssertPassed(run.Result(typeof(CleanWitness<NestedRunOnly>), "OnlyTest"));
        }

        [Test]
        public void RefusalInTestCaseSource_MakesEveryTestOfTheRunInconclusive()
        {
            PortableNestedRun run = PortableNestedRun.Execute(
                typeof(TestCaseSourceRefusalWitness<NestedRunOnly>), typeof(CleanWitness<NestedRunOnly>));

            AssertInconclusive(run.Result(typeof(TestCaseSourceRefusalWitness<NestedRunOnly>), "SourcedTest(1)"),
                "while NUnit built the test tree");
            AssertInconclusive(run.Result(typeof(CleanWitness<NestedRunOnly>), "OnlyTest"),
                "while NUnit built the test tree");
        }

        [Test]
        public void RefusalInSetUp_MakesTheTestInconclusive()
        {
            PortableNestedRun run = PortableNestedRun.Execute(
                typeof(SetUpRefusalWitness<NestedRunOnly>), typeof(CleanWitness<NestedRunOnly>));

            AssertInconclusive(run.Result(typeof(SetUpRefusalWitness<NestedRunOnly>), "OnlyTest"),
                "the test reached engine members");
            AssertPassed(run.Result(typeof(CleanWitness<NestedRunOnly>), "OnlyTest"));
        }

        [Test]
        public void RefusalInTearDown_MakesTheTestInconclusive()
        {
            PortableNestedRun run = PortableNestedRun.Execute(
                typeof(TearDownRefusalWitness<NestedRunOnly>), typeof(CleanWitness<NestedRunOnly>));

            AssertInconclusive(run.Result(typeof(TearDownRefusalWitness<NestedRunOnly>), "OnlyTest"),
                "the test reached engine members");
            AssertPassed(run.Result(typeof(CleanWitness<NestedRunOnly>), "OnlyTest"));
        }

        [Test]
        public void RefusalInTestBody_MakesAFailingTestInconclusiveToo()
        {
            PortableNestedRun run = PortableNestedRun.Execute(typeof(BodyRefusalWitness<NestedRunOnly>));

            AssertInconclusive(run.Result(typeof(BodyRefusalWitness<NestedRunOnly>), "PassesAfterCatchingRefusal"),
                "portable outcome: Passed");
            AssertInconclusive(run.Result(typeof(BodyRefusalWitness<NestedRunOnly>), "FailsAfterCatchingRefusal"),
                "portable outcome: Failed");
        }

        [Test]
        public void RefusalInOneTimeTearDown_ChargesNoTestBecauseEveryOutcomeWasAlreadyReported()
        {
            PortableNestedRun run = PortableNestedRun.Execute(typeof(OneTimeTearDownRefusalWitness<NestedRunOnly>));

            AssertPassed(run.Result(typeof(OneTimeTearDownRefusalWitness<NestedRunOnly>), "OnlyTest"));
        }

        [Test]
        public void UnexpectedErrorLog_FailsTheTest_AndAMatchedExpectationPasses()
        {
            PortableNestedRun run = PortableNestedRun.Execute(typeof(LogRuleWitness<NestedRunOnly>));

            ITestResult unexpected = run.Result(typeof(LogRuleWitness<NestedRunOnly>), "LogsUnexpectedError");
            Assert.That(unexpected.ResultState.Status, Is.EqualTo(TestStatus.Failed), Describe(unexpected));
            StringAssert.Contains("Unity Test Framework log rule", unexpected.Message, Describe(unexpected));
            StringAssert.Contains("Unhandled log message", unexpected.Message, Describe(unexpected));

            ITestResult missing = run.Result(typeof(LogRuleWitness<NestedRunOnly>), "ExpectsErrorThatNeverComes");
            Assert.That(missing.ResultState.Status, Is.EqualTo(TestStatus.Failed), Describe(missing));
            StringAssert.Contains("Expected log did not appear", missing.Message, Describe(missing));

            AssertPassed(run.Result(typeof(LogRuleWitness<NestedRunOnly>), "ExpectsErrorAndLogsIt"));
            AssertPassed(run.Result(typeof(LogRuleWitness<NestedRunOnly>), "LogsOnlyAWarning"));
        }

        [Test]
        public void NotAllocatingGCMemory_FailsForAnAllocatingDelegate_AndPassesForAnAllocationFreeOne()
        {
            PortableNestedRun run = PortableNestedRun.Execute(typeof(AllocationWitness<NestedRunOnly>));

            ITestResult allocating = run.Result(typeof(AllocationWitness<NestedRunOnly>), "AllocatingDelegateIsNotAllocationFree");
            Assert.That(allocating.ResultState.Status, Is.EqualTo(TestStatus.Failed), Describe(allocating));
            StringAssert.Contains("not allocates GC memory", allocating.Message, Describe(allocating));

            AssertPassed(run.Result(typeof(AllocationWitness<NestedRunOnly>), "AllocationFreeDelegateIsAllocationFree"));
            AssertPassed(run.Result(typeof(AllocationWitness<NestedRunOnly>), "AllocatingDelegateAllocates"));
        }

        private static void AssertInconclusive(ITestResult result, string reason)
        {
            Assert.That(result.ResultState.Status, Is.EqualTo(TestStatus.Inconclusive), Describe(result));
            StringAssert.Contains("PORTABLE_ENGINE_UNAVAILABLE", result.Message, Describe(result));
            StringAssert.Contains(Refused, result.Message, Describe(result));
            StringAssert.Contains(reason, result.Message, Describe(result));
        }

        private static void AssertPassed(ITestResult result)
        {
            Assert.That(result.ResultState.Status, Is.EqualTo(TestStatus.Passed), Describe(result));
        }

        private static string Describe(ITestResult result)
        {
            return result.FullName + " ended " + result.ResultState + ": " + result.Message;
        }
    }

    /// <summary>
    /// Checks the UnityEngine shim's managed twins against Unity's reference implementation
    /// (UnityCsReference, <c>Runtime/Export/Math/Vector3.cs</c>).
    /// </summary>
    public sealed class PortableShimMathSelfTests
    {
        [Test]
        public void Vector3Normalized_BelowUnityKEpsilon_IsZero()
        {
            Vector3 normalized = new Vector3(5e-6F, 0F, 0F).normalized;

            Assert.That(normalized.x, Is.EqualTo(0F), "Unity's Normalize returns zero when magnitude <= kEpsilon (1e-5).");
            Assert.That(normalized.y, Is.EqualTo(0F));
            Assert.That(normalized.z, Is.EqualTo(0F));
        }

        [Test]
        public void Vector3Normalized_AboveUnityKEpsilon_IsUnitLength()
        {
            Vector3 normalized = new Vector3(0F, 2e-5F, 0F).normalized;

            Assert.That(normalized.x, Is.EqualTo(0F));
            Assert.That(normalized.y, Is.EqualTo(1F));
            Assert.That(normalized.z, Is.EqualTo(0F));
        }
    }

    /// <summary>
    /// The only type argument the witness fixtures are ever closed with.
    /// </summary>
    /// <remarks>
    /// WHY the witnesses are generic: NUnit's default suite builder skips an open generic class that has
    /// no fixture attribute, so the outer run never discovers a witness and its deliberate
    /// Inconclusive or Failed outcome never reaches the published result; the nested run builds the
    /// closed type explicitly.
    /// </remarks>
    public sealed class NestedRunOnly
    {
    }

    /// <summary>Reaches a refused member and catches the refusal, as runtime code with a generic catch does.</summary>
    internal static class WitnessRefusal
    {
        public static void Swallow()
        {
            try
            {
                int unused = Time.frameCount;
            }
            catch (PortableEngineUnavailableException)
            {
            }
        }
    }

    public sealed class CleanWitness<TNestedRunOnly>
    {
        [Test]
        public void OnlyTest()
        {
            Assert.That(1, Is.EqualTo(1));
        }
    }

    public sealed class OneTimeSetUpRefusalWitness<TNestedRunOnly>
    {
        [OneTimeSetUp]
        public void BuildSharedState()
        {
            WitnessRefusal.Swallow();
        }

        [Test]
        public void FirstTest()
        {
            Assert.That(1, Is.EqualTo(1));
        }

        [Test]
        public void SecondTest()
        {
            Assert.That(1, Is.EqualTo(1));
        }
    }

    public sealed class ConstructorRefusalWitness<TNestedRunOnly>
    {
        public ConstructorRefusalWitness()
        {
            WitnessRefusal.Swallow();
        }

        [Test]
        public void OnlyTest()
        {
            Assert.That(1, Is.EqualTo(1));
        }
    }

    public sealed class TestCaseSourceRefusalWitness<TNestedRunOnly>
    {
        public static IEnumerable Cases()
        {
            WitnessRefusal.Swallow();
            yield return 1;
        }

        [TestCaseSource(nameof(Cases))]
        public void SourcedTest(int value)
        {
            Assert.That(value, Is.EqualTo(1));
        }
    }

    public sealed class SetUpRefusalWitness<TNestedRunOnly>
    {
        [SetUp]
        public void SetUp()
        {
            WitnessRefusal.Swallow();
        }

        [Test]
        public void OnlyTest()
        {
            Assert.That(1, Is.EqualTo(1));
        }
    }

    public sealed class TearDownRefusalWitness<TNestedRunOnly>
    {
        [TearDown]
        public void TearDown()
        {
            WitnessRefusal.Swallow();
        }

        [Test]
        public void OnlyTest()
        {
            Assert.That(1, Is.EqualTo(1));
        }
    }

    public sealed class BodyRefusalWitness<TNestedRunOnly>
    {
        [Test]
        public void PassesAfterCatchingRefusal()
        {
            WitnessRefusal.Swallow();
            Assert.That(1, Is.EqualTo(1));
        }

        [Test]
        public void FailsAfterCatchingRefusal()
        {
            WitnessRefusal.Swallow();
            Assert.That(1, Is.EqualTo(2));
        }
    }

    public sealed class OneTimeTearDownRefusalWitness<TNestedRunOnly>
    {
        [OneTimeTearDown]
        public void ReleaseSharedState()
        {
            WitnessRefusal.Swallow();
        }

        [Test]
        public void OnlyTest()
        {
            Assert.That(1, Is.EqualTo(1));
        }
    }

    public sealed class LogRuleWitness<TNestedRunOnly>
    {
        [Test]
        public void LogsUnexpectedError()
        {
            Debug.LogError("portable runner witness: unexpected error");
        }

        [Test]
        public void ExpectsErrorAndLogsIt()
        {
            LogAssert.Expect(LogType.Error, "portable runner witness: expected error");
            Debug.LogError("portable runner witness: expected error");
        }

        [Test]
        public void ExpectsErrorThatNeverComes()
        {
            LogAssert.Expect(LogType.Error, "portable runner witness: never logged");
        }

        [Test]
        public void LogsOnlyAWarning()
        {
            Debug.LogWarning("portable runner witness: warning");
        }
    }

    public sealed class AllocationWitness<TNestedRunOnly>
    {
        private static object _sink;
        private static int _counter;

        [Test]
        public void AllocatingDelegateIsNotAllocationFree()
        {
            Assert.That(() => { _sink = new object[8]; }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void AllocatingDelegateAllocates()
        {
            Assert.That(() => { _sink = new object[8]; }, Is.AllocatingGCMemory());
        }

        [Test]
        public void AllocationFreeDelegateIsAllocationFree()
        {
            Assert.That(() => { _counter++; }, Is.Not.AllocatingGCMemory());
        }
    }

    /// <summary>
    /// Runs witness fixtures in a nested NUnit run rooted at a new suite for this test assembly, so the
    /// assembly-level <see cref="PortableUnityTestScopeAttribute"/> wraps them exactly as it wraps every
    /// linked fixture, and restores the outer test's execution context afterwards.
    /// </summary>
    /// <remarks>
    /// WHY the calling test must neither log an error nor reach a refused member itself: the log scope
    /// and the refusal log are process-wide, so the nested run closes the caller's log scope and drains
    /// whatever the caller had recorded into its own witnesses.
    /// </remarks>
    internal sealed class PortableNestedRun
    {
        private readonly ITestResult _root;

        private PortableNestedRun(ITestResult root)
        {
            _root = root;
        }

        public static PortableNestedRun Execute(params Type[] fixtures)
        {
            using (new TestExecutionContext.IsolatedContext())
            {
                TestAssembly root = new TestAssembly(typeof(PortableNestedRun).Assembly, "portable-runner-self-test");
                foreach (Type fixture in fixtures)
                {
                    root.Add(new NUnitTestFixtureBuilder().BuildFrom(new TypeWrapper(fixture), new MatchEveryMethod()));
                }

                WorkItem work = WorkItemBuilder.CreateWorkItem(root, TestFilter.Empty, true);
                work.InitializeContext(new TestExecutionContext { Dispatcher = new InlineDispatcher() });
                work.Execute();
                Assert.That(work.State, Is.EqualTo(WorkItemState.Complete),
                    "The nested run must finish on the calling thread.");
                return new PortableNestedRun(work.Result);
            }
        }

        public ITestResult Result(Type fixture, string testName)
        {
            foreach (ITestResult fixtureResult in _root.Children)
            {
                if (fixtureResult.Test.TypeInfo != null && fixtureResult.Test.TypeInfo.Type == fixture)
                {
                    ITestResult found = FindLeaf(fixtureResult, testName);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }

            Assert.Fail("The nested run has no result for " + fixture + "." + testName + ".");
            return null;
        }

        private static ITestResult FindLeaf(ITestResult result, string testName)
        {
            if (!result.Test.IsSuite)
            {
                return result.Name == testName ? result : null;
            }

            foreach (ITestResult child in result.Children)
            {
                ITestResult found = FindLeaf(child, testName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private sealed class MatchEveryMethod : IPreFilter
        {
            public bool IsMatch(Type type)
            {
                return true;
            }

            public bool IsMatch(Type type, System.Reflection.MethodInfo method)
            {
                return true;
            }
        }

        private sealed class InlineDispatcher : IWorkItemDispatcher
        {
            public int LevelOfParallelism => 0;

            public void Start(WorkItem topLevelWorkItem)
            {
                topLevelWorkItem.Execute();
            }

            public void Dispatch(WorkItem work)
            {
                work.Execute();
            }

            public void CancelRun(bool force)
            {
            }
        }
    }
}
