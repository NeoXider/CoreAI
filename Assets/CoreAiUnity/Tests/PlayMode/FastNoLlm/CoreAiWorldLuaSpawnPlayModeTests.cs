#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA && !UNITY_WEBGL && UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public sealed class CoreAiWorldLuaSpawnPlayModeTests
    {
        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        [UnityTest]
        public IEnumerator LuaWorldSpawn_WithDemoEnemyPrefab_CreatesVisibleObject()
        {
            yield return null;

            CoreAiPrefabRegistryAsset registry =
                AssetDatabase.LoadAssetAtPath<CoreAiPrefabRegistryAsset>(
                    "Assets/CoreAiUnity/Settings/CoreAiPrefabRegistry.asset");
            Assert.IsNotNull(registry);
            Assert.IsTrue(registry.TryResolve("enemy.basic", out GameObject prefab));
            Assert.IsNotNull(prefab);

            ListSink sink = new();
            LuaApiRegistry luaRegistry = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(luaRegistry);
            SecureLuaEnvironment env = new();
            MoonSharp.Interpreter.Script script = env.CreateScript(luaRegistry);

            env.RunChunk(script, "coreai_world_spawn('enemy.basic', 'LuaSpawnedEnemySmoke', 12, 1, 12)");
            Assert.AreEqual(1, sink.Items.Count);

            CoreAiWorldCommandExecutor executor =
                new(GameLoggerUnscopedFallback.Instance, registry);
            Assert.IsTrue(executor.TryExecute(sink.Items[0]));

            yield return null;

            GameObject spawned = GameObject.Find("LuaSpawnedEnemySmoke");
            try
            {
                Assert.IsNotNull(spawned);
                Assert.IsNotNull(spawned.GetComponent<Renderer>());
                Assert.AreEqual(12f, spawned.transform.position.x, 0.01f);
                Assert.AreEqual(1f, spawned.transform.position.y, 0.01f);
                Assert.AreEqual(12f, spawned.transform.position.z, 0.01f);
            }
            finally
            {
                if (spawned != null)
                {
                    Object.Destroy(spawned);
                }
            }
        }
    }
}
#endif
