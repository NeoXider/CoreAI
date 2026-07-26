using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Scripting;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Proves the withheld-capability stubs: an API whose tier/flag is withheld resolves to a stub
    /// that raises an actionable <see cref="LuaApiWithheldException"/> naming the missing grant and
    /// the alternative, instead of a bare "attempt to call a nil value" that feeds the quarantine
    /// error streak with no hint. The test assembly does not reference Lua.dll, so Lua-side failures
    /// are caught via the non-generic <see cref="Assert.Catch(TestDelegate)"/> and asserted by message.
    /// </summary>
    public sealed class LuaCsWithheldApiStubEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>
        /// See <see cref="LuaCsModRuntimeEditModeTests"/>: the Lua-CSharp runtime blocks on its async
        /// VM, so the Unity main-thread SynchronizationContext must be detached to avoid deadlocks.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class FakeCommandSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Commands.Add(command);
            }
        }

        private sealed class FakeGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        /// <summary>Records registered global names so the stub name lists can be drift-checked.</summary>
        private sealed class RecordingRegistry : IScriptFunctionRegistry
        {
            public readonly HashSet<string> Names = new(StringComparer.Ordinal);

            public void Register(string name, Delegate callback)
            {
                Names.Add(name);
            }

            public void RegisterVarArgs(string name, Func<ScriptCallContext, ScriptCallResult> callback)
            {
                Names.Add(name);
            }

            public bool Contains(string name)
            {
                return Names.Contains(name);
            }

            public void ApplyTo(IScriptState state)
            {
            }
        }

        private static LuaCsModStack BuildStack(
            FakeCommandSink sink,
            bool registerWorldEditBuildBindings)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                CommandSink = sink,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = new LuaCsRbxApiBindings(),
                RegisterWorldEditBuildBindings = registerWorldEditBuildBindings
            });
        }

        [Test]
        public void WorldBuildBindingsDisabled_CallingWorldDestroy_ThrowsActionableError_NotNilCall()
        {
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(sink, false);

            Exception ex = Assert.Catch(() =>
                stack.Runtime.LoadMod("m", "coreai_world_destroy('victim')", LuaCapabilities.All));

            string text = ex.ToString();
            StringAssert.Contains("coreai_world_destroy", text,
                "The error must name the withheld API the mod called.");
            StringAssert.Contains("WorldEdit build bindings", text,
                "The error must name the withheld surface instead of a bare nil-value call.");
            StringAssert.Contains("Rbx", text, "The error must point at the Rbx alternative.");
            StringAssert.DoesNotContain("attempt to call a nil value", text,
                "The stub must preempt the unactionable nil-call error.");
            Assert.IsFalse(stack.Runtime.IsLoaded("m"));
            Assert.AreEqual(0, sink.Commands.Count, "A withheld stub must never route a world command.");
        }

        [Test]
        public void FullNotGranted_CallingUnityFindAll_ThrowsActionableError_NamingFullCapability()
        {
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(sink, true);

            // WHY: the mod asks for All | Full but the host ceiling (All) masks Full away — the exact
            // trap sample_camera_pulse hits under the default composition.
            Exception ex = Assert.Catch(() => stack.Runtime.LoadMod("m",
                "unity_find_all('Camera', 1)",
                LuaCapabilities.All | LuaCapabilities.Full));

            string text = ex.ToString();
            StringAssert.Contains("unity_find_all", text,
                "The error must name the withheld API the mod called.");
            StringAssert.Contains("Full capability", text,
                "The error must name the missing Full grant instead of a bare nil-value call.");
            StringAssert.DoesNotContain("attempt to call a nil value", text,
                "The stub must preempt the unactionable nil-call error.");
            Assert.IsFalse(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void StubNameLists_MatchTheRealRegistrations()
        {
            RecordingRegistry world = new();
            new LuaCsWorldRuntimeBindings(new FakeCommandSink()).RegisterGameplayApis(world);
            CollectionAssert.AreEquivalent(LuaCsWorldRuntimeBindings.BuildApiNames, world.Names,
                "BuildApiNames must mirror RegisterGameplayApis so the stubs cover the exact surface.");

            RecordingRegistry components = new();
            new LuaCsComponentRuntimeBindings(new FakeCommandSink()).RegisterGameplayApis(components);
            CollectionAssert.AreEquivalent(LuaCsComponentRuntimeBindings.BuildApiNames, components.Names,
                "BuildApiNames must mirror RegisterGameplayApis so the stubs cover the exact surface.");

            RecordingRegistry full = new();
            new LuaCsFullUnityRuntimeBindings().RegisterGameplayApis(full);
            CollectionAssert.AreEquivalent(LuaCsFullUnityRuntimeBindings.ApiNames, full.Names,
                "ApiNames must mirror RegisterGameplayApis so the stubs cover the exact surface.");
        }
    }
}
