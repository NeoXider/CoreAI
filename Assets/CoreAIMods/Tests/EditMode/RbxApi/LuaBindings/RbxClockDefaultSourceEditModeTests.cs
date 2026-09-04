using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// MVP2 clock model (roadmap §5.2.6) against the PRODUCTION default source: the fake-driven
    /// fixture next door proves exact semantics and monotonic smoothing, while these tests prove
    /// the default wiring actually reads the machine's clocks (integer epoch near host UTC,
    /// non-negative process time, epoch-shared tick, server time comparable with os.time()).
    /// </summary>
    [TestFixture]
    public sealed class RbxClockDefaultSourceEditModeTests
    {
        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> keys = new();
                foreach ((string storedModId, string key) in _values.Keys)
                {
                    if (storedModId == modId)
                    {
                        keys.Add((storedModId, key));
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class FakeGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }
        }

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox,
            MemoryStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox
            });
        }

        private static double ParseStoredNumber(MemoryStore store, string modId, string key)
        {
            string raw = store.Get(modId, key);
            Assert.IsTrue(double.TryParse(raw,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value),
                "store[" + modId + "][" + key + "] must be a parseable number, got '" + raw + "'.");
            return value;
        }

        [Test]
        public void DefaultSource_OsTime_IntegerNearHostUtc()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);
            stack.Runtime.LoadMod("m",
                "store_set('n', string.format('%.0f', os.time()))\n" +
                "store_set('int', tostring(os.time() % 1 == 0))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));

            double reported = ParseStoredNumber(store, "m", "n");
            double host = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Assert.LessOrEqual(Math.Abs(host - reported), 5d,
                "os.time() must be within a few seconds of the host UTC clock.");
            Assert.AreEqual("true", store.Get("m", "int"),
                "os.time() must be an integer number of seconds (Roblox scripts use % arithmetic).");
        }

        [Test]
        public void DefaultSource_OsClock_IsNonNegativeAndNonDecreasing()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);
            stack.Runtime.LoadMod("m",
                "local a = os.clock()\n" +
                "local b = os.clock()\n" +
                "store_set('ok', tostring(a >= 0 and b >= a))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));

            Assert.AreEqual("true", store.Get("m", "ok"),
                "os.clock() must be non-negative and must not go backwards across two calls.");
        }

        [Test]
        public void DefaultSource_Tick_And_OsTime_ShareHostEpoch()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);
            stack.Runtime.LoadMod("m",
                "local t1 = tick()\n" +
                "local t2 = tick()\n" +
                "local t3 = tick()\n" +
                "local n = os.time()\n" +
                "local function hasFrac(x) local f = x % 1 return f > 0 and f < 1 end\n" +
                "store_set('frac', tostring(hasFrac(t1) or hasFrac(t2) or hasFrac(t3)))\n" +
                "store_set('t', string.format('%.6f', t1))\n" +
                "store_set('n', string.format('%.0f', n))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));

            Assert.AreEqual("true", store.Get("m", "frac"),
                "tick() must carry a fractional part where os.time() does not.");
            double tick = ParseStoredNumber(store, "m", "t");
            double now = ParseStoredNumber(store, "m", "n");
            Assert.LessOrEqual(Math.Abs(tick - now), 2d,
                "tick() and os.time() must sit near the same epoch second.");
        }

        [Test]
        public void DefaultSource_GetServerTimeNow_IsEpochComparableWithOsTime()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);
            stack.Runtime.LoadMod("m",
                "store_set('s', string.format('%.6f', workspace:GetServerTimeNow()))\n" +
                "store_set('n', string.format('%.0f', os.time()))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));

            double server = ParseStoredNumber(store, "m", "s");
            double now = ParseStoredNumber(store, "m", "n");
            Assert.LessOrEqual(Math.Abs(server - now), 2d,
                "GetServerTimeNow() must be epoch-comparable with os.time().");
        }
    }
}
