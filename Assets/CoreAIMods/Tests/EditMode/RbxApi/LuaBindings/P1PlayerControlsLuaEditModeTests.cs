using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// P1 (player controls and camera) through the real mod runtime with headless seams: the input
    /// focus behind <c>gameProcessedEvent</c>, pointer lock and the device delta, the mouse wheel.
    /// </summary>
    [TestFixture]
    public sealed class P1PlayerControlsLuaEditModeTests
    {
        private const int KeyW = 119;

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

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox, MemoryStore store,
            LuaCapabilities capabilities = LuaCapabilities.All)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentLogger(),
                ModStore = store,
                Capabilities = capabilities,
                OneOffCapabilities = capabilities,
                RbxApi = roblox
            });
        }

        private static void Frame(LuaCsRbxApiBindings roblox)
        {
            roblox.Scheduler.Advance(0d);
        }

        [Test]
        public void InputBegan_CarriesGameProcessedTrue_WhileTheChatHoldsTheInput()
        {
            InputFocusState focus = new();
            InMemoryInputSource source = new() { FocusState = focus };
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                UserInputService.InputBegan:Connect(function(input, gameProcessed)
                    store_set('gp', tostring(gameProcessed))
                end)");

            focus.SetCaptured("chat", true);
            source.PressKey(KeyW);
            Frame(roblox);

            Assert.AreEqual("true", store.Get("m", "gp"));
        }

        [Test]
        public void InputBegan_CarriesGameProcessedFalse_WhenTheChatIsClosed()
        {
            InputFocusState focus = new();
            InMemoryInputSource source = new() { FocusState = focus };
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                UserInputService.InputBegan:Connect(function(input, gameProcessed)
                    store_set('gp', tostring(gameProcessed))
                end)");

            focus.SetCaptured("chat", true);
            focus.SetCaptured("chat", false);
            source.PressKey(KeyW);
            Frame(roblox);

            Assert.AreEqual("false", store.Get("m", "gp"));
        }

        [Test]
        public void MouseBehavior_LockCenter_LocksTheCursor_AndGetMouseDeltaReadsTheDevice()
        {
            InMemoryCursorPort port = new();
            InMemoryInputSource source = new() { CursorPort = port };
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("lock", @"
                UserInputService.MouseBehavior = Enum.MouseBehavior.LockCenter
                UserInputService.InputChanged:Connect(function(input)
                    if input.UserInputType == Enum.UserInputType.MouseMovement then
                        store_set('delta', tostring(input.Delta))
                    end
                end)");
            Assert.AreEqual(RbxMouseLock.LockCenter, port.Applied);

            source.SetMouseDelta(6f, -3f);
            Frame(roblox);
            stack.Runtime.LoadMod("read", @"
                local d = UserInputService:GetMouseDelta()
                store_set('get', (d.X == 6 and d.Y == -3) and 'ok' or 'bad')");

            Assert.AreEqual("6, -3, 0", store.Get("lock", "delta"));
            Assert.AreEqual("ok", store.Get("read", "get"));

            stack.Runtime.LoadMod("free", "UserInputService.MouseBehavior = Enum.MouseBehavior.Default");
            Assert.AreEqual(RbxMouseLock.Default, port.Applied);
        }

        [Test]
        public void MouseDeltaSensitivity_ReadsAndWrites_ThroughLua()
        {
            LuaCsRbxApiBindings roblox = new(inputSource: new InMemoryInputSource());
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                store_set('before', UserInputService.MouseDeltaSensitivity == 1 and 'one' or 'other')
                UserInputService.MouseDeltaSensitivity = 2.5
                store_set('after', UserInputService.MouseDeltaSensitivity == 2.5 and 'set' or 'other')");

            Assert.AreEqual("one", store.Get("m", "before"));
            Assert.AreEqual("set", store.Get("m", "after"));
        }

        /// <summary>A motor that records Move and flight requests.</summary>
        private sealed class RecordingMotor : IRbxCharacterMotor
        {
            public RbxVector3 LastMove = RbxVector3.Zero;
            public int NonZeroMoves;
            public bool Flying;

            public RbxVector3 Position => RbxVector3.Zero;

            public RbxVector3 MoveDirection => RbxVector3.Zero;

            public bool IsGrounded => true;

            public bool IsFlying => Flying;

            public void SetWalkSpeed(double studsPerSecond)
            {
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
            }

            public void MoveTo(RbxVector3? targetStuds)
            {
            }

            public void Move(RbxVector3 directionStuds)
            {
                LastMove = directionStuds;
                if (directionStuds.Magnitude > 1e-4f)
                {
                    NonZeroMoves++;
                }
            }

            public bool SetFlying(bool flying)
            {
                Flying = flying;
                return true;
            }
        }

        private const int KeySpace = 32;
        private const double FrameSeconds = 1d / 60d;

        private static string ControlScriptSource()
        {
            string path = System.IO.Path.Combine(UnityEngine.Application.dataPath, "CoreAIMods", "Runtime",
                "Resources", "CoreAIMods", "coreai_player_controls.lua");
            return System.IO.File.ReadAllText(path);
        }

        private sealed class ControlledWorld
        {
            public InMemoryInputSource Source;
            public InputFocusState Focus;
            public RecordingMotor Motor;
            public LuaCsRbxApiBindings Roblox;
            public LuaCsModStack Stack;
            public MemoryStore Store;

            public void Frames(int count)
            {
                for (int index = 0; index < count; index++)
                {
                    Roblox.Scheduler.Advance(FrameSeconds);
                }
            }
        }

        private static ControlledWorld LoadControls()
        {
            ControlledWorld world = new()
            {
                Focus = new InputFocusState(),
                Motor = new RecordingMotor(),
                Store = new MemoryStore()
            };
            world.Source = new InMemoryInputSource { FocusState = world.Focus };
            world.Roblox = new LuaCsRbxApiBindings(inputSource: world.Source);
            world.Roblox.AttachCharacterMotorFactory(_ => world.Motor);
            world.Stack = BuildStack(world.Roblox, world.Store);
            world.Roblox.Players.EnsureActor(world.Roblox.Registry, "player-1");
            world.Stack.Runtime.LoadMod("coreai_player_controls", ControlScriptSource());
            world.Frames(3);
            return world;
        }

        [Test]
        public void ControlScript_W_MovesTheCharacterForwardRelativeToTheCamera()
        {
            ControlledWorld world = LoadControls();

            world.Source.PressKey(KeyW);
            world.Frames(2);

            Assert.Greater(world.Motor.NonZeroMoves, 0, "W must reach Humanoid:Move");
            Assert.AreEqual(-1f, world.Motor.LastMove.Z, 1e-4f, "the default camera looks along -Z");

            world.Source.ReleaseKey(KeyW);
            world.Frames(2);
            Assert.AreEqual(0f, world.Motor.LastMove.Magnitude, 1e-4f, "releasing the key stops");
        }

        [Test]
        public void ControlScript_LeftThumbstick_WalksForward_AndInsideTheDeadZoneDoesNot()
        {
            ControlledWorld world = LoadControls();

            world.Source.SetThumbstick(1, 0.1f, 0.1f);
            world.Frames(2);
            Assert.AreEqual(0, world.Motor.NonZeroMoves, "a resting stick inside the 0.2 dead zone");

            world.Source.SetThumbstick(1, 0f, 1f);
            world.Frames(2);
            Assert.Greater(world.Motor.NonZeroMoves, 0, "the stick reaches Humanoid:Move");
            Assert.AreEqual(-1f, world.Motor.LastMove.Z, 1e-4f, "pushing it forward walks away from the camera");

            world.Source.SetThumbstick(1, 0f, 0f);
            world.Frames(2);
            Assert.AreEqual(0f, world.Motor.LastMove.Magnitude, 1e-4f, "letting go stops");
        }

        [Test]
        public void ControlScript_LeftThumbstick_WhileTheChatHoldsTheInput_MovesNothing()
        {
            ControlledWorld world = LoadControls();

            world.Focus.SetCaptured("chat", true);
            world.Source.SetThumbstick(1, 0f, 1f);
            world.Frames(3);

            Assert.AreEqual(0, world.Motor.NonZeroMoves);
        }

        [Test]
        public void ControlScript_TypingInTheChat_MovesNothing()
        {
            ControlledWorld world = LoadControls();

            world.Focus.SetCaptured("chat", true);
            world.Source.PressKey(KeyW);
            world.Frames(3);

            Assert.AreEqual(0, world.Motor.NonZeroMoves);
        }

        [Test]
        public void ControlScript_Scriptable_LeavesTheCharacterToTheGame_WhichStillMovesIt()
        {
            ControlledWorld world = LoadControls();
            world.Stack.Runtime.LoadMod("game", @"
                game:GetService('StarterPlayer').DevComputerMovementMode =
                    Enum.DevComputerMovementMode.Scriptable");

            world.Source.PressKey(KeyW);
            world.Frames(3);
            Assert.AreEqual(0, world.Motor.NonZeroMoves, "the default controls are off");

            world.Stack.Runtime.LoadMod("gamecontrols", @"
                local humanoid = game:GetService('Players'):GetPlayers()[1].Character:FindFirstChildOfClass('Humanoid')
                humanoid:Move(Vector3.new(1, 0, 0))");
            Assert.AreEqual(1f, world.Motor.LastMove.X, 1e-4f, "the game's own script still drives it");
        }

        [Test]
        public void ControlScript_DoubleTappedJump_TogglesFlight()
        {
            ControlledWorld world = LoadControls();

            world.Source.PressKey(KeySpace);
            world.Frames(1);
            world.Source.ReleaseKey(KeySpace);
            world.Frames(1);
            world.Source.PressKey(KeySpace);
            world.Frames(1);
            world.Source.ReleaseKey(KeySpace);
            world.Frames(1);

            Assert.IsTrue(world.Motor.Flying, "a second tap within 0.3 s enters flight");

            world.Source.PressKey(KeySpace);
            world.Frames(1);
            world.Source.ReleaseKey(KeySpace);
            world.Frames(1);
            world.Source.PressKey(KeySpace);
            world.Frames(1);

            Assert.IsFalse(world.Motor.Flying, "another double tap leaves it");
        }

        [Test]
        public void ControlScript_SlowSecondJump_DoesNotFly()
        {
            ControlledWorld world = LoadControls();

            world.Source.PressKey(KeySpace);
            world.Frames(1);
            world.Source.ReleaseKey(KeySpace);
            world.Frames(30);
            world.Source.PressKey(KeySpace);
            world.Frames(1);

            Assert.IsFalse(world.Motor.Flying);
        }

        [Test]
        public void MouseWheel_FiresInputChanged_WithTheDirectionInPositionZ()
        {
            InMemoryInputSource source = new();
            LuaCsRbxApiBindings roblox = new(inputSource: source);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                local seen = ''
                UserInputService.InputChanged:Connect(function(input)
                    if input.UserInputType == Enum.UserInputType.MouseWheel then
                        seen = seen .. (input.Position.Z > 0 and '+' or '-') .. ';'
                        store_set('wheel', seen)
                    end
                end)");

            source.ScrollWheel(120f);
            Frame(roblox);
            source.ScrollWheel(-3f);
            Frame(roblox);
            Frame(roblox);

            Assert.AreEqual("+;-;", store.Get("m", "wheel"));
        }
    }
}
