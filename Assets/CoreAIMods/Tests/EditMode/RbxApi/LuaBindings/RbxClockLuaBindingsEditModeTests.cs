using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// MVP2 clock model (roadmap §5.2.6) through the REAL mod runtime: every Lua-visible clock
    /// reads through the injectable <see cref="IRbxClockSource"/> port, the sandbox <c>os</c>
    /// table holds ONLY <c>time</c>/<c>clock</c>, and <c>workspace:GetServerTimeNow()</c> never
    /// steps back even when the source does. A fake source drives every exact assertion, so no
    /// test touches the machine's real clock or sleeps.
    /// </summary>
    [TestFixture]
    public sealed class RbxClockLuaBindingsEditModeTests
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

        private sealed class FakeClockSource : IRbxClockSource
        {
            public double GameTimeSeconds { get; set; }

            public long UnixTimeSeconds { get; set; }

            public double ProcessTimeSeconds { get; set; }

            public double UnixTimeSecondsFractional { get; set; }
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
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox,
            MemoryStore store = null, LuaCapabilities caps = LuaCapabilities.All)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store ?? new MemoryStore(),
                Capabilities = caps,
                OneOffCapabilities = caps,
                RbxApi = roblox
            });
        }

        private static LuaCsRbxApiBindings BindingsWith(FakeClockSource fake,
            List<string> log = null)
        {
            return new LuaCsRbxApiBindings(
                clockSource: fake, log: log == null ? null : (Action<string>)log.Add);
        }

        [Test]
        public void Lua_OsTable_HoldsOnlyTimeAndClock_DangerousMembersAreNil()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                assert(type(os) == 'table')
                assert(type(os.time) == 'function')
                assert(type(os.clock) == 'function')
                assert(os.execute == nil)
                assert(os.remove == nil)
                assert(os.rename == nil)
                assert(os.exit == nil)
                assert(os.getenv == nil)
                assert(os.tmpname == nil)
                local count = 0
                for _ in pairs(os) do count = count + 1 end
                assert(count == 2)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_OsTime_IsIntegerMatchingInjectedSourceExactly()
        {
            FakeClockSource fake = new() { UnixTimeSeconds = 1700000000L };
            LuaCsModStack stack = BuildStack(BindingsWith(fake));
            stack.Runtime.LoadMod("m", @"
                assert(os.time() == 1700000000)
                assert(os.time() % 1 == 0)
                assert(os.time() % 86400 == 1700000000 % 86400)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_OsClock_DoesNotGoBackwards()
        {
            FakeClockSource fake = new() { ProcessTimeSeconds = 42.5d };
            LuaCsModStack stack = BuildStack(BindingsWith(fake));
            stack.Runtime.LoadMod("m", @"
                local first = os.clock()
                local second = os.clock()
                assert(type(first) == 'number')
                assert(second >= first)
                assert(first == 42.5)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_Tick_HasFractionWhereOsTimeDoesNot()
        {
            FakeClockSource fake = new()
            {
                UnixTimeSeconds = 1700000000L,
                UnixTimeSecondsFractional = 1700000000.5d
            };
            LuaCsModStack stack = BuildStack(BindingsWith(fake));
            stack.Runtime.LoadMod("m", @"
                assert(tick() == 1700000000.5)
                assert(tick() % 1 > 0)
                assert(os.time() % 1 == 0)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_Tick_LogsDeprecationOncePerMod()
        {
            List<string> messages = new();
            LuaCsModStack stack = BuildStack(BindingsWith(new FakeClockSource(), messages));
            stack.Runtime.LoadMod("m", "tick() tick()");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
            Assert.AreEqual(1, messages.Count(message => message.Contains("tick()")));

            stack.Runtime.LoadMod("other", "tick()");
            Assert.IsTrue(stack.Runtime.IsLoaded("other"));
            Assert.AreEqual(2, messages.Count(message => message.Contains("tick()")));
        }

        [Test]
        public void Lua_Time_AdvancesWithScheduler_AndFreezesAtZeroDelta()
        {
            LuaCsRbxApiBindings bindings = new();
            LuaCsModStack stack = BuildStack(bindings);
            stack.Runtime.LoadMod("t0", "assert(time() == 0)");
            Assert.IsTrue(stack.Runtime.IsLoaded("t0"));

            bindings.Scheduler.Advance(2.5d);
            stack.Runtime.LoadMod("t1", "assert(time() == 2.5)");
            Assert.IsTrue(stack.Runtime.IsLoaded("t1"));

            // WHY: time scale 0 reaches the scheduler as a zero host delta, so advancing by
            // zero must leave time() exactly where it was.
            bindings.Scheduler.Advance(0d);
            bindings.Scheduler.Advance(0d);
            stack.Runtime.LoadMod("t2", "assert(time() == 2.5)");
            Assert.IsTrue(stack.Runtime.IsLoaded("t2"));
        }

        [Test]
        public void Lua_GetServerTimeNow_NeverDecreasesWhenSourceStepsBackwards()
        {
            FakeClockSource fake = new() { UnixTimeSecondsFractional = 1700000000.5d };
            LuaCsModStack stack = BuildStack(BindingsWith(fake));
            stack.Runtime.LoadMod("s1",
                "assert(workspace:GetServerTimeNow() == 1700000000.5)");
            Assert.IsTrue(stack.Runtime.IsLoaded("s1"));

            // WHY: forcing the source backwards is the whole point of the port — NTP/system-clock
            // corrections must surface as a repeated last value, never a rewind.
            fake.UnixTimeSecondsFractional = 1699999999.25d;
            stack.Runtime.LoadMod("s2",
                "assert(workspace:GetServerTimeNow() == 1700000000.5)");
            Assert.IsTrue(stack.Runtime.IsLoaded("s2"));

            fake.UnixTimeSecondsFractional = 1700000001d;
            stack.Runtime.LoadMod("s3",
                "assert(workspace:GetServerTimeNow() == 1700000001)");
            Assert.IsTrue(stack.Runtime.IsLoaded("s3"));
        }

        [Test]
        public void Lua_GetServerTimeNow_BackwardStepSlewsInsteadOfFreezing()
        {
            FakeClockSource fake = new()
            {
                UnixTimeSecondsFractional = 1700000000d,
                ProcessTimeSeconds = 100d
            };
            LuaCsRbxApiBindings bindings = BindingsWith(fake);
            Assert.AreEqual(1700000000d, bindings.GetServerTimeNow());

            // WHY: a 10 s NTP correction used to freeze the clock for all 10 s, stalling every timer
            // built on it. Slewed, it keeps moving at ServerTimeSlewRate below real time until it has
            // caught up with the corrected source.
            fake.UnixTimeSecondsFractional = 1699999991d;
            fake.ProcessTimeSeconds = 101d;
            double previous = bindings.GetServerTimeNow();
            Assert.AreEqual(1700000000d + (1d - LuaCsRbxApiBindings.ServerTimeSlewRate), previous,
                "one real second after the step the clock has moved on, at the slewed rate");

            int secondsToConverge = 1;
            while (previous != fake.UnixTimeSecondsFractional && secondsToConverge < 100)
            {
                fake.UnixTimeSecondsFractional += 1d;
                fake.ProcessTimeSeconds += 1d;
                double next = bindings.GetServerTimeNow();
                Assert.Greater(next, previous, "every reading moves forward while slewing");
                previous = next;
                secondsToConverge++;
            }

            Assert.AreEqual(fake.UnixTimeSecondsFractional, previous, "the clock converges on the source");
            Assert.LessOrEqual(secondsToConverge, 20,
                "a 10 s correction is absorbed within twice its size of real time");
        }

        [Test]
        public void Negative_Lua_GetServerTimeNow_ForwardStepIsTakenAtOnce()
        {
            FakeClockSource fake = new()
            {
                UnixTimeSecondsFractional = 1700000000d,
                ProcessTimeSeconds = 100d
            };
            LuaCsRbxApiBindings bindings = BindingsWith(fake);
            bindings.GetServerTimeNow();

            fake.UnixTimeSecondsFractional = 1700000030d;
            fake.ProcessTimeSeconds = 101d;

            Assert.AreEqual(1700000030d, bindings.GetServerTimeNow(),
                "moving ahead cannot run time backwards, so it needs no slewing");
        }

        [Test]
        public void Lua_OsTimeTable_ReturnsUtcUnixSecondsOfTheDate()
        {
            LuaCsModStack stack = BuildStack(BindingsWith(new FakeClockSource { UnixTimeSeconds = 5L }));

            stack.Runtime.LoadMod("m", @"
                local function expect(fields, seconds, what)
                    local got = os.time(fields)
                    assert(got == seconds, what .. ': expected ' .. seconds .. ', got ' .. tostring(got))
                end
                expect({year = 2024, month = 1, day = 1, hour = 0}, 1704067200, 'midnight UTC')
                expect({year = 1970, month = 1, day = 1}, 43200, 'hour defaults to 12')
                expect({year = 2000, month = 2, day = 29, hour = 23, min = 59, sec = 58, isdst = true},
                    951868798, 'leap day, minutes and seconds; isdst changes nothing under UTC')
                expect({year = 2023, month = 13, day = 1, hour = 0}, 1704067200, 'month 13 carries a year')
                expect({year = 2024, month = 0, day = 1, hour = 0}, 1701388800, 'month 0 is December')
                expect({year = 2024, month = 1, day = 32, hour = 0}, 1706745600, 'day 32 carries a month')
                expect({year = '2024', month = 1, day = 1, hour = 0}, 1704067200, 'numeric strings coerce')
                assert(os.time() == 5, 'no argument still reads the clock source')
                assert(os.time(nil) == 5, 'nil reads the clock source')");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Negative_Lua_OsTimeTable_MissingRequiredFieldOrWrongType_RaisesBadArgument()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());

            stack.Runtime.LoadMod("m", @"
                local ok, err = pcall(os.time, {year = 2024, month = 1})
                assert(not ok, 'a date without a day must not become today')
                assert(string.find(tostring(err), ""field 'day' missing in date table"", 1, true),
                    tostring(err))
                local okType, errType = pcall(os.time, 5)
                assert(not okType and string.find(tostring(errType), 'BAD_ARGUMENT', 1, true),
                    tostring(errType))
                local okField, errField = pcall(os.time, {year = 2024, month = 'June', day = 1})
                assert(not okField and string.find(tostring(errField), ""field 'month'"", 1, true),
                    tostring(errField))
                local okHuge, errHuge = pcall(os.time, {year = 1e300, month = 1, day = 1})
                assert(not okHuge and string.find(tostring(errHuge), 'out of range', 1, true),
                    tostring(errHuge))
                local okNaN, errNaN = pcall(os.time, {year = 2024, month = 1, day = 0/0})
                assert(not okNaN and string.find(tostring(errNaN), ""field 'day' is out of range"", 1, true),
                    tostring(errNaN))");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_CustomClockSource_FullyReplacesDefault()
        {
            FakeClockSource fake = new()
            {
                GameTimeSeconds = 123.25d,
                UnixTimeSeconds = 1711111111L,
                ProcessTimeSeconds = 7.5d,
                UnixTimeSecondsFractional = 1711111111.75d
            };
            LuaCsModStack stack = BuildStack(BindingsWith(fake));
            stack.Runtime.LoadMod("m", @"
                assert(time() == 123.25)
                assert(os.time() == 1711111111)
                assert(os.clock() == 7.5)
                assert(tick() == 1711111111.75)
                assert(workspace:GetServerTimeNow() == 1711111111.75)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }
    }
}
