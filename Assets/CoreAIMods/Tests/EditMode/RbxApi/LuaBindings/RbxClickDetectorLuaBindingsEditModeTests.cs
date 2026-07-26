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
    /// Proof of the ClickDetector wiring through the REAL mod runtime for the parts that do not need
    /// a live scene (Physics/Camera raycasting is a Play Mode check — the pick pump itself is verified
    /// there): Instance.new("ClickDetector") is creatable and parents under a Part, MouseClick is a
    /// dispatch-enabled signal a mod connects and that fires its handler when the host fire path runs,
    /// MaxActivationDistance round-trips, and a mod's MouseClick connection is dropped on unload.
    /// </summary>
    [TestFixture]
    public sealed class RbxClickDetectorLuaBindingsEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>Same sync-over-async hazard as the sibling RunService fixture: detach Unity's
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

        /// <summary>Same wiring as the CoreAiModsInstaller / the connection-teardown fixture: a shared
        /// ledger plus the ModTearingDown sweep that disconnects a mod's connections on unload.</summary>
        private static LuaCsModStack BuildWiredStack(out LuaCsRbxApiBindings roblox, MemoryStore store)
        {
            ModConnectionRegistry connections = new();
            roblox = new LuaCsRbxApiBindings(connections: connections);
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.ModTearingDown += (modId, reason) => connections.DisconnectOwnedBy(
                modId, reason == LuaModTeardownReason.Reload);
            return stack;
        }

        // WHY: the pick pump resolves the clicked part's ClickDetector child from C#; the tests fire
        // that same signal directly (the raycast that selects it is a Play Mode concern).
        private static RbxClickDetector FindClickDetector(LuaCsRbxApiBindings roblox)
        {
            foreach (RbxInstance descendant in roblox.Game.GetDescendants())
            {
                if (descendant is RbxClickDetector detector)
                {
                    return detector;
                }
            }

            return null;
        }

        [Test]
        public void Lua_ClickDetector_IsCreatable_AndParentsUnderPart()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                local cd = Instance.new('ClickDetector')
                cd.Parent = part
                store_set('class', cd.ClassName)
                store_set('is_instance', tostring(cd:IsA('Instance')))
                store_set('parented', tostring(cd.Parent == part))
                store_set('found', tostring(part:FindFirstChildOfClass('ClickDetector') == cd))");

            Assert.AreEqual("ClickDetector", store.Get("m", "class"));
            Assert.AreEqual("true", store.Get("m", "is_instance"));
            Assert.AreEqual("true", store.Get("m", "parented"), "the ClickDetector parents under the Part");
            Assert.AreEqual("true", store.Get("m", "found"),
                "the Part exposes its ClickDetector via FindFirstChildOfClass");
            Assert.IsNotNull(FindClickDetector(roblox), "the ClickDetector materialized in the world");
        }

        [Test]
        public void Lua_ClickDetector_MaxActivationDistance_DefaultsTo32_AndRoundTrips()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local cd = Instance.new('ClickDetector')
                store_set('default', tostring(cd.MaxActivationDistance))
                cd.MaxActivationDistance = 12
                store_set('after', tostring(cd.MaxActivationDistance))");

            Assert.AreEqual("32", store.Get("m", "default"), "Roblox default MaxActivationDistance is 32");
            Assert.AreEqual("12", store.Get("m", "after"), "MaxActivationDistance round-trips through Lua");
        }

        [Test]
        public void Lua_ClickDetector_MouseClick_ConnectHandler_FiresOnHostFire()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                local cd = Instance.new('ClickDetector')
                cd.Parent = part
                local n = 0
                cd.MouseClick:Connect(function()
                    n = n + 1
                    store_set('clicks', tostring(n))
                end)");

            RbxClickDetector detector = FindClickDetector(roblox);
            Assert.IsNotNull(detector);
            Assert.IsTrue(detector.MouseClick.HasConnections, "the mod connected a MouseClick handler");

            // WHY: drive the same fire path the pick pump uses when the part is clicked; the handler
            // must run once per fire.
            detector.MouseClick.Fire();
            Assert.AreEqual("1", store.Get("m", "clicks"), "MouseClick handler runs when the signal fires");

            detector.MouseClick.Fire();
            Assert.AreEqual("2", store.Get("m", "clicks"), "each MouseClick fire invokes the handler once");
        }

        [Test]
        public void ClickDetector_MouseClick_Connection_IsDropped_OnModUnload()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(out LuaCsRbxApiBindings roblox, store);

            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                local cd = Instance.new('ClickDetector')
                cd.Parent = part
                cd.MouseClick:Connect(function() end)");

            RbxClickDetector detector = FindClickDetector(roblox);
            Assert.IsNotNull(detector);
            Assert.IsTrue(detector.MouseClick.HasConnections,
                "the mod's MouseClick connection is live while loaded");

            Assert.IsTrue(stack.Runtime.UnloadMod("m"), "the mod unloads");

            // WHY: the ModTearingDown sweep disconnects the mod's MouseClick connection, exactly like
            // a RunService.Heartbeat connection, so a clicked part never fires a torn-down mod's handler.
            Assert.IsFalse(detector.MouseClick.HasConnections,
                "unloading the mod disconnects its MouseClick connection");
        }
    }
}
