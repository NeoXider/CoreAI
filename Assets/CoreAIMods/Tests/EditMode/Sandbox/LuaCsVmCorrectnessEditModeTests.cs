#if COREAI_LUA
using System.Threading;
using CoreAI.Ai.LuaCs;
using CoreAI.Sandbox.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Sandbox
{
    /// <summary>
    /// Mod-visible Lua-CSharp fixes taken with 0.5.8, run the way a mod runs them (the production sandbox under its
    /// guard): the length and sort of a table filled from the top down (#345/#346), <c>self</c> in a method call
    /// through an <c>__index</c> function (#344/#347), and the positional <c>table.insert</c> that now shifts only up to
    /// the border the length reports. Each has a negative twin that held before the fix as well.
    /// </summary>
    [TestFixture]
    public sealed class LuaCsVmCorrectnessEditModeTests
    {
        private SynchronizationContext _savedContext;

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

        private static LuaValue[] RunInSandbox(string source)
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            return env.RunChunk(state, source, new LuaCsExecutionGuard(60_000, 5_000_000, 0));
        }

        [TestCase(9)]
        [TestCase(10)]
        [TestCase(16)]
        [TestCase(17)]
        [TestCase(33)]
        public void TableFilledFromTheTopDown_HasItsFullLength(int count)
        {
            // WHY: on 0.5.6 a table filled backwards with nine or more elements reported #t == 8, so a loop over it
            // silently skipped the rest.
            LuaValue[] result = RunInSandbox("local t = {} for i = " + count + ", 1, -1 do t[i] = i end return #t");
            Assert.AreEqual((double)count, result[0].Read<double>());
        }

        [TestCase(8)]
        [TestCase(9)]
        [TestCase(33)]
        public void TableFilledFromTheBottomUp_HasItsFullLength_NegativeTwin(int count)
        {
            LuaValue[] result = RunInSandbox("local t = {} for i = 1, " + count + " do t[i] = i end return #t");
            Assert.AreEqual((double)count, result[0].Read<double>());
        }

        [TestCase(9)]
        [TestCase(17)]
        public void TableFilledFromTheTopDown_SortsEveryElement(int count)
        {
            LuaValue[] result = RunInSandbox(
                "local t = {} for i = " + count + ", 1, -1 do t[i] = " + (count + 1) + " - i end\n" +
                "table.sort(t)\n" +
                "return table.concat(t, ',')");
            Assert.AreEqual(Sequence(count), result[0].Read<string>());
        }

        [Test]
        public void TableFilledFromTheBottomUp_SortsEveryElement_NegativeTwin()
        {
            LuaValue[] result = RunInSandbox(
                "local t = {} for i = 1, 9 do t[i] = 10 - i end table.sort(t) return table.concat(t, ',')");
            Assert.AreEqual(Sequence(9), result[0].Read<string>());
        }

        [Test]
        public void MethodCall_ThroughAnIndexFunction_PassesTheObjectAsSelf()
        {
            // WHY: on 0.5.6 obj:Method() on a local whose metatable's __index is a Lua function handed the method the
            // KEY ("Method") as self - the shape of every class built on an __index function (inheritance helpers,
            // proxies, lazy members).
            LuaValue[] result = RunInSandbox(
                "local mt = {} mt.__index = function(t, k) return function(self) return self end end\n" +
                "local obj = setmetatable({}, mt)\n" +
                "local s = obj:Method()\n" +
                "return rawequal(s, obj), type(s)");
            Assert.IsTrue(result[0].Read<bool>(), "self was a " + result[1]);
        }

        [Test]
        public void MethodCall_AfterAWriteThroughNewIndex_PassesTheObjectAsSelf()
        {
            // WHY (#344): the reported shape - a value written through __newindex just before the call.
            LuaValue[] result = RunInSandbox(
                "local mt = {} mt.__index = function(t, k) return function(self) return self end end\n" +
                "mt.__newindex = function(t, k, v) rawset(t, '_' .. k, v) end\n" +
                "local obj = setmetatable({}, mt)\n" +
                "local position = {x = 1}\n" +
                "obj.Position = position\n" +
                "local s = obj:Method()\n" +
                "return rawequal(s, obj), rawequal(s, position)");
            Assert.IsTrue(result[0].Read<bool>(), "self must be the object");
            Assert.IsFalse(result[1].Read<bool>(), "never the value the previous write passed");
        }

        [Test]
        public void MethodCall_ThroughAnIndexTable_PassesTheObjectAsSelf_NegativeTwin()
        {
            LuaValue[] result = RunInSandbox(
                "local Class = {} Class.__index = Class\n" +
                "function Class.Method(self) return self end\n" +
                "local obj = setmetatable({}, Class)\n" +
                "return rawequal(obj:Method(), obj)");
            Assert.IsTrue(result[0].Read<bool>());
        }

        [Test]
        public void InstanceMethodCall_AfterAPropertyWrite_PassesTheInstance()
        {
            // WHY: CoreAI's instance proxies resolve members in a C# __index, which neither VM got wrong; this pins
            // the Roblox shape the fix concerns on the real bindings.
            LuaCsRbxApiBindings bindings = new();
            LuaCsSecureSandboxEditModeTests.NestedRunModStore store = new();
            LuaCsModStack stack = LuaCsSecureSandboxEditModeTests.NewNestedRunModStack(bindings, store);
            stack.Runtime.LoadMod("m",
                "local part = Instance.new('Part')\n" +
                "part.Parent = workspace\n" +
                "part.Position = Vector3.new(1, 2, 3)\n" +
                "local isPart = part:IsA('Part')\n" +
                "part.Size = Vector3.new(2, 2, 2)\n" +
                "store_set('result', tostring(isPart) .. '|' .. part:GetFullName())");

            Assert.AreEqual("true|Workspace.Part", store.Get("m", "result"),
                LuaCsSecureSandboxEditModeTests.HandlerErrorsOf(stack));
        }

        [Test]
        public void PositionalTableInsert_ShiftsOnlyUpToTheBorderTheLengthReports()
        {
            // WHY: 0.5.8 shifts t[pos..#t] and nothing past it; 0.5.6 also moved the values behind the first nil.
            // For this table this VM reports #t == 2. Nothing CoreAI ships inserts by position into a table with a
            // hole; a mod that did gets the new layout.
            LuaValue[] result = RunInSandbox(
                "local t = {1, 2, nil, 4}\n" +
                "local before = #t\n" +
                "table.insert(t, 1, 0)\n" +
                "return before, tostring(t[1]) .. ',' .. tostring(t[2]) .. ',' .. tostring(t[3]) .. ',' .. " +
                "tostring(t[4]) .. ',' .. tostring(t[5])");
            Assert.AreEqual(2d, result[0].Read<double>());
            Assert.AreEqual("0,1,2,4,nil", result[1].Read<string>());
        }

        [Test]
        public void PositionalTableInsert_IntoASequence_ShiftsEveryElement_NegativeTwin()
        {
            LuaValue[] result = RunInSandbox(
                "local t = {1, 2, 3} table.insert(t, 2, 9) return table.concat(t, ',')");
            Assert.AreEqual("1,9,2,3", result[0].Read<string>());
        }

        private static string Sequence(int count)
        {
            string[] values = new string[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = (i + 1).ToString();
            }

            return string.Join(",", values);
        }
    }
}
#endif
