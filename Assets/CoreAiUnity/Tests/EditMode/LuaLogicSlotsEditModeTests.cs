#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LuaLogicSlotsEditModeTests
    {
        [Test]
        public void LuaLogicSlots_DeclaredSlotLogicDefine_TryInvokeNumberReturnsOverride()
        {
            LuaLogicSlots slots = new();
            slots.DeclareSlot("damage");
            Script script = CreateScript(slots);

            new SecureLuaEnvironment().RunChunk(
                script,
                "logic_define('damage', function(a, b) return a * 2 + b end)");

            Assert.IsTrue(slots.TryInvokeNumber("damage", out double value, 10, 5));
            Assert.AreEqual(25d, value);
        }

        [Test]
        public void LuaLogicSlots_UndeclaredSlotLogicDefine_ThrowsLuaError()
        {
            LuaLogicSlots slots = new();
            Script script = CreateScript(slots);

            Assert.Throws<ScriptRuntimeException>(() =>
                new SecureLuaEnvironment().RunChunk(script, "logic_define('missing', function() return 1 end)"));
        }

        [Test]
        public void LuaLogicSlots_NonOverriddenSlot_TryInvokeNumberReturnsFalse()
        {
            LuaLogicSlots slots = new();
            slots.DeclareSlot("damage");

            Assert.IsFalse(slots.TryInvokeNumber("damage", out double value, 10, 5));
            Assert.AreEqual(0d, value);
        }

        [Test]
        public void LuaLogicSlots_LogicReset_RemovesOverride()
        {
            LuaLogicSlots slots = new();
            slots.DeclareSlot("damage");
            SecureLuaEnvironment env = new();
            Script script = CreateScript(slots);

            env.RunChunk(script, "logic_define('damage', function() return 7 end)");
            Assert.IsTrue(slots.IsOverridden("damage"));

            env.RunChunk(script, "logic_reset('damage')");

            Assert.IsFalse(slots.IsOverridden("damage"));
            Assert.IsFalse(slots.TryInvokeNumber("damage", out _));
        }

        [Test]
        public void LuaLogicSlots_OverrideThrows_TryInvokeReturnsFalseAndRemovesOverride()
        {
            LuaLogicSlots slots = new();
            slots.DeclareSlot("damage");
            Script script = CreateScript(slots);

            new SecureLuaEnvironment().RunChunk(
                script,
                "logic_define('damage', function() error('boom') end)");

            Assert.IsFalse(slots.TryInvoke("damage", out DynValue result));
            Assert.AreEqual(DataType.Nil, result.Type);
            Assert.IsFalse(slots.IsOverridden("damage"));
            Assert.IsNotEmpty(slots.LastError);
        }

        [Test]
        public void LuaLogicSlots_LogicList_ReturnsDeclaredSlotsWithOverrideFlags()
        {
            LuaLogicSlots slots = new();
            slots.DeclareSlot("damage");
            slots.DeclareSlot("price");
            SecureLuaEnvironment env = new();
            Script script = CreateScript(slots);

            DynValue result = env.RunChunk(script, @"
                logic_define('damage', function() return 1 end)
                local list = logic_list()
                local foundDamage = false
                local foundPrice = false
                for i = 1, #list do
                    if list[i].name == 'damage' and list[i].overridden == true then
                        foundDamage = true
                    end
                    if list[i].name == 'price' and list[i].overridden == false then
                        foundPrice = true
                    end
                end
                return #list == 2 and foundDamage and foundPrice");

            Assert.IsTrue(result.Boolean);
        }

        private static Script CreateScript(LuaLogicSlots slots)
        {
            LuaApiRegistry reg = new();
            slots.RegisterApis(reg);
            return new SecureLuaEnvironment().CreateScript(reg);
        }
    }
}
#endif
