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
            // ,  timeScale = 1  
            Time.timeScale = 1f;

            SecureLuaEnvironment env = new();
            LuaApiRegistry reg = new();

            //  Time Bindings
            LuaTimeBindings timeBindings = new();
            timeBindings.RegisterTimeApis(reg);

            int iterations = 0;
            reg.Register("mark_iteration", new System.Action(() => iterations++));

            //  :   time_now()  
            //      (, wait_seconds)
            string luaCode = @"
                local start_time = time_now()
                while time_now() - start_time < 0.1 do
                    mark_iteration()
                    coroutine.yield()
                end
            ";

            LuaCoroutineHandle handle = env.CreateCoroutine(reg, luaCode, 5000);

            //    
            _runner.Register(handle);

            Assert.IsTrue(handle.IsAlive);
            Assert.AreEqual(1, _runner.ActiveCount);

            //  Unity  ~0.15 
            yield return new WaitForSeconds(0.15f);

            //     ,  time_now() 
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

            //     
            string luaCode = @"
                time_set_scale(0.0)
                coroutine.yield()
                local unscaled_delta = time_unscaled_delta()
                coroutine.yield(unscaled_delta)
                mark_done()
            ";

            LuaCoroutineHandle handle = env.CreateCoroutine(reg, luaCode, 5000);
            _runner.Register(handle);

            //  :   TimeScale = 0  yield
            yield return null;

            Assert.AreEqual(0f, Time.timeScale, "Lua script should have set timeScale to 0");

            //  :   unscaled_delta  yield  
            yield return null;

            Assert.IsTrue(handle.LastResult.Number > 0,
                "Unscaled delta should be greater than 0 even when timeScale is 0");

            //  : 
            yield return null;

            Assert.IsTrue(reachedEnd);
            Assert.IsFalse(handle.IsAlive);

            //  TimeScale   
            Time.timeScale = 1f;
        }
    }
}
