using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// End-to-end proof of the RunService game-loop slice through the REAL mod runtime: the
    /// per-frame pump (PumpFrame → RunService.Step) fires Heartbeat/Stepped/RenderStepped once
    /// per frame, and the connected Lua handler receives the frame delta as a number, so
    /// <c>RunService.Heartbeat:Connect(function(dt) ... end)</c> is the idiomatic per-frame loop.
    /// </summary>
    [TestFixture]
    public sealed class RbxRunServiceLuaBindingsEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>Same sync-over-async hazard as LuaCsModRuntimeEditModeTests: detach Unity's
        /// SynchronizationContext so VM continuations complete on the thread pool.</summary>
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

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox, MemoryStore store)
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

        [Test]
        public void Lua_RunService_Heartbeat_FiresOncePerFrameWithNumericDelta()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                local n = 0
                rs.Heartbeat:Connect(function(dt)
                    n = n + 1
                    store_set('n', tostring(n))
                    store_set('dt_is_number', tostring(type(dt) == 'number'))
                    store_set('dt', tostring(dt))
                end)");

            // WHY: drive the same path the host uses each frame (PumpFrame → RunService.Step(dt)).
            roblox.PumpFrame(0.25f);
            Assert.AreEqual("1", store.Get("m", "n"));
            Assert.AreEqual("true", store.Get("m", "dt_is_number"), "Heartbeat handler must receive a numeric dt");
            Assert.AreEqual("0.25", store.Get("m", "dt"));

            roblox.PumpFrame(0.25f);
            roblox.PumpFrame(0.25f);

            // WHY: one fire per frame — three pumps deliver exactly three Heartbeat invocations.
            Assert.AreEqual("3", store.Get("m", "n"));
        }

        [Test]
        public void Lua_RunService_GetService_ResolvesRealService()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox, new MemoryStore());

            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                assert(rs.ClassName == 'RunService')
                assert(rs:IsA('Instance'))");
        }

        [Test]
        public void CSharp_RunService_Step_FiresHeartbeatWithDelta()
        {
            LuaCsRbxApiBindings roblox = new();
            RbxRunService service = roblox.RunService;
            Assert.IsNotNull(service);

            int count = 0;
            float lastDelta = 0f;
            service.Heartbeat.Connect((System.Action<object[]>)(args =>
            {
                count++;
                lastDelta = (float)args[0];
            }));

            roblox.PumpFrame(0.5f);
            roblox.PumpFrame(0.5f);

            Assert.AreEqual(2, count, "Heartbeat fires once per pump");
            Assert.AreEqual(0.5f, lastDelta, "Heartbeat carries the frame delta");
        }
    }
}
