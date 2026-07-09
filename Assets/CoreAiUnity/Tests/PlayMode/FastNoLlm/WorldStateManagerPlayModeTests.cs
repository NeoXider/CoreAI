using System.Collections;
using System.IO;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Exercises WorldStateManager save/load/reset, including the optional-colour semantics:
    /// only objects with an explicit MaterialPropertyBlock colour persist it; prefabs and primitives
    /// without one reload with their original material.
    /// </summary>
    public sealed class WorldStateManagerPlayModeTests
    {
        private string _saveFilePath;
        private string _backupPath;

        private sealed class StubPrefabRegistry : ICoreAiPrefabRegistry
        {
            private readonly GameObject _prefab;
            private readonly string _key;

            public StubPrefabRegistry(GameObject prefab, string key)
            {
                _prefab = prefab;
                _key = key;
            }

            public bool TryResolve(string keyOrName, out GameObject prefab)
            {
                if (string.Equals(keyOrName, _key, System.StringComparison.Ordinal))
                {
                    prefab = _prefab;
                    return true;
                }

                prefab = null;
                return false;
            }
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _saveFilePath = Path.Combine(
                Application.persistentDataPath,
                CoreAiPersistentPaths.RootFolderName,
                CoreAiPersistentPaths.WorldState,
                "world_state.json");
            _backupPath = _saveFilePath + ".test_backup";

            // Back up any real save so the test never clobbers a live world.
            if (File.Exists(_saveFilePath))
            {
                File.Copy(_saveFilePath, _backupPath, true);
                File.Delete(_saveFilePath);
            }

            DestroyAllWorldObjects();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            DestroyAllWorldObjects();

            if (File.Exists(_saveFilePath))
            {
                File.Delete(_saveFilePath);
            }

            if (File.Exists(_backupPath))
            {
                File.Copy(_backupPath, _saveFilePath, true);
                File.Delete(_backupPath);
            }

            yield return null;
        }

        private static void DestroyAllWorldObjects()
        {
            WorldObjectComponent[] tags =
                UnityEngine.Object.FindObjectsByType<WorldObjectComponent>(FindObjectsSortMode.None);
            foreach (WorldObjectComponent t in tags)
            {
                if (t != null && t.gameObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(t.gameObject);
                }
            }
        }

        private static WorldObjectComponent Tag(GameObject go, string key)
        {
            WorldObjectComponent tag = go.AddComponent<WorldObjectComponent>();
            tag.persistentId = System.Guid.NewGuid().ToString();
            tag.prefabKey = key;
            return tag;
        }

        private static void SetColor(GameObject go, Color color)
        {
            MaterialPropertyBlock mpb = new();
            Renderer r = go.GetComponent<Renderer>();
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_Color", color);
            r.SetPropertyBlock(mpb);
        }

        [UnityTest]
        public IEnumerator Save_ThenLoad_RestoresTransformsAndExplicitColors()
        {
            // Coloured primitive.
            GameObject cube = CoreAiPrimitiveFactory.Create("cube");
            cube.name = "ColorCube";
            cube.transform.position = new Vector3(1, 2, 3);
            cube.transform.localScale = new Vector3(2, 2, 2);
            Tag(cube, "cube");
            SetColor(cube, Color.red);

            // Plain primitive — no explicit colour, must keep default material on reload.
            GameObject sphere = CoreAiPrimitiveFactory.Create("sphere");
            sphere.name = "PlainSphere";
            sphere.transform.position = new Vector3(-1, 0.5f, 4);
            Tag(sphere, "sphere");

            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance);
            manager.Save();
            Assert.IsTrue(File.Exists(_saveFilePath), "Save should write the world_state.json file.");

            UnityEngine.Object.DestroyImmediate(cube);
            UnityEngine.Object.DestroyImmediate(sphere);
            yield return null;

            Assert.IsTrue(manager.TryLoad(), "TryLoad should succeed.");
            yield return null;

            GameObject loadedCube = GameObject.Find("ColorCube");
            GameObject loadedSphere = GameObject.Find("PlainSphere");
            Assert.IsNotNull(loadedCube);
            Assert.IsNotNull(loadedSphere);

            Assert.AreEqual(new Vector3(1, 2, 3), loadedCube.transform.position);
            Assert.AreEqual(new Vector3(2, 2, 2), loadedCube.transform.localScale);

            MaterialPropertyBlock mpb = new();
            loadedCube.GetComponent<Renderer>().GetPropertyBlock(mpb);
            Assert.IsTrue(mpb.HasColor("_Color"), "Explicit colour should be restored via MPB.");
            Assert.AreEqual(Color.red, mpb.GetColor("_Color"));

            MaterialPropertyBlock sphereMpb = new();
            loadedSphere.GetComponent<Renderer>().GetPropertyBlock(sphereMpb);
            Assert.IsFalse(sphereMpb.HasColor("_Color"),
                "Plain primitive should NOT get an MPB colour override on reload.");
        }

        [UnityTest]
        public IEnumerator Save_ThenLoad_PrefabKeepsOriginalMaterialWithoutColorOverride()
        {
            GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/CoreAI.Demos/Shared/EnemyBasic.prefab");
            Assert.IsNotNull(prefab, "Demo enemy.basic prefab should exist.");

            GameObject enemy = UnityEngine.Object.Instantiate(prefab, new Vector3(5, 0.5f, 0), Quaternion.identity);
            enemy.name = "TestEnemy";
            Tag(enemy, "enemy.basic");
            // Intentionally no SetColor — prefab material must be preserved.

            WorldStateManager manager = new(
                GameLoggerUnscopedFallback.Instance,
                new StubPrefabRegistry(prefab, "enemy.basic"));
            manager.Save();
            UnityEngine.Object.DestroyImmediate(enemy);
            yield return null;

            Assert.IsTrue(manager.TryLoad(), "Prefab should reload from the registry.");
            yield return null;

            GameObject loaded = GameObject.Find("TestEnemy");
            Assert.IsNotNull(loaded, "Prefab instance should be re-created.");
            Assert.AreEqual("enemy.basic", loaded.GetComponent<WorldObjectComponent>().prefabKey);

            MaterialPropertyBlock mpb = new();
            loaded.GetComponent<Renderer>().GetPropertyBlock(mpb);
            Assert.IsFalse(mpb.HasColor("_Color"),
                "Prefab without explicit colour must not receive an MPB override.");
        }

        [UnityTest]
        public IEnumerator Reset_DeletesSaveFileAndDestroysObjects()
        {
            GameObject cube = CoreAiPrimitiveFactory.Create("cube");
            cube.name = "ResetCube";
            Tag(cube, "cube");

            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance);
            manager.Save();
            Assert.IsTrue(File.Exists(_saveFilePath));

            manager.Reset();
            yield return null;
            Assert.IsFalse(File.Exists(_saveFilePath), "Reset must delete the save file.");
            Assert.IsNull(GameObject.Find("ResetCube"), "Reset must destroy tracked objects.");
            Assert.IsFalse(manager.HasSavedState);

            yield return null;
        }
    }
}
