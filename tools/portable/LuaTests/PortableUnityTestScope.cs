using System;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using UnityEngine;

[assembly: PortableUnityTestScope]

/// <summary>
/// Applies, around every linked EditMode test, the two rules that make a portable result mean the same
/// as a Unity result: (1) the Unity Test Framework's log rule, where an Error, Assert or Exception log
/// that no <c>LogAssert.Expect</c> consumed fails the test and an expectation that never matched fails
/// it too (see <see cref="PortableLogScope"/>); (2) a test that touched a refused engine member
/// (<see cref="PortableEngineUnavailableException"/>) is reported as Inconclusive whatever its portable
/// outcome, because only Unity can decide it.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class PortableUnityTestScopeAttribute : Attribute, ITestAction
{
    /// <inheritdoc />
    public ActionTargets Targets => ActionTargets.Test;

    /// <inheritdoc />
    public void BeforeTest(ITest test)
    {
        PortableRefusalLog.Drain();
        PortableLogScope.Begin();
    }

    /// <inheritdoc />
    public void AfterTest(ITest test)
    {
        string logFailure = PortableLogScope.End();
        string[] refused = PortableRefusalLog.Drain();
        TestContext.ResultAdapter result = TestContext.CurrentContext.Result;
        if (refused.Length > 0)
        {
            string list = string.Join(", ", new System.Collections.Generic.SortedSet<string>(refused));
            TestContext.Progress.WriteLine("[portable] " + test.FullName + " touched refused engine members: " + list);
            // WHY: once a refused member was reached the run left the path Unity takes, so neither a
            // pass (the code may have caught the refusal and carried on) nor a failure (typically a
            // missing side effect downstream of the refusal) says anything about Unity. The original
            // outcome and message stay visible in the result text NUnit appends this to.
            Assert.Inconclusive("PORTABLE_ENGINE_UNAVAILABLE: the test reached engine members the portable " +
                                "suite refuses (" + list + "); only Unity can decide it (portable outcome: " +
                                result.Outcome.Status + ")." + (logFailure != null ? " Also: " + logFailure : ""));
        }

        if (logFailure != null && result.Outcome.Status == TestStatus.Passed)
        {
            Assert.Fail("Unity Test Framework log rule: " + logFailure);
        }
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
