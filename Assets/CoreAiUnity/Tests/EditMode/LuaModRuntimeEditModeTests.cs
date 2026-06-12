#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using MoonSharp.Interpreter;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LuaModRuntimeEditModeTests
    {
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
                foreach ((string storedModId, string key) in _values.Keys)
                {
                    if (storedModId == modId)
                    {
                        keys.Add((storedModId, key));
                    }
                }

                foreach ((string storedModId, string key) in keys)
                {
                    _values.Remove((storedModId, key));
                }
            }
        }

        [Test]
        public void LuaModRuntime_LoadMod_ValidChunkLoadsAndDuplicateIdThrows()
        {
            LuaModRuntime runtime = new();

            runtime.LoadMod("m", "local x = 1");

            Assert.IsTrue(runtime.IsLoaded("m"));
            Assert.Throws<InvalidOperationException>(() => runtime.LoadMod("m", "local x = 2"));
        }

        [Test]
        public void LuaModRuntime_LoadMod_BadLuaCodeThrowsAndIsLoadedFalse()
        {
            LuaModRuntime runtime = new();

            Assert.Throws<ScriptRuntimeException>(() => runtime.LoadMod("bad", "error('bad')"));
            Assert.IsFalse(runtime.IsLoaded("bad"));
        }

        [Test]
        public void LuaModRuntime_HooksOnEmitEventTick_DispatchesHandler()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", @"
                hooks_on('hit', function(name, payload)
                    store_set('last', payload)
                end)");

            runtime.EmitEvent("hit", "42");
            runtime.Tick(0);

            Assert.AreEqual("42", store.Get("m", "last"));
        }

        [Test]
        public void LuaModRuntime_HooksEvery_IntervalElapsed_FiresOnce()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", @"
                hooks_every(0.1, function()
                    local n = tonumber(store_get('ticks')) or 0
                    store_set('ticks', tostring(n + 1))
                end)");

            runtime.Tick(0.05);
            Assert.AreEqual("", store.Get("m", "ticks"));

            runtime.Tick(0.06);
            Assert.AreEqual("1", store.Get("m", "ticks"));
        }

        [Test]
        public void LuaModRuntime_HooksEvery_IntervalBelowMinimum_LoadModThrows()
        {
            LuaModRuntime runtime = new();

            Assert.Throws<ArgumentException>(() =>
                runtime.LoadMod("m", "hooks_every(0.01, function() end)"));
            Assert.IsFalse(runtime.IsLoaded("m"));
        }

        [Test]
        public void LuaModRuntime_EventsEmit_FromOneMod_ReachesOtherModAndRaisesEvent()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            string emittedModId = "";
            string emittedName = "";
            string emittedPayload = "";
            runtime.ModEventEmitted += (modId, name, payload) =>
            {
                emittedModId = modId;
                emittedName = name;
                emittedPayload = payload;
            };

            runtime.LoadMod("b", @"
                hooks_on('ping', function(name, payload)
                    store_set('received', payload)
                end)");
            runtime.LoadMod("a", "events_emit('ping', 'hello')");

            Assert.AreEqual("a", emittedModId);
            Assert.AreEqual("ping", emittedName);
            Assert.AreEqual("hello", emittedPayload);

            runtime.Tick(0);
            Assert.AreEqual("hello", store.Get("b", "received"));
        }

        [Test]
        public void LuaModRuntime_HandlerRepeatedErrors_AutoUnloadsMod()
        {
            LuaModRuntime runtime = new();
            runtime.LoadMod("m", "hooks_on('bad', function() error('boom') end)");

            for (int i = 0; i < LuaModRuntime.MaxErrorsBeforeUnload; i++)
            {
                runtime.EmitEvent("bad", "");
                runtime.Tick(0);
            }

            Assert.IsFalse(runtime.IsLoaded("m"));
        }

        [Test]
        public void LuaModRuntime_UnloadModAndReloadMod_BasicBehavior()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", @"
                hooks_on('set', function()
                    store_set('value', 'old')
                end)");

            Assert.IsTrue(runtime.UnloadMod("m"));
            Assert.IsFalse(runtime.IsLoaded("m"));
            Assert.IsFalse(runtime.UnloadMod("m"));

            runtime.LoadMod("m", @"
                hooks_on('set', function()
                    store_set('value', 'old')
                end)");
            runtime.ReloadMod("m", @"
                hooks_on('set', function()
                    store_set('value', 'new')
                end)");

            runtime.EmitEvent("set", "");
            runtime.Tick(0);
            Assert.IsTrue(runtime.IsLoaded("m"));
            Assert.AreEqual("new", store.Get("m", "value"));
        }

        [Test]
        public void LuaModRuntime_ReloadMod_BadNewCode_KeepsOldModWorking()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", @"
                hooks_on('set', function()
                    store_set('value', 'old')
                end)");

            Assert.Throws<ScriptRuntimeException>(() => runtime.ReloadMod("m", "error('broken')"));

            Assert.IsTrue(runtime.IsLoaded("m"));
            runtime.EmitEvent("set", "");
            runtime.Tick(0);
            Assert.AreEqual("old", store.Get("m", "value"));
        }

        [Test]
        public void LuaModRuntime_SuccessfulHandlerCall_ResetsErrorCount()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", @"
                hooks_on('bad', function() error('boom') end)
                hooks_on('good', function() store_set('ok', '1') end)");

            // Just below the unload threshold, then one success: the counter must reset, so the
            // same amount of sporadic failures again must not unload the mod.
            for (int i = 0; i < LuaModRuntime.MaxErrorsBeforeUnload - 1; i++)
            {
                runtime.EmitEvent("bad", "");
                runtime.Tick(0);
            }

            runtime.EmitEvent("good", "");
            runtime.Tick(0);
            Assert.AreEqual("1", store.Get("m", "ok"));

            for (int i = 0; i < LuaModRuntime.MaxErrorsBeforeUnload - 1; i++)
            {
                runtime.EmitEvent("bad", "");
                runtime.Tick(0);
            }

            Assert.IsTrue(runtime.IsLoaded("m"));
        }

        private sealed class ScopedBindingsStub : IGameLuaRuntimeBindings, ICapabilityScopedLuaBindings
        {
            public void RegisterGameplayApis(CoreAI.Sandbox.LuaApiRegistry registry)
            {
                RegisterGameplayApis(registry, LuaCapabilities.All);
            }

            public void RegisterGameplayApis(
                CoreAI.Sandbox.LuaApiRegistry registry,
                LuaCapabilities capabilities)
            {
                if ((capabilities & LuaCapabilities.Read) != 0)
                {
                    registry.Register("stub_read", new Func<double>(() => 1d));
                }

                if ((capabilities & LuaCapabilities.WorldEdit) != 0)
                {
                    registry.Register("stub_edit", new Func<double>(() => 2d));
                }
            }
        }

        private sealed class UnscopedBindingsStub : IGameLuaRuntimeBindings
        {
            public void RegisterGameplayApis(CoreAI.Sandbox.LuaApiRegistry registry)
            {
                registry.Register("stub_edit", new Func<double>(() => 2d));
            }
        }

        [Test]
        public void LuaModRuntime_LoadMod_ReadCapability_DoesNotExposeWorldEditApi()
        {
            LuaModRuntime runtime = new(new ScopedBindingsStub());

            runtime.LoadMod("reader", "local v = stub_read()", LuaCapabilities.Read);
            Assert.IsTrue(runtime.IsLoaded("reader"));

            // World-edit binding must be physically absent for a read-only mod.
            Assert.Throws<ScriptRuntimeException>(() =>
                runtime.LoadMod("writer", "stub_edit()", LuaCapabilities.Read));
        }

        [Test]
        public void LuaModRuntime_LoadMod_RestrictedModWithUnscopedBindings_FailsClosed()
        {
            LuaModRuntime runtime = new(new UnscopedBindingsStub());

            // Unscoped bindings cannot be trimmed, so a restricted mod must get no game APIs.
            Assert.Throws<ScriptRuntimeException>(() =>
                runtime.LoadMod("m", "stub_edit()", LuaCapabilities.Read));

            // Full capabilities keep historical behavior.
            runtime.LoadMod("full", "stub_edit()", LuaCapabilities.All);
            Assert.IsTrue(runtime.IsLoaded("full"));
        }
    }
}
#endif
