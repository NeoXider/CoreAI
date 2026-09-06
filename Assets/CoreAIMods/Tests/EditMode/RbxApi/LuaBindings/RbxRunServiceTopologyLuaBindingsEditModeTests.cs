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
    /// End-to-end proof of the RunService topology queries through the REAL mod runtime:
    /// <c>IsServer</c>/<c>IsClient</c>/<c>IsStudio</c>/<c>IsRunning</c> answer from the instance's
    /// <see cref="IRbxRuntimeTopology"/> (solo by default), the four are gone from the
    /// known-unimplemented catalog while the render-step bindings stay loud stubs, and swapping
    /// the topology source changes the same Lua call's answer.
    /// </summary>
    [TestFixture]
    public sealed class RbxRunServiceTopologyLuaBindingsEditModeTests
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

        /// <summary>Client-only topology proving the Lua call reads the seam, not a literal.</summary>
        private sealed class ClientOnlyTopology : IRbxRuntimeTopology
        {
            public bool IsServer => false;

            public bool IsClient => true;

            public bool IsStudio => false;

            public bool IsRunning => true;

            public bool RendersFrames => true;
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
        public void Lua_RunService_TopologyQueries_ReturnSoloMirrorAnswers()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                store_set('is_server', tostring(rs:IsServer()))
                store_set('is_client', tostring(rs:IsClient()))
                store_set('is_studio', tostring(rs:IsStudio()))
                store_set('is_running', tostring(rs:IsRunning()))");

            // WHY: the solo runtime is the server authority with no client execution context and
            // no Studio; the Roblox reference mirror answers IsClient true only in a client
            // context, so solo reports server-only here while running.
            Assert.AreEqual("true", store.Get("m", "is_server"));
            Assert.AreEqual("false", store.Get("m", "is_client"));
            Assert.AreEqual("false", store.Get("m", "is_studio"));
            Assert.AreEqual("true", store.Get("m", "is_running"));
        }

        [Test]
        public void Catalog_RunService_TopologyQueriesUnstubbed_RenderStepStubsRemain()
        {
            InstanceRegistry registry = new();

            string[] unstubbed = { "IsServer", "IsClient", "IsStudio", "IsRunning" };
            foreach (string member in unstubbed)
            {
                bool found = registry.Catalog.TryGetKnownUnimplementedMember(
                    "RunService", member, RbxKnownUnimplementedMemberAccess.Read,
                    out string _, out RbxKnownUnimplementedMemberDescriptor _);
                Assert.IsFalse(found, "RunService:" + member + " must no longer be a loud stub");
            }

            string[] stillStubbed = { "BindToRenderStep", "UnbindFromRenderStep" };
            foreach (string member in stillStubbed)
            {
                bool found = registry.Catalog.TryGetKnownUnimplementedMember(
                    "RunService", member, RbxKnownUnimplementedMemberAccess.Read,
                    out string _, out RbxKnownUnimplementedMemberDescriptor _);
                Assert.IsTrue(found, "RunService:" + member + " must remain a loud stub");
            }
        }

        [Test]
        public void Lua_RunService_TopologySource_IsSubstitutableThroughSameLuaCall()
        {
            LuaCsRbxApiBindings roblox = new();
            roblox.RunService.Topology = new ClientOnlyTopology();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                store_set('is_server', tostring(rs:IsServer()))
                store_set('is_client', tostring(rs:IsClient()))");

            Assert.AreEqual("false", store.Get("m", "is_server"));
            Assert.AreEqual("true", store.Get("m", "is_client"));
        }

        [Test]
        public void Lua_RunService_RenderStepBinding_RemainsLoudStub()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                local bindOk, bindErr = pcall(function() return rs.BindToRenderStep end)
                store_set('bind_ok', tostring(bindOk))
                store_set('bind_err', tostring(bindErr))
                local unbindOk, unbindErr = pcall(function() return rs.UnbindFromRenderStep end)
                store_set('unbind_ok', tostring(unbindOk))
                store_set('unbind_err', tostring(unbindErr))");

            Assert.AreEqual("false", store.Get("m", "bind_ok"));
            StringAssert.Contains("NOT_IMPLEMENTED", store.Get("m", "bind_err"));
            Assert.AreEqual("false", store.Get("m", "unbind_ok"));
            StringAssert.Contains("NOT_IMPLEMENTED", store.Get("m", "unbind_err"));
        }
    }
}
