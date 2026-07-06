using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// input_* Lua bindings. EditMode has no pressed keys, so every held/edge check must read
    /// false without throwing — including unknown key names and out-of-range mouse buttons.
    /// </summary>
    public sealed class CoreAiInputLuaRuntimeEditModeTests
    {
        private static Script CreateScript(out SecureLuaEnvironment env)
        {
            LuaApiRegistry registry = new();
            new CoreAiInputLuaRuntimeBindings().RegisterGameplayApis(registry);
            env = new SecureLuaEnvironment();
            return env.CreateScript(registry);
        }

        [Test]
        public void InputBindings_UnheldUnknownAndAliasKeys_ReadFalseWithoutThrowing()
        {
            Script script = CreateScript(out SecureLuaEnvironment env);

            DynValue result = env.RunChunk(script, @"
                return input_key('f13'), input_key('NoSuchKeyName'), input_key(''),
                       input_key('left'), input_key('0'), input_key_down('space'), input_key_up('a')");

            Assert.AreEqual(DataType.Tuple, result.Type);
            foreach (DynValue value in result.Tuple)
            {
                Assert.AreEqual(DataType.Boolean, value.Type);
                Assert.IsFalse(value.Boolean);
            }
        }

        [Test]
        public void InputBindings_MouseAndAxis_NumbersAndSafeFallbacks()
        {
            Script script = CreateScript(out SecureLuaEnvironment env);

            DynValue result = env.RunChunk(script, @"
                return type(input_mouse_x()), type(input_mouse_y()),
                       input_mouse_button(0), input_mouse_button(5), input_mouse_down(-1),
                       input_axis('NoSuchAxisDefined')");

            Assert.AreEqual("number", result.Tuple[0].String);
            Assert.AreEqual("number", result.Tuple[1].String);
            Assert.IsFalse(result.Tuple[3].Boolean, "out-of-range button must read false, not throw");
            Assert.IsFalse(result.Tuple[4].Boolean, "negative button must read false, not throw");
            Assert.AreEqual(0d, result.Tuple[5].Number, 0.0001d, "undefined axis must read 0");
        }

        [Test]
        public void Aggregator_GameplayTier_ExposesInputApi()
        {
            AggregatingGameLuaRuntimeBindings aggregator = new(
                null, null, null,
                input: new CoreAiInputLuaRuntimeBindings());

            LuaApiRegistry registry = new();
            aggregator.RegisterGameplayApis(registry, LuaCapabilities.Gameplay);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(registry);

            DynValue result = env.RunChunk(script,
                "return type(input_key) == 'function' and type(input_axis) == 'function'");
            Assert.IsTrue(result.Boolean);
        }
    }
}
