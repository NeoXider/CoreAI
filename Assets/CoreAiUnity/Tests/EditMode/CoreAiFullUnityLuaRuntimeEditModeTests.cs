#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class CoreAiFullUnityLuaRuntimeEditModeTests
    {
        [Test]
        public void FullBindings_NotRegistered_WhenCapabilityMissing()
        {
            LuaApiRegistry registry = new();
            new AggregatingGameLuaRuntimeBindings(
                    GameLoggerUnscopedFallback.Instance,
                    new CoreAiVersioningLuaRuntimeBindings(null, null),
                    null,
                    full: new CoreAiFullUnityLuaRuntimeBindings(),
                    capabilities: LuaCapabilities.Read)
                .RegisterGameplayApis(registry, LuaCapabilities.All);

            Assert.IsFalse(registry.TryGet("unity_find", out _));
        }

        [Test]
        public void FullBindings_Registered_WhenFullCapabilityGranted()
        {
            LuaApiRegistry registry = new();
            new AggregatingGameLuaRuntimeBindings(
                    GameLoggerUnscopedFallback.Instance,
                    new CoreAiVersioningLuaRuntimeBindings(null, null),
                    null,
                    full: new CoreAiFullUnityLuaRuntimeBindings(),
                    capabilities: LuaCapabilities.All | LuaCapabilities.Full)
                .RegisterGameplayApis(registry, LuaCapabilities.All | LuaCapabilities.Full);

            Assert.IsTrue(registry.TryGet("unity_find", out _));
            Assert.IsTrue(registry.TryGet("unity_set_member", out _));
        }

        [Test]
        public void FullBindings_unity_find_AndSetPosition_WorksOnSceneObject()
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "FullLuaTestCube";
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                cube.transform.position = new Vector3(1f, 2f, 3f);

                env.RunChunk(script, @"
local id = unity_find('FullLuaTestCube')
assert(id ~= 0, 'find failed')
local p = unity_get_position(id)
assert(math.abs(p.x - 1) < 0.01 and math.abs(p.y - 2) < 0.01, 'get position')
unity_set_position(id, 5, 6, 7)
");
                Vector3 pos = cube.transform.position;
                Assert.AreEqual(5f, pos.x, 0.01f);
                Assert.AreEqual(6f, pos.y, 0.01f);
                Assert.AreEqual(7f, pos.z, 0.01f);
            }
            finally
            {
                Object.DestroyImmediate(cube);
            }
        }
    }
}
#endif
