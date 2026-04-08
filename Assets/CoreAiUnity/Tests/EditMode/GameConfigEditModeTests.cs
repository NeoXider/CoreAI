using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using CoreAI.Config;
using CoreAI.Ai;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты GameConfigTool и GameConfigPolicy.
    /// Проверяют что инфраструктура работает: чтение/запись JSON конфигов.
    /// </summary>
    [TestFixture]
    public class GameConfigEditModeTests
    {
        private InMemoryConfigStore _store;
        private GameConfigPolicy _policy;

        [SetUp]
        public void SetUp()
        {
            _store = new InMemoryConfigStore();
            _policy = new GameConfigPolicy();
        }

        #region GameConfigPolicy Tests

        [Test]
        public void Policy_ConfigureRole_CanReadWrite()
        {
            _policy.ConfigureRole("Creator", new[] { "session" }, new[] { "session" });
            _policy.SetKnownKeys(new[] { "session", "crafting" });

            Assert.IsTrue(_policy.CanRead("Creator", "session"));
            Assert.IsTrue(_policy.CanWrite("Creator", "session"));
            Assert.IsFalse(_policy.CanRead("Creator", "crafting"));
        }

        [Test]
        public void Policy_GrantFullAccess_CanReadWriteAll()
        {
            _policy.GrantFullAccess("Creator");
            _policy.SetKnownKeys(new[] { "session", "crafting" });

            Assert.IsTrue(_policy.CanRead("Creator", "session"));
            Assert.IsTrue(_policy.CanWrite("Creator", "crafting"));
            CollectionAssert.AreEquivalent(new[] { "session", "crafting" }, _policy.GetAllowedKeys("Creator"));
        }

        [Test]
        public void Policy_RevokeAccess_CannotReadWrite()
        {
            _policy.GrantFullAccess("Creator");
            _policy.RevokeAccess("Creator");

            Assert.IsFalse(_policy.CanRead("Creator", "session"));
            Assert.IsFalse(_policy.CanWrite("Creator", "session"));
            Assert.IsEmpty(_policy.GetAllowedKeys("Creator"));
        }

        #endregion

        #region GameConfigTool Read Tests

        [Test]
        public void ConfigTool_Read_ReturnsConfigJson()
        {
            _store.TrySave("session", "{\"difficulty\":2,\"enemy_hp_mult\":1.5}");
            _policy.ConfigureRole("Creator", new[] { "session" }, new[] { "session" });

            GameConfigTool tool = new(_store, _policy, "Creator");
            GameConfigTool.GameConfigResult result =
                JsonConvert.DeserializeObject<GameConfigTool.GameConfigResult>(tool.ExecuteAsync("read").Result);

            Assert.IsTrue(result.Success);
            StringAssert.Contains("difficulty", result.ConfigJson);
            StringAssert.Contains("2", result.ConfigJson);
            StringAssert.Contains("enemy_hp_mult", result.ConfigJson);
        }

        [Test]
        public void ConfigTool_ReadUnknownRole_ReturnsError()
        {
            _policy.SetKnownKeys(new[] { "session" });
            GameConfigTool tool = new(_store, _policy, "UnknownRole");
            GameConfigTool.GameConfigResult result =
                JsonConvert.DeserializeObject<GameConfigTool.GameConfigResult>(tool.ExecuteAsync("read").Result);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("no allowed config", result.Error);
        }

        #endregion

        #region GameConfigTool Update Tests

        [Test]
        public void ConfigTool_Update_SavesNewJson()
        {
            _store.TrySave("session", "{\"difficulty\":1,\"enemy_hp_mult\":1.0}");
            _policy.ConfigureRole("Creator", new[] { "session" }, new[] { "session" });

            GameConfigTool tool = new(_store, _policy, "Creator");
            string newConfig = "{\"difficulty\":3,\"enemy_hp_mult\":2.5}";
            GameConfigTool.GameConfigResult result =
                JsonConvert.DeserializeObject<GameConfigTool.GameConfigResult>(tool.ExecuteAsync("update", newConfig)
                    .Result);

            Assert.IsTrue(result.Success);
            _store.TryLoad("session", out string savedJson);
            StringAssert.Contains("difficulty", savedJson);
            StringAssert.Contains("3", savedJson);
            StringAssert.Contains("2.5", savedJson);
        }

        [Test]
        public void ConfigTool_UpdateWithoutContent_ReturnsError()
        {
            _policy.GrantFullAccess("Creator");
            GameConfigTool tool = new(_store, _policy, "Creator");
            GameConfigTool.GameConfigResult result =
                JsonConvert.DeserializeObject<GameConfigTool.GameConfigResult>(tool.ExecuteAsync("update").Result);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("Content", result.Error);
        }

        [Test]
        public void ConfigTool_UpdateInvalidJson_ReturnsError()
        {
            _policy.GrantFullAccess("Creator");
            GameConfigTool tool = new(_store, _policy, "Creator");
            GameConfigTool.GameConfigResult result =
                JsonConvert.DeserializeObject<GameConfigTool.GameConfigResult>(tool.ExecuteAsync("update", "not json")
                    .Result);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("JSON", result.Error);
        }

        [Test]
        public void ConfigTool_UnknownAction_ReturnsError()
        {
            GameConfigTool tool = new(_store, _policy, "Creator");
            GameConfigTool.GameConfigResult result =
                JsonConvert.DeserializeObject<GameConfigTool.GameConfigResult>(tool.ExecuteAsync("delete").Result);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("Unknown action", result.Error);
        }

        #endregion

        #region Integration: Read → Modify → Write

        [Test]
        public void ConfigTool_ReadModifyWrite_RoundTrip()
        {
            // Начальный конфиг
            _store.TrySave("session", "{\"difficulty\":1,\"enemy_hp_mult\":1.0,\"max_enemies\":50}");
            _policy.GrantFullAccess("Creator");
            _policy.SetKnownKeys(new[] { "session" });

            GameConfigTool tool = new(_store, _policy, "Creator");

            // Шаг 1: Читаем
            GameConfigTool.GameConfigResult readResult =
                JsonConvert.DeserializeObject<GameConfigTool.GameConfigResult>(tool.ExecuteAsync("read").Result);
            Assert.IsTrue(readResult.Success);
            StringAssert.Contains("difficulty", readResult.ConfigJson);

            // Шаг 2: Модифицируем (имитация что AI изменил JSON)
            string modifiedJson = "{\"difficulty\":2,\"enemy_hp_mult\":1.5,\"max_enemies\":80}";

            // Шаг 3: Сохраняем
            GameConfigTool.GameConfigResult writeResult =
                JsonConvert.DeserializeObject<GameConfigTool.GameConfigResult>(tool.ExecuteAsync("update", modifiedJson)
                    .Result);
            Assert.IsTrue(writeResult.Success);

            // Шаг 4: Проверяем что сохранилось
            _store.TryLoad("session", out string finalJson);
            StringAssert.Contains("difficulty", finalJson);
            StringAssert.Contains("2", finalJson);
            StringAssert.Contains("1.5", finalJson);
            StringAssert.Contains("80", finalJson);
        }

        #endregion

        #region InMemory Config Store (для тестов)

        private sealed class InMemoryConfigStore : IGameConfigStore
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
                return _configs.Keys.ToArray();
            }
        }

        #endregion
    }
}