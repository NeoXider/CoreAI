using System;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Cross-mod surface: mods_export / mods_get / mods_call / mods_list_exports. Checks are
    /// asserted inside the Lua chunks (error() on mismatch), so a failing expectation fails the
    /// LoadMod call itself.
    /// </summary>
    public sealed class LuaModExportsEditModeTests
    {
        private const string ProviderSource = @"
            local hits = 0
            mods_export('greeting', 'hi')
            mods_export('cfg', { n = 2, inner = { k = 'v' } })
            mods_export('add', function(a, b)
                hits = hits + 1
                return a + b
            end)
            mods_export('hits', function() return hits end)
        ";

        [Test]
        public void ModsGet_VariableAndNestedTable_ReadableFromAnotherMod()
        {
            LuaModRuntime runtime = new();
            runtime.LoadMod("b", ProviderSource);

            Assert.DoesNotThrow(() => runtime.LoadMod("a", @"
                if mods_get('b', 'greeting') ~= 'hi' then error('greeting mismatch') end
                local cfg = mods_get('b', 'cfg')
                if cfg.n ~= 2 or cfg.inner.k ~= 'v' then error('cfg mismatch') end
                cfg.n = 99 -- mutate the copy
            "));

            // Marshalling is by value: the mutation above must not leak into b's export.
            Assert.DoesNotThrow(() => runtime.LoadMod("c",
                "if mods_get('b', 'cfg').n ~= 2 then error('export was mutated by a reader') end"));
        }

        [Test]
        public void ModsCall_FunctionWithArgsAndState_WorksAcrossMods()
        {
            LuaModRuntime runtime = new();
            runtime.LoadMod("b", ProviderSource);

            Assert.DoesNotThrow(() => runtime.LoadMod("a", @"
                if mods_call('b', 'add', 20, 22) ~= 42 then error('add result') end
                if mods_call('b', 'hits') ~= 1 then error('provider state not kept') end
                local names = mods_list_exports('b')
                if #names ~= 4 then error('expected 4 exports, got ' .. #names) end
            "));
        }

        [Test]
        public void ModsGet_OnFunction_AndModsCall_OnValue_ThrowDescriptiveErrors()
        {
            LuaModRuntime runtime = new();
            runtime.LoadMod("b", ProviderSource);

            Exception get = Assert.Catch<Exception>(() =>
                runtime.LoadMod("x1", "mods_get('b', 'add')"));
            StringAssert.Contains("mods_call", get.Message);

            Exception call = Assert.Catch<Exception>(() =>
                runtime.LoadMod("x2", "mods_call('b', 'greeting')"));
            StringAssert.Contains("not a function", call.Message);
        }

        [Test]
        public void ModsCall_UnknownModOrExport_NamesTheMissingPiece()
        {
            LuaModRuntime runtime = new();
            runtime.LoadMod("b", ProviderSource);

            Exception noMod = Assert.Catch<Exception>(() =>
                runtime.LoadMod("x1", "mods_call('ghost', 'add', 1, 2)"));
            StringAssert.Contains("ghost", noMod.Message);

            Exception noExport = Assert.Catch<Exception>(() =>
                runtime.LoadMod("x2", "mods_call('b', 'missing')"));
            StringAssert.Contains("missing", noExport.Message);
        }

        [Test]
        public void Reload_DropsOldExports()
        {
            LuaModRuntime runtime = new();
            runtime.LoadMod("b", ProviderSource);
            runtime.ReloadMod("b", "local quiet = true");

            Assert.DoesNotThrow(() => runtime.LoadMod("a",
                "if #mods_list_exports('b') ~= 0 then error('stale exports survived reload') end"));
            Assert.Catch<Exception>(() => runtime.LoadMod("x", "mods_get('b', 'greeting')"));
        }

        [Test]
        public void ModsCall_MutualRecursion_FailsWithDepthError_NotStackOverflow()
        {
            LuaModRuntime runtime = new();
            runtime.LoadMod("a", "mods_export('f', function() return mods_call('b', 'g') end)");
            runtime.LoadMod("b", "mods_export('g', function() return mods_call('a', 'f') end)");

            Exception cycle = Assert.Catch<Exception>(() =>
                runtime.LoadMod("c", "mods_call('a', 'f')"));
            StringAssert.Contains("depth", cycle.Message);
        }

        [Test]
        public void ModsExport_NonPortableValue_IsRejectedAtReadTime()
        {
            LuaModRuntime runtime = new();
            // Exporting a coroutine is allowed (the export dictionary stores any DynValue),
            // but reading it from another mod must fail with the portable-types error.
            runtime.LoadMod("b", "mods_export('co', coroutine.create(function() end))");

            Exception read = Assert.Catch<Exception>(() =>
                runtime.LoadMod("a", "mods_get('b', 'co')"));
            StringAssert.Contains("nil/boolean/number/string/table", read.Message);
        }
    }
}
