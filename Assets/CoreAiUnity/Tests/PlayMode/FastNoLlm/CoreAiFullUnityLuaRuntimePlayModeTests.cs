#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA && !UNITY_WEBGL
using System.Collections;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public sealed class CoreAiFullUnityLuaRuntimePlayModeTests
    {
        [UnityTest]
        public IEnumerator Full_unity_find_AndSetPosition_MovesLiveSceneObject()
        {
            yield return null;

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "FullLuaPlayModeCube";
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                cube.transform.position = Vector3.zero;

                env.RunChunk(script, @"
local id = unity_find('FullLuaPlayModeCube')
assert(id ~= 0, 'find failed')
unity_set_position(id, 4, 1, -2)
");

                Vector3 p = cube.transform.position;
                Assert.AreEqual(4f, p.x, 0.01f);
                Assert.AreEqual(1f, p.y, 0.01f);
                Assert.AreEqual(-2f, p.z, 0.01f);
            }
            finally
            {
                Object.Destroy(cube);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator FullBindings_unity_find_AndSetPosition_WorksOnSceneObject_PlayMode()
        {
            yield return null;

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "FullLuaPlayModeMoveCube";
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                cube.transform.position = new Vector3(1f, 2f, 3f);

                env.RunChunk(script, @"
local id = unity_find('FullLuaPlayModeMoveCube')
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
                Object.Destroy(cube);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Full_GameObjectDiscoveryTransformAndHierarchy_WorkInLiveScene()
        {
            yield return null;

            GameObject platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
            platform.name = "FullLuaPlayModePlatform";
            GameObject lightGo = new("FullLuaPlayModeSun");
            lightGo.AddComponent<Light>();
            GameObject parent = new("FullLuaPlayModeParent");

            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);

                MoonSharp.Interpreter.DynValue result = env.RunChunk(script, @"
local platform_matches = unity_find_all('FullLuaPlayModePlatform', 10)
local light_matches = unity_find_by_component('Light', 10)
local sun_id = unity_find('FullLuaPlayModeSun')
local platform_id = platform_matches[1].id
local parent_id = unity_find('FullLuaPlayModeParent')
assert(platform_id ~= 0 and parent_id ~= 0 and sun_id ~= 0 and #light_matches > 0, 'missing objects')
assert(unity_set_position(platform_id, 2, 3, 4), 'move platform')
assert(unity_set_scale(platform_id, 2, 1, 3), 'scale platform')
assert(unity_set_rotation_euler(sun_id, 45, 90, 0), 'rotate light')
assert(unity_parent(platform_id, parent_id, true), 'parent platform')
local desc = unity_describe_object(platform_id)
local children = unity_get_children(parent_id)
return desc.parent == 'FullLuaPlayModeParent'
    and desc.transform.position.y == 3
    and children[1].name == 'FullLuaPlayModePlatform'
");

                Assert.IsTrue(result.Boolean);
                Assert.AreEqual(3f, platform.transform.position.y, 0.01f);
                Assert.AreEqual(90f, lightGo.transform.eulerAngles.y, 0.01f);
                Assert.AreSame(parent.transform, platform.transform.parent);
            }
            finally
            {
                if (parent != null)
                {
                    Object.Destroy(parent);
                }

                if (platform != null)
                {
                    Object.Destroy(platform);
                }

                if (lightGo != null)
                {
                    Object.Destroy(lightGo);
                }
            }

            yield return null;
        }
    }
}
#endif
