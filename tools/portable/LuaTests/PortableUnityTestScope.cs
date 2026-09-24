using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using UnityEngine;

[assembly: PortableUnityTestScope]

/// <summary>
/// Applies, around every linked EditMode test, the two rules that make a portable result mean the same
/// as a Unity result: (1) the Unity Test Framework's log rule, where an Error, Assert or Exception log
/// that no <c>LogAssert.Expect</c> consumed fails the test and an expectation that never matched fails
/// it too (see <see cref="PortableLogScope"/>); (2) a test that depends on a refused engine member
/// (<see cref="PortableEngineUnavailableException"/>) is reported as Inconclusive whatever its portable
/// outcome, because only Unity can decide it. A test depends on a refusal raised in its own SetUp, body
/// or TearDown, in the constructor or OneTimeSetUp of its fixture or of an enclosing SetUpFixture, or
/// while NUnit built the test tree (see <see cref="PortableRefusalLedger"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class PortableUnityTestScopeAttribute : Attribute, ITestAction
{
    private readonly PortableRefusalLedger _ledger = new PortableRefusalLedger();

    /// <inheritdoc />
    /// <remarks>
    /// WHY Suite as well: the suite hooks of an assembly-level action fire for the assembly suite only,
    /// so they bracket the run. Its BeforeTest is the first point after NUnit built the test tree (where
    /// test-case sources ran) and before any fixture started; its AfterTest comes after the last
    /// OneTimeTearDown.
    /// </remarks>
    public ActionTargets Targets => ActionTargets.Test | ActionTargets.Suite;

    /// <inheritdoc />
    public void BeforeTest(ITest test)
    {
        if (test.IsSuite)
        {
            _ledger.StartRun(PortableRefusalLog.Drain());
            return;
        }

        _ledger.StartTest(PortableRefusalLog.Drain());
        PortableLogScope.Begin();
    }

    /// <inheritdoc />
    public void AfterTest(ITest test)
    {
        if (test.IsSuite)
        {
            _ledger.EndRun(PortableRefusalLog.Drain(), TestContext.Progress);
            return;
        }

        string logFailure = PortableLogScope.End();
        string refusal = _ledger.EndTest(test, PortableRefusalLog.Drain());
        TestContext.ResultAdapter result = TestContext.CurrentContext.Result;
        if (refusal != null)
        {
            TestContext.Progress.WriteLine("[portable] " + test.FullName + ": " + refusal);
            // WHY: once a refused member was reached the run left the path Unity takes, so neither a
            // pass (the code may have caught the refusal and carried on) nor a failure (typically a
            // missing side effect downstream of the refusal) says anything about Unity. The original
            // outcome and message stay visible in the result text NUnit appends this to.
            Assert.Inconclusive("PORTABLE_ENGINE_UNAVAILABLE: " + refusal + "; only Unity can decide it " +
                                "(portable outcome: " + result.Outcome.Status + ")." +
                                (logFailure != null ? " Also: " + logFailure : ""));
        }

        if (logFailure != null && result.Outcome.Status == TestStatus.Passed)
        {
            Assert.Fail("Unity Test Framework log rule: " + logFailure);
        }
    }
}

/// <summary>
/// Decides which tests a recorded refusal is charged to. Every refusal carries the NUnit test or suite
/// that was running when it was raised (<see cref="PortableRefusalOwnerProbe"/>):
/// <list type="bullet">
/// <item>raised by a suite (a fixture constructor or OneTimeSetUp, a SetUpFixture's OneTimeSetUp):
/// charged to every test of that suite that has not reported yet;</item>
/// <item>raised before the first test with no suite running (a TestCaseSource, ValueSource or
/// TestFixtureSource, which NUnit evaluates while it builds the test tree): charged to every test of
/// the run, because nothing says which test consumed it;</item>
/// <item>anything else (the test's own SetUp, body and TearDown, or work that outlived an earlier test):
/// charged to the test running when it is collected, or to the next test when collected between
/// tests.</item>
/// </list>
/// A refusal raised by a suite after its last test reported (OneTimeTearDown) cannot change a reported
/// outcome; it is written to the progress log at the end of the run.
/// </summary>
internal sealed class PortableRefusalLedger
{
    private readonly List<string> _runWide = new List<string>();
    private readonly List<string> _pending = new List<string>();
    private readonly Dictionary<string, SuiteCharge> _bySuite = new Dictionary<string, SuiteCharge>();
    private bool _started;

    /// <summary>Collects what NUnit refused while it built the test tree, before any fixture ran.</summary>
    public void StartRun(PortableRefusal[] drained)
    {
        if (_started)
        {
            Collect(drained, _pending);
            return;
        }

        _started = true;
        Collect(drained, _runWide);
    }

