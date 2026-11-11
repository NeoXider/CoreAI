using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.UiFocus;

namespace CoreAI.Tests.EditMode.RbxApi.RuntimeUi
{
    /// <summary>
    /// One Rbx world with the production mod stack (<see cref="LuaCsModRuntimeFactory"/>, so unload and
    /// reload tear down the way the composition does) and a <see cref="RuntimeUiLuaSurface"/> attached
    /// to it the way <c>RuntimeUiLuaInstaller.AttachRuntimeUi</c> attaches every composed world.
    /// </summary>
    internal sealed class UxmlServiceTestWorld : IDisposable
    {
        public UxmlServiceTestWorld(IRbxUiBackend backend, RbxUiBudgets budgets = null, ICoreAiUiFocus uiFocus = null,
            bool attach = true, LuaCapabilities hostGrant = LuaCapabilities.All)
        {
            Backend = backend;
            Input = new InMemoryInputSource { FocusState = new InputFocusState() };
            Connections = new ModConnectionRegistry();
            Roblox = new LuaCsRbxApiBindings(inputSource: Input, connections: Connections);
            Store = new MemoryStore();
            Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentGameLogger(),
                ModStore = Store,
                Capabilities = hostGrant,
                OneOffCapabilities = hostGrant,
                RbxApi = Roblox
            });
            Surface = new RuntimeUiLuaSurface(backend, budgets, uiFocus);
            if (attach)
            {
                World = Surface.AttachTo(Roblox);
            }
        }

        public IRbxUiBackend Backend { get; }

        public FakeRbxUiBackend Fake => Backend as FakeRbxUiBackend;

        public InMemoryInputSource Input { get; }

        public ModConnectionRegistry Connections { get; }

        public LuaCsRbxApiBindings Roblox { get; }

        public MemoryStore Store { get; }

        public LuaCsModStack Stack { get; }

        public RuntimeUiLuaSurface Surface { get; }

        public RbxUiWorld World { get; }

        public static UxmlServiceTestWorld WithFake(RbxUiBudgets budgets = null, ICoreAiUiFocus uiFocus = null)
        {
            return new UxmlServiceTestWorld(new FakeRbxUiBackend(), budgets, uiFocus);
        }

        public void Load(string modId, string source, LuaCapabilities capabilities = LuaCapabilities.All)
        {
            Stack.Runtime.LoadMod(modId, source, capabilities);
        }

        public void Reload(string modId, string source)
        {
            Stack.Runtime.ReloadMod(modId, source);
        }

        public bool Unload(string modId)
        {
            return Stack.Runtime.UnloadMod(modId);
        }

        /// <summary>Runs one scheduler frame: deferred handlers run, budgets reset, unclaimed screens go.</summary>
        public void Frame()
        {
            Roblox.Scheduler.Advance(0d);
        }

        public string Get(string modId, string key)
        {
            return Store.Get(modId, key);
        }

        public void Dispose()
        {
            Surface.Dispose();
            Roblox.Dispose();
        }

        internal sealed class MemoryStore : ILuaModStore
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

        private sealed class SilentGameLogger : IGameLogger
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
}
