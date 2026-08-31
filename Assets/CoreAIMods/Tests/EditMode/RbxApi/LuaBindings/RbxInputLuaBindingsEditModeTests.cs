using System;
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
    /// End-to-end proof of the MVP1 UserInputService slice through the REAL mod runtime with the
    /// headless <see cref="InMemoryInputSource"/>: IsKeyDown/GetKeysPressed/GetMouseLocation
    /// polls, InputBegan/InputEnded/InputChanged dispatch via the per-frame pump, connection
    /// lifecycle, MouseBehavior, and Enum.KeyCode/UserInputType Roblox value parity.
    /// </summary>
    [TestFixture]
    public sealed class RbxInputLuaBindingsEditModeTests
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

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox, MemoryStore store,
            LuaCapabilities capabilities = LuaCapabilities.All)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = capabilities,
                OneOffCapabilities = capabilities,
                RbxApi = roblox
            });
        }

        // Roblox Enum.KeyCode values used below.
        private const int KeySpace = 32;
        private const int KeyA = 97;
        private const int KeyE = 101;
        private const int ButtonA = 1002;

        // ---- Poll surface -------------------------------------------------------------------

        [Test]
        public void Lua_UserInputService_IsKeyDown_ReflectsSourceState()
        {
            InMemoryInputSource source = new();
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            LuaCsModStack stack = BuildStack(roblox, new MemoryStore());

            source.PressKey(KeyE);
            stack.Runtime.LoadMod("m1", @"
                local uis = game:GetService('UserInputService')
                assert(uis:IsKeyDown(Enum.KeyCode.E) == true)
                assert(uis:IsKeyDown(Enum.KeyCode.Space) == false)");

            source.ReleaseKey(KeyE);
            stack.Runtime.LoadMod("m2", @"
                assert(UserInputService:IsKeyDown(Enum.KeyCode.E) == false)");
        }

        [Test]
        public void Lua_UserInputService_GetKeysPressed_ReturnsInputObjectsForHeldKeys()
        {
            InMemoryInputSource source = new();
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            LuaCsModStack stack = BuildStack(roblox, new MemoryStore());

            source.PressKey(KeyA);
            source.PressKey(KeySpace);
            stack.Runtime.LoadMod("m", @"
                local keys = UserInputService:GetKeysPressed()
                assert(#keys == 2)
                local seenA, seenSpace = false, false
                for _, input in ipairs(keys) do
                    assert(input.UserInputType == Enum.UserInputType.Keyboard)
                    if input.KeyCode == Enum.KeyCode.A then seenA = true end
                    if input.KeyCode == Enum.KeyCode.Space then seenSpace = true end
                end
                assert(seenA and seenSpace)");
        }

        [Test]
        public void Lua_UserInputService_GetMouseLocation_ReturnsVector2()
        {
            InMemoryInputSource source = new();
            source.SetMouseLocation(120f, 45f);
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            LuaCsModStack stack = BuildStack(roblox, new MemoryStore());

            stack.Runtime.LoadMod("m", @"
                local location = UserInputService:GetMouseLocation()
                assert(location == Vector2.new(120, 45))");
        }

        // ---- Signal dispatch ----------------------------------------------------------------

        [Test]
        public void Lua_UserInputService_InputBegan_FiresWithKeyCodeAndUserInputType()
        {
            InMemoryInputSource source = new();
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local uis = game:GetService('UserInputService')
                uis.InputBegan:Connect(function(input, gameProcessed)
                    store_set('kc', tostring(input.KeyCode))
                    store_set('type', tostring(input.UserInputType))
                    store_set('state', tostring(input.UserInputState))
                    store_set('gp', tostring(gameProcessed))
                end)");

            source.PressKey(KeySpace);
            roblox.PumpInput();

            Assert.AreEqual("Enum.KeyCode.Space", store.Get("m", "kc"));
            Assert.AreEqual("Enum.UserInputType.Keyboard", store.Get("m", "type"));
            Assert.AreEqual("Enum.UserInputState.Begin", store.Get("m", "state"));
            Assert.AreEqual("false", store.Get("m", "gp"));
        }

        [Test]
        public void Lua_UserInputService_InputEnded_FiresOnRelease()
        {
            InMemoryInputSource source = new();
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                UserInputService.InputEnded:Connect(function(input)
                    store_set('kc', tostring(input.KeyCode))
                    store_set('state', tostring(input.UserInputState))
                end)");

            source.PressKey(KeyE);
            roblox.PumpInput();
            Assert.AreEqual("", store.Get("m", "kc"));

            source.ReleaseKey(KeyE);
            roblox.PumpInput();
            Assert.AreEqual("Enum.KeyCode.E", store.Get("m", "kc"));
            Assert.AreEqual("Enum.UserInputState.End", store.Get("m", "state"));
        }

        [Test]
        public void Lua_UserInputService_MouseButton1_FiresBeganWithPosition()
        {
            InMemoryInputSource source = new();
            source.SetMouseLocation(10f, 20f);
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                UserInputService.InputBegan:Connect(function(input)
                    store_set('type', tostring(input.UserInputType))
                    store_set('pos', tostring(input.Position))
                    store_set('kc', tostring(input.KeyCode))
                end)");

            source.SetMouseButton(0, true);
            roblox.PumpInput();

            Assert.AreEqual("Enum.UserInputType.MouseButton1", store.Get("m", "type"));
            Assert.AreEqual("10, 20, 0", store.Get("m", "pos"));
            Assert.AreEqual("Enum.KeyCode.Unknown", store.Get("m", "kc"));
        }

        [Test]
        public void Lua_UserInputService_InputChanged_FiresMouseMovementWithDelta()
        {
            InMemoryInputSource source = new();
            source.SetMouseLocation(100f, 50f);
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                UserInputService.InputChanged:Connect(function(input)
                    store_set('type', tostring(input.UserInputType))
                    store_set('pos', tostring(input.Position))
                    store_set('delta', tostring(input.Delta))
                end)");

            // WHY: the first pump records the baseline location; only the second observes motion.
            roblox.PumpInput();
            source.SetMouseLocation(105f, 56f);
            roblox.PumpInput();

            Assert.AreEqual("Enum.UserInputType.MouseMovement", store.Get("m", "type"));
            Assert.AreEqual("105, 56, 0", store.Get("m", "pos"));
            Assert.AreEqual("5, 6, 0", store.Get("m", "delta"));
        }

        [Test]
        public void Lua_UserInputService_GamepadButton_FiresAsGamepad1()
        {
            InMemoryInputSource source = new();
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                UserInputService.InputBegan:Connect(function(input)
                    store_set('kc', tostring(input.KeyCode))
                    store_set('type', tostring(input.UserInputType))
                end)");

            source.PressKey(ButtonA);
            roblox.PumpInput();

            Assert.AreEqual("Enum.KeyCode.ButtonA", store.Get("m", "kc"));
            Assert.AreEqual("Enum.UserInputType.Gamepad1", store.Get("m", "type"));
        }

        [Test]
        public void Lua_UserInputService_Connection_DisconnectStopsDelivery()
        {
            InMemoryInputSource source = new();
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local count = 0
                local connection
                connection = UserInputService.InputBegan:Connect(function(input)
                    count = count + 1
                    store_set('count', tostring(count))
                    assert(connection.Connected == true)
                    connection:Disconnect()
                    assert(connection.Connected == false)
                end)");

            source.PressKey(KeyA);
            roblox.PumpInput();
            Assert.AreEqual("1", store.Get("m", "count"));

            source.PressKey(KeyE);
            roblox.PumpInput();
            Assert.AreEqual("1", store.Get("m", "count"));
        }

        // ---- MouseBehavior + enums + service identity ---------------------------------------

        [Test]
        public void Lua_UserInputService_MouseBehavior_DefaultAndAssignment()
        {
            LuaCsRbxApiBindings roblox = new(inputSource: new InMemoryInputSource());
            LuaCsModStack stack = BuildStack(roblox, new MemoryStore());

            stack.Runtime.LoadMod("m", @"
                local uis = game:GetService('UserInputService')
                assert(uis.MouseBehavior == Enum.MouseBehavior.Default)
                uis.MouseBehavior = Enum.MouseBehavior.LockCenter
                assert(uis.MouseBehavior == Enum.MouseBehavior.LockCenter)");
        }

        [Test]
        public void Lua_UserInputService_MouseBehavior_RequiresWorldEdit()
        {
            LuaCsRbxApiBindings roblox = new(inputSource: new InMemoryInputSource());
            LuaCapabilities readOnly = LuaCapabilities.Read | LuaCapabilities.Gameplay;
            LuaCsModStack stack = BuildStack(roblox, new MemoryStore(), readOnly);

            Exception error = Assert.Catch(() => stack.Runtime.LoadMod("reader", @"
                local uis = game:GetService('UserInputService')
                uis.MouseBehavior = Enum.MouseBehavior.LockCenter"));

            StringAssert.Contains("WorldEdit", error.ToString());
            Assert.AreEqual("Default", roblox.UserInputService.MouseBehavior.Name);
        }

        [Test]
        public void Lua_Enum_KeyCodeAndUserInputType_ValuesMatchRoblox()
        {
            LuaCsRbxApiBindings roblox = new(inputSource: new InMemoryInputSource());
            LuaCsModStack stack = BuildStack(roblox, new MemoryStore());

            stack.Runtime.LoadMod("m", @"
                assert(Enum.KeyCode.A.Value == 97)
                assert(Enum.KeyCode.Space.Value == 32)
                assert(Enum.KeyCode.F1.Value == 282)
                assert(Enum.KeyCode.ButtonA.Value == 1002)
                assert(Enum.KeyCode.DPadDown.Value == 1015)
                assert(tostring(Enum.KeyCode.Space) == 'Enum.KeyCode.Space')
                assert(Enum.UserInputType.MouseButton1.Value == 0)
                assert(Enum.UserInputType.MouseMovement.Value == 4)
                assert(Enum.UserInputType.Keyboard.Value == 8)
                assert(Enum.UserInputType.Gamepad1.Value == 12)
                assert(Enum.UserInputState.Begin.Value == 0)
                assert(Enum.MouseBehavior.LockCurrentPosition.Value == 2)");
        }

        [Test]
        public void Lua_UserInputService_GetServiceAndGlobal_AreSameInstance()
        {
            LuaCsRbxApiBindings roblox = new(inputSource: new InMemoryInputSource());
            LuaCsModStack stack = BuildStack(roblox, new MemoryStore());

            stack.Runtime.LoadMod("m", @"
                local uis = game:GetService('UserInputService')
                assert(uis == UserInputService)
                assert(uis.ClassName == 'UserInputService')
                assert(uis:IsA('Instance'))");
        }

        // ---- C#-level source/service checks -------------------------------------------------

        [Test]
        public void CSharp_UserInputService_StepDiff_FiresBeganOncePerHold()
        {
            InMemoryInputSource source = new();
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            RbxUserInputService service = roblox.UserInputService;
            Assert.IsNotNull(service);

            int beganCount = 0;
            service.InputBegan.Connect((System.Action<object[]>)(_ => beganCount++));

            source.PressKey(KeyA);
            roblox.PumpInput();
            roblox.PumpInput();

            Assert.AreEqual(1, beganCount);
        }

        [Test]
        public void CSharp_GetKeysPressed_ExcludesGamepadButtons()
        {
            InMemoryInputSource source = new();
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            RbxUserInputService service = roblox.UserInputService;

            source.PressKey(KeyA);
            source.PressKey(ButtonA);

            IReadOnlyList<RbxInputObject> pressed = service.GetKeysPressed();
            Assert.AreEqual(1, pressed.Count);
            Assert.AreEqual(KeyA, pressed[0].KeyCode.Value);
        }

        [Test]
        public void Lua_UserInputService_CannotBeDestroyedOrCloned()
        {
            LuaCsRbxApiBindings roblox = new(inputSource: new InMemoryInputSource());
            LuaCsModStack stack = BuildStack(roblox, new MemoryStore());

            // WHY: destroying a shared service would brick input for every mod; cloning would fork a
            // second service instance. Roblox locks/refuses both — Destroy errors, Clone yields nil.
            stack.Runtime.LoadMod("m", @"
                local uis = game:GetService('UserInputService')
                local okClone, clone = pcall(function() return uis:Clone() end)
                assert(okClone and clone == nil, 'Clone of a service must return nil')
                local okDestroy = pcall(function() uis:Destroy() end)
                assert(not okDestroy, 'Destroy of a service must error')
                local okParent = pcall(function() uis.Parent = nil end)
                assert(not okParent, 'reparenting a service must error')
                game:ClearAllChildren()
                assert(game:GetService('UserInputService') == uis,
                    'the service survives Destroy/reparent/ClearAllChildren')");
        }

        [Test]
        public void CSharp_GetKeysPressed_FromInputBeganHandler_DoesNotCorruptThePump()
        {
            InMemoryInputSource source = new();
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            RbxUserInputService service = roblox.UserInputService;

            // WHY: a handler polling GetKeysPressed() mid-dispatch must not throw "collection
            // modified" out of the pump — GetKeysPressed uses its own buffer, not the pump's.
            int handled = 0;
            service.InputBegan.Connect((System.Action<object[]>)(_ =>
            {
                service.GetKeysPressed();
                handled++;
            }));

            source.PressKey(KeyA);
            source.PressKey(KeySpace);
            Assert.DoesNotThrow(() => roblox.PumpInput());
            Assert.AreEqual(2, handled, "both key-down events dispatched without a pump fault");
        }
    }
}
