using System.Collections;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public sealed class LuaCoroutineRunnerPlayModeTests
    {
        private GameObject _runnerObj;
        private LuaCoroutineRunner _runner;

        [SetUp]
        public void Setup()
        {
            _runnerObj = new GameObject("LuaCoroutineRunnerTest");
            _runner = _runnerObj.AddComponent<LuaCoroutineRunner>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_runnerObj != null)
            {
                Object.Destroy(_runnerObj);
            }
        }

        [UnityTest]
        public IEnumerator CoroutineRunner_TicksCoroutine_WithTimeBindings()
        {
            // Убедимся, что timeScale = 1 перед началом
            Time.timeScale = 1f;

            SecureLuaEnvironment env = new();
            LuaApiRegistry reg = new();

            // Регистрируем Time Bindings
            LuaTimeBindings timeBindings = new();
            timeBindings.RegisterTimeApis(reg);

            int iterations = 0;
            reg.Register("mark_iteration", new System.Action(() => iterations++));

            // Простой скрипт: ждать пока time_now() не увеличится
            // Это симулирует ожидание в игре (например, wait_seconds)
            string luaCode = @"
                local start_time = time_now()
                while time_now() - start_time < 0.1 do
                    mark_iteration()
                    coroutine.yield()
                end
            ";

            LuaCoroutineHandle handle = env.CreateCoroutine(reg, luaCode, 5000);

            // Регистрируем корутину в раннер
            _runner.Register(handle);

            Assert.IsTrue(handle.IsAlive);
            Assert.AreEqual(1, _runner.ActiveCount);

            // Даем Unity поработать ~0.15 секунд
            yield return new WaitForSeconds(0.15f);

            // Корутина должна была завершиться самостоятельно, когда time_now() вырос
            Assert.IsFalse(handle.IsAlive);
            Assert.AreEqual(0, _runner.ActiveCount, "Runner should auto-remove dead coroutines");
            Assert.Greater(iterations, 0, "Coroutine should have been ticked multiple times");
        }

        [UnityTest]
        public IEnumerator CoroutineRunner_RespectsTimeScale()
        {
            SecureLuaEnvironment env = new();
            LuaApiRegistry reg = new();

            LuaTimeBindings timeBindings = new();
            timeBindings.RegisterTimeApis(reg);

            bool reachedEnd = false;
            reg.Register("mark_done", new System.Action(() => reachedEnd = true));

            // Мы ставим паузу через скрипт
            string luaCode = @"
                time_set_scale(0.0)
                coroutine.yield()
                local unscaled_delta = time_unscaled_delta()
                coroutine.yield(unscaled_delta)
                mark_done()
            ";

            LuaCoroutineHandle handle = env.CreateCoroutine(reg, luaCode, 5000);
            _runner.Register(handle);

            // Первый кадр: скрипт ставит TimeScale = 0 и yield
            yield return null;

            Assert.AreEqual(0f, Time.timeScale, "Lua script should have set timeScale to 0");

            // Второй кадр: скрипт получает unscaled_delta и yield с ним
            yield return null;

            Assert.IsTrue(handle.LastResult.Number > 0,
                "Unscaled delta should be greater than 0 even when timeScale is 0");

            // Третий кадр: завершается
            yield return null;

            Assert.IsTrue(reachedEnd);
            Assert.IsFalse(handle.IsAlive);

            // Сбрасываем TimeScale для других тестов
            Time.timeScale = 1f;
        }
    }
}