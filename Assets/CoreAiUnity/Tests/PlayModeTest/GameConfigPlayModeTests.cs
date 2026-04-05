using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Config;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Tests.PlayModeTest
{
    /// <summary>
    /// PlayMode тест: AI (Creator) читает конфиг, меняет его, и изменения сохраняются.
    /// Использует StubLlmClient который возвращает предсказуемый JSON ответ.
    /// </summary>
    [TestFixture]
    public class GameConfigPlayModeTests
    {
        private IObjectResolver _container;
        private TestConfigStore _testStore;
        private GameConfigPolicy _policy;

        [SetUp]
        public void SetUp()
        {
            _testStore = new TestConfigStore();
            _policy = new GameConfigPolicy();

            // Начальный конфиг сессии
            _testStore.TrySave("session", "{\"difficulty\":1,\"enemy_hp_mult\":1.0,\"max_enemies\":50}");

            // Creator имеет полный доступ
            _policy.GrantFullAccess("Creator");
            _policy.SetKnownKeys(new[] { "session" });
        }

        [TearDown]
        public void TearDown()
        {
            _container?.Dispose();
        }

        /// <summary>
        /// Тест: AI читает конфиг и возвращает изменённый JSON.
        /// Имитирует полный цикл: read → AI modifies → update.
        /// </summary>
        [UnityTest]
        public IEnumerator GameConfig_AI_ReadModifyWrite_FullCycle()
        {
            yield return null; // Даём Unity инициализироваться

            // Arrange
            GameConfigTool tool = new(_testStore, _policy, "Creator");

            // Act 1: AI читает текущий конфиг
            GameConfigTool.GameConfigResult readResult = tool.ExecuteAsync("read").Result;
            Assert.IsTrue(readResult.Success, $"Read failed: {readResult.Error}");
            Assert.IsTrue(readResult.ConfigJson.Contains("difficulty"));
            Assert.IsTrue(readResult.ConfigJson.Contains("1.0"), "Initial enemy_hp_mult should be 1.0");

            // Act 2: AI возвращает изменённый конфиг (имитация ответа LLM)
            string modifiedConfig = "{\"difficulty\":2,\"enemy_hp_mult\":1.5,\"max_enemies\":80}";
            GameConfigTool.GameConfigResult writeResult = tool.ExecuteAsync("update", modifiedConfig).Result;
            Assert.IsTrue(writeResult.Success, $"Update failed: {writeResult.Error}");

            // Assert: Проверяем что конфиг обновился
            _testStore.TryLoad("session", out string finalJson);
            Assert.IsNotNull(finalJson);
            Assert.IsTrue(finalJson.Contains("2"), "Difficulty should be 2");
            Assert.IsTrue(finalJson.Contains("1.5"), "enemy_hp_mult should be 1.5");
            Assert.IsTrue(finalJson.Contains("80"), "max_enemies should be 80");

            Debug.Log($"[GameConfigTest] AI successfully updated config: {finalJson}");
        }

        /// <summary>
        /// Тест: AI без доступа к конфигам получает ошибку.
        /// </summary>
        [UnityTest]
        public IEnumerator GameConfig_NoAccess_ReturnsError()
        {
            yield return null;

            GameConfigPolicy restrictedPolicy = new();
            restrictedPolicy.RevokeAccess("AINpc");

            GameConfigTool tool = new(_testStore, restrictedPolicy, "AINpc");
            GameConfigTool.GameConfigResult result = tool.ExecuteAsync("read").Result;

            Assert.IsFalse(result.Success);
            StringAssert.Contains("no allowed config", result.Error);

            Debug.Log("[GameConfigTest] Restricted role correctly denied access");
        }

        /// <summary>
        /// Тест: Multiple keys — AI читает несколько конфигов.
        /// </summary>
        [UnityTest]
        public IEnumerator GameConfig_MultipleKeys_ReadAll()
        {
            yield return null;

            _testStore.TrySave("crafting", "{\"max_ingredients\":6,\"quality_min\":0,\"quality_max\":100}");
            _policy.GrantFullAccess("Creator");
            _policy.SetKnownKeys(new[] { "session", "crafting" });

            GameConfigTool tool = new(_testStore, _policy, "Creator");
            GameConfigTool.GameConfigResult result = tool.ExecuteAsync("read").Result;

            Assert.IsTrue(result.Success, $"Read failed: {result.Error}");
            Assert.IsTrue(result.ConfigJson.Contains("session"));
            Assert.IsTrue(result.ConfigJson.Contains("crafting"));
            Assert.IsTrue(result.ConfigJson.Contains("difficulty"));
            Assert.IsTrue(result.ConfigJson.Contains("max_ingredients"));

            Debug.Log($"[GameConfigTest] Multi-key read: {result.ConfigJson}");
        }

        #region Test Config Store

        private sealed class TestConfigStore : IGameConfigStore
        {
            private readonly Dictionary<string, string> _configs = new();

            public bool TryLoad(string key, out string json)
            {
                return _configs.TryGetValue(key, out json);
            }

            public bool TrySave(string key, string json)
            {
                _configs[key] = json;
                return true;
            }

            public string[] GetKnownKeys()
            {
                return new List<string>(_configs.Keys).ToArray();
            }
        }

        #endregion
    }
}