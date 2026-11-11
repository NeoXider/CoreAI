using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// P4's voxel Terrain through the shipped registration only (no test harness chaining): the bootstrap
    /// creates <c>workspace.Terrain</c>, the Instance member read and the method table answer it, the
    /// <c>Region3</c> global is real, and the Terrain is a protected singleton. Each with a negative twin.
    /// </summary>
    [TestFixture]
    public sealed class P4TerrainShippedRegistrationEditModeTests
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

        private static (LuaCsRbxApiBindings Roblox, LuaCsModStack Stack, MemoryStore Store) Build()
        {
            MemoryStore store = new();
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox
            });
            return (roblox, stack, store);
        }

        [Test]
        public void P4_ShippedWorld_HasTerrain_AndItsMethodsAndRegion3Work()
        {
            (LuaCsRbxApiBindings roblox, LuaCsModStack stack, MemoryStore store) = Build();
            Assert.IsNotNull(RbxTerrainBootstrap.Find(roblox.Game.FindFirstChildOfClass("Workspace")),
                "the bootstrap creates the Workspace's Terrain");
            stack.Runtime.LoadMod("m", @"
                local terrain = workspace.Terrain
                store_set('class', terrain.ClassName)
                store_set('region', typeof(Region3.new(Vector3.new(0, 0, 0), Vector3.new(8, 8, 8))))
                terrain:FillBlock(CFrame.new(2, 2, 2), Vector3.new(4, 4, 4), Enum.Material.Grass)
                local materials = terrain:ReadVoxels(Region3.new(Vector3.new(0, 0, 0), Vector3.new(4, 4, 4)), 4)
                store_set('cell', tostring(materials[1][1][1]))");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
            Assert.AreEqual("Terrain", store.Get("m", "class"));
            Assert.AreEqual("Region3", store.Get("m", "region"));
            Assert.AreEqual("Enum.Material.Grass", store.Get("m", "cell"));
        }

        [Test]
        public void P4_ShippedTerrain_IsAProtectedSingleton_NegativeTwin()
        {
            (LuaCsRbxApiBindings roblox, LuaCsModStack stack, MemoryStore store) = Build();
            stack.Runtime.LoadMod("m", @"
                local function try(f) local ok, err = pcall(f) return tostring(ok) .. '|' .. tostring(err) end
                store_set('destroy', try(function() workspace.Terrain:Destroy() end))
                store_set('parent', try(function() workspace.Terrain.Parent = nil end))
                workspace:ClearAllChildren()
                store_set('after', tostring(workspace:FindFirstChild('Terrain') ~= nil))");

            StringAssert.StartsWith("false|", store.Get("m", "destroy"));
            StringAssert.StartsWith("false|", store.Get("m", "parent"));
            Assert.AreEqual("true", store.Get("m", "after"), "ClearAllChildren keeps the Terrain");
        }
    }
}
