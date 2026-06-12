#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the <see cref="LuaCoroutineRunner.MaxActiveCoroutines"/> registration cap:
    /// up-to-limit succeeds, over-limit is rejected, completed/killed coroutines free their slot.
    /// </summary>
    [TestFixture]
    public sealed class LuaCoroutineRunnerLimitEditModeTests
    {
        private GameObject _runnerObj;
        private LuaCoroutineRunner _runner;
        private SecureLuaEnvironment _env;

        [SetUp]
        public void Setup()
        {
            _runnerObj = new GameObject("LuaCoroutineRunnerLimitTest");
            _runner = _runnerObj.AddComponent<LuaCoroutineRunner>();
            _env = new SecureLuaEnvironment();
        }

        [TearDown]
        public void TearDown()
        {
            if (_runnerObj != null)
            {
                UnityEngine.Object.DestroyImmediate(_runnerObj);
            }
        }

        private LuaCoroutineHandle CreateYieldingCoroutine()
        {
            return _env.CreateCoroutine(new LuaApiRegistry(),
                "while true do coroutine.yield() end");
        }

        [Test]
        public void MaxActiveCoroutines_DefaultIs64()
        {
            Assert.AreEqual(LuaCoroutineRunner.DefaultMaxActiveCoroutines, _runner.MaxActiveCoroutines);
            Assert.AreEqual(64, _runner.MaxActiveCoroutines);
        }

        [Test]
        public void MaxActiveCoroutines_InvalidValue_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _runner.MaxActiveCoroutines = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => _runner.MaxActiveCoroutines = -5);
        }

        [Test]
        public void Register_UpToLimit_Succeeds()
        {
            _runner.MaxActiveCoroutines = 4;

            for (int i = 0; i < 4; i++)
            {
                _runner.Register(CreateYieldingCoroutine());
            }

            Assert.AreEqual(4, _runner.ActiveCount);
        }

        [Test]
        public void Register_ExceedingLimit_RejectedWithExceptionAndLog()
        {
            _runner.MaxActiveCoroutines = 2;
            _runner.Register(CreateYieldingCoroutine());
            _runner.Register(CreateYieldingCoroutine());

            UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex(
                    @".*\[LuaCoroutineRunner\] Coroutine limit reached \(2\); registration rejected\."));
            Assert.Throws<InvalidOperationException>(() => _runner.Register(CreateYieldingCoroutine()));
            Assert.AreEqual(2, _runner.ActiveCount, "Отклонённая регистрация не должна менять ActiveCount");
        }

        [Test]
        public void Register_AfterKillingOne_SlotIsFreed()
        {
            _runner.MaxActiveCoroutines = 2;
            LuaCoroutineHandle first = CreateYieldingCoroutine();
            _runner.Register(first);
            _runner.Register(CreateYieldingCoroutine());

            // Завершаем одну корутину — её слот должен освободиться при следующей регистрации.
            first.Kill();

            Assert.DoesNotThrow(() => _runner.Register(CreateYieldingCoroutine()),
                "Слот завершённой корутины должен освобождаться");
            Assert.AreEqual(2, _runner.ActiveCount);
        }

        [Test]
        public void Register_AfterNaturalCompletion_SlotIsFreed()
        {
            _runner.MaxActiveCoroutines = 2;
            LuaCoroutineHandle finishing = _env.CreateCoroutine(new LuaApiRegistry(), "return 1");
            _runner.Register(finishing);
            _runner.Register(CreateYieldingCoroutine());

            finishing.Resume();
            Assert.IsFalse(finishing.IsAlive);

            Assert.DoesNotThrow(() => _runner.Register(CreateYieldingCoroutine()),
                "Слот естественно завершившейся корутины должен освобождаться");
            Assert.AreEqual(2, _runner.ActiveCount);
        }

        [Test]
        public void Unregister_FreesSlotImmediately()
        {
            _runner.MaxActiveCoroutines = 1;
            LuaCoroutineHandle handle = CreateYieldingCoroutine();
            _runner.Register(handle);

            _runner.Unregister(handle);
            Assert.AreEqual(0, _runner.ActiveCount);

            Assert.DoesNotThrow(() => _runner.Register(CreateYieldingCoroutine()));
        }
    }
}
#endif