#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA && UNITY_EDITOR
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
        public IEnumerator LuaWorldEdit_WithDemoEnemyPrefab_AppliesSpawnTransformParentAndDestroy()
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

            env.RunChunk(script, @"
coreai_world_spawn({ prefab = 'enemy.basic', name = 'LuaSpawnedEnemySmoke', x = 12, y = 1, z = 12, ry = 90, scale = 1.25, parent = 'LuaWorldParentSmoke' })
coreai_world_change('LuaSpawnedEnemySmoke', { x = 13, ry = 180, scale = 1.5 })
");
            Assert.AreEqual(2, sink.Items.Count);

            CoreAiWorldCommandExecutor executor =
                new(GameLoggerUnscopedFallback.Instance, registry);
            GameObject parent = new("LuaWorldParentSmoke");
            GameObject spawned = null;
            try
            {
                for (int i = 0; i < sink.Items.Count; i++)
                {
                    Assert.IsTrue(executor.TryExecute(sink.Items[i]), $"world command {i} should execute");
                }

                yield return null;

                spawned = GameObject.Find("LuaSpawnedEnemySmoke");
                Assert.IsNotNull(spawned);
                Assert.IsNotNull(spawned.GetComponent<Renderer>());
                Assert.AreEqual(13f, spawned.transform.position.x, 0.01f);
                Assert.AreEqual(1f, spawned.transform.position.y, 0.01f);
                Assert.AreEqual(12f, spawned.transform.position.z, 0.01f);
                Assert.AreEqual(180f, spawned.transform.eulerAngles.y, 0.01f);
                Assert.AreEqual(1.5f, spawned.transform.localScale.x, 0.01f);
                Assert.AreSame(parent.transform, spawned.transform.parent);

                sink.Items.Clear();
                env.RunChunk(script, "coreai_world_change('LuaSpawnedEnemySmoke', { parent = 'none' }) coreai_world_destroy('LuaSpawnedEnemySmoke')");
                Assert.AreEqual(2, sink.Items.Count);
                Assert.IsTrue(executor.TryExecute(sink.Items[0]));
                Assert.IsNull(spawned.transform.parent);
                Assert.IsTrue(executor.TryExecute(sink.Items[1]));
                yield return null;
                Assert.IsNull(GameObject.Find("LuaSpawnedEnemySmoke"));
            }
            finally
            {
                if (spawned != null)
                {
                    Object.Destroy(spawned);
                }

                Object.Destroy(parent);
            }
        }
    }
}
#endif
