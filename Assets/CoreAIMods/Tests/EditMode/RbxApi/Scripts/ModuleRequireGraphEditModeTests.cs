using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Scripts
{
    /// <summary>
    /// The engine-free require cache and cycle detector of MVP4 design note SC-11/SC-12: one load per environment
    /// (R3.2, R3.3), a fresh load in another environment, failures that stick (R3.8), cycles found through the wait-for
    /// chain in both execution models (inline bodies and body threads; DEV-1) and waits that are not cycles.
    /// </summary>
    [TestFixture]
    public sealed class ModuleRequireGraphEditModeTests
    {
        private static readonly InstanceId ModuleA = new InstanceId(11);
        private static readonly InstanceId ModuleB = new InstanceId(12);
        private static readonly InstanceId ModuleC = new InstanceId(13);
        private const string PathA = "ReplicatedStorage.A";
        private const string PathB = "ReplicatedStorage.B";
        private const string PathC = "ReplicatedStorage.C";

        private ModuleRequireGraph _graph;
        private object _environment;
        private object _script;

        [SetUp]
        public void SetUp()
        {
            _graph = new ModuleRequireGraph();
            _environment = new object();
            _script = new object();
        }

        [Test]
        public void RequireGraph_R3_2_SecondBeginIsCached()
        {
            object value = new object();
            Assert.AreEqual(RequireStepKind.Load, _graph.Begin(_environment, ModuleA, PathA, _script).Kind);
            Assert.AreEqual(0, _graph.Complete(_environment, ModuleA, value).Count);
            RequireStep again = _graph.Begin(_environment, ModuleA, PathA, _script);
            RequireStep otherThread = _graph.Begin(_environment, ModuleA, PathA, new object());
            Assert.AreEqual(RequireStepKind.Cached, again.Kind);
            Assert.AreSame(value, again.Value);
            Assert.AreSame(value, otherThread.Value, "R3.3: every require in the environment gets the same value");
        }

        [Test]
        public void RequireGraph_InvalidatedModuleLoadsAgain()
        {
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.Complete(_environment, ModuleA, new object());
            _graph.Invalidate(ModuleA);
            Assert.AreEqual(RequireStepKind.Load, _graph.Begin(_environment, ModuleA, PathA, _script).Kind);
        }

        [Test]
        public void RequireGraph_InvalidateLeavesALoadInProgress()
        {
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.Invalidate(ModuleA);
            Assert.IsTrue(_graph.TryGetStatus(_environment, ModuleA, out ModuleLoadStatus status));
            Assert.AreEqual(ModuleLoadStatus.Loading, status);
        }

        [Test]
        public void RequireGraph_DEV1_TwoModuleCycleNamesPath()
        {
            Assert.AreEqual(RequireStepKind.Load, _graph.Begin(_environment, ModuleA, PathA, _script).Kind);
            Assert.AreEqual(RequireStepKind.Load, _graph.Begin(_environment, ModuleB, PathB, _script).Kind);
            RequireStep step = _graph.Begin(_environment, ModuleA, PathA, _script);
            Assert.AreEqual(RequireStepKind.Cycle, step.Kind);
            CollectionAssert.AreEqual(new string[] { PathA, PathB, PathA }, step.CyclePath);
        }

        [Test]
        public void RequireGraph_SelfRequire_IsACycle()
        {
            object bodyA = new object();
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.HandOff(_environment, ModuleA, bodyA);
            RequireStep step = _graph.Begin(_environment, ModuleA, PathA, bodyA);
            Assert.AreEqual(RequireStepKind.Cycle, step.Kind);
            CollectionAssert.AreEqual(new string[] { PathA, PathA }, step.CyclePath);
        }

        [Test]
        public void RequireGraph_DEV1_CrossThreadWaitCycleIsDetected()
        {
            object bodyA = new object();
            object bodyB = new object();
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.HandOff(_environment, ModuleA, bodyA);
            Assert.AreEqual(RequireStepKind.Load, _graph.Begin(_environment, ModuleB, PathB, bodyA).Kind);
            _graph.HandOff(_environment, ModuleB, bodyB);
            RequireStep step = _graph.Begin(_environment, ModuleA, PathA, bodyB);
            Assert.AreEqual(RequireStepKind.Cycle, step.Kind);
            CollectionAssert.AreEqual(new string[] { PathA, PathB, PathA }, step.CyclePath);
        }

        [Test]
        public void RequireGraph_DEV1_ThreeModuleCycleAcrossInlineAndBodyThreads()
        {
            object bodyB = new object();
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.Begin(_environment, ModuleB, PathB, _script);
            _graph.HandOff(_environment, ModuleB, bodyB);
            Assert.AreEqual(RequireStepKind.Load, _graph.Begin(_environment, ModuleC, PathC, bodyB).Kind);
            RequireStep step = _graph.Begin(_environment, ModuleA, PathA, bodyB);
            Assert.AreEqual(RequireStepKind.Cycle, step.Kind);
            CollectionAssert.AreEqual(new string[] { PathA, PathB, PathC, PathA }, step.CyclePath);
        }

        [Test]
        public void RequireGraph_ConcurrentRequestWaitsInsteadOfCycling()
        {
            object bodyA = new object();
            object sibling = new object();
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.HandOff(_environment, ModuleA, bodyA);
            RequireStep step = _graph.Begin(_environment, ModuleA, PathA, sibling);
            Assert.AreEqual(RequireStepKind.Wait, step.Kind);
            Assert.AreSame(bodyA, step.LoadingThread);
        }

        [Test]
        public void RequireGraph_WaitChainWithoutLoopIsNotACycle()
        {
            object bodyA = new object();
            object bodyB = new object();
            object sibling = new object();
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.HandOff(_environment, ModuleA, bodyA);
            _graph.Begin(_environment, ModuleB, PathB, bodyA);
            _graph.HandOff(_environment, ModuleB, bodyB);
            Assert.AreEqual(RequireStepKind.Wait, _graph.Begin(_environment, ModuleA, PathA, sibling).Kind);
            Assert.AreEqual(RequireStepKind.Wait, _graph.Begin(_environment, ModuleB, PathB, sibling).Kind);
        }

        [Test]
        public void RequireGraph_CompleteWakesEveryWaiterOnce()
        {
            object bodyA = new object();
            object sibling = new object();
            object value = new object();
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.HandOff(_environment, ModuleA, bodyA);
            _graph.Begin(_environment, ModuleA, PathA, sibling);
            IReadOnlyList<object> woken = _graph.Complete(_environment, ModuleA, value);
            Assert.AreEqual(2, woken.Count);
            CollectionAssert.Contains(woken, _script);
            CollectionAssert.Contains(woken, sibling);
            Assert.AreSame(value, _graph.Begin(_environment, ModuleA, PathA, sibling).Value);
        }

        [Test]
        public void RequireGraph_R3_8_FailedModuleStaysFailedPerEnvironment()
        {
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.Fail(_environment, ModuleA, "ReplicatedStorage.A:3: attempt to index nil");
            RequireStep first = _graph.Begin(_environment, ModuleA, PathA, _script);
            RequireStep second = _graph.Begin(_environment, ModuleA, PathA, new object());
            Assert.AreEqual(RequireStepKind.Failed, first.Kind);
            Assert.AreEqual("ReplicatedStorage.A:3: attempt to index nil", first.FirstError);
            Assert.AreEqual(first.FirstError, second.FirstError);
        }

        [Test]
        public void RequireGraph_OtherEnvironmentLoadsTheModuleAfresh()
        {
            object otherEnvironment = new object();
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.Fail(_environment, ModuleA, "boom");
            Assert.AreEqual(RequireStepKind.Load, _graph.Begin(otherEnvironment, ModuleA, PathA, new object()).Kind);
        }

        [Test]
        public void RequireGraph_FailWakesWaitersWhoThenSeeTheFailure()
        {
            object bodyA = new object();
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.HandOff(_environment, ModuleA, bodyA);
            IReadOnlyList<object> woken = _graph.Fail(_environment, ModuleA, "boom");
            CollectionAssert.AreEqual(new object[] { _script }, woken);
            Assert.AreEqual(RequireStepKind.Failed, _graph.Begin(_environment, ModuleA, PathA, _script).Kind);
        }

        [Test]
        public void RequireGraph_ForgetThread_FailsItsLoadsAndWakesWaiters()
        {
            object bodyA = new object();
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.HandOff(_environment, ModuleA, bodyA);
            IReadOnlyList<object> woken = _graph.ForgetThread(_environment, bodyA, "cancelled");
            CollectionAssert.AreEqual(new object[] { _script }, woken);
            RequireStep step = _graph.Begin(_environment, ModuleA, PathA, _script);
            Assert.AreEqual(RequireStepKind.Failed, step.Kind);
            Assert.AreEqual("cancelled", step.FirstError);
            Assert.AreEqual(0, _graph.ForgetThread(_environment, new object(), "unknown").Count);
        }

        [Test]
        public void RequireGraph_ForgetEnvironment_DropsEverything()
        {
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.Complete(_environment, ModuleA, new object());
            _graph.ForgetEnvironment(_environment);
            Assert.IsFalse(_graph.TryGetStatus(_environment, ModuleA, out ModuleLoadStatus _));
            Assert.AreEqual(RequireStepKind.Load, _graph.Begin(_environment, ModuleA, PathA, _script).Kind);
        }

        [Test]
        public void RequireGraph_EnvironmentsUsing_ListsTransitiveDependents()
        {
            object otherEnvironment = new object();
            object unrelated = new object();
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.Begin(_environment, ModuleB, PathB, _script);
            _graph.Complete(_environment, ModuleB, new object());
            _graph.Complete(_environment, ModuleA, new object());
            _graph.Begin(otherEnvironment, ModuleB, PathB, new object());
            _graph.Begin(unrelated, ModuleC, PathC, new object());
            IReadOnlyList<object> usingB = _graph.EnvironmentsUsing(ModuleB);
            Assert.AreEqual(2, usingB.Count);
            CollectionAssert.Contains(usingB, _environment);
            CollectionAssert.Contains(usingB, otherEnvironment);
            Assert.AreEqual(0, _graph.EnvironmentsUsing(new InstanceId(99)).Count);
        }

        [Test]
        public void RequireGraph_CompleteOfAModuleNotLoading_Throws()
        {
            _graph.Begin(_environment, ModuleA, PathA, _script);
            _graph.Complete(_environment, ModuleA, new object());
            Assert.Throws<InvalidOperationException>(() => _graph.Complete(_environment, ModuleA, new object()));
            Assert.Throws<InvalidOperationException>(() => _graph.Fail(_environment, ModuleB, "boom"));
            Assert.Throws<InvalidOperationException>(() => _graph.Complete(new object(), ModuleA, new object()));
        }

        [Test]
        public void RequireGraph_InvalidArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => _graph.Begin(null, ModuleA, PathA, _script));
            Assert.Throws<ArgumentNullException>(() => _graph.Begin(_environment, ModuleA, PathA, null));
            Assert.Throws<ArgumentException>(() => _graph.Begin(_environment, InstanceId.None, PathA, _script));
            Assert.Throws<ArgumentException>(() => _graph.Begin(_environment, ModuleA, "", _script));
        }
    }
}
