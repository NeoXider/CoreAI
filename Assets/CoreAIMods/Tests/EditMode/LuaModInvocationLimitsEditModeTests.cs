using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Exercises actual VM instruction guards and invocation-scoped cancellation.</summary>
#if !UNITY_5_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class LuaModInvocationLimitsEditModeTests
    {
        private SynchronizationContext _previous;
        [SetUp] public void SetUp() { _previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null); }
        [TearDown] public void TearDown() => SynchronizationContext.SetSynchronizationContext(_previous);

        private sealed class Store : ILuaModStore
        {
            private readonly Dictionary<(string, string), string> _data = new();
            public string Get(string id, string key) => _data.TryGetValue((id, key), out string value) ? value : "";
            public void Set(string id, string key, string value) => _data[(id, key)] = value;
            public void Clear(string id) { }
        }

        private static bool HasCause<T>(Exception error) where T : Exception
        {
            for (Exception current = error; current != null; current = ScriptExecutionErrors.NextCause(current))
                if (current is T) return true;
            return false;
        }

        private static LuaCsModStack NewStack(Store store, LuaCsRbxApiBindings rbx = null) =>
            LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                ModStore = store, RbxApi = rbx, Log = NullLog.Instance,
                Capabilities = LuaCapabilities.All, OneOffCapabilities = LuaCapabilities.All,
                RegisterWorldEditBuildBindings = false
            });

        private static void Load(LuaCsModStack stack, string id, string source) =>
            stack.Runtime.LoadMod(id, source, LuaCapabilities.All, persistToStore: false);

        [TestCase(0, 1L)]
        [TestCase(1001, 1L)]
        [TestCase(100, 0L)]
        public void InvocationLimitsRejectUnboundedValues(int milliseconds, long instructions)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LuaModInvocationLimits(milliseconds, instructions));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShortInstructionBudgetStopsAnInfiniteChunk_ThenNormalInvocationStillRuns(bool withRbx)
        {
            Store store = new();
            using LuaCsRbxApiBindings rbx = withRbx ? new LuaCsRbxApiBindings(
                registry: new InstanceRegistry(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion)) : null;
            LuaCsModStack stack = NewStack(store, rbx);
            try
            {
                Exception failure;
                using (stack.Runtime.BeginInvocation(new LuaModInvocationLimits(1000, 1000)))
                    failure = Assert.Catch(() => Load(stack, "budget-failure", "while true do end"));
                Assert.IsTrue(HasCause<LuaStepBudgetException>(failure), failure.ToString());
                Assert.IsFalse(stack.Runtime.IsLoaded("budget-failure"));
                Load(stack, "normal-after-budget", "local n=0 for i=1,20000 do n=n+i end assert(n==200010000) store_set('finished','yes')");
                Assert.AreEqual("yes", store.Get("normal-after-budget", "finished"),
                    "Disposing the short invocation must restore the host's normal instruction budget.");
            }
            finally { stack.Runtime.ShutdownWithoutPersistence(); }
        }

        [Test]
        public void CancellationBeforeLoadCreatesNoMod_AndTheNextInvocationIsIndependent()
        {
            Store store = new();
            LuaCsModStack stack = NewStack(store);
            try
            {
                using CancellationTokenSource cancellation = new();
                using (stack.Runtime.BeginInvocation(new LuaModInvocationLimits(), cancellation.Token))
                {
                    cancellation.Cancel();
                    Exception failure = Assert.Catch(() => Load(stack, "cancelled", "store_set('unexpected','yes')"));
                    Assert.IsTrue(HasCause<OperationCanceledException>(failure), failure.ToString());
                }
                Assert.IsFalse(stack.Runtime.IsLoaded("cancelled"));
                Assert.AreEqual("", store.Get("cancelled", "unexpected"));
                Load(stack, "next", "store_set('finished','yes')");
                Assert.AreEqual("yes", store.Get("next", "finished"));
            }
            finally { stack.Runtime.ShutdownWithoutPersistence(); }
        }

        [Test]
        public void YieldedMainChunkDoesNotRetainTheEditorsCancellationOrShortInstructionBudget()
        {
            Store store = new();
            using LuaCsRbxApiBindings rbx = new(registry: new InstanceRegistry(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion));
            LuaCsModStack stack = NewStack(store, rbx);
            try
            {
                using CancellationTokenSource cancellation = new();
                using (stack.Runtime.BeginInvocation(new LuaModInvocationLimits(1000, 2000), cancellation.Token))
                    Load(stack, "yielded", "task.wait(0.05) local n=0 for i=1,20000 do n=n+i end assert(n==200010000) store_set('finished','yes')");
                cancellation.Cancel();
                rbx.Scheduler.Advance(0.1f);
                Assert.AreEqual("yes", store.Get("yielded", "finished"));
                Assert.IsTrue(stack.Runtime.IsLoaded("yielded"));
            }
            finally { stack.Runtime.ShutdownWithoutPersistence(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancellationDuringTheVmLoopEscapesProtectedCalls(bool withRbx)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Assert.Ignore("The timer-driven cancellation probe requires a host timer thread.");
#endif
            Store store = new();
            using LuaCsRbxApiBindings rbx = withRbx ? new LuaCsRbxApiBindings(
                registry: new InstanceRegistry(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion)) : null;
            LuaCsModStack stack = NewStack(store, rbx);
            try
            {
                using CancellationTokenSource cancellation = new();
                using (stack.Runtime.BeginInvocation(new LuaModInvocationLimits(1000, long.MaxValue), cancellation.Token))
                {
                    cancellation.CancelAfter(10);
                    Exception failure = Assert.Catch(() => Load(stack, "cancel-in-loop", "pcall(function() while true do end end) store_set('unexpected','yes')"));
                    Assert.IsTrue(HasCause<OperationCanceledException>(failure), failure.ToString());
                }
                Assert.AreEqual("", store.Get("cancel-in-loop", "unexpected"));
                Assert.IsFalse(stack.Runtime.IsLoaded("cancel-in-loop"));
            }
            finally { stack.Runtime.ShutdownWithoutPersistence(); }
        }

        /// <summary>Compile-only coverage for the existing public default-literal call shape.</summary>
        private static void ExistingDefaultLiteralCalls(LuaCsCoroutineHandle handle, LuaCsScriptCoroutine coroutine)
        {
            if (handle != null) handle.Resume(default);
            if (coroutine != null) coroutine.Resume(default);
        }
    }
}
