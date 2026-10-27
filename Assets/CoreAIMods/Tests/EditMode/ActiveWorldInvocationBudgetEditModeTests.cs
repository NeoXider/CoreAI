#if COREAI_LUA
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using VContainer;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Verifies editor budgets against the real replacement-world runtime facade.</summary>
#if !UNITY_5_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class ActiveWorldInvocationBudgetEditModeTests
    {
        private const string ModId = "world-budget";
        private const string HealthySource = "store_set('healthy', 'yes')";
        private const string FiniteSource =
            "local n=0 for i=1,20000 do n=n+i end assert(n==200010000) store_set('finished','yes')";
        private readonly List<string> _directories = new List<string>();
        private SynchronizationContext _previousContext;

        private static ActorContext Host => CoreServicesInstaller.DefaultLocalHostIdentityProvider
            .GetActorContext(BuiltInAgentRoleIds.Programmer);

        [SetUp]
        public void SetUp()
        {
            // WHY: The synchronous VM entry point cannot wait on a blocked Unity synchronization context.
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                foreach (string directory in _directories)
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                _directories.Clear();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(_previousContext);
            }
        }

        [TestCase("load")]
        [TestCase("reload")]
        [TestCase("reload-with-mode")]
        public void ScopeOpenedBeforeWorldReplacement_BoundsActualDispatch_ThenRestoresNormalPolicy(string operation)
        {
            using IObjectResolver container = NewContainer();
            ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
            IBudgetedLuaModRuntime budgeted = (IBudgetedLuaModRuntime)runtime;
            using (budgeted.BeginInvocation(new LuaModInvocationLimits(1000, 1000)))
            {
                ReplaceWorld(container, operation == "load" ? null : HealthySource);
                Exception failure = Assert.Catch(() => Dispatch(runtime, operation, FiniteSource));
                AssertStepBudget(failure);
                if (operation == "load")
                    Assert.IsFalse(runtime.IsLoaded(Host, ModId));
                else
                    AssertSource(runtime, HealthySource);
            }

            Dispatch(runtime, operation, FiniteSource);
            AssertSource(runtime, FiniteSource);
        }

        [Test]
        public void ImportAfterWorldReplacement_UsesInvocationPolicy_AndKeepsItsFailureContract()
        {
            using IObjectResolver container = NewContainer();
            ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
            runtime.LoadMod(Host, ModId, FiniteSource);
            string bundle = runtime.ExportMod(Host, ModId);
            Assert.IsNotNull(bundle);

            using (((IBudgetedLuaModRuntime)runtime).BeginInvocation(new LuaModInvocationLimits(1000, 1000)))
            {
                ReplaceWorld(container, null);
                Assert.IsFalse(runtime.ImportMod(Host, bundle, LuaCapabilities.All),
                    "The existing import API reports a failed initial chunk as false.");
                Assert.IsFalse(runtime.IsLoaded(Host, ModId));
            }

            Assert.IsTrue(runtime.ImportMod(Host, bundle, LuaCapabilities.All));
            AssertSource(runtime, FiniteSource);
        }

        [Test]
        public void RevertAfterWorldReplacement_BoundsTheRestoredInitialChunk()
        {
            using IObjectResolver container = NewContainer();
            ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
            int revisionIndex;
            using (((IBudgetedLuaModRuntime)runtime).BeginInvocation(new LuaModInvocationLimits(1000, 1000)))
            {
                ReplaceWorld(container, FiniteSource);
                revisionIndex = FindRevision(runtime, FiniteSource);
                runtime.ReloadMod(Host, ModId, HealthySource);
                Exception failure = Assert.Catch(() => runtime.TryRevertMod(Host, ModId, revisionIndex, out _));
                AssertStepBudget(failure);
                AssertSource(runtime, HealthySource);
            }

            Assert.IsTrue(runtime.TryRevertMod(Host, ModId, revisionIndex, out string restored));
            Assert.AreEqual(FiniteSource, restored);
            AssertSource(runtime, FiniteSource);
        }

        [Test]
        public void NestedScopesEnforceLifo_AndRestoreOuterPolicyAcrossAnotherWorldReplacement()
        {
            using IObjectResolver container = NewContainer();
            ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
            IBudgetedLuaModRuntime budgeted = (IBudgetedLuaModRuntime)runtime;
            using IDisposable outer = budgeted.BeginInvocation(new LuaModInvocationLimits(1000, 1000));
            using (IDisposable inner = budgeted.BeginInvocation(new LuaModInvocationLimits(1000, 1_000_000)))
            {
                Assert.Throws<InvalidOperationException>(() => outer.Dispose());
                ReplaceWorld(container, null);
                Dispatch(runtime, "load", FiniteSource);
                AssertSource(runtime, FiniteSource);
            }

            ReplaceWorld(container, null);
            AssertStepBudget(Assert.Catch(() => Dispatch(runtime, "load", FiniteSource)));
            outer.Dispose();
            outer.Dispose();
            Dispatch(runtime, "load", FiniteSource);
            AssertSource(runtime, FiniteSource);
        }

        [Test]
        public void CancellationAfterReplacementStopsDispatch_AndDoesNotPoisonTheNextScope()
        {
            using IObjectResolver container = NewContainer();
            ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            using (((IBudgetedLuaModRuntime)runtime).BeginInvocation(new LuaModInvocationLimits(), cancellation.Token))
            {
                ReplaceWorld(container, null);
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => Dispatch(runtime, "load", HealthySource));
                Assert.IsFalse(runtime.IsLoaded(Host, ModId));
            }

            Dispatch(runtime, "load", HealthySource);
            AssertSource(runtime, HealthySource);
        }

        [Test]
        public void ScopeValidationRejectsMissingLimitsAndPrecancelledTokens_WithoutReplacingPolicy()
        {
            using IObjectResolver container = NewContainer();
            ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
            IBudgetedLuaModRuntime budgeted = (IBudgetedLuaModRuntime)runtime;
            using IDisposable outer = budgeted.BeginInvocation(new LuaModInvocationLimits(1000, 1000));
            Assert.Throws<ArgumentNullException>(() => budgeted.BeginInvocation(null));
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => budgeted.BeginInvocation(
                new LuaModInvocationLimits(1000, 1_000_000), cancellation.Token));
            AssertStepBudget(Assert.Catch(() => Dispatch(runtime, "load", FiniteSource)));
        }

        [Test]
        public void InvocationPolicyDoesNotEscapeItsLogicalExecutionContext()
        {
            using IObjectResolver container = NewContainer();
            ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
            ExecutionContext independentContext = ExecutionContext.Capture();
            Assert.IsNotNull(independentContext);
            using (((IBudgetedLuaModRuntime)runtime).BeginInvocation(new LuaModInvocationLimits(1000, 1000)))
            {
                ExecutionContext.Run(independentContext, _ => Dispatch(runtime, "load", FiniteSource), null);
                AssertSource(runtime, FiniteSource);
                AssertStepBudget(Assert.Catch(() => Dispatch(runtime, "reload", FiniteSource)));
            }

            Dispatch(runtime, "reload", FiniteSource);
            AssertSource(runtime, FiniteSource);
        }

        private static void Dispatch(ILuaModRuntime runtime, string operation, string source)
        {
            if (operation == "load") runtime.LoadMod(Host, ModId, source, persistToStore: false);
            else if (operation == "reload") runtime.ReloadMod(Host, ModId, source);
            else runtime.ReloadMod(Host, ModId, source, ModReloadMode.CleanStartupObjects);
        }

        private static void AssertStepBudget(Exception failure)
        {
            Assert.IsNotNull(failure, "The short editor policy must reject the finite chunk.");
            for (Exception cause = failure; cause != null; cause = ScriptExecutionErrors.NextCause(cause))
                if (cause is LuaStepBudgetException) return;
            Assert.Fail("Expected a typed LuaStepBudgetException cause: " + failure);
        }

        private static void AssertSource(ILuaModRuntime runtime, string expected)
        {
            Assert.IsTrue(runtime.TryGetModSource(Host, ModId, out string source));
            Assert.AreEqual(expected, source);
        }

        private static int FindRevision(ILuaModRuntime runtime, string source)
        {
            foreach (LuaScriptRevision revision in runtime.ListModVersions(Host, ModId))
                if (string.Equals(revision.Source, source, StringComparison.Ordinal)) return revision.Index;
            Assert.Fail("The replacement VM must record the executed source in its real version store.");
            return -1;
        }

        private static void ReplaceWorld(IObjectResolver container, string source)
        {
            LuaCsModRuntime previous = container.Resolve<LuaCsModStack>().Runtime;
            IRbxWorldRuntimeService worlds = container.Resolve<IRbxWorldRuntimeService>();
            RbxWorldPackagePayload captured = worlds.CaptureCurrent();
            RbxWorldModSource[] mods = source == null ? Array.Empty<RbxWorldModSource>() :
                new[] { new RbxWorldModSource(new LuaModManifest
                    { Id = ModId, Active = true, Capabilities = LuaCapabilities.All.ToString() }, source) };
            RbxWorldPackagePayload replacement = new RbxWorldPackagePayload(
                captured.CapturedAtUtc, captured.Settings, captured.Tree, captured.Parts, captured.CameraCFrame, mods);
            RbxWorldLoadResult result = worlds.LoadConfirmedAsync(replacement).GetAwaiter().GetResult();
            Assert.IsTrue(result.Success, result.Error);
            Assert.AreNotSame(previous, container.Resolve<LuaCsModStack>().Runtime);
        }

        private IObjectResolver NewContainer()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CoreAI-WorldBudget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            _directories.Add(directory);
            FileLuaModSourceStore sources = new FileLuaModSourceStore(directory, NullLog.Instance,
                persistenceSyncAsync: _ => UniTask.FromResult(true));
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance<IGameLogger>(new SilentGameLogger());
            builder.RegisterInstance<ILog>(NullLog.Instance);
            builder.RegisterInstance<IAiGameCommandSink>(new NoopCommandSink());
            builder.RegisterInstance<ILuaModStore>(new MemoryModStore());
            builder.RegisterInstance<ILuaScriptVersionStore>(new MemoryLuaScriptVersionStore());
            builder.RegisterCoreAiMods(applicationIsPlayingProvider: () => false,
                skillTextProvider: _ => null, worldSessionSourceStore: sources, worldPackageStore: new MemoryPackageStore());
            return builder.Build();
        }

        private sealed class MemoryModStore : ILuaModStore
        {
            private readonly Dictionary<(string Mod, string Key), string> _values =
                new Dictionary<(string Mod, string Key), string>();
            public string Get(string modId, string key) =>
                _values.TryGetValue((modId, key), out string value) ? value : "";
            public void Set(string modId, string key, string value) => _values[(modId, key)] = value;
            public void Clear(string modId)
            {
                foreach ((string Mod, string Key) key in new List<(string Mod, string Key)>(_values.Keys))
                    if (key.Mod == modId) _values.Remove(key);
            }
        }

        private sealed class MemoryPackageStore : IRbxWorldPackageStore
        {
            public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(string slot, RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default) =>
                UniTask.FromResult(new RbxWorldPackageWriteResult(true, "memory://manual/" + slot, ""));
            public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(string trigger, RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default) =>
                UniTask.FromResult(new RbxWorldPackageWriteResult(true, "memory://auto/" + trigger, ""));
            public UniTask<RbxWorldPackagePayload> LoadManualAsync(string slot,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public UniTask<RbxWorldPackagePayload> LoadAutoAsync(string fileName,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public IReadOnlyList<string> ListManualSlots() => Array.Empty<string>();
            public IReadOnlyList<string> ListAutoFiles() => Array.Empty<string>();
            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves() => Array.Empty<RbxAutoSaveInfo>();
        }

        private sealed class NoopCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command) { }
        }

        private sealed class SilentGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, Object context = null) { }
            public void LogInfo(GameLogFeature feature, string message, Object context = null) { }
            public void LogWarning(GameLogFeature feature, string message, Object context = null) { }
            public void LogError(GameLogFeature feature, string message, Object context = null) { }
        }
    }
}
#endif