    /// <summary>Collects what was refused between the previous test and this one.</summary>
    public void StartTest(PortableRefusal[] drained)
    {
        if (!_started)
        {
            StartRun(drained);
            return;
        }

        Collect(drained, _pending);
    }

    /// <summary>
    /// Returns why <paramref name="test"/> cannot be decided here, or null when it depends on no refusal.
    /// </summary>
    public string EndTest(ITest test, PortableRefusal[] drained)
    {
        List<string> parts = new List<string>();
        List<string> own = new List<string>(_pending);
        _pending.Clear();
        foreach (PortableRefusal refusal in drained)
        {
            own.Add(refusal.Api);
        }

        if (own.Count > 0)
        {
            parts.Add("the test reached engine members the portable suite refuses (" + Describe(own) + ")");
        }

        for (ITest suite = test.Parent; suite != null; suite = suite.Parent)
        {
            if (_bySuite.TryGetValue(suite.Id, out SuiteCharge charge))
            {
                charge.Drawn = true;
                parts.Add("before the test started, " + charge.Name + " (a constructor, a OneTimeSetUp or " +
                          "work they started) reached engine members the portable suite refuses (" +
                          Describe(charge.Apis) + ")");
            }
        }

        if (_runWide.Count > 0)
        {
            parts.Add("while NUnit built the test tree (a TestCaseSource, ValueSource or TestFixtureSource), " +
                      "engine members the portable suite refuses were reached (" + Describe(_runWide) +
                      "), and every test of the run is charged because nothing says which test used them");
        }

        // WHY after the ancestors were read: a suite-owned refusal collected inside this test's window
        // (work a OneTimeSetUp started) is this test's own; the suite's later tests inherit it.
        foreach (PortableRefusal refusal in drained)
        {
            if (refusal.Owner is ITest owner && owner.IsSuite)
            {
                Charge(owner, refusal.Api);
            }
        }

        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }

    /// <summary>Writes the refusals no test was charged with (OneTimeTearDown, work that outlived a test).</summary>
    public void EndRun(PortableRefusal[] drained, TextWriter progress)
    {
        List<string> late = new List<string>(_pending);
        _pending.Clear();
        Collect(drained, late);
        foreach (SuiteCharge charge in _bySuite.Values)
        {
            if (!charge.Drawn)
            {
                progress.WriteLine("[portable] " + charge.Name + " reached refused engine members after its " +
                                   "last test reported, or had no test run: " + Describe(charge.Apis));
            }
        }

        if (late.Count > 0)
        {
            progress.WriteLine("[portable] refused engine members reached after the last test reported: " +
                               Describe(late));
        }
    }

    private void Collect(PortableRefusal[] drained, List<string> unowned)
    {
        foreach (PortableRefusal refusal in drained)
        {
            if (refusal.Owner is ITest owner && owner.IsSuite)
            {
                Charge(owner, refusal.Api);
            }
            else
            {
                unowned.Add(refusal.Api);
            }
        }
    }

    private void Charge(ITest suite, string api)
    {
        if (!_bySuite.TryGetValue(suite.Id, out SuiteCharge charge))
        {
            charge = new SuiteCharge(suite.FullName);
            _bySuite.Add(suite.Id, charge);
        }

        charge.Apis.Add(api);
    }

    private static string Describe(IEnumerable<string> apis)
    {
        return string.Join(", ", new SortedSet<string>(apis, StringComparer.Ordinal));
    }

    private sealed class SuiteCharge
    {
        public SuiteCharge(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public List<string> Apis { get; } = new List<string>();

        public bool Drawn { get; set; }
    }
}

/// <summary>
/// Tags every refusal with the NUnit test or suite running on the refusing thread (NUnit flows its
/// execution context into async continuations and tasks), or null when none is: NUnit evaluates
/// test-case sources while it builds the test tree, under an ad hoc context that names no test.
/// </summary>
internal static class PortableRefusalOwnerProbe
{
    /// <summary>Installs the probe before any code of this assembly runs.</summary>
    /// <remarks>
    /// WHY a module initializer: NUnit runs test-case sources, fixture constructors and attribute
    /// constructors of this assembly before any test action exists, and each of them may reach a
    /// refused member; the module initializer is guaranteed to run before the first of them.
    /// </remarks>
    [ModuleInitializer]
    internal static void Install()
    {
        PortableRefusalLog.OwnerProbe = CurrentOwner;
    }

    private static object CurrentOwner()
    {
        TestExecutionContext context = TestExecutionContext.CurrentContext;
        return context is TestExecutionContext.AdhocContext ? null : context.CurrentTest;
    }
}

/// <summary>Removes the per-run persistent data directory the UnityEngine shim may have created.</summary>
[SetUpFixture]
public sealed class PortablePersistentDataCleanup
{
    [OneTimeTearDown]
    public void DeletePersistentDataPath()
    {
        PortableShim.DeletePersistentDataPath();
    }
}
