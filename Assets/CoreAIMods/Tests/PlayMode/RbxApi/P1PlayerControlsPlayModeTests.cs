#if COREAI_HAS_INPUT_SYSTEM
using System.Collections;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.Logging;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi
{
    /// <summary>
    /// P1 in a running scene with virtual Input System devices and no LLM: the bundled control script
    /// walks the character on W with the chat closed and not with it open, a double-tapped Space enters
    /// flight, and the wheel zooms the default camera into first person. The world is a real
    /// <see cref="RbxWorldHost"/> with the Unity input source, camera rig and character motor; only the
    /// cursor is an in-memory port, so a run never captures the editor's own cursor.
    /// </summary>
    /// <remarks>
    /// WHY no fixed-step override: the motor runs on the physics clock, and each frame here waits for a
    /// fixed update and then does what the production tick driver (<c>LuaModRuntimeTickDriver</c>) does
    /// across FixedUpdate and Update: opens the physics step, steps the motors, applies the world's
    /// gravity (bound bodies have Unity gravity off) and advances the scheduler once. Without the gravity
    /// step the character spawned five studs up floated there, never grounded, so W and Space tested a
    /// body that could not stand.
    /// WHY manual input updates: the test owns the input clock. Each frame runs one
    /// <see cref="InputSystem.Update()"/> right before the world polls, so a queued press is processed
    /// exactly once and a wheel delta is read before the next update resets it. It also makes this
    /// fixture's input manager the current one again: in a batchmode Editor the input manager that
    /// <see cref="InputTestFixture"/> saved keeps its player-loop hook, and after every Editor update that
    /// hook points the state buffers back at itself; it has no devices in batchmode, so a FixedUpdate
    /// continuation read "Control 'Key:/Keyboard/w' does not have an associated state".
    /// </remarks>
    [TestFixture]
    [Category("InputDevice")]
    public sealed class P1PlayerControlsPlayModeTests : InputTestFixture
    {
        private const string ActorId = "p1-local-player";

        private readonly List<GameObject> _created = new();
        private RbxWorldHost _host;
        private LuaCsRbxApiBindings _roblox;
        private LuaCsModStack _stack;
        private UnityCameraRig _rig;
        private InMemoryCursorPort _cursor;
        private Keyboard _keyboard;
        private Mouse _mouse;
        private RecordingLogger _logger;

        private sealed class NullStore : ILuaModStore
        {
            private readonly Dictionary<string, string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue(modId + "/" + key, out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                _values[modId + "/" + key] = value;
            }

            public void Clear(string modId)
            {
            }
        }

        /// <summary>Keeps the mods' warnings and errors, so a failed check can say what the script hit.</summary>
        private sealed class RecordingLogger : IGameLogger
        {
            public List<string> Problems { get; } = new();

            public void LogDebug(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, Object context = null)
            {
                Problems.Add("warning: " + message);
            }

            public void LogError(GameLogFeature feature, string message, Object context = null)
            {
                Problems.Add("error: " + message);
            }
        }

        public override void TearDown()
        {
            try
            {
                foreach (GameObject created in _created)
                {
                    if (created != null)
                    {
                        Object.Destroy(created);
                    }
                }

                _created.Clear();
            }
            finally
            {
                base.TearDown();
            }
        }

        private IEnumerator BuildWorld()
        {
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
            _logger = new RecordingLogger();
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _keyboard.MakeCurrent();
            _mouse.MakeCurrent();

            GameObject cameraObject = new("P1Camera");
            _created.Add(cameraObject);
            cameraObject.AddComponent<Camera>();
            GameObject hostObject = new("P1WorldHost");
            _created.Add(hostObject);
            _host = hostObject.AddComponent<RbxWorldHost>();
            _host.Initialize();
            yield return null;

            _rig = new UnityCameraRig(cameraObject.transform, _host.Binder);
            _cursor = new InMemoryCursorPort();
            UnityNewInputSource source = new(_host.InputFocus, _cursor);
            _roblox = new LuaCsRbxApiBindings(_host.Registry, _host.Game, log: line => _logger.Problems.Add(line),
                partSink: _host.Binder, cameraRig: _rig, inputSource: source);
            LuaLogService modLog = new(mirrorLogger: _logger);
            modLog.EntryAppended += entry =>
            {
                if (entry.Level != LuaLogLevel.Print)
                {
                    _logger.Problems.Add(entry.ModId + " " + entry.Level + ": " + entry.Message);
                }
            };
            _roblox.WorldPhysics.AttachPort(_host.PhysicsPort);
            InstanceGameObjectBinder binder = _host.Binder;
            _roblox.AttachCharacterMotorFactory(humanoid =>
            {
                RbxInstance root = humanoid.RootPart;
                return root != null && binder.TryGetBoundObject(root.Id, out GameObject body)
                       && body.TryGetComponent(out Rigidbody rigidbody)
                    ? new UnityRbxCharacterMotor(rigidbody)
                    : null;
            });
            _stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = _logger,
                LogService = modLog,
                ModStore = new NullStore(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = _roblox
            });
            // WHY every mod is loaded as the local player: an actor that loads a mod joins the world as a
            // player with its own character (the mod context connects its actor), and a mod's
            // Players.LocalPlayer is that player. The composed play world has exactly one player
            // (ReadyToPlayWorldPlayModeTests): its default content is loaded as the local player and the
            // bundled controls with no actor. Loaded by a separate host actor, the script spawned and drove
            // a second character at the spawn point while this test read the first: W "walked" only when
            // the second one was pushed out behind the first and shoved it, and the double tap flew the
            // other character.
            Load(ActorId, "p1_floor", @"
                local floor = Instance.new('Part')
                floor.Name = 'P1Floor'
                floor.Size = Vector3.new(400, 2, 400)
                floor.Position = Vector3.new(0, -1, 0)
                floor.Anchored = true
                floor.Parent = workspace");
            TextAsset controls = Resources.Load<TextAsset>("CoreAIMods/coreai_player_controls");
            Assert.IsNotNull(controls, "the bundled control script ships in Resources/CoreAIMods");
            Load(ActorId, "coreai_player_controls", controls.text);
            _roblox.LocalPlayerActorId = ActorId;
            _roblox.Players.EnsureActor(_roblox.Registry, ActorId);
            yield return Frames(60);
            Assert.IsNotNull(Humanoid(), "the local character spawned");
            Assert.AreEqual(1, _roblox.Players.GetPlayers().Count,
                "one player sits at this screen; a second one means the controls drive someone else");
        }

        /// <summary>Loads a mod as an unprivileged local actor, the public path a host uses.</summary>
        private void Load(string actorId, string id, string code)
        {
            ActorContext loader = new LocalActorIdentityProvider(actorId).GetActorContext(null);
            _stack.Runtime.LoadMod(loader, id, code, LuaCapabilities.All, false);
        }

        private IEnumerator Frames(int count)
        {
            for (int frame = 0; frame < count; frame++)
            {
                yield return new WaitForFixedUpdate();
                InputSystem.Update();
                _roblox.WorldPhysics.BeginPhysicsStep();
                _roblox.StepCharacterMotors(Time.fixedDeltaTime);
                _host.PhysicsPort.ApplyGravity();
                _roblox.Scheduler.Advance(Time.fixedDeltaTime);
            }
        }

        /// <summary>Queues a press; the next frame's input update applies it.</summary>
        private void Hold(ButtonControl button)
        {
            // WHY the update first: queueing reads the device's current state, which needs this fixture's
            // input manager to be the current one (see the remarks on the class).
            InputSystem.Update();
            Press(button);
        }

        /// <summary>Queues a release; the next frame's input update applies it.</summary>
        private void Lift(ButtonControl button)
        {
            InputSystem.Update();
            Release(button);
        }

        /// <summary>What the world saw, for the failure messages.</summary>
        private string Describe()
        {
            RbxHumanoid humanoid = Humanoid();
            int shown = System.Math.Min(6, _logger.Problems.Count);
            string problems = _logger.Problems.Count == 0
                ? "none"
                : _logger.Problems.Count + ", last: " +
                  string.Join(" | ", _logger.Problems.GetRange(_logger.Problems.Count - shown, shown));
            return "state " + humanoid?.GetState() + ", requested move " + humanoid?.RequestedMoveDirection +
                   ", root " + RootPosition() + ", W down at the source " +
                   _roblox.UserInputService.InputSource.IsKeyCodeDown(119) + ", mod problems: " + problems;
        }

        private RbxHumanoid Humanoid()
        {
            _roblox.Players.TryGetByActorId(ActorId, out RbxPlayer player);
            return player?.Character?.FindFirstChildOfClass("Humanoid") as RbxHumanoid;
        }

        private RbxVector3 RootPosition()
        {
            return _roblox.PartSink.GetLivePositionStuds(Humanoid().RootPart.Id);
        }

        [UnityTest]
        public IEnumerator HoldingW_WithTheChatClosed_WalksTheCharacterAwayFromTheCamera()
        {
            yield return BuildWorld();
            RbxVector3 start = RootPosition();

            Hold(_keyboard.wKey);
            yield return Frames(60);
            string whileHeld = Describe();
            Lift(_keyboard.wKey);

            RbxVector3 moved = RootPosition() - start;
            TestContext.WriteLine("[P1] W moved the character by " + moved + "; while held: " + whileHeld);
            Assert.Less(moved.Z, -2f, "the default camera looks along -Z, so W walks toward -Z (while held: " +
                                      whileHeld + ")");
        }

        [UnityTest]
        public IEnumerator HoldingW_WhileTheChatHoldsTheInput_MovesNothing()
        {
            yield return BuildWorld();
            _host.InputFocus.SetCaptured("chat", true);
            RbxVector3 start = RootPosition();

            Hold(_keyboard.wKey);
            yield return Frames(60);
            Lift(_keyboard.wKey);
            _host.InputFocus.SetCaptured("chat", false);

            RbxVector3 moved = RootPosition() - start;
            Assert.Less(new RbxVector3(moved.X, 0f, moved.Z).Magnitude, 0.25f, "typing wasd moves nothing");
        }

        [UnityTest]
        public IEnumerator DoubleTappedSpace_EntersFlight()
        {
            yield return BuildWorld();

            Hold(_keyboard.spaceKey);
            yield return Frames(3);
            Lift(_keyboard.spaceKey);
            yield return Frames(3);
            Hold(_keyboard.spaceKey);
            yield return Frames(3);
            Lift(_keyboard.spaceKey);
            yield return Frames(3);

            TestContext.WriteLine("[P1] after the double tap: " + Describe());
            Assert.AreEqual(RbxHumanoidState.Flying, Humanoid().GetState(), Describe());
        }

        [UnityTest]
        public IEnumerator WheelForward_ZoomsTheDefaultCameraIntoFirstPerson()
        {
            yield return BuildWorld();

            for (int notch = 0; notch < 8; notch++)
            {
                // WHY one notch per frame: the scroll is a per-update delta; the frame's input update applies
                // it, the world reads it in the same frame, and the next update resets it.
                InputSystem.Update();
                Set(_mouse.scroll, new Vector2(0f, 120f));
                yield return Frames(1);
            }

            yield return Frames(1);
            RbxVector3 focus = RootPosition() + LuaCsRbxApiBindings.HumanoidCameraSubjectOffset;
            float distance = (focus - _rig.GetCFrame().Position).Magnitude;
            TestContext.WriteLine("[P1] camera distance after 8 notches: " + distance);
            Assert.Less(distance, 1f, "first person sits half a stud from the head");
            Assert.AreEqual(RbxMouseLock.LockCenter, _cursor.Applied, "first person locks the pointer");
        }
    }
}
#endif
