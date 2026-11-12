#if COREAI_LUA
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Scripting;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Sandbox
{
    /// <summary>
    /// The stock-take of MVP13 (RT5) pinned: every function and value of Luau's <c>table</c>, <c>string</c>,
    /// <c>math</c>, <c>utf8</c>, <c>bit32</c>, <c>os</c>, <c>coroutine</c>, <c>task</c>, <c>debug</c>,
    /// <c>buffer</c> and <c>vector</c> libraries and the Lua and Roblox globals the mirror lists
    /// (<c>reference/engine/libraries</c>, <c>reference/engine/globals</c>) is checked in the environment a mod
    /// really gets, against its recorded decision: real, a loud stub, refused with an error, or absent for a
    /// written reason. A member that appears or disappears fails here with its name.
    /// </summary>
    [TestFixture]
    public sealed class LuauLibraryInventoryEditModeTests
    {
        /// <summary>Real members: a function (or a value of the given type) that works.</summary>
        private static readonly string[] Real =
        {
            "table.clear", "table.clone", "table.concat", "table.create", "table.find", "table.foreach",
            "table.foreachi", "table.freeze", "table.getn", "table.insert", "table.isfrozen", "table.maxn",
            "table.move", "table.pack", "table.remove", "table.sort", "table.unpack",
            "string.byte", "string.char", "string.find", "string.format", "string.gmatch", "string.gsub",
            "string.len", "string.lower", "string.match", "string.rep", "string.reverse", "string.split",
            "string.sub", "string.upper",
            "math.abs", "math.acos", "math.asin", "math.atan", "math.atan2", "math.ceil", "math.clamp", "math.cos",
            "math.cosh", "math.deg", "math.exp", "math.floor", "math.fmod", "math.frexp", "math.isfinite",
            "math.isinf", "math.isnan", "math.ldexp", "math.lerp", "math.log", "math.log10", "math.map", "math.max",
            "math.min", "math.modf", "math.noise", "math.pow", "math.rad", "math.random", "math.randomseed",
            "math.round", "math.sign", "math.sin", "math.sinh", "math.sqrt", "math.tan", "math.tanh",
            "utf8.char", "utf8.codes", "utf8.codepoint", "utf8.len", "utf8.offset", "utf8.graphemes",
            "utf8.nfcnormalize", "utf8.nfdnormalize",
            "bit32.arshift", "bit32.band", "bit32.bnot", "bit32.bor", "bit32.btest", "bit32.bxor", "bit32.byteswap",
            "bit32.countlz", "bit32.countrz", "bit32.extract", "bit32.replace", "bit32.lrotate", "bit32.lshift",
            "bit32.rrotate", "bit32.rshift",
            "os.clock", "os.date", "os.difftime", "os.time",
            "coroutine.close", "coroutine.create", "coroutine.isyieldable", "coroutine.resume", "coroutine.running",
            "coroutine.status", "coroutine.wrap", "coroutine.yield",
            "task.spawn", "task.defer", "task.delay", "task.desynchronize", "task.synchronize", "task.wait",
            "task.cancel",
            "debug.traceback", "debug.info", "debug.profilebegin", "debug.profileend", "debug.getmemorycategory",
            "debug.setmemorycategory", "debug.resetmemorycategory",
            "assert", "error", "gcinfo", "getfenv", "getmetatable", "ipairs", "newproxy", "next", "pairs", "pcall",
            "print", "rawequal", "rawget", "rawlen", "rawset", "select", "setmetatable", "tonumber", "tostring",
            "type", "unpack", "xpcall", "collectgarbage", "loadstring", "require",
            "delay", "spawn", "tick", "time", "typeof", "wait", "warn", "ypcall"
        };

        /// <summary>Real values that are not functions, with the type <c>type</c> gives them.</summary>
        private static readonly Dictionary<string, string> RealValues = new()
        {
            { "math.huge", "number" }, { "math.pi", "number" }, { "math.e", "number" }, { "math.nan", "number" },
            { "math.phi", "number" }, { "math.sqrt2", "number" }, { "math.tau", "number" },
            { "utf8.charpattern", "string" }, { "_G", "table" }, { "_VERSION", "string" }, { "Enum", "table" },
            { "game", "userdata" }, { "workspace", "userdata" }, { "script", "userdata" }
        };

        /// <summary>Loud stubs: every use raises NOT_IMPLEMENTED naming the rung, or the deliberate refusal.</summary>
        private static readonly string[] Stubbed =
        {
            "string.pack", "string.packsize", "string.unpack", "buffer.create", "buffer.fromstring", "buffer.len",
            "buffer.readu8", "buffer.writeu8", "buffer.copy", "vector.create", "vector.magnitude",
            "debug.dumpcodesize", "setfenv"
        };

        /// <summary>
        /// Absent on purpose, each with its reason: <c>plugin</c> (nil in a Roblox game too), and the Studio and host
        /// globals no game script uses. <c>require</c> is real since MVP4 step C4.
        /// </summary>
        private static readonly string[] Absent =
        {
            "plugin", "settings", "UserSettings", "stats", "version", "printidentity", "elapsedTime",
            "DebuggerManager", "PluginManager", "load", "dofile", "loadfile", "io", "package", "string.dump"
        };

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
            }
        }

        private sealed class SilentLogger : IGameLogger
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

        /// <summary>Runs <paramref name="body"/> as a mod with every capability and returns what it stored.</summary>
        private static string RunAsMod(string body)
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = new LuaCsRbxApiBindings()
            });
            stack.Runtime.LoadMod("inventory", body, persistToStore: false);
            return store.Get("inventory", "result");
        }

        private static string LuaList(IEnumerable<string> names)
        {
            List<string> quoted = new();
            foreach (string name in names)
            {
                quoted.Add("'" + name + "'");
            }

            return "{" + string.Join(", ", quoted) + "}";
        }

        private const string Resolve = @"
            local function resolve(path)
              local value = _G
              for part in string.gmatch(path, '[^%.]+') do
                local ok, inner = pcall(function() return value[part] end)
                if not ok then return 'raises', inner end
                value = inner
                if value == nil then return nil end
              end
              return value
            end";

        [Test]
        public void EveryRealMember_IsThereWithItsType()
        {
            string result = RunAsMod(Resolve + @"
                local wrong = {}
                for _, name in ipairs(" + LuaList(Real) + @") do
                  local v = resolve(name)
                  if type(v) ~= 'function' then wrong[#wrong + 1] = name .. '=' .. type(v) end
                end
                store_set('result', table.concat(wrong, ' '))");
            Assert.AreEqual("", result, "members that should be real functions");

            List<string> checks = new();
            foreach (KeyValuePair<string, string> value in RealValues)
            {
                checks.Add("{'" + value.Key + "', '" + value.Value + "'}");
            }

            string values = RunAsMod(Resolve + @"
                local wrong = {}
                for _, pair in ipairs({" + string.Join(", ", checks) + @"}) do
                  local v = resolve(pair[1])
                  if type(v) ~= pair[2] then wrong[#wrong + 1] = pair[1] .. '=' .. type(v) end
                end
                store_set('result', table.concat(wrong, ' '))");
            Assert.AreEqual("", values, "values that should exist with their type");
        }

        [Test]
        public void EveryStubbedMember_RaisesNotImplemented_InsteadOfIndexingNil()
        {
            string result = RunAsMod(Resolve + @"
                local wrong = {}
                for _, name in ipairs(" + LuaList(Stubbed) + @") do
                  local status, payload = resolve(name)
                  local message
                  if status == 'raises' then
                    message = tostring(payload)
                  elseif type(status) == 'function' then
                    local ok, err = pcall(status)
                    message = ok and 'returned' or tostring(err)
                  else
                    message = type(status)
                  end
                  if not string.find(message, 'NOT_IMPLEMENTED', 1, true) then wrong[#wrong + 1] = name .. ': ' .. message end
                end
                store_set('result', table.concat(wrong, ' / '))");
            Assert.AreEqual("", result, "members that should be loud stubs");
        }

        [Test]
        public void EveryAbsentMember_IsNil()
        {
            string result = RunAsMod(Resolve + @"
                local wrong = {}
                for _, name in ipairs(" + LuaList(Absent) + @") do
                  if resolve(name) ~= nil then wrong[#wrong + 1] = name end
                end
                store_set('result', table.concat(wrong, ' '))");
            Assert.AreEqual("", result, "members recorded as absent that now exist: record the new decision");
        }

        [Test]
        public void TheLibraryTables_HoldNothingTheInventoryDoesNotName()
        {
            HashSet<string> known = new(Real);
            known.UnionWith(Stubbed);
            known.UnionWith(RealValues.Keys);
            string result = RunAsMod(@"
                local extra = {}
                for _, lib in ipairs({'table', 'string', 'math', 'utf8', 'bit32', 'os', 'coroutine', 'debug'}) do
                  for k in pairs(_G[lib]) do extra[#extra + 1] = lib .. '.' .. tostring(k) end
                end
                table.sort(extra)
                store_set('result', table.concat(extra, ' '))");
            List<string> unknown = new();
            foreach (string name in result.Split(' '))
            {
                if (name.Length > 0 && !known.Contains(name))
                {
                    unknown.Add(name);
                }
            }

            Assert.IsEmpty(unknown, "library members no decision covers (Roblox has none of these)");
        }
    }
}
#endif
