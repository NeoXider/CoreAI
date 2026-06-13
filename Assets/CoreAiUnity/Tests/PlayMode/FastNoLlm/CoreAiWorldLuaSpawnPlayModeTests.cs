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
coreai_world_spawn('enemy.basic', 'LuaSpawnedEnemySmoke', 12, 1, 12)
coreai_world_rotate('LuaSpawnedEnemySmoke', 0, 90, 0)
coreai_world_set_transform('LuaSpawnedEnemySmoke', 13, 1, 12, 0, 180, 0, 1.5)
coreai_world_parent('LuaSpawnedEnemySmoke', 'LuaWorldParentSmoke')
");
            Assert.AreEqual(4, sink.Items.Count);

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
                env.RunChunk(script, "coreai_world_parent('LuaSpawnedEnemySmoke', 'none') coreai_world_destroy('LuaSpawnedEnemySmoke')");
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
