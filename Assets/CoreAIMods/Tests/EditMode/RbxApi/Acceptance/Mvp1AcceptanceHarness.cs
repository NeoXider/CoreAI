using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// Shared world for the MVP1 acceptance-gate suite (ROBLOX_API_ROADMAP.md §5.1.8): a real
    /// <see cref="InstanceGameObjectBinder"/>-backed registry + DataModel + Lua mod stack, so the
    /// gate tests exercise the exact build path a shipping mod uses (Lua → registry → binder →
    /// GameObject), not a headless stand-in.
    /// </summary>
    internal sealed class Mvp1AcceptanceWorld : IDisposable
    {
        public GameObject Root { get; }

        public InstanceGameObjectBinder Binder { get; }

        public InstanceRegistry Registry { get; }

        public RbxDataModel Game { get; }

        public LuaCsRbxApiBindings Bindings { get; }

        public LuaCsModStack Stack { get; }

        public Mvp1AcceptanceMemoryStore Store { get; }

        public Mvp1AcceptanceWorld(float metersPerStud = RbxSpace.DefaultMetersPerStud,
            IRbxMaterialProvider<Material> materialProvider = null)
        {
            RbxSpace.ResetForTests(metersPerStud);
            Root = new GameObject("Mvp1AcceptanceRoot");
            Binder = new InstanceGameObjectBinder(Root.transform,
                materialProvider: materialProvider);
            Registry = new InstanceRegistry(null, Binder);
            Game = DataModelBootstrap.CreateGame(Registry);
            Bindings = new LuaCsRbxApiBindings(Registry, Game, partSink: Binder);
            Store = new Mvp1AcceptanceMemoryStore();
            Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new Mvp1AcceptanceNullLogger(),
                ModStore = Store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = Bindings
            });
        }

        public RbxInstance Workspace => Registry.WorldRoot;

        public GameObject BoundObject(RbxInstance instance)
        {
            NUnit.Framework.Assert.IsTrue(
                Binder.TryGetBoundObject(instance.Id, out GameObject gameObject),
                instance.Name + " should have a backing GameObject");
            return gameObject;
        }

        public void Dispose()
        {
            Game.Destroy();
            UnityEngine.Object.DestroyImmediate(Root);
            RbxSpace.ResetForTests();
        }
    }

    /// <summary>In-memory ILuaModStore mirroring the runtime-test harness: store_set from Lua is
    /// the read-back channel for values the C# side asserts on.</summary>
    internal sealed class Mvp1AcceptanceMemoryStore : ILuaModStore
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

    /// <summary>Silent logger; acceptance tests assert on world state, not log lines.</summary>
    internal sealed class Mvp1AcceptanceNullLogger : IGameLogger
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
}
