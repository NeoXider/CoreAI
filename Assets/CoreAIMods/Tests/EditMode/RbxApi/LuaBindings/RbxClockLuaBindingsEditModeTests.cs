using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Networking;
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

        /// <summary>A client transport synchronized with a server whose clock agrees with its own.</summary>
        private sealed class SynchronizedClientBridge : INetworkBridge
        {
            public RbxNetworkTopology Topology => RbxNetworkTopology.Client;

            public IReadOnlyList<string> ActorIds => Array.Empty<string>();

            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public event Action<RbxNetworkEventMessage> EventReceived
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkPeerDisconnected> PeerDisconnected
            {
                add { }
                remove { }
            }

            public void RegisterActor(string actorId)
            {
            }

            public void UnregisterActor(string actorId)
            {
            }

            public void SendEvent(RbxNetworkEventMessage message)
            {
            }

            public void SendRequest(RbxNetworkRequestMessage message,
                Action<RbxNetworkResponse> response)
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
        public void Lua_GetServerTimeNow_WithoutANetwork_ReadsAClockThatStandsStillTheSameAsRealTimePasses()
        {
            // WHY: the slew's free-running clock advanced by process time in a solo world too, so a
            // game that froze its injected Unix clock read a later server time on every call.
            FakeClockSource fake = new()
            {
                UnixTimeSecondsFractional = 1700000000.25d,
                ProcessTimeSeconds = 10d
            };
            LuaCsRbxApiBindings bindings = BindingsWith(fake);
            LuaCsModStack stack = BuildStack(bindings);
            stack.Runtime.LoadMod("s1",
                "assert(workspace:GetServerTimeNow() == 1700000000.25, " +
                "'got ' .. string.format('%.6f', workspace:GetServerTimeNow()))");
            Assert.IsTrue(stack.Runtime.IsLoaded("s1"));

            fake.ProcessTimeSeconds = 11d;
            stack.Runtime.LoadMod("s2",
                "assert(workspace:GetServerTimeNow() == 1700000000.25, " +
                "'a clock that stood still for a real second read ' .. " +
                "string.format('%.6f', workspace:GetServerTimeNow()))");
            Assert.IsTrue(stack.Runtime.IsLoaded("s2"));

            fake.ProcessTimeSeconds = 12d;
            Assert.AreEqual(1700000000.25d, bindings.GetServerTimeNow());
        }

        [Test]
        public void Lua_GetServerTimeNow_BackwardStepSlewsInsteadOfFreezing()
        {
            FakeClockSource fake = new()
            {
                UnixTimeSecondsFractional = 1700000000d,
                ProcessTimeSeconds = 100d
            };
            // WHY a client: only a client slews, onto a server clock it measures from afar; where
            // this process is the server clock, a backward step holds the last reading instead.
            LuaCsRbxApiBindings bindings = new(networkBridge: new SynchronizedClientBridge(),
                clockSource: fake);
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
        public void Lua_A3_06_OsTimeTable_BeforeTheEpoch_IsNil_AsInLuau()
        {
            // WHY: Luau's os_timegm fails a date before 1970 and os.time returns nil; a negative
            // number reached scripts that test the result for nil (A3-06).
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);

            stack.Runtime.LoadMod("m", @"
                store_set('dayBefore', tostring(os.time({year = 1969, month = 12, day = 31, hour = 0})))
                store_set('secondBefore', tostring(os.time({year = 1970, month = 1, day = 1, hour = 0, sec = -1})))
                store_set('dayBeforeLateInTheDay', tostring(os.time({year = 1969, month = 12, day = 31, hour = 48})))
                store_set('epoch', string.format('%d', os.time({year = 1970, month = 1, day = 1, hour = 0})))");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
            Assert.AreEqual("nil", store.Get("m", "dayBefore"));
            Assert.AreEqual("nil", store.Get("m", "secondBefore"),
                "a time of day before midnight on 1970-01-01 is before the epoch too");
            Assert.AreEqual("nil", store.Get("m", "dayBeforeLateInTheDay"),
                "Luau fails a day before 1970 whatever hour carries it past midnight");
            Assert.AreEqual("0", store.Get("m", "epoch"));
        }

        [Test]
        public void Lua_A3_06_OsTimeTable_AnOptionalFieldThatIsNotANumber_CountsAsMissing()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);

            stack.Runtime.LoadMod("m", @"
                store_set('hour', string.format('%d', os.time({year = 2024, month = 1, day = 1, hour = true})))
                store_set('min', string.format('%d', os.time({year = 2024, month = 1, day = 1, hour = 0, min = {}})))
                local ok, err = pcall(os.time, {year = 2024, month = true, day = 1})
                store_set('requiredOk', tostring(ok))
                store_set('requiredErr', tostring(err))");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
            Assert.AreEqual("1704110400", store.Get("m", "hour"), "hour = true reads as the default noon");
            Assert.AreEqual("1704067200", store.Get("m", "min"), "min = {} reads as the default zero");
            Assert.AreEqual("false", store.Get("m", "requiredOk"),
                "a required field that is not a number is missing, and missing is an error");
            StringAssert.Contains("field 'month' missing in date table", store.Get("m", "requiredErr"));
        }

        [Test]
        public void Lua_A3_06_TheOsTableHasNoDate_SoNoDocumentPromisesARoundTripThroughIt()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", "assert(os.date == nil, 'os.date is not part of the sandbox os table')");
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
    /// <summary>
    /// The camera_* convenience globals in a world whose Camera is gone.
    /// </summary>
    /// <remarks>
    /// WHY in this file: it runs in the portable Lua-tier suite, and the camera fixture proper builds
    /// a scene camera and runs in the editor only.
    /// </remarks>
    [TestFixture]
    public sealed class RbxCameraGlobalsWithoutACameraEditModeTests
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

        [Test]
        public void Lua_A3_10_CameraGlobals_WithNoCamera_RefuseBeforeMovingAnything()
        {
            // WHY: with the world's Camera destroyed, camera_set_cframe moved the rig first and then
            // failed recording the mutation of a null instance, so the script saw an internal error
            // for a write that had half happened (A3-10).
            LuaCsRbxApiBindings bindings = new();
            ScriptStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = bindings
            });
            RbxCFrame before = bindings.CameraRig.GetCFrame();
            bindings.Game.FindFirstChildOfClass("Workspace").FindFirstChildOfClass("Camera").Destroy();

            stack.Runtime.LoadMod("m", @"
                local ok, err = pcall(camera_set_cframe, CFrame.new(10, 20, 30))
                store_set('setOk', tostring(ok))
                store_set('setErr', tostring(err))
                local okFollow, errFollow = pcall(camera_follow, nil)
                store_set('followOk', tostring(okFollow))
                store_set('followErr', tostring(errFollow))");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
            Assert.AreEqual("false", store.Get("m", "setOk"));
            StringAssert.Contains("BAD_ARGUMENT", store.Get("m", "setErr"));
            StringAssert.Contains("no Camera", store.Get("m", "setErr"));
            Assert.AreEqual(before, bindings.CameraRig.GetCFrame(), "a refused write moves nothing");
            Assert.AreEqual("false", store.Get("m", "followOk"));
            StringAssert.Contains("no Camera", store.Get("m", "followErr"));
        }

        private sealed class ScriptStore : ILuaModStore
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

            public string Get(string modId, string key)
            {
                return _values.TryGetValue(modId + "\n" + key, out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove(modId + "\n" + key);
                    return;
                }

                _values[modId + "\n" + key] = value;
            }

            public void Clear(string modId)
            {
            }
        }
    }
}
